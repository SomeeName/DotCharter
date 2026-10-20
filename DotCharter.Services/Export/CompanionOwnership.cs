using System.Security.Cryptography;
using System.Text.Json;

namespace DotCharter.Services.Export;

/// <summary>
///     Decides whether a save may overwrite a companion file it wrote on an
///     earlier save, by remembering the exact bytes it last put there.
/// </summary>
/// <remarks>
///     <para>
///         Most save companions are pure derived output — the <c>DTRS XML</c>
///         bundle, the <c>.psarc</c> — and regenerating them wholesale is correct.
///         Two are not: the <c>.rs2dlc</c> and the <c>.eof</c> are the NATIVE
///         PROJECT FORMATS of two other editors, so the charter opens them, edits
///         them there and saves. Everything they add lives in fields DotCharter
///         does not author and cannot round-trip.
///     </para>
///     <para>
///         Owner report: tones assigned by hand in DLC Builder were
///         gone after the next Ctrl+S in DotCharter. <c>Rs2DlcProjectDto.Tones</c>
///         and the per-arrangement <c>BaseTone</c>/<c>Tones</c> are always written
///         EMPTY by us — so the rewrite is not a merge that lost them, it is a
///         blank that replaced them, silently, on a save the charter made for an
///         unrelated reason.
///     </para>
///     <para>
///         The rule: we overwrite a companion only when the file on disk is
///         byte-for-byte what we last wrote there. Anything else — edited, replaced,
///         or simply never stamped — is somebody's work and is left alone. Note
///         which way that fails: an absent or unreadable stamp means DON'T write.
///         The cost of a wrong skip is a stale derived file the charter can delete;
///         the cost of a wrong write is hand-authored data with no undo.
///     </para>
///     <para>
///         The stamps live in one <c>.dotcharter-companions.json</c> beside the
///         project, keyed by file NAME — so it travels when the folder is moved or
///         copied. Deliberately NOT hidden: it is the only visible answer to
///         "why has my <c>.rs2dlc</c> stopped following the chart". It is a cache,
///         never a source of truth — losing it costs one skipped regeneration, and
///         the charter recovers by deleting the companion.
///     </para>
/// </remarks>
public static class CompanionOwnership
{
    /// <summary>The stamp file, a sibling of the companions it describes.</summary>
    public const string StampFileName = ".dotcharter-companions.json";

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
        var name = Path.GetFileName(full);

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

        return true;
    }

    /// <summary>Forgets the stamp for a companion, so the next save treats whatever
    /// is on disk as somebody else's file.</summary>
    /// <remarks>Exists for the tests and for a future "stop managing this file"
    /// action; nothing in the save path calls it.</remarks>
    public static void Forget(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path);
        var stamps = ReadStamps(full);
        if (!stamps.Remove(Path.GetFileName(full))) return;
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

    private static string StampPathFor(string companionPath) =>
        Path.Combine(Path.GetDirectoryName(companionPath) ?? ".", StampFileName);

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
