using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DotCharter.Services.Export.Rocksmith;

/// <summary>Whether Rocksmith is running right now: the one fact a test export asks the machine.</summary>
public interface IRocksmithGameProbe
{
    bool IsRunning { get; }
}

/// <inheritdoc />
public sealed class RocksmithGameProbe : IRocksmithGameProbe
{
    /// <summary>DLC Builder's own probe: a process called <c>Rocksmith2014</c>.</summary>
    public bool IsRunning
    {
        get
        {
            var processes = Process.GetProcessesByName("Rocksmith2014");
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }
    }
}

/// <summary>
///     What a test export writes and what it removes, the way DLC Builder's
///     <c>TestPackageBuilder</c> decides it. Snapshot plans use the shared artist/title
///     file name and carry the version "test". While the game runs it cannot be replaced, so the
///     build is a NEW version: <c>_vN</c> on the file name, <c>vN</c> on the DLC key and
///     <c>" vN"</c> on the title (N is one past the newest <c>_vN</c> in the folder, or the
///     number of earlier test builds when none carries one). With the game closed, the
///     plain name is reused; earlier names are proposed for cleanup only after successful publication.
/// </summary>
public sealed record RocksmithTestPlan(string DlcKey, string FileStem, string VersionTag, IReadOnlyList<string> Superseded)
{
    /// <summary>The title after the plain one: empty for the plain build, <c>" v2"</c> for a versioned one.</summary>
    public string TitleSuffix => VersionTag.Length == 0 ? "" : " " + VersionTag;
}

/// <summary>Plans a test export. Pure: the folder's files and the running game are inputs.</summary>
public static partial class RocksmithTestExport
{
    /// <summary>Legacy build entry point without ownership evidence; it retains superseded paths.</summary>
    public static async Task<RocksmithBuildResult> BuildAndCleanUpAsync(Func<Task<RocksmithBuildResult>> build,
        IExportTargetWriter files, IReadOnlyList<string> superseded) => await build().ConfigureAwait(false);

    /// <summary>The version a test build carries (<c>project.Version = "test"</c>).</summary>
    public const string Version = "test";

    /// <summary>The legacy file name stem for a key, retained for compatibility and recognizing old test builds.</summary>
    public static string PackageName(string dlcKey) => dlcKey.ToLowerInvariant();

    /// <summary>Plans artist/title test names while recognizing legacy lower-case-key packages for cleanup and numbering.</summary>
    public static RocksmithTestPlan Plan(ChartProject snapshot, string dlcKey, IEnumerable<string> testFolderFiles, bool gameRunning)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var packageName = RocksmithPackageName.Stem(snapshot, Version, test: true);
        var existing = MatchingPackages(testFolderFiles, packageName, PackageName(dlcKey)).ToArray();
        return CreatePlan(dlcKey, packageName, existing, gameRunning);
    }

    /// <summary>Legacy key-only planning for callers without a project snapshot.</summary>
    public static RocksmithTestPlan Plan(string dlcKey, IEnumerable<string> testFolderFiles, bool gameRunning)
    {
        var packageName = PackageName(dlcKey);
        var existing = testFolderFiles
            .Where(file => IsLegacyPackageName(Path.GetFileName(file), packageName))
            .Select(file => (File: file, Suffix: Path.GetFileName(file)[packageName.Length..]))
            .ToArray();
        return CreatePlan(dlcKey, packageName, existing, gameRunning);
    }

    private static RocksmithTestPlan CreatePlan(string dlcKey, string packageName,
        IReadOnlyList<(string File, string Suffix)> existing, bool gameRunning)
    {
        if (!gameRunning) return new RocksmithTestPlan(dlcKey, packageName, "", existing.Select(file => file.File).ToArray());

        var numbered = existing
            .Select(file => VersionNumber().Match(file.Suffix))
            .Where(match => match.Success)
            .Select(match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        var latest = numbered.Count == 0 ? existing.Count : numbered.Max();
        var tag = $"v{latest + 1}";
        return new RocksmithTestPlan(dlcKey + tag, $"{packageName}_{tag}", tag, []);
    }

    private static IEnumerable<(string File, string Suffix)> MatchingPackages(
        IEnumerable<string> files, string packageName, string legacyName)
    {
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith(packageName, StringComparison.OrdinalIgnoreCase) && CurrentPackageSuffix().IsMatch(name[packageName.Length..]))
                yield return (file, name[packageName.Length..]);
            else if (IsLegacyPackageName(name, legacyName))
                yield return (file, name[legacyName.Length..]);
        }
    }

    internal static bool IsLegacyPackageName(string name, string dlcKey)
    {
        if (!name.StartsWith(dlcKey, StringComparison.OrdinalIgnoreCase)) return false;
        return PackageSuffix().IsMatch(name[dlcKey.Length..]);
    }

    [GeneratedRegex(@"\A(?:_v[0-9]+)?(?:_rsxr_[pm]|_[pm])\.psarc\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CurrentPackageSuffix();

    [GeneratedRegex(@"\A(?:_v[0-9]+)?(?:_rsxr_p|_[pm])?\.psarc\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PackageSuffix();

    [GeneratedRegex(@"\A_v([0-9]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionNumber();
}
