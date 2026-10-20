using DotCharter.Services.Import.Youtube;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Import.Youtube;

/// <summary>
///     <see cref="YtDlpAudioDownloader" /> was the one child-process launcher in
///     the codebase with no timeout, no cancellation and no kill-on-cancel, and
///     it had zero tests of any kind. A stalled yt-dlp (dead network, throttled
///     or geoblocked video, an interactive prompt) hung the import dialog with
///     <c>IsBusy</c> stuck forever, and disposing the managed wrapper never
///     killed the child — so yt-dlp and the ffmpeg it spawns kept running,
///     re-creating the file the cancel path had just deleted.
/// </summary>
public sealed class YtDlpAudioDownloaderTests : IDisposable
{
    private readonly string _toolDir = Directory.CreateTempSubdirectory("dotcharter-ytdlp-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_toolDir);

    // ---- The command line ---------------------------------------------------

    [Fact]
    public void BuildArguments_TerminatesOptionParsingBeforeTheUrl()
    {
        var args = YtDlpAudioDownloader.BuildArguments(@"C:\tools", @"C:\out\song.%(ext)s",
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ");

        var terminator = args.ToList().IndexOf("--");
        Assert.True(terminator >= 0, "the argument list must carry a `--` option terminator");
        Assert.Equal(args.Count - 2, terminator);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", args[^1]);
    }

    [Fact]
    public void BuildArguments_KeepsTheExtractAudioAndFfmpegLocationFlags()
    {
        var args = YtDlpAudioDownloader.BuildArguments(@"C:\tools", @"C:\out\song.%(ext)s", "url");

        Assert.Contains("-x", args);
        Assert.Contains("--no-playlist", args);
        Assert.Contains("--newline", args);

        var ffmpegFlag = args.ToList().IndexOf("--ffmpeg-location");
        Assert.True(ffmpegFlag >= 0);
        Assert.Equal(@"C:\tools", args[ffmpegFlag + 1]);

        var output = args.ToList().IndexOf("-o");
        Assert.True(output >= 0);
        Assert.Equal(@"C:\out\song.%(ext)s", args[output + 1]);
    }

    // ---- The resolver-failure early returns ---------------------------------

    [Fact]
    public async Task WithNoYtDlp_ReportsFailureInsteadOfThrowing()
    {
        var downloader = new YtDlpAudioDownloader(new StubConfig(null, null), null, _toolDir, static () => []);
        var result = await downloader.DownloadMp3Async("https://example.invalid", OutputBase(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("yt-dlp", result.Error);
    }

    [Fact]
    public async Task WithNoFfmpeg_ReportsFailureInsteadOfThrowing()
    {
        var ytDlp = FakeTool("yt-dlp.exe");
        // PATH pinned empty: this machine HAS ffmpeg installed, and the resolver's
        // PATH fallback is exactly what this test must not see.
        var downloader = new YtDlpAudioDownloader(new StubConfig(ytDlp, null), null, _toolDir, static () => []);
        var result = await downloader.DownloadMp3Async("https://example.invalid", OutputBase(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("ffmpeg", result.Error);
    }

    // ---- The bound itself ---------------------------------------------------

    /// <summary>
    ///     The wall clock. An expired budget must come back as a clean failure
    ///     result — not a hang, and not an exception the dialog would surface as
    ///     an import error. Before the fix there was no budget at all: the method
    ///     took no timeout and awaited <c>WaitForExitAsync(default)</c>.
    /// </summary>
    [Fact]
    public async Task AnExpiredTimeout_ReturnsAFailureInsteadOfWaitingForever()
    {
        var downloader = RealChildDownloader(out var output);

        var result = await downloader.DownloadMp3Async(
            "https://example.invalid", output, cancellationToken: TestContext.Current.CancellationToken,
            timeout: TimeSpan.Zero);

        Assert.False(result.Success);
        Assert.Contains("timed out", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output + ".mp3"), "the partial output must be cleaned up");
    }

    /// <summary>
    ///     An explicit cancel must still PROPAGATE (the dialog aborts the import
    ///     on it) rather than degrade to a failure result — and must not leave the
    ///     partial mp3 behind.
    /// </summary>
    [Fact]
    public async Task AnAlreadyCancelledToken_PropagatesAndCleansUp()
    {
        var downloader = RealChildDownloader(out var output);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            downloader.DownloadMp3Async("https://example.invalid", output, cancellationToken: cts.Token));

        Assert.False(File.Exists(output + ".mp3"));
    }

    /// <summary>The timeout must not fire on a normal run: a child that exits on
    /// its own inside the budget reports its own exit code.</summary>
    [Fact]
    public async Task AChildThatExitsWithinTheBudget_ReportsItsOwnFailureNotATimeout()
    {
        var downloader = RealChildDownloader(out var output);

        var result = await downloader.DownloadMp3Async(
            "https://example.invalid", output, cancellationToken: TestContext.Current.CancellationToken,
            timeout: TimeSpan.FromMinutes(1));

        Assert.False(result.Success);
        Assert.DoesNotContain("timed out", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
    }

    // ---- helpers ------------------------------------------------------------

    /// <summary>A downloader wired to a REAL, startable child process. The system
    /// `ping` binary is copied in under the tool names: it starts, rejects
    /// yt-dlp's arguments and exits, which is all these cases need — the budget,
    /// not the child, is what is under test.</summary>
    private YtDlpAudioDownloader RealChildDownloader(out string outputBase)
    {
        var systemPing = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe");
        Assert.SkipWhen(!OperatingSystem.IsWindows() || !File.Exists(systemPing),
            "needs a real startable child process (Windows ping.exe)");

        var ytDlp = Path.Combine(_toolDir, "yt-dlp.exe");
        var ffmpeg = Path.Combine(_toolDir, "ffmpeg.exe");
        File.Copy(systemPing, ytDlp, true);
        File.Copy(systemPing, ffmpeg, true);

        outputBase = OutputBase();
        return new YtDlpAudioDownloader(new StubConfig(ytDlp, ffmpeg), null, _toolDir, static () => []);
    }

    private string OutputBase() => Path.Combine(_toolDir, $"out-{Guid.NewGuid():N}");

    private string FakeTool(string fileName)
    {
        var path = Path.Combine(_toolDir, fileName);
        File.WriteAllBytes(path, [0x00]);
        return path;
    }

    private sealed class StubConfig(string? ytDlpPath, string? ffmpegPath) : IConfigurationService
    {
        private readonly AppConfig _config = new() { YtDlpPath = ytDlpPath ?? "", FfmpegPath = ffmpegPath ?? "" };

        public AppConfig LoadAppConfig() => _config;
        public void SaveAppConfig(AppConfig config) { }
        public void Update(Action<AppConfig> mutate) => mutate(_config);
        public KeybindConfig LoadKeybinds() => new();
        public void SaveKeybinds(KeybindConfig config) { }
        public void InvalidateCache() { }
        public void Flush() { }
    }
}
