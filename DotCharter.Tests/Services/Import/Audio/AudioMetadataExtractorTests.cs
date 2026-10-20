using System.Diagnostics;
using DotCharter.Services.Export.Audio;
using DotCharter.Services.Import.Audio;
using DotCharter.Tone.Infrastructure.Storage;
using SkiaSharp;

namespace DotCharter.Tests.Services.Import.Audio;

/// <summary>
///     Covers the pure ffprobe-JSON → metadata mapping (no tools required) plus a
///     tools-gated integration pass that encodes a tagged file with the repo's
///     cached ffmpeg/ffprobe (tools/redist/win-x64) and reads it back. The
///     integration tests skip cleanly when the tools are not cached.
/// </summary>
public sealed class AudioMetadataExtractorTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    // ── Pure ParseFfprobeJson cases (no ffmpeg/ffprobe) ────────────────────────

    [Fact]
    public void ParseFfprobeJson_FullTags_MapsEveryFieldAndDetectsCover()
    {
        const string json = """
        {
          "streams": [
            { "codec_type": "audio", "codec_name": "mp3" },
            { "codec_type": "video", "codec_name": "mjpeg", "disposition": { "attached_pic": 1 } }
          ],
          "format": {
            "tags": {
              "title": "My Song",
              "artist": "The Band",
              "album": "The Album",
              "date": "2007-08-15",
              "genre": "Progressive Rock"
            }
          }
        }
        """;

        var meta = AudioMetadataExtractor.ParseFfprobeJson(json);

        Assert.Equal("My Song", meta.Title);
        Assert.Equal("The Band", meta.Artist);
        Assert.Equal("The Album", meta.Album);
        Assert.Equal("Progressive Rock", meta.Genre);
        Assert.Equal("2007-08-15", meta.ReleaseDate);
        Assert.Equal("2007", meta.AlbumYear);
        Assert.True(meta.HasAttachedPicture);
    }

    [Fact]
    public void ParseFfprobeJson_MissingTags_YieldsEmpty()
    {
        const string json = """
        { "streams": [ { "codec_type": "audio" } ], "format": { "tags": {} } }
        """;

        var meta = AudioMetadataExtractor.ParseFfprobeJson(json);

        Assert.Equal("", meta.Artist);
        Assert.Equal("", meta.Title);
        Assert.Equal("", meta.Album);
        Assert.Equal("", meta.Genre);
        Assert.Equal("", meta.ReleaseDate);
        Assert.Equal("", meta.AlbumYear);
        Assert.False(meta.HasAttachedPicture);
    }

    [Fact]
    public void ParseFfprobeJson_YearOnlyDate_ExtractsYear()
    {
        const string json = """{ "format": { "tags": { "date": "1994" } } }""";

        var meta = AudioMetadataExtractor.ParseFfprobeJson(json);

        Assert.Equal("1994", meta.AlbumYear);
        Assert.Equal("1994", meta.ReleaseDate);
    }

    [Fact]
    public void ParseFfprobeJson_UppercaseKeysAndTyer_AreCaseInsensitive()
    {
        // ID3v2.3 exposes the year as TYER; Vorbis comments are often uppercase.
        const string json = """
        { "format": { "tags": { "TITLE": "T", "ARTIST": "A", "GENRE": "Metal", "TYER": "1986" } } }
        """;

        var meta = AudioMetadataExtractor.ParseFfprobeJson(json);

        Assert.Equal("T", meta.Title);
        Assert.Equal("A", meta.Artist);
        Assert.Equal("Metal", meta.Genre);
        Assert.Equal("1986", meta.AlbumYear);
    }

    [Fact]
    public void ParseFfprobeJson_AttachedPicZero_IsNotACover()
    {
        const string json = """
        {
          "streams": [ { "codec_type": "video", "disposition": { "attached_pic": 0 } } ],
          "format": {}
        }
        """;

        Assert.False(AudioMetadataExtractor.ParseFfprobeJson(json).HasAttachedPicture);
    }

    [Fact]
    public void ParseFfprobeJson_StreamTags_AreAFallbackWhenFormatHasNone()
    {
        const string json = """
        {
          "streams": [ { "codec_type": "audio", "tags": { "title": "StreamTitle", "artist": "StreamArtist" } } ],
          "format": { "tags": {} }
        }
        """;

        var meta = AudioMetadataExtractor.ParseFfprobeJson(json);

        Assert.Equal("StreamTitle", meta.Title);
        Assert.Equal("StreamArtist", meta.Artist);
    }

    [Fact]
    public void ParseFfprobeJson_FormatTags_WinOverStreamTags()
    {
        const string json = """
        {
          "streams": [ { "tags": { "title": "StreamTitle" } } ],
          "format": { "tags": { "title": "FormatTitle" } }
        }
        """;

        Assert.Equal("FormatTitle", AudioMetadataExtractor.ParseFfprobeJson(json).Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json {{{")]
    [InlineData("{ \"format\": ")]
    public void ParseFfprobeJson_MalformedOrEmpty_ReturnsEmptyNeverThrows(string json) => Assert.Equal(FfprobeMetadata.Empty, AudioMetadataExtractor.ParseFfprobeJson(json));

    [Fact]
    public void Extract_MissingFile_ReturnsEmpty()
    {
        Assert.Equal(AudioMetadataResult.Empty,
            AudioMetadataExtractor.Extract(Path.Combine(_dir, "does-not-exist.mp3")));
    }

    // ── Integration: real ffprobe/ffmpeg over an encoded file ──────────────────

    [Fact]
    public void Extract_TaggedFile_ReadsTagsBack()
    {
        var (ffmpeg, ffprobe) = RedistTools();
        Assert.SkipWhen(ffmpeg is null, "repo redist ffmpeg not cached on this machine.");

        var mp3 = EncodeTaggedMp3(ffmpeg!, coverPng: null);
        Assert.SkipWhen(mp3 is null, "tagged mp3 encode failed on this machine.");

        var meta = ExtractWith(mp3!, ffmpeg!, ffprobe);

        Assert.Equal("Integration Title", meta.Title);
        Assert.Equal("Integration Artist", meta.Artist);
        Assert.Equal("Integration Album", meta.Album);
        Assert.Equal("Rock", meta.Genre);
        Assert.Equal("2007", meta.AlbumYear);
    }

    [Fact]
    public void Extract_FileWithEmbeddedCover_Returns512PngBytes()
    {
        var (ffmpeg, ffprobe) = RedistTools();
        Assert.SkipWhen(ffmpeg is null, "repo redist ffmpeg not cached on this machine.");

        var cover = WriteCoverPng(300);
        var mp3 = EncodeTaggedMp3(ffmpeg!, coverPng: cover);
        Assert.SkipWhen(mp3 is null, "cover mp3 encode failed on this machine.");

        var meta = ExtractWith(mp3!, ffmpeg!, ffprobe);
        Assert.SkipWhen(meta.CoverArtPng is null, "this ffmpeg build did not embed/expose the attached picture.");

        using var bmp = SKBitmap.Decode(meta.CoverArtPng);
        Assert.NotNull(bmp);
        Assert.Equal(AudioMetadataExtractor.CoverArtSize, bmp!.Width);
        Assert.Equal(AudioMetadataExtractor.CoverArtSize, bmp.Height);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The repo-root cache the release packer fills (not on PATH by design): its
    ///     ffmpeg, and the ffprobe beside it when there is one. The release's minimal
    ///     ffmpeg (#409) comes without ffprobe, and a release install has none, so
    ///     the tests run the ffmpeg-only path then instead of skipping.
    /// </summary>
    private static (string? Ffmpeg, string? Ffprobe) RedistTools()
    {
        var repo = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && repo != null; i++, repo = Path.GetDirectoryName(repo))
        {
            var baseDir = Path.Combine(repo, "tools", "redist", "win-x64");
            var ffmpeg = Path.Combine(baseDir, "ffmpeg.exe");
            var ffprobe = Path.Combine(baseDir, "ffprobe.exe");
            if (File.Exists(ffmpeg)) return (ffmpeg, File.Exists(ffprobe) ? ffprobe : null);
        }

        return (null, null);
    }

    /// <summary>Extracts with exactly these tools: no PATH lookup, so a missing ffprobe stays missing.</summary>
    private static AudioMetadataResult ExtractWith(string mp3, string ffmpeg, string? ffprobe) =>
        AudioMetadataExtractor.Extract(
            mp3,
            (name, _) => name switch { "ffmpeg" => ffmpeg, "ffprobe" => ffprobe, _ => null },
            MediaProcess.Run);

    private string WriteToneWav()
    {
        Directory.CreateDirectory(_dir);
        const int sr = 44100;
        var path = Path.Combine(_dir, "tone.wav");
        using var writer = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write));
        var n = sr / 2; // half a second, mono
        var dataBytes = n * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sr);
        writer.Write(sr * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        for (var i = 0; i < n; i++)
            writer.Write((short)(Math.Sin(2 * Math.PI * 220 * i / sr) * 12000));
        return path;
    }

    private string WriteCoverPng(int edge)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "cover.png");
        using var bmp = new SKBitmap(edge, edge);
        using (var canvas = new SKCanvas(bmp))
            canvas.Clear(new SKColor(200, 40, 40));
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(path);
        data.SaveTo(fs);
        return path;
    }

    /// <summary>Encodes the tone to a tagged mp3, optionally embedding a cover
    /// image as an attached picture. Returns null if the encode produced nothing.</summary>
    private string? EncodeTaggedMp3(string ffmpeg, string? coverPng)
    {
        var wav = WriteToneWav();
        var mp3 = Path.Combine(_dir, coverPng is null ? "tagged.mp3" : "tagged_cover.mp3");

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-i", wav };
        if (coverPng is not null) args.AddRange(["-i", coverPng]);

        args.AddRange(["-map", "0:a", "-c:a", "libmp3lame", "-b:a", "128k", "-id3v2_version", "3"]);
        if (coverPng is not null)
        {
            args.AddRange(["-map", "1:v", "-c:v", "png", "-disposition:v:0", "attached_pic",
                "-metadata:s:v", "title=Album cover", "-metadata:s:v", "comment=Cover (front)"]);
        }

        args.AddRange([
            "-metadata", "title=Integration Title",
            "-metadata", "artist=Integration Artist",
            "-metadata", "album=Integration Album",
            "-metadata", "genre=Rock",
            "-metadata", "date=2007",
            mp3
        ]);

        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var proc = Process.Start(psi)!;
        proc.StandardError.ReadToEnd();
        proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(60_000);

        return File.Exists(mp3) && new FileInfo(mp3).Length > 0 ? mp3 : null;
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
