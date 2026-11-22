using System.Security.Cryptography;
using System.Text.Json;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Services.Export;

/// <summary>
///     Decides whether a save may overwrite a companion file it wrote on an
///     earlier save, by remembering the exact bytes it last put there.
/// </summary>
/// <remarks>
///     The Charter, DLC Builder and EOF companions are other editors' native project
///     formats. Only byte-for-byte matches of our last write may be replaced.
///     Missing or unreadable ownership is a refusal. Stamps travel with the chart
///     in hidden <c>.dotcharter/companions.json</c>, keyed by chart-relative path.
///     Generated media and XML use the same evidence before replacement or recycling.
/// </remarks>
public static class CompanionOwnership
{
    /// <summary>The stamp file, a sibling of the companions it describes.</summary>
    public const string StampFileName = ".dotcharter/companions.json";

    internal const string LegacyStampFileName = ".dotcharter-companions.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    ///     Writes <paramref name="path" /> through <paramref name="write" /> unless
    ///     the file already there is one we did not write, and stamps the result.
    /// </summary>
    /// <param name="path">Absolute path of the companion file.</param>
    /// <param name="write">
    ///     Produces the file at the path it is handed. Called at most once, and only
    ///     when writing is allowed.
    /// </param>
    /// <param name="skipReason">
    ///     Why the write was refused, phrased for a log line — null when it went
    ///     ahead.
    /// </param>
    /// <returns><c>true</c> when the file was written.</returns>
    public static bool WriteIfUnmodified(string path, Action<string> write, out string? skipReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(write);

        var full = Path.GetFullPath(path);
        var name = KeyFor(full);

        if (File.Exists(full))
        {
            var stamps = ReadStamps(full);
            var recorded = stamps.GetValueOrDefault(name);

            if (string.IsNullOrEmpty(recorded))
            {
                skipReason = "it was not written by DotCharter (no ownership stamp)";
                return false;
            }

            var actual = HashOf(full);
            if (actual is null)
            {
                skipReason = "its current contents could not be read to compare";
                return false;
            }

            if (!string.Equals(actual, recorded, StringComparison.Ordinal))
            {
                skipReason = "it has been edited since DotCharter wrote it";
                return false;
            }
        }

        write(full);
        skipReason = null;
        RecordStampBestEffort(full, name);
        return true;
    }

    /// <summary>Builds a release and stamps it when it publishes the saved project's companion package.</summary>
    public static async Task<RocksmithBuildResult> BuildAndRecordReleaseAsync(Func<Task<RocksmithBuildResult>> build,
        string? projectDirectory, ChartProject snapshot)
    {
        var result = await build().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(projectDirectory)) return result;
        // A mixed export also publishes the standard package beside the RS Extended Range one.
        bool[] kinds = result.StandardPackagePaths.Count > 0 ? [true, false] : [result.IsPsxr];
        foreach (var extended in kinds)
        {
            var name = RocksmithPackageName.For(snapshot, snapshot.Chart.Version, extended: extended);
            var expected = Path.Combine(projectDirectory, ProjectFolderLayout.ExportFolderName, name);
            if (result.PackagePaths.Any(path => Path.GetFullPath(path).Equals(expected, StringComparison.OrdinalIgnoreCase)))
                RecordStampBestEffort(expected, KeyFor(expected));
        }
        return result;
    }

    private static void RecordStampBestEffort(string full, string name)
    {
        // Stamping is best-effort BY DESIGN: if it fails, the next save sees an
        // unstamped file and skips, which is the safe direction.
        try
        {
            RecordStamp(full, name);
        }
        catch (Exception)
        {
            // ignored — see above
        }
    }

    /// <summary>Forgets the stamp for a companion, so the next save treats whatever
    /// is on disk as somebody else's file.</summary>
    public static void Forget(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path);
        var stamps = ReadStamps(full);
        if (!stamps.Remove(KeyFor(full))) return;
        WriteStamps(full, stamps);
    }

    private static void RecordStamp(string companionPath, string name)
    {
        var hash = HashOf(companionPath);
        if (hash is null) return;

        var stamps = ReadStamps(companionPath);
        stamps[name] = hash;
        WriteStamps(companionPath, stamps);
    }

    internal static string StampPathFor(string companionPath) =>
        Path.Combine(ProjectFolderLayout.RootForArtifact(companionPath), StampFileName);

    private static string KeyFor(string path) =>
        Path.GetRelativePath(ProjectFolderLayout.RootForArtifact(path), path).Replace('\\', '/');

    internal static bool IsUnmodified(string path) =>
        ReadStamps(path).TryGetValue(KeyFor(path), out var recorded) && HashOf(path) is { } hash && hash == recorded;

    internal static void AdoptMovedFile(string path) => RecordStamp(path, KeyFor(path));

    internal static void ImportLegacyStamps(string projectPath, Action<string> recycle)
    {
        var root = ProjectFolderLayout.ProjectDirectory(projectPath);
        var legacy = Path.Combine(root, LegacyStampFileName);
        if (!File.Exists(legacy)) return;
        var imported = JsonSerializer.Deserialize<StampFile>(AtomicFile.ReadAllText(legacy));
        if (imported?.Version != 1 || imported.Files == null) return;
        var anchor = ProjectFiles.DefaultPath(root);
        var stamps = ReadStamps(anchor);
        foreach (var (key, value) in imported.Files)
        {
            if (key != Path.GetFileName(key) || key.IndexOfAny(['/', '\\']) >= 0) continue;
            stamps.TryAdd(key, value);
        }
        WriteStamps(anchor, stamps);
        recycle(legacy);
    }

    private static Dictionary<string, string> ReadStamps(string companionPath)
    {
        var path = StampPathFor(companionPath);
        // OrdinalIgnoreCase: the file names come from the same code every time, but
        // the paths they are derived from are Windows paths and case is not ours.
        var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return empty;

        try
        {
            var file = JsonSerializer.Deserialize<StampFile>(AtomicFile.ReadAllText(path));
            if (file?.Files is null) return empty;
            foreach (var (key, value) in file.Files) empty[key] = value;
            return empty;
        }
        catch (Exception)
        {
            // A corrupt stamp file means every companion reads as foreign, which
            // costs a regeneration and protects everything. Do not delete it.
            return empty;
        }
    }

    private static void WriteStamps(string companionPath, Dictionary<string, string> stamps)
    {
        ProjectFolderLayout.MetadataDirectory(ProjectFolderLayout.RootForArtifact(companionPath));
        AtomicFile.WriteAllText(StampPathFor(companionPath),
            JsonSerializer.Serialize(new StampFile { Version = 1, Files = stamps }, JsonOptions));
    }

    private static string? HashOf(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed class StampFile
    {
        public int Version { get; set; }
        public Dictionary<string, string> Files { get; set; } = [];
    }
}
