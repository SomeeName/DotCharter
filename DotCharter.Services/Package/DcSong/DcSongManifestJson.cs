using System.Globalization;
using System.Text.Json;
using DotCharter.Services.Update;

namespace DotCharter.Services.Package.DcSong;

/// <summary>
///     Writes and parses <c>manifest.json</c>. The parse is STRICT, because the
///     manifest arrives inside an untrusted file: every member name is held to
///     <see cref="ZipCentralDirectory.NormalizeEntryName" />, every role to its fixed
///     path, every checksum to 64 lower-case hex digits. The version is read FIRST,
///     so a newer major is refused as <see cref="DcSongReadFailure.NewerVersion" />
///     whatever the rest of its manifest looks like.
/// </summary>
public static class DcSongManifestJson
{
    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 16 };

    public static byte[] Write(DcSongManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("format", DcSongFormat.FormatId);
            json.WriteString("formatVersion",
                string.Create(CultureInfo.InvariantCulture, $"{manifest.FormatMajor}.{manifest.FormatMinor}"));
            json.WriteString("writer", manifest.Writer);

            var song = manifest.Song;
            json.WriteStartObject("song");
            json.WriteString("key", song.Key);
            json.WriteString("projectId", song.ProjectId);
            json.WriteString("title", song.Title);
            json.WriteString("artist", song.Artist);
            json.WriteString("album", song.Album);
            json.WriteString("year", song.Year);
            json.WriteString("charter", song.Charter);
            json.WriteNumber("lengthMs", Math.Round(song.LengthMs, 3));
            json.WriteBoolean("hasVocals", song.HasVocals);
            json.WriteStartArray("arrangements");
            foreach (var arrangement in song.Arrangements)
            {
                json.WriteStartObject();
                json.WriteString("name", arrangement.Name);
                json.WriteString("type", arrangement.Type);
                json.WriteString("tuning", arrangement.Tuning);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();

            var audio = manifest.Audio;
            json.WriteStartObject("audio");
            json.WriteString("codec", CodecName(audio.Codec));
            json.WriteBoolean("lossless", audio.Lossless);
            if (audio.BitrateKbps is { } kbps) json.WriteNumber("bitrateKbps", kbps);
            json.WriteNumber("previewStartMs", Math.Round(audio.PreviewStartMs, 3));
            json.WriteNumber("previewLengthMs", Math.Round(audio.PreviewLengthMs, 3));
            json.WriteEndObject();

            json.WriteStartArray("files");
            foreach (var file in manifest.Files)
            {
                json.WriteStartObject();
                json.WriteString("path", file.Path);
                json.WriteString("role", RoleName(file.Role));
                json.WriteNumber("size", file.Size);
                json.WriteString("sha256", file.Sha256);
                if (file.StemName != null) json.WriteString("stem", file.StemName);
                if (file.MediaType != null) json.WriteString("mediaType", file.MediaType);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>Parses and validates a manifest. Throws <see cref="DcSongReadException" />.</summary>
    public static DcSongManifest Parse(ReadOnlySpan<byte> utf8)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8.ToArray(), ParseOptions);
        }
        catch (JsonException ex)
        {
            throw Invalid($"manifest.json is not valid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid("manifest.json is not a JSON object.");

            if (String(root, "format") != DcSongFormat.FormatId)
                throw Invalid($"manifest.json does not name the '{DcSongFormat.FormatId}' format.");

            var versionText = String(root, "formatVersion");
            if (!DcSongFormat.TryParseVersion(versionText, out var major, out var minor))
                throw Invalid($"manifest.json has no readable formatVersion ('{versionText}').");
            if (major > DcSongFormat.CurrentMajor)
            {
                throw new DcSongReadException(DcSongReadFailure.NewerVersion,
                    $"The package is format {versionText}; this build reads {DcSongFormat.CurrentMajor}.x. " +
                    "It was written by a newer DotCharter.");
            }

            if (major < 1) throw Invalid($"formatVersion {versionText} does not exist.");

            var song = ParseSong(Object(root, "song"));
            var audio = ParseAudio(Object(root, "audio"));
            var files = ParseFiles(root, audio);
            return new DcSongManifest(major, minor, String(root, "writer"), song, audio, files);
        }
    }

    public static string RoleName(DcSongFileRole role) => role.ToString().ToLowerInvariant();

    public static string CodecName(DcSongAudioCodec codec) => codec.ToString().ToLowerInvariant();

    /// <summary>Where the song audio lives for <paramref name="codec" />.</summary>
    public static string AudioPath(DcSongAudioCodec codec) => $"{DcSongFormat.AudioStem}.{CodecName(codec)}";

    public static string StemPath(string name) => $"{DcSongFormat.StemFolder}{name}.opus";

    public static string ImagePath(DcSongFileRole role, string mediaType) =>
        (role == DcSongFileRole.Cover ? DcSongFormat.CoverStem : DcSongFormat.BannerStem)
        + (mediaType == DcSongImages.Png ? ".png" : ".jpg");

    private static DcSongInfo ParseSong(JsonElement song)
    {
        var arrangements = new List<DcSongArrangementInfo>();
        if (song.TryGetProperty("arrangements", out var list))
        {
            if (list.ValueKind != JsonValueKind.Array) throw Invalid("song.arrangements is not an array.");
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw Invalid("an arrangement is not an object.");
                arrangements.Add(new DcSongArrangementInfo(String(item, "name"), String(item, "type"), String(item, "tuning")));
            }
        }

        var lengthMs = Number(song, "lengthMs");
        if (lengthMs < 0) throw Invalid("song.lengthMs is negative.");

        return new DcSongInfo(
            String(song, "key"), String(song, "projectId"), String(song, "title"), String(song, "artist"),
            String(song, "album"), String(song, "year"), String(song, "charter"), lengthMs,
            Bool(song, "hasVocals"), arrangements);
    }

    private static DcSongAudioInfo ParseAudio(JsonElement audio)
    {
        var codec = String(audio, "codec") switch
        {
            "opus" => DcSongAudioCodec.Opus,
            "flac" => DcSongAudioCodec.Flac,
            var other => throw Invalid($"audio.codec '{other}' is not one this build plays.")
        };

        int? kbps = null;
        if (audio.TryGetProperty("bitrateKbps", out var rate))
        {
            if (rate.ValueKind != JsonValueKind.Number || !rate.TryGetInt32(out var value) || value <= 0)
                throw Invalid("audio.bitrateKbps is not a positive integer.");
            kbps = value;
        }

        var lossless = Bool(audio, "lossless");
        if (lossless != (codec == DcSongAudioCodec.Flac))
            throw Invalid("audio.lossless does not match audio.codec.");

        var start = Number(audio, "previewStartMs");
        var length = Number(audio, "previewLengthMs");
        if (start < 0 || length < 0) throw Invalid("the preview window is negative.");

        return new DcSongAudioInfo(codec, lossless, kbps, start, length);
    }

    private static List<DcSongFileEntry> ParseFiles(JsonElement root, DcSongAudioInfo audio)
    {
        if (!root.TryGetProperty("files", out var list) || list.ValueKind != JsonValueKind.Array)
            throw Invalid("manifest.json has no files array.");
        if (list.GetArrayLength() > DcSongFormat.MaxEntries - 1)
            throw new DcSongReadException(DcSongReadFailure.TooManyEntries, "the manifest lists too many files.");

        var files = new List<DcSongFileEntry>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in list.EnumerateArray())
            files.Add(ParseFileEntry(item, audio, paths, stems));

        CheckSingleRoles(files);

        if (stems.Count > DcSongFormat.MaxStems)
            throw new DcSongReadException(DcSongReadFailure.TooManyEntries, "the manifest lists too many stems.");
        return files;
    }

    private static DcSongFileEntry ParseFileEntry(JsonElement item, DcSongAudioInfo audio, HashSet<string> paths,
        HashSet<string> stems)
    {
        if (item.ValueKind != JsonValueKind.Object) throw Invalid("a files entry is not an object.");

        var path = String(item, "path");
        var normalized = ZipCentralDirectory.NormalizeEntryName(path);
        if (normalized == null || normalized != path || path.EndsWith('/'))
            throw new DcSongReadException(DcSongReadFailure.UnsafePath, $"the manifest names an unsafe path '{path}'.");
        if (string.Equals(path, DcSongFormat.ManifestPath, StringComparison.OrdinalIgnoreCase))
            throw Invalid("the manifest lists itself.");
        if (!paths.Add(path)) throw Invalid($"the manifest lists '{path}' twice.");

        var role = ParseRole(String(item, "role"));
        if (!item.TryGetProperty("size", out var sizeElement) || !sizeElement.TryGetInt64(out var size) || size < 0)
            throw Invalid($"'{path}' has no valid size.");

        var sha = String(item, "sha256");
        if (sha.Length != 64 || !sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw Invalid($"'{path}' has no valid sha256.");

        var (stemName, mediaType, expectedPath) = RoleDetails(item, role, path, audio, stems);
        if (expectedPath != null && path != expectedPath)
            throw Invalid($"a {RoleName(role)} must be stored as '{expectedPath}', not '{path}'.");

        return new DcSongFileEntry(path, role, size, sha, stemName, mediaType);
    }

    /// <summary>The stem name or image type a role carries, and the one path that role may be stored at.</summary>
    private static (string? StemName, string? MediaType, string? ExpectedPath) RoleDetails(JsonElement item, DcSongFileRole role,
        string path, DcSongAudioInfo audio, HashSet<string> stems)
    {
        switch (role)
        {
            case DcSongFileRole.Stem:
            {
                var stemName = String(item, "stem");
                if (!DcSongFormat.IsValidStemName(stemName) || !stems.Add(stemName))
                    throw Invalid($"'{path}' has a bad or duplicate stem name.");
                return (stemName, null, StemPath(stemName));
            }
            case DcSongFileRole.Cover or DcSongFileRole.Banner:
            {
                var mediaType = String(item, "mediaType");
                if (mediaType is not (DcSongImages.Png or DcSongImages.Jpeg))
                    throw Invalid($"'{path}' is not a PNG or JPEG.");
                return (null, mediaType, ImagePath(role, mediaType));
            }
            default:
                var expectedPath = role switch
                {
                    DcSongFileRole.Chart => DcSongFormat.ChartPath,
                    DcSongFileRole.Audio => AudioPath(audio.Codec),
                    DcSongFileRole.Preview => DcSongFormat.PreviewPath,
                    _ => null
                };
                return (null, null, expectedPath);
        }
    }

    private static void CheckSingleRoles(List<DcSongFileEntry> files)
    {
        foreach (var single in new[] { DcSongFileRole.Chart, DcSongFileRole.Audio, DcSongFileRole.Preview, DcSongFileRole.Cover, DcSongFileRole.Banner })
        {
            var count = files.Count(f => f.Role == single);
            if (count > 1) throw Invalid($"the manifest lists more than one {RoleName(single)}.");
            if (count == 0 && single is DcSongFileRole.Chart or DcSongFileRole.Audio)
                throw Invalid($"the manifest lists no {RoleName(single)}.");
        }
    }

    private static DcSongFileRole ParseRole(string role) => role switch
    {
        "chart" => DcSongFileRole.Chart,
        "audio" => DcSongFileRole.Audio,
        "preview" => DcSongFileRole.Preview,
        "cover" => DcSongFileRole.Cover,
        "banner" => DcSongFileRole.Banner,
        "stem" => DcSongFileRole.Stem,
        "" => throw Invalid("a files entry has no role."),
        _ => DcSongFileRole.Other
    };

    private static JsonElement Object(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            throw Invalid($"manifest.json has no '{name}' object.");
        return value;
    }

    /// <summary>A string field, "" when absent. A field of another kind is an error.</summary>
    private static string String(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)) return "";
        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : throw Invalid($"'{name}' is not a string.");
    }

    private static double Number(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)) return 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number
            : throw Invalid($"'{name}' is not a number.");
    }

    private static bool Bool(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid($"'{name}' is not true or false.")
        };
    }

    private static DcSongReadException Invalid(string detail, Exception? inner = null) =>
        new(DcSongReadFailure.ManifestInvalid, detail, inner);
}
