namespace DotCharter.Services.Update;

/// <summary>
///     The files a patch payload needs REMOVED from the install, carried inside
///     the patch as a plain text file.
///     <para>
///         <b>Why it has to exist.</b> An update is robocopied over the install
///         dir with <b>no purge</b> — that is exactly what lets a patch contain
///         only the changed files. The same property means a file the new release
///         DROPPED survives forever unless something deletes it explicitly. For a
///         full zip that is merely untidy; for a patch it is the difference
///         between "0.42 removed this DLL" and "every patched install still loads
///         the 0.41 one".
///     </para>
///     <para>
///         <b>Why the parser is strict.</b> Each surviving line becomes a
///         <c>del</c> argument in a generated batch script that runs over the
///         install directory with the user's rights. The list is produced by the
///         release script, but it arrives inside a downloaded archive, so it is
///         treated as untrusted input: anything rooted, anything that walks up out
///         of the payload, anything cmd.exe would re-interpret, anything
///         non-ASCII (the script must stay plain ASCII for cmd's default
///         codepage), and — above all — anything in
///         <see cref="PayloadManifest.AlwaysInclude" /> is dropped. Deleting
///         <c>DotCharter.exe</c> would leave an install that cannot start and
///         cannot self-repair.
///     </para>
/// </summary>
public static class PayloadDeletions
{
    /// <summary>The file a patch payload carries this list in.</summary>
    public const string FileName = "payload-deletions.txt";

    /// <summary>
    ///     Bytes of this list we will read. A real one is a handful of paths; the
    ///     file arrives inside an archive an unauthenticated LAN peer streamed at
    ///     us, and <see cref="TakeFrom" /> used <c>File.ReadAllText</c> on it —
    ///     unbounded, after the extractor's byte budget had already been spent
    ///     writing it to disk. Truncation just parses fewer lines, which is the
    ///     right answer for a list this size.
    /// </summary>
    public const int MaxListBytes = 1024 * 1024;

    /// <summary>
    ///     Entries kept from one list. Every surviving line becomes a <c>del</c> in
    ///     a generated batch script, so this bounds the SCRIPT as well as the parse
    ///     — an attacker-authored list of a million legal-looking relative paths is
    ///     a million-line cmd file run over the install directory.
    /// </summary>
    public const int MaxEntries = 4096;

    /// <summary>
    ///     Characters cmd.exe would re-interpret inside a quoted argument, plus
    ///     <c>~</c>.
    ///     <para>
    ///         <b>The tilde is the non-obvious one and it defeated this guard.</b>
    ///         NTFS still generates 8.3 aliases, so <c>DotCharter.exe</c> is also
    ///         reachable as <c>DOTCHA~1.EXE</c> — a name that is pure ASCII, has no
    ///         traversal segment, and does not match any entry in
    ///         <see cref="PayloadManifest.AlwaysInclude" />. It therefore passed
    ///         every check here and deleted the executable, which is exactly the
    ///         unbootable, unrepairable install this class exists to prevent
    ///         (verified against real <c>cmd.exe</c>). No legitimate payload path
    ///         contains a tilde.
    ///     </para>
    /// </summary>
    private static readonly char[] Unsafe = ['"', '%', '&', '|', '<', '>', '^', '*', '?', ':', '~'];

    /// <summary>Renders the list, one forward-slashed relative path per line.</summary>
    public static string Write(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var lines = new List<string>
        {
            "# Files this patch removes from the install. One relative path per line.",
        };
        lines.AddRange(Parse(string.Join('\n', paths)));
        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>
    ///     Parses a deletion list, keeping only entries that are safe to hand to
    ///     a batch <c>del</c>. Never throws; an unparseable list yields none.
    /// </summary>
    public static IReadOnlyList<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var kept = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var path = line.Replace('\\', '/');

            // A rooted path is a bug in whatever produced the list. Trimming the
            // leading slash would silently REINTERPRET it as relative to the
            // install dir and delete something — refusing is the honest answer.
            if (path.StartsWith('/') || Path.IsPathRooted(path)) continue;
            if (path.Length == 0) continue;

            if (path.Any(c => c > 127 || char.IsControl(c))) continue;
            if (path.IndexOfAny(Unsafe) >= 0) continue;

            var segments = path.Split('/');
            if (segments.Any(s => s.Length == 0 || s == "." || s == "..")) continue;

            // Never delete what the payload has to keep — an install missing any
            // of these cannot start, and cannot be repaired by another update.
            if (PayloadManifest.AlwaysInclude.Contains(segments[^1], StringComparer.OrdinalIgnoreCase)) continue;
            if (string.Equals(segments[^1], FileName, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(segments[^1], PayloadManifest.FileName, StringComparison.OrdinalIgnoreCase)) continue;

            if (seen.Add(path)) kept.Add(path);
            if (kept.Count >= MaxEntries) break;
        }

        return kept;
    }

    /// <summary>
    ///     Reads at most <see cref="MaxListBytes" /> of a file as UTF-8. Returns
    ///     null when it cannot be read at all; a truncated read is handed back
    ///     truncated, because <see cref="Parse" /> drops whatever the cut mangled
    ///     and keeps the rest.
    /// </summary>
    private static string? TryReadBounded(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[Math.Min(MaxListBytes, Math.Max(1, stream.Length))];
            var read = 0;
            while (read < buffer.Length)
            {
                var got = stream.Read(buffer, read, buffer.Length - read);
                if (got <= 0) break;
                read += got;
            }

            return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Takes the deletion list OUT of a staged payload: reads it, then removes
    ///     the file so the robocopy that follows does not plant it in the install.
    ///     Returns an empty list when the payload carries none.
    /// </summary>
    public static IReadOnlyList<string> TakeFrom(string payloadDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadDirectory);

        var path = Path.Combine(payloadDirectory, FileName);
        if (!File.Exists(path)) return [];

        // BOUNDED, not ReadAllText: this file came out of an archive an
        // unauthenticated peer streamed at us, and the extractor's budget is
        // already spent by the time it is on disk.
        var text = TryReadBounded(path);
        if (text == null) return [];
        var parsed = Parse(text);

        // A leftover would only be copied over as an inert text file.
        BestEffortDelete.File(path);

        return parsed;
    }
}
