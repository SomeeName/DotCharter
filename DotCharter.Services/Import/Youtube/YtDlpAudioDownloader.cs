using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace DotCharter.Services.Import.Youtube;

public sealed record YtDlpDownloadResult(bool Success, string? FilePath, string? Error);

public interface IYtDlpAudioDownloader
{
    /// <summary>True when both yt-dlp and ffmpeg are resolvable (configured, next
    /// to the app, in the app <c>tools/</c> folder, or on PATH) — YouTube audio
    /// import needs ffmpeg to transcode to the app-decodable mp3.</summary>
    bool IsAvailable { get; }

    string? YtDlpPath { get; }
    string? FfmpegPath { get; }

    /// <summary>Downloads a YouTube video's audio and transcodes it to mp3 at
    /// <paramref name="outputBasePathNoExtension" />.mp3. Reports [0..1]
    /// progress.
    /// <para>
    /// Bounded on BOTH axes: <paramref name="timeout" /> (default
    /// <see cref="YtDlpAudioDownloader.DefaultTimeout" />) is a wall clock, and
    /// <paramref name="cancellationToken" /> aborts on demand. Either way the
    /// child PROCESS TREE is killed — yt-dlp spawns ffmpeg, and disposing the
    /// managed wrapper kills neither. Never throws except
    /// <see cref="OperationCanceledException" /> for an explicit cancel;
    /// everything else comes back on the result.
    /// </para>
    /// </summary>
    Task<YtDlpDownloadResult> DownloadMp3Async(
        string videoUrl, string outputBasePathNoExtension,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default,
        TimeSpan? timeout = null);
}

/// <summary>
///     <see cref="IYtDlpAudioDownloader" /> backed by the yt-dlp + ffmpeg CLIs.
///     Both are resolved via <see cref="MediaToolResolver" /> (configured path →
///     bundled next to the app → app <c>tools/</c> folder → PATH). Downloading
///     YouTube audio is subject to
///     YouTube's Terms of Service — this is a user-driven, personal-use feature.
/// </summary>
public sealed partial class YtDlpAudioDownloader : IYtDlpAudioDownloader
{
    private readonly IConfigurationService _config;
    private readonly IAppLogger? _logger;
    private readonly string _exeDir;
    private readonly Func<IEnumerable<string>> _pathDirs;

    public YtDlpAudioDownloader(IConfigurationService config, IAppLogger? logger = null, string? exeDir = null)
        : this(config, logger, exeDir, MediaToolResolver.CurrentPathDirs) { }

    /// <summary>Test seam: the PATH probe injected. The resolver falls back to
    /// the machine PATH, so a "tool not found" test on a machine that HAS the
    /// tool installed (this repo's dev box has ffmpeg) passes or fails with the
    /// weather unless the probe is pinned.</summary>
    internal YtDlpAudioDownloader(IConfigurationService config, IAppLogger? logger,
        string? exeDir, Func<IEnumerable<string>> pathDirs)
    {
        _config = config;
        _logger = logger;
        _exeDir = exeDir ?? AppContext.BaseDirectory;
        _pathDirs = pathDirs;
    }

    public string? YtDlpPath => Resolve("yt-dlp", _config.LoadAppConfig().YtDlpPath);
    public string? FfmpegPath => Resolve("ffmpeg", _config.LoadAppConfig().FfmpegPath);
    public bool IsAvailable => YtDlpPath != null && FfmpegPath != null;

    private string? Resolve(string tool, string? configured) => MediaToolResolver.Resolve(
        tool, _exeDir, configured, _pathDirs(), File.Exists, OperatingSystem.IsWindows());

    /// <summary>
    ///     Wall clock for one download. Generous — a long video on a slow link is
    ///     legitimate — but finite: this was the ONE child process in the codebase
    ///     with no bound at all, so a stalled yt-dlp (dead network, throttled or
    ///     geoblocked video, an interactive prompt) hung the import dialog with
    ///     <c>IsBusy</c> stuck and no way to abort.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(20);

    /// <summary>Cap on the retained stderr. Only the last 400 characters are ever
    /// used, and yt-dlp can emit megabytes of warnings.</summary>
    private const int MaxStderrChars = 8192;

