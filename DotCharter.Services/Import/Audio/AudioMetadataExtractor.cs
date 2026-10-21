using System.Text.Json;
using DotCharter.Services.Export.Audio;
using DotCharter.Services.Import.Youtube;
using DotCharter.Services.Infrastructure;
using DotCharter.Services.Parsing;
using SkiaSharp;

namespace DotCharter.Services.Import.Audio;

/// <summary>
///     Reads artist / title / album / year / release-date / genre tags and the
///     embedded cover art out of an audio file, using the bundled ffprobe/ffmpeg
///     CLI. The tag mapping (<see cref="ParseFfprobeJson" />) is a pure, static
///     function so it can be unit-tested without either tool; the process
///     invocation is a thin wrapper around it.
/// </summary>
/// <remarks>
///     This runs on an import path — a missing tool, an untagged file or a
///     malformed probe output must never throw. Every failure degrades to
///     <see cref="AudioMetadataResult.Empty" /> (or a null cover), so the caller
///     can always fall back to filename-derived defaults.
/// </remarks>
public static class AudioMetadataExtractor
{
    /// <summary>Bounded so an import can never wedge on a stuck child. ffprobe
    /// reads only headers/tags; the single-frame ffmpeg extract is likewise
    /// cheap. Generous multiples of the expected sub-second cost.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan CoverExtractTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Cover art edge, in pixels (square). Matches the largest DDS art
    /// tier the Rocksmith package builder emits.</summary>
    public const int CoverArtSize = 512;

    /// <summary>
    ///     Extracts metadata + cover art from <paramref name="audioFilePath" />.
    ///     Returns <see cref="AudioMetadataResult.Empty" /> when the file is
    ///     missing, neither ffprobe nor ffmpeg can be resolved, or both reads fail. The cover art
    ///     is only populated when the file carries an attached picture AND ffmpeg
    ///     is available; otherwise <see cref="AudioMetadataResult.CoverArtPng" />
    ///     is null.
    /// </summary>
    /// <param name="audioFilePath">The audio file to read.</param>
    /// <param name="configuredFfprobePath">An explicit ffprobe path (config), or null to auto-resolve.</param>
    /// <param name="configuredFfmpegPath">An explicit ffmpeg path (config), or null to auto-resolve.</param>
    public static AudioMetadataResult Extract(
        string audioFilePath,
        string? configuredFfprobePath = null,
        string? configuredFfmpegPath = null) =>
        Extract(
            audioFilePath,
            (name, configured) => MediaToolResolver.Resolve(
                name, AppContext.BaseDirectory, configured,
                MediaToolResolver.CurrentPathDirs(), File.Exists, OperatingSystem.IsWindows()),
            MediaProcess.Run,
            configuredFfprobePath,
            configuredFfmpegPath);

    /// <summary>Resolves a tool (<c>ffprobe</c> / <c>ffmpeg</c>, optional configured path) to an executable or null.</summary>
    internal delegate string? ToolResolver(string baseName, string? configuredPath);

