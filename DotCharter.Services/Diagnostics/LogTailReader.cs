using System.Text;

namespace DotCharter.Services.Diagnostics;

/// <summary>
///     Reads the END of a log file without materializing the whole thing.
///     <para>
///         The rolling sink is bounded by <c>AppConfig.LogFileMaxBytes</c>
///         (10 MB), so "read it and keep the last N lines" is up to 10 MB of file
///         → a ~20 MB string → another ~20 MB of split fragments, all on the LOH
///         and all thrown away, every time the Session Health panel refreshes.
///         Seeking to <c>length - maxBytes</c> first turns that into a bounded
///         read no matter how large the log grew.
///     </para>
///     <para>
///         Both readers open <c>FileShare.ReadWrite</c> so the LIVE log — the one
///         the sink still holds — can be read.
///     </para>
/// </summary>
public static class LogTailReader
{
    /// <summary>
    ///     The last <paramref name="maxBytes" /> bytes of <paramref name="path" />
    ///     as UTF-8 text. <paramref name="truncated" /> reports whether the file
    ///     was larger (so a caller can say so). The seek can land mid-character or
    ///     mid-line; callers that care drop the first partial line.
    /// </summary>
    public static string ReadTailText(string path, int maxBytes, out bool truncated)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = fs.Length;
        truncated = length > maxBytes;
        if (truncated) fs.Seek(length - maxBytes, SeekOrigin.Begin);

        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    ///     The last <paramref name="maxLines" /> lines of <paramref name="path" />,
    ///     reading at most <paramref name="maxBytes" /> from the end. A truncated
    ///     read drops its first line, which the seek may have cut in half.
    ///     <para>
    ///         Fewer lines than asked for is a legitimate answer: it means the
    ///         byte window did not hold that many (a log tail of enormous stack
    ///         traces), and showing what fits is the right degradation — the point
    ///         of the window is that the cost cannot depend on the file's size.
    ///     </para>
    /// </summary>
    public static string ReadTailLines(string path, int maxLines, int maxBytes)
    {
        var text = ReadTailText(path, maxBytes, out var truncated);
        var lines = text.Split('\n');

        // A log file ends with a newline, so the split's last element is empty —
        // count it and the caller gets maxLines-1 real ones.
        var end = lines.Length;
        while (end > 0 && lines[end - 1].Trim('\r').Length == 0) end--;

        var skip = truncated && end > 1 ? 1 : 0;
        var start = Math.Max(skip, end - maxLines);
        return string.Join('\n', lines[start..end]).TrimEnd();
    }

    /// <summary>The log file that is ACTUALLY being written. The configured
    /// path is the sink's BASE name (<c>dotcharter.log</c>) — the rolling sink
    /// writes <c>dotcharter_&lt;n&gt;.log</c> next to it, so the base name
    /// often does not exist and the Recent Log panel read as empty (owner
    /// report). Newest matching sibling wins.</summary>
    public static string? ResolveActiveLogFile(string? configuredPath)
    {
        if (string.IsNullOrEmpty(configuredPath)) return null;
        try
        {
            var dir = Path.GetDirectoryName(configuredPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return File.Exists(configuredPath) ? configuredPath : null;

            var baseName = Path.GetFileNameWithoutExtension(configuredPath);
            var ext = Path.GetExtension(configuredPath);
            var newest = Directory.EnumerateFiles(dir, baseName + "*" + ext)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            return newest?.FullName ?? (File.Exists(configuredPath) ? configuredPath : null);
        }
        catch
        {
            return File.Exists(configuredPath) ? configuredPath : null;
        }
    }
}
