using DotCharter.Services.Export.Audio;
using DotCharter.Services.Import.Audio;
using DotCharter.Tone.Infrastructure.Storage;
using SkiaSharp;

namespace DotCharter.Tests.Services.Import.Audio;

/// <summary>
///     Release installs bundle ffmpeg but NOT ffprobe (issue #152). The tag read
///     and the cover extract must work from ffmpeg alone: the pure
///     <c>-f ffmetadata</c> parser, and the extractor driven through a fake
///     resolver/runner where only ffmpeg resolves.
/// </summary>
public sealed class AudioMetadataFfmpegOnlyTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ParseFfmetadata_Id3Style_MapsTags()
    {
        const string text = ";FFMETADATA1\ntitle=My Song\nartist=The Band\nalbum=The Album\ngenre=Rock\ndate=2007\nencoder=Lavf61.7.100\n";

        var meta = AudioMetadataExtractor.ParseFfmetadata(text);

        Assert.Equal("My Song", meta.Title);
        Assert.Equal("The Band", meta.Artist);
        Assert.Equal("The Album", meta.Album);
        Assert.Equal("Rock", meta.Genre);
        Assert.Equal("2007", meta.AlbumYear);
        Assert.Equal("2007", meta.ReleaseDate);
    }

    [Fact]
    public void ParseFfmetadata_VorbisUpperCaseKeysAndCrLf_MapsTags()
    {
        const string text = ";FFMETADATA1\r\nTITLE=Vorbis Song\r\nARTIST=Vorbis Artist\r\nALBUM=Vorbis Album\r\nGENRE=Metal\r\nDATE=2011-03-04\r\n";

        var meta = AudioMetadataExtractor.ParseFfmetadata(text);

        Assert.Equal("Vorbis Song", meta.Title);
        Assert.Equal("Vorbis Artist", meta.Artist);
        Assert.Equal("Vorbis Album", meta.Album);
        Assert.Equal("Metal", meta.Genre);
        Assert.Equal("2011-03-04", meta.ReleaseDate);
        Assert.Equal("2011", meta.AlbumYear);
    }

    [Fact]
    public void ParseFfmetadata_Mp4Style_AlbumArtistFallsBackForArtist()
    {
        const string text = ";FFMETADATA1\nmajor_brand=M4A \nalbum_artist=Various\ntitle=Track\nalbum=Comp\ndate=1999\n";

        var meta = AudioMetadataExtractor.ParseFfmetadata(text);

        Assert.Equal("Various", meta.Artist);
        Assert.Equal("Track", meta.Title);
        Assert.Equal("1999", meta.AlbumYear);
    }

    [Fact]
    public void ParseFfmetadata_EscapedCharacters_AreUnescaped()
    {
        const string text = ";FFMETADATA1\ntitle=a\\=b\\;c\\#d\\\\e\nartist=AC\\/DC\n";

        var meta = AudioMetadataExtractor.ParseFfmetadata(text);

        Assert.Equal("a=b;c#d\\e", meta.Title);
        Assert.Equal("AC/DC", meta.Artist);
    }

    [Fact]
    public void ParseFfmetadata_EscapedEqualsInKey_DoesNotSplitThere()
    {
        const string text = ";FFMETADATA1\ntitle=x=y\n";

        Assert.Equal("x=y", AudioMetadataExtractor.ParseFfmetadata(text).Title);
    }

    [Fact]
    public void ParseFfmetadata_MultiLineValue_JoinsContinuationLines()
    {
        // ffmpeg writes a newline inside a value as backslash + real newline.
        const string text = ";FFMETADATA1\ntitle=First\\\nSecond\nartist=Next\n";

        var meta = AudioMetadataExtractor.ParseFfmetadata(text);

        Assert.Equal("First\nSecond", meta.Title);
        Assert.Equal("Next", meta.Artist);
    }

    [Fact]
    public void ParseFfmetadata_BackslashAtLineEnd_EscapedBackslashIsNotContinuation()
    {
        const string text = ";FFMETADATA1\ntitle=ends with slash\\\\\nartist=Next\n";

        var meta = AudioMetadataExtractor.ParseFfmetadata(text);

        Assert.Equal("ends with slash\\", meta.Title);
        Assert.Equal("Next", meta.Artist);
    }

    [Fact]
    public void ParseFfmetadata_Unicode_IsPreserved()
    {
        const string text = ";FFMETADATA1\ntitle=宇宙船\nartist=Ångström ❤\nalbum=アルバム\n";

        var meta = AudioMetadataExtractor.ParseFfmetadata(text);

        Assert.Equal("宇宙船", meta.Title);
        Assert.Equal("Ångström ❤", meta.Artist);
        Assert.Equal("アルバム", meta.Album);
    }

    [Fact]
    public void ParseFfmetadata_StreamSections_AreIgnored()
    {
        // A cover stream carries its own title/comment; it must not become the song title.
        const string text = ";FFMETADATA1\nartist=Band\n[STREAM]\ntitle=Album cover\ncomment=Cover (front)\n[CHAPTER]\nTIMEBASE=1/1000\ntitle=Intro\n";

        var meta = AudioMetadataExtractor.ParseFfmetadata(text);

        Assert.Equal("Band", meta.Artist);
        Assert.Equal("", meta.Title);
    }

    [Fact]
    public void ParseFfmetadata_NulInValue_IsCleaned()
    {
        var meta = AudioMetadataExtractor.ParseFfmetadata(";FFMETADATA1\ntitle=Song\0\n");

        Assert.Equal("Song", meta.Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n")]
    [InlineData(";FFMETADATA1\n")]
    [InlineData("garbage without equals")]
    public void ParseFfmetadata_EmptyOrNoTags_ReturnsEmptyNeverThrows(string text) => Assert.Equal(FfprobeMetadata.Empty, AudioMetadataExtractor.ParseFfmetadata(text));

    [Fact]
    public void Extract_OnlyFfmpegResolvable_ReadsTagsAndCover()
    {
        var audio = TouchAudio();
        var calls = new List<(string Exe, string[] Args)>();

        var result = AudioMetadataExtractor.Extract(
            audio,
            (name, _) => name == "ffmpeg" ? "fake-ffmpeg" : null,
            (exe, args, _, _) =>
            {
                calls.Add((exe, [.. args]));
                if (args.Contains("ffmetadata"))
                    return new MediaProcess.MediaProcessResult(0, "", ";FFMETADATA1\ntitle=T\nartist=A\nalbum=Al\ngenre=G\ndate=2020\n");
                WritePng(args[^1]);
                return new MediaProcess.MediaProcessResult(0, "");
            });

        Assert.Equal("T", result.Title);
        Assert.Equal("A", result.Artist);
        Assert.Equal("Al", result.Album);
        Assert.Equal("G", result.Genre);
        Assert.Equal("2020", result.AlbumYear);
        Assert.NotNull(result.CoverArtPng);
        Assert.All(calls, c => Assert.Equal("fake-ffmpeg", c.Exe));
    }

    [Fact]
    public void Extract_FfmpegOnly_FileWithoutPicture_StillReturnsTags()
    {
        var audio = TouchAudio();

        var result = AudioMetadataExtractor.Extract(
            audio,
            (name, _) => name == "ffmpeg" ? "fake-ffmpeg" : null,
            (_, args, _, _) => args.Contains("ffmetadata")
                ? new MediaProcess.MediaProcessResult(0, "", ";FFMETADATA1\ntitle=T\n")
                : new MediaProcess.MediaProcessResult(1, "no video stream"));

        Assert.Equal("T", result.Title);
        Assert.Null(result.CoverArtPng);
    }

    [Fact]
    public void Extract_FfprobeFailsToStart_FallsBackToFfmpeg()
    {
        var audio = TouchAudio();

        var result = AudioMetadataExtractor.Extract(
            audio,
            (name, _) => name,
            (exe, args, _, _) => exe == "ffprobe"
                ? throw new InvalidOperationException("broken exe")
                : args.Contains("ffmetadata")
                    ? new MediaProcess.MediaProcessResult(0, "", ";FFMETADATA1\ntitle=Fallback\n")
                    : new MediaProcess.MediaProcessResult(1, ""));

        Assert.Equal("Fallback", result.Title);
    }

    [Fact]
    public void Extract_NeitherToolResolvable_ReturnsEmpty()
    {
        var audio = TouchAudio();

        var result = AudioMetadataExtractor.Extract(
            audio, (_, _) => null, (_, _, _, _) => throw new InvalidOperationException("must not run"));

        Assert.Equal(AudioMetadataResult.Empty, result);
    }

    [Fact]
    public void Extract_FfmpegFails_ReturnsEmptyNeverThrows()
    {
        var audio = TouchAudio();

        var result = AudioMetadataExtractor.Extract(
            audio,
            (name, _) => name == "ffmpeg" ? "fake-ffmpeg" : null,
            (_, _, _, _) => throw new InvalidOperationException("boom"));

        Assert.Equal(AudioMetadataResult.Empty, result);
    }

    [Fact]
    public void Extract_RealFfmpegOnly_ReadsUnicodeTagsAndCover()
    {
        var ffmpeg = RedistFfmpeg();
        Assert.SkipWhen(ffmpeg is null, "repo redist ffmpeg not cached on this machine.");

        Directory.CreateDirectory(_dir);
        var cover = Path.Combine(_dir, "cover.png");
        WritePng(cover);
        var mp3 = Path.Combine(_dir, "tagged.mp3");
        // A WAV written here, not `-f lavfi -i sine`: the release's minimal ffmpeg
        // (#409) has no lavfi, and this test must run against it.
        var wav = Path.Combine(_dir, "tone.wav");
        WriteToneWav(wav);
        var run = MediaProcess.Run(
            ffmpeg!,
            ["-hide_banner", "-loglevel", "error", "-y", "-i", wav, "-i", cover,
             "-map", "0:a", "-map", "1:v", "-c:a", "libmp3lame", "-c:v", "png", "-id3v2_version", "3",
             "-disposition:v:0", "attached_pic",
             "-metadata", "title=Café = 宇宙; #1", "-metadata", "artist=バンド",
             "-metadata", "album=Alb", "-metadata", "genre=Rock", "-metadata", "date=2007", mp3],
            TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.SkipWhen(!run.Succeeded || !File.Exists(mp3), "tagged mp3 encode failed on this machine.");

        var result = AudioMetadataExtractor.Extract(
            mp3,
            (name, configured) => name == "ffmpeg" ? ffmpeg : null,
            MediaProcess.Run);

        Assert.Equal("Café = 宇宙; #1", result.Title);
        Assert.Equal("バンド", result.Artist);
        Assert.Equal("Rock", result.Genre);
        Assert.Equal("2007", result.AlbumYear);
        Assert.NotNull(result.CoverArtPng);
    }

    private string TouchAudio()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "song.mp3");
        File.WriteAllBytes(path, [0]);
        return path;
    }

    private static void WriteToneWav(string path)
    {
        const int sr = 44100;
        const int frames = sr * 3 / 10;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + frames * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sr);
        writer.Write(sr * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(frames * 2);
        for (var i = 0; i < frames; i++)
            writer.Write((short)(Math.Sin(2 * Math.PI * 440 * i / sr) * 12000));
    }

    private static void WritePng(string path)
    {
        using var bmp = new SKBitmap(64, 64);
        using (var canvas = new SKCanvas(bmp))
            canvas.Clear(new SKColor(200, 40, 40));
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    private static string? RedistFfmpeg()
    {
        var repo = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && repo != null; i++, repo = Path.GetDirectoryName(repo))
        {
            var ffmpeg = Path.Combine(repo, "tools", "redist", "win-x64", "ffmpeg.exe");
            if (File.Exists(ffmpeg)) return ffmpeg;
        }

        return null;
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
