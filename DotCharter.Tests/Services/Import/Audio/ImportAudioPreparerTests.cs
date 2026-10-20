using DotCharter.Services.Import.Audio;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Import.Audio;

/// <summary>
///     Decision + fallback logic for <see cref="ImportAudioPreparer" />. The
///     ffmpeg resolution and encode process are injected, so these run without the
///     CLI. A separate integration test (<see cref="ImportAudioPreparerFfmpegTests" />)
///     exercises the real ffmpeg when the repo redist copy is present.
/// </summary>
public sealed class ImportAudioPreparerTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("dotcharter-importprep-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private string WriteSource(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8]);
        return path;
    }

    // ---- pure filter/args builders --------------------------------------------

    [Fact]
    public void BuildSilenceFilter_LeadAndTrail_UsesAllChannelAdelayAndApad()
        => Assert.Equal("adelay=10000:all=1,apad=pad_dur=2",
            ImportAudioPreparer.BuildSilenceFilter(10.0, 2.0));

    [Fact]
    public void BuildSilenceFilter_LeadOnly_OmitsApad()
        => Assert.Equal("adelay=10000:all=1", ImportAudioPreparer.BuildSilenceFilter(10.0, 0.0));

    [Fact]
    public void BuildSilenceFilter_TrailOnly_OmitsAdelay()
        => Assert.Equal("apad=pad_dur=2", ImportAudioPreparer.BuildSilenceFilter(0.0, 2.0));

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(-5.0, -1.0)]
    public void BuildSilenceFilter_NoSilence_ReturnsNull(double lead, double trail)
        => Assert.Null(ImportAudioPreparer.BuildSilenceFilter(lead, trail));

    [Fact]
    public void BuildEncodeArgs_EncodesVorbisWithFilterToTarget()
    {
        var args = ImportAudioPreparer.BuildEncodeArgs("in.mp3", "out.ogg", "adelay=10000:all=1");

        Assert.Contains("libvorbis", args);
        Assert.Contains("adelay=10000:all=1", args);
        Assert.Equal("in.mp3", args[args.ToList().IndexOf("-i") + 1]);
        Assert.Equal("out.ogg", args[^1]); // output is the last positional arg
    }

    /// <summary>#461: cover art is a video stream; mapped into the ogg it needs Theora, which the shipped
    ///     minimal ffmpeg lacks, so the bake fails. The encode must take the audio and nothing else.</summary>
    [Fact]
    public void BuildEncodeArgs_MapsTheAudioStreamOnly()
        => AssertAudioOnly(ImportAudioPreparer.BuildEncodeArgs("in.flac", "out.ogg", "adelay=10000:all=1"), "out.ogg");

    /// <summary>The audio-only options sit between the input and the output path.</summary>
    internal static void AssertAudioOnly(IReadOnlyList<string> args, string output)
    {
        var list = args.ToList();
        var input = list.IndexOf("-i");
        var map = list.IndexOf("-map");
        Assert.True(input >= 0 && map > input + 1, $"no -map after the input: {string.Join(' ', args)}");
        Assert.Equal(["-map", "0:a:0", "-vn", "-sn", "-dn"], list.Skip(map).Take(5));
        Assert.Equal(output, list[^1]);
    }

    // ---- Prepare: decision + fallback -----------------------------------------

    [Fact]
    public void Prepare_NoSilenceRequested_CopiesVerbatim_NeverInvokesFfmpeg()
    {
        var src = WriteSource("Band - Song.mp3");
        var ran = false;

        var result = ImportAudioPreparer.Prepare(
            src, _dir, leadingSeconds: 0, trailingSeconds: 0,
            resolveFfmpeg: () => "ffmpeg",
            runEncode: (_, _) => { ran = true; return true; });

        Assert.False(ran); // no silence ⇒ no encode
        Assert.Equal("song.mp3", result.WorkingAudioFileName);
        Assert.Null(result.OriginalFileName);
        Assert.False(result.SilenceBaked);
        Assert.True(File.Exists(Path.Combine(_dir, "song.mp3")));
        Assert.False(File.Exists(Path.Combine(_dir, "original.mp3")));
    }

    [Fact]
    public void Prepare_NoFfmpeg_FallsBackToVerbatimAndWarns()
    {
        var src = WriteSource("song source.flac");
        var warnings = new List<string>();

        var result = ImportAudioPreparer.Prepare(
            src, _dir, leadingSeconds: 10, trailingSeconds: 2,
            resolveFfmpeg: () => null,
            runEncode: (_, _) => throw new Exception("must not run"),
            logWarning: warnings.Add);

        Assert.Equal("song.flac", result.WorkingAudioFileName);
        Assert.Null(result.OriginalFileName);
        Assert.False(result.SilenceBaked);
        Assert.True(File.Exists(Path.Combine(_dir, "song.flac")));
        Assert.Contains(warnings, w => w.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Prepare_EncodeFails_FallsBackToVerbatim_DeletesPartialOgg()
    {
        var src = WriteSource("in.wav");
        var warnings = new List<string>();

        var result = ImportAudioPreparer.Prepare(
            src, _dir, leadingSeconds: 10, trailingSeconds: 2,
            resolveFfmpeg: () => "ffmpeg",
            // Simulate ffmpeg writing a partial file then failing.
            runEncode: (_, args) => { File.WriteAllBytes(args[^1], [9]); return false; },
            logWarning: warnings.Add);

        Assert.Equal("song.wav", result.WorkingAudioFileName);
        Assert.False(result.SilenceBaked);
        Assert.Null(result.OriginalFileName);
        Assert.True(File.Exists(Path.Combine(_dir, "song.wav")));
        Assert.False(File.Exists(Path.Combine(_dir, "song.ogg"))); // partial cleaned up
        Assert.Contains(warnings, w => w.Contains("failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Prepare_EncodeExitsZeroButProducesNothing_FallsBack()
    {
        var src = WriteSource("in.mp3");

        var result = ImportAudioPreparer.Prepare(
            src, _dir, leadingSeconds: 10, trailingSeconds: 2,
            resolveFfmpeg: () => "ffmpeg",
            runEncode: (_, _) => true); // claims success but writes no file

        Assert.Equal("song.mp3", result.WorkingAudioFileName);
        Assert.False(result.SilenceBaked);
    }

    [Fact]
    public void Prepare_EncodeSucceeds_BakesOgg_AndPreservesOriginal()
    {
        var src = WriteSource("Band - Song.mp3");
        IReadOnlyList<string>? seenArgs = null;

        var result = ImportAudioPreparer.Prepare(
            src, _dir, leadingSeconds: 10, trailingSeconds: 2,
            resolveFfmpeg: () => "ffmpeg",
            runEncode: (ffmpeg, args) =>
            {
                seenArgs = args;
                File.WriteAllBytes(args[^1], [42, 42, 42]); // a "real" ogg
                return true;
            });

        Assert.True(result.SilenceBaked);
        Assert.Equal("song.ogg", result.WorkingAudioFileName);
        Assert.Equal("original.mp3", result.OriginalFileName);

        // The working ogg and the byte-exact original both landed in the folder.
        Assert.True(File.Exists(Path.Combine(_dir, "song.ogg")));
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(Path.Combine(_dir, "original.mp3")));

        // The encode carried the composed silence filter.
        Assert.NotNull(seenArgs);
        Assert.Contains("adelay=10000:all=1,apad=pad_dur=2", seenArgs!);
    }
}