    /// <summary>Runs a resolved tool; the seam that lets tests drive the extractor without a real binary.</summary>
    internal delegate MediaProcess.MediaProcessResult ToolRunner(
        string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    ///     Tag source order: ffprobe when it resolves and probes cleanly, else ffmpeg
    ///     alone (<c>-f ffmetadata</c>). Release installs bundle ffmpeg but NOT
    ///     ffprobe, so the ffmpeg path must be complete on its own.
    /// </summary>
    internal static AudioMetadataResult Extract(
        string audioFilePath,
        ToolResolver resolve,
        ToolRunner run,
        string? configuredFfprobePath = null,
        string? configuredFfmpegPath = null)
    {
        if (string.IsNullOrWhiteSpace(audioFilePath) || !File.Exists(audioFilePath))
            return AudioMetadataResult.Empty;

        try
        {
            FfprobeMetadata? meta = null;

            var ffprobe = resolve("ffprobe", configuredFfprobePath);
            if (ffprobe is not null)
            {
                var probe = TryRun(run, ffprobe,
                    ["-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", audioFilePath]);
                if (probe is { Succeeded: true } && !string.IsNullOrWhiteSpace(probe.Value.StandardOutput))
                    meta = ParseFfprobeJson(probe.Value.StandardOutput);
            }

            var ffmpeg = resolve("ffmpeg", configuredFfmpegPath);
            if (meta is null)
            {
                if (ffmpeg is null) return AudioMetadataResult.Empty;
                var dump = TryRun(run, ffmpeg,
                    ["-hide_banner", "-loglevel", "error", "-i", audioFilePath, "-f", "ffmetadata", "-"]);
                if (dump is not { Succeeded: true } || string.IsNullOrWhiteSpace(dump.Value.StandardOutput))
                    return AudioMetadataResult.Empty;
                // Without ffprobe there is no stream list to read attached_pic from,
                // so the cover extract is attempted unconditionally (it just fails
                // cleanly when the file has no picture).
                meta = ParseFfmetadata(dump.Value.StandardOutput) with { HasAttachedPicture = true };
            }

            var tags = meta.Value;
            byte[]? cover = null;
            if (tags.HasAttachedPicture && ffmpeg is not null)
                cover = TryExtractCoverArt(audioFilePath, ffmpeg, run);

            return new AudioMetadataResult(
                tags.Artist, tags.Title, tags.Album, tags.AlbumYear, tags.ReleaseDate, tags.Genre, cover);
        }
        catch
        {
            // Import convenience only — never take down the import over metadata.
            return AudioMetadataResult.Empty;
        }
    }

    private static MediaProcess.MediaProcessResult? TryRun(
        ToolRunner run, string executable, IReadOnlyList<string> args)
    {
        try
        {
            return run(executable, args, ProbeTimeout, CancellationToken.None);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Pure <c>ffmpeg -f ffmetadata</c> text to metadata mapping. Handles the
    ///     format's escaping (backslash before =, ;, #, backslash, and a backslash
    ///     before a line break for multi-line values), keeps only the
    ///     global section (<c>[STREAM]</c> / <c>[CHAPTER]</c> blocks are skipped,
    ///     so a cover stream's "title" never becomes the song title) and shares
    ///     the key precedence of <see cref="ParseFfprobeJson" />. Never throws.
    ///     <see cref="FfprobeMetadata.HasAttachedPicture" /> is always false here:
    ///     the text does not say.
    /// </summary>
    public static FfprobeMetadata ParseFfmetadata(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return FfprobeMetadata.Empty;

        try
        {
            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var inGlobal = true;
            foreach (var line in LogicalLines(text))
            {
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[' && line[^1] == ']')
                {
                    inGlobal = false;
                    continue;
                }

                if (!inGlobal) continue;
                var eq = IndexOfUnescaped(line, '=');
                if (eq <= 0) continue;
                var key = Unescape(line[..eq]);
                var value = Unescape(line[(eq + 1)..]);
                if (key.Length > 0 && !string.IsNullOrWhiteSpace(value)) tags.TryAdd(key, value);
            }

            return MapTags(tags, false);
        }
        catch
        {
            return FfprobeMetadata.Empty;
        }
    }

    /// <summary>Splits on line breaks, but a line ending in an odd run of
    /// backslashes continues on the next one (the escaped newline inside a value).</summary>
    private static IEnumerable<string> LogicalLines(string text)
    {
        var pending = new System.Text.StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var physical = raw.TrimEnd('\r');
            var slashes = 0;
            for (var i = physical.Length - 1; i >= 0 && physical[i] == '\\'; i--) slashes++;
            if (slashes % 2 == 1)
            {
                pending.Append(physical).Append('\n');
                continue;
            }

            if (pending.Length == 0)
            {
                yield return physical;
                continue;
            }

            yield return pending.Append(physical).ToString();
            pending.Clear();
        }

        if (pending.Length > 0) yield return pending.ToString().TrimEnd('\n');
    }

    private static int IndexOfUnescaped(string line, char target)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\') i++;
            else if (line[i] == target) return i;
        }

        return -1;
    }

    private static string Unescape(string value)
    {
        if (!value.Contains('\\')) return value;
        var sb = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length) i++;
            sb.Append(value[i]);
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Pure ffprobe-JSON → metadata mapping. Reads <c>format.tags</c> (with
    ///     the first tag-bearing stream as a fallback) using case-insensitive
    ///     keys and detects an <c>attached_pic</c> video stream. Never throws —
    ///     malformed JSON yields <see cref="FfprobeMetadata.Empty" />.
    /// </summary>
    public static FfprobeMetadata ParseFfprobeJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return FfprobeMetadata.Empty;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // format.tags is the primary source; stream tags are a fallback for
            // containers (e.g. some MP4/MKV) that hang tags off the stream.
            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var hasAttachedPicture = false;

            if (root.TryGetProperty("streams", out var streams) &&
                streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    MergeTags(tags, stream, overwrite: false); // format tags win, so don't clobber
                    if (IsAttachedPicture(stream)) hasAttachedPicture = true;
                }
            }

            if (root.TryGetProperty("format", out var format))
                MergeTags(tags, format, overwrite: true);