    public async Task<YtDlpDownloadResult> DownloadMp3Async(
        string videoUrl, string outputBasePathNoExtension,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        // An already-cancelled call must do NOTHING observable. Without this the
        // method went on to delete the expected mp3 and start a child process
        // before the token was ever looked at, so whether it threw or returned a
        // failure result was a race with the child — which also made the caller's
        // "cancel aborts the import" contract depend on process scheduling.
        cancellationToken.ThrowIfCancellationRequested();

        // A non-positive budget is already spent, so starting a child just to
        // cancel it is both wasteful and RACY: the timeout token fires before
        // Process.Start, and a child that exits quickly lets WaitForExitAsync
        // return success before the token is ever observed — so the same call
        // returned "timed out" or a completed download depending on scheduling.
        if (timeout is { } budget && budget <= TimeSpan.Zero)
            return new YtDlpDownloadResult(false, null, "yt-dlp timed out.");

        var ytDlp = YtDlpPath;
        var ffmpeg = FfmpegPath;
        if (ytDlp == null) return new YtDlpDownloadResult(false, null, "yt-dlp not found.");
        if (ffmpeg == null) return new YtDlpDownloadResult(false, null, "ffmpeg not found (needed to convert YouTube audio to mp3).");

        var ffmpegDir = Path.GetDirectoryName(ffmpeg) ?? "";
        var outputTemplate = outputBasePathNoExtension + ".%(ext)s";
        var expectedMp3 = outputBasePathNoExtension + ".mp3";
        BestEffortDelete.File(expectedMp3);

        var psi = new ProcessStartInfo
        {
            FileName = ytDlp,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in BuildArguments(ffmpegDir, outputTemplate, videoUrl))
            psi.ArgumentList.Add(argument);

        var stderr = new StringBuilder();
        Process? process = null;
        try
        {
            using var timeoutSource = new CancellationTokenSource(timeout ?? DefaultTimeout);
            using var linked =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

            process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => ReportProgress(e.Data, progress);
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (stderr)
                {
                    if (stderr.Length < MaxStderrChars)
                        stderr.AppendLine(e.Data);
                }
            };

            if (!process.Start())
                return new YtDlpDownloadResult(false, null, "Could not start yt-dlp.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Disposing the wrapper does NOT kill the child, and yt-dlp has
                // its own ffmpeg child: without this the tree keeps running,
                // re-creating the file we are about to delete, potentially past
                // app exit.
                KillTree(process);
                BestEffortDelete.File(expectedMp3);

                if (!cancellationToken.IsCancellationRequested)
                {
                    _logger?.Warning(AppLogCategory.Import,
                        $"yt-dlp timed out after {(timeout ?? DefaultTimeout).TotalMinutes:0.#} min; killed the process tree.");
                    return new YtDlpDownloadResult(false, null, "yt-dlp timed out.");
                }

                throw;
            }

            if (process.ExitCode != 0)
            {
                string tail;
                lock (stderr) tail = Tail(stderr.ToString(), 400);
                _logger?.Warning(AppLogCategory.Import, $"yt-dlp exited {process.ExitCode}: {tail}");
                return new YtDlpDownloadResult(false, null, $"yt-dlp failed (exit {process.ExitCode}). {tail}");
            }
        }
        catch (OperationCanceledException)
        {
            BestEffortDelete.File(expectedMp3);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Import, $"yt-dlp run threw: {ex.Message}");
            return new YtDlpDownloadResult(false, null, ex.Message);
        }
        finally
        {
            // Any path out of the try — including a throw between Start and the
            // wait — must not leave the tree running.
            if (process != null)
            {
                KillTree(process);
                process.Dispose();
            }
        }

        if (!File.Exists(expectedMp3) || new FileInfo(expectedMp3).Length == 0)
            return new YtDlpDownloadResult(false, null, "yt-dlp reported success but produced no mp3.");

        progress?.Report(1.0);
        _logger?.Info(AppLogCategory.Import, $"YouTube audio downloaded to '{expectedMp3}'.");
        return new YtDlpDownloadResult(true, expectedMp3, null);
    }

    /// <summary>
    ///     The yt-dlp command line, as a pure function so its shape has a test
    ///     seam. Everything goes through <c>ProcessStartInfo.ArgumentList</c> with
    ///     <c>UseShellExecute = false</c>, so no shell ever sees these — but the
    ///     trailing <c>--</c> still matters: it terminates option parsing so a URL
    ///     (or a future caller's raw video id) can never be read as a switch.
    /// </summary>
    internal static IReadOnlyList<string> BuildArguments(string ffmpegDir, string outputTemplate, string videoUrl)
    {
        return
        [
            "-x",
            "--audio-format", "mp3",
            "--audio-quality", "0",
            "--no-playlist",
            "--newline", // one progress line per update
            "--ffmpeg-location", ffmpegDir,
            "-o", outputTemplate,
            "--", videoUrl
        ];
    }

    private static void ReportProgress(string? line, IProgress<double>? progress)
    {
        if (line == null || progress == null) return;
        var m = DownloadPercentRegex().Match(line);
        if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture,
                out var pct))
        {
            progress.Report(Math.Clamp(pct / 100.0, 0.0, 1.0));
        }
    }

    private static string Tail(string text, int max)
        => text.Length <= max ? text.Trim() : text[^max..].Trim();

    /// <summary>Kills a still-running child and everything it spawned. yt-dlp
    /// launches ffmpeg via <c>--ffmpeg-location</c>, so killing only the parent
    /// leaves the transcode running and re-creating the output.</summary>
    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(true);
        }
        catch
        {
            // Already gone, or never started.
        }
    }

    [GeneratedRegex(@"\[download\]\s+([\d.]+)%")]
    private static partial Regex DownloadPercentRegex();
}
