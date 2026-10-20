using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Services.Import.Youtube;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
/// S4. The stem encoder used to hand-roll a FOURTH spelling of the media-tool
/// resolution order. These pin it to the single documented one
/// (<see cref="MediaToolResolver"/>: configured → exe dir → exe dir/<c>tools</c>
/// → PATH → dev-tree redist), because the encoder writes the NEVER-REGENERATED
/// stem cache: whichever ffmpeg it picks decides that archive's codec for good.
/// </summary>
public sealed class StemFfmpegResolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "dc-ffmpegres-" + Guid.NewGuid().ToString("N"));

    public StemFfmpegResolutionTests() => Directory.CreateDirectory(_root);

    public void Dispose() => BestEffortDelete.Directory(_root);

    private string Touch(params string[] parts)
    {
        var p = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "x");
        return p;
    }

    /// <summary>
    /// The configured path (<c>AppConfig.FfmpegPath</c>) wins outright. The old
    /// hand-rolled resolver ignored it entirely, so a user who pointed Settings
    /// at a specific ffmpeg got their stems from a different one — or none.
    /// </summary>
    [Fact]
    public void TheConfiguredFfmpegPathWinsOverEveryOtherLocation()
    {
        var configured = Touch("custom", "my-ffmpeg.exe");
        var exeDir = Path.Combine(_root, "app");
        Touch("app", "ffmpeg.exe");
        var pathDir = Path.Combine(_root, "onpath");
        Touch("onpath", "ffmpeg.exe");

        var resolved = MediaToolResolver.Resolve(
            "ffmpeg", exeDir, configured, [pathDir], File.Exists, isWindows: true);

        Assert.Equal(configured, resolved);
    }

    /// <summary>
    /// A bundled copy in the app's <c>tools/</c> folder beats an ambient PATH one.
    /// The hand-rolled loop nested NAMES outer / directories inner, so the whole
    /// of PATH was searched for <c>ffmpeg.exe</c> before <c>tools/ffmpeg</c> was
    /// tried — inverting exactly this rule.
    /// </summary>
    [Fact]
    public void TheBundledToolsCopyBeatsAnAmbientPathCopy()
    {
        var exeDir = Path.Combine(_root, "app");
        Directory.CreateDirectory(exeDir);
        var bundled = Touch("app", "tools", "ffmpeg.exe");
        var pathDir = Path.Combine(_root, "onpath");
        Touch("onpath", "ffmpeg.exe");

        var resolved = MediaToolResolver.Resolve(
            "ffmpeg", exeDir, configuredPath: null, [pathDir], File.Exists, isWindows: true);

        Assert.Equal(bundled, resolved);
    }

    /// <summary>
    /// And the encoder itself agrees with that order rather than re-deriving it:
    /// pointing its configured-path provider at a file makes it the ffmpeg used.
    /// </summary>
    [Fact]
    public async Task TheEncoderResolvesThroughTheSharedOrderAndHonoursTheConfiguredPath()
    {
        var configured = Touch("custom", "my-ffmpeg.exe");

        string? invokedWith = null;
        var encoder = new FfmpegStemEncoder(
            logger: null,
            ffmpegResolver: null,
            runner: (exe, _, _) => { invokedWith = exe; return Task.FromResult(false); },
            configuredFfmpegPath: () => configured);

        Assert.True(encoder.IsExternalEncoderAvailable);
        Assert.Equal(StemCodec.Opus, encoder.PreferredCodec);

        var wav = Touch("in", "drums.wav");
        await encoder.EncodeAsync(wav, Path.Combine(_root, "out", "drums"), StemCodec.Opus,
            TestContext.Current.CancellationToken);

        Assert.Equal(configured, invokedWith);
    }
}