            return MapTags(tags, hasAttachedPicture);
        }
        catch (JsonException)
        {
            return FfprobeMetadata.Empty;
        }
    }

    private static FfprobeMetadata MapTags(Dictionary<string, string> tags, bool hasAttached)
    {
        var artist = Lookup(tags, "artist", "album_artist", "ARTIST", "author");
        var title = Lookup(tags, "title", "TITLE");
        var album = Lookup(tags, "album", "ALBUM");
        var genre = Lookup(tags, "genre", "GENRE");

        // ReleaseDate prefers the fullest date-bearing tag; a bare year is an
        // acceptable last resort. AlbumYear is a 4-digit year distilled from
        // whichever date-ish tag we can find (id3 TYER is exactly a year;
        // "date" is often a bare year, sometimes a full ISO date).
        var releaseDate = Lookup(tags,
            "releasedate", "release_date", "RELEASE_DATE", "date", "DATE", "originaldate", "year");
        var albumYear = ExtractYear(Lookup(tags,
            "year", "YEAR", "date", "DATE", "TYER", "originalyear", "releasedate", "originaldate"));

        return new FfprobeMetadata(artist, title, album, albumYear, releaseDate, genre, hasAttached);
    }

    private static void MergeTags(Dictionary<string, string> into, JsonElement container, bool overwrite)
    {
        if (!container.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
            return;

        foreach (var tag in tags.EnumerateObject())
        {
            if (tag.Value.ValueKind != JsonValueKind.String) continue;
            var value = tag.Value.GetString();
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (overwrite) into[tag.Name] = value;
            else into.TryAdd(tag.Name, value);
        }
    }

    private static bool IsAttachedPicture(JsonElement stream)
    {
        // The reliable signal is disposition.attached_pic == 1 (cover art riding
        // as a still-image "video" stream), independent of codec name.
        if (stream.TryGetProperty("disposition", out var disposition) &&
            disposition.ValueKind == JsonValueKind.Object &&
            disposition.TryGetProperty("attached_pic", out var attached) &&
            attached.ValueKind == JsonValueKind.Number &&
            attached.TryGetInt32(out var flag) && flag == 1)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    ///     First non-blank tag among <paramref name="keys" />, CLEANED — not
    ///     merely trimmed. These strings come from an arbitrary file's tags and
    ///     become project metadata, and the <c>.rscp</c> is XML: a single NUL
    ///     (ID3 text frames are NUL-terminated and taggers routinely leave one
    ///     behind; ffprobe hands it straight back in its JSON) makes every later
    ///     SAVE of that project throw out of <c>XmlWriter</c>, naming no field.
    ///     See <see cref="MetadataText" />.
    /// </summary>
    private static string Lookup(Dictionary<string, string> tags, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (tags.TryGetValue(key, out var value) && MetadataText.Clean(value) is { Length: > 0 } cleaned)
                return cleaned;
        }

        return "";
    }

    /// <summary>First run of 4 consecutive digits, or "" — turns "1999-08-15",
    /// "1999" or "(P) 1999 Label" all into "1999".</summary>
    private static string ExtractYear(string value)
    {
        for (var i = 0; i + 4 <= value.Length; i++)
        {
            var slice = value.AsSpan(i, 4);
            var allDigits = true;
            foreach (var c in slice)
                if (!char.IsAsciiDigit(c)) { allDigits = false; break; }
            if (allDigits) return slice.ToString();
        }
        return "";
    }

    private static byte[]? TryExtractCoverArt(string audioFilePath, string ffmpeg, ToolRunner run)
    {
        var tempPng = Path.Combine(Path.GetTempPath(), "dc_cover_" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            // -an drops audio; -map 0:v -frames:v 1 takes the single attached
            // picture; -f image2 writes it as an image file.
            var result = run(
                ffmpeg,
                ["-hide_banner", "-loglevel", "error", "-y", "-i", audioFilePath,
                 "-an", "-map", "0:v", "-frames:v", "1", "-f", "image2", tempPng],
                CoverExtractTimeout, CancellationToken.None);

            if (!result.Succeeded || !File.Exists(tempPng)) return null;

            return ResizeToPng(tempPng);
        }
        catch
        {
            return null;
        }
        finally
        {
            BestEffortDelete.File(tempPng);
        }
    }

    /// <summary>Decodes the extracted image, resizes it to a square
    /// <see cref="CoverArtSize" /> px (ignoring source aspect ratio, mirroring
    /// the DDS art pipeline) and re-encodes it as PNG bytes.</summary>
    internal static byte[]? ResizeToPng(string imagePath)
    {
        // Embedded art is whatever the tagger put in the file: a header that
        // declares an absurd size is refused before the decoder allocates for it.
        if (!ImageHeaderProbe.IsDecodable(imagePath)) return null;

        using var source = SKBitmap.Decode(imagePath);
        if (source is null) return null;

        var info = new SKImageInfo(CoverArtSize, CoverArtSize, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var resized = source.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell));
        if (resized is null) return null;

        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }
}

/// <summary>
///     Audio metadata plus already-resized (512x512 PNG) cover art. Every string
///     field is empty and <see cref="CoverArtPng" /> is null when the source has
///     no such value.
/// </summary>
public sealed record AudioMetadataResult(
    string Artist,
    string Title,
    string Album,
    string AlbumYear,
    string ReleaseDate,
    string Genre,
    byte[]? CoverArtPng)
{
    public static AudioMetadataResult Empty { get; } = new("", "", "", "", "", "", null);
}

/// <summary>
///     The pure result of <see cref="AudioMetadataExtractor.ParseFfprobeJson" />:
///     the tag-derived fields plus whether the file carries an attached picture
///     (the flag that gates the ffmpeg cover-art extraction). No image bytes —
///     those require ffmpeg and live on <see cref="AudioMetadataResult" />.
/// </summary>
public readonly record struct FfprobeMetadata(
    string Artist,
    string Title,
    string Album,
    string AlbumYear,
    string ReleaseDate,
    string Genre,
    bool HasAttachedPicture)
{
    public static FfprobeMetadata Empty { get; } = new("", "", "", "", "", "", false);
}
