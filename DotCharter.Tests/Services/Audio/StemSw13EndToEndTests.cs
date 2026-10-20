using System.Collections.Concurrent;
using System.Diagnostics;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Gpu;
using DotCharter.Services.Audio.StemSeparation.Onnx;
using DotCharter.Services.Update;
using JuceSharp.AudioIo;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
/// #267, opt-in (<c>DOTCHARTER_SW13_E2E=1</c>): the 13 s BS-RoFormer SW through the app's REAL
/// path, end to end, on this machine's GPU. The signed-in token comes only through the app's own
/// <see cref="GitHubTokenStore" /> + <see cref="GitHubAuthService" /> and is never printed; the
/// model downloads into a TEMP models folder (<c>DOTCHARTER_SW13_E2E_DIR</c>, else a new temp
/// folder) — never the user's — through <see cref="StemDefaultModelProvisioner" />, is verified
/// against its pin, and then separates the 224 s Jukkai song on the default model through
/// <see cref="OnnxStemSeparationService" />. A report (times, adapter, fusion line, stems-sum)
/// is written beside the stems as <c>sw13-e2e.txt</c>; video memory is read from outside.
/// </summary>
public sealed class StemSw13EndToEndTests
{
    [Fact]
    public async Task The13sModel_DownloadsSignedIn_VerifiesAndSeparatesJukkai_OnTheGpu()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("DOTCHARTER_SW13_E2E") == "1",
            "Opt-in GPU + network run: set DOTCHARTER_SW13_E2E=1 (and optionally DOTCHARTER_SW13_E2E_DIR).");
        var ct = TestContext.Current.CancellationToken;
        // A worktree under .claude/worktrees finds the main checkout's TestData by walking up.
        var song = Ancestors(TestPaths.RepoRoot())
            .Select(d => Path.Combine(d, "TestData", "EOF", "Ado - Jukkai (1984)", "Jukkai (1984).wav"))
            .FirstOrDefault(File.Exists);
        Assert.SkipUnless(song != null, "The corpus song (TestData/EOF/Ado - Jukkai (1984)) is not here.");

        var work = Environment.GetEnvironmentVariable("DOTCHARTER_SW13_E2E_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Path.GetTempPath(), "dc-sw13-e2e-" + Guid.NewGuid().ToString("N"));
        var models = Path.Combine(work, "models");
        var stems = Path.Combine(work, "stems");
        Directory.CreateDirectory(models);
        var report = new List<string>();
        var log = new CaptureLogger();

        // The app's own sign-in: the DPAPI store under the app's config root.
        var store = new GitHubTokenStore();
        Assert.True(store.HasToken, "No GitHub sign-in is stored for this Windows user.");
        using var auth = new GitHubAuthService(store, log);
        using var assets = GitHubReleaseAssetDownloader.For(auth, log, new Version(0, 61, 0));
        Assert.True(assets.IsSignedIn);

        var config = new FakeConfigurationService();
        var manager = new StemModelManager(config, log, models, assets);
        var gpu = new OnnxStemGpuEnvironment();
        foreach (var a in gpu.Adapters())
            report.Add($"adapter {a.DeviceId}: {a.Name}, dedicated {a.DedicatedVideoMemoryBytes:N0} bytes, software={a.IsSoftware}");
        var plan = OnnxStemSeparationService.ResolvePlan(manager, gpu);
        report.Add($"plan: {plan.Provider} device {plan.DeviceId} ({plan.Adapter?.Name}), tier {StemDefaultModel.TierFor(plan)}");

        var provisioner = new StemDefaultModelProvisioner(manager, gpu, log);
        var offer = await provisioner.GetOfferAsync(ct);
        Assert.NotNull(offer);
        Assert.Equal(StemDefaultModel.LargeGpuDefaultId, offer.ModelId);
        report.Add($"offer: {offer.ModelId} ({offer.Bytes:N0} bytes), fallback {offer.FallbackId}");

        var download = Stopwatch.StartNew();
        Assert.Equal(StemDefaultModel.LargeGpuDefaultId, await provisioner.DownloadAsync(offer, null, ct));
        download.Stop();
        Assert.True(await manager.VerifyInstalledAsync(StemDefaultModel.LargeGpuDefaultId, ct));
        report.Add($"download + verify: {download.Elapsed.TotalSeconds:0.0} s, " +
                   $"{new FileInfo(manager.GetLocalPathIfInstalled(StemDefaultModel.LargeGpuDefaultId)!).Length:N0} bytes, hash OK");
        Assert.Equal("", config.AppConfig.HtDemucsActiveModelId);

        var service = new OnnxStemSeparationService(manager, log, gpu: gpu);
        var started = DateTime.UtcNow;
        var separate = Stopwatch.StartNew();
        var result = await service.SeparateAsync(song, stems, null, ct);
        separate.Stop();
        var ended = DateTime.UtcNow;
        Assert.True(result.Success, result.ErrorMessage);
        report.Add($"separation: {separate.Elapsed.TotalSeconds:0.0} s ({started:O} .. {ended:O}), {result.Stems.Count} stems");
        report.AddRange(log.Lines.Where(l => l.Contains("graph fusion", StringComparison.Ordinal) ||
                                             l.Contains("model=", StringComparison.Ordinal)));
        Assert.Contains(log.Lines, l => l.Contains("bs_roformer_sw_6stem_13s_fp16.onnx", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Contains("graph fusion on", StringComparison.Ordinal));

        var mix = WavIo.Read(song);
        double signal = 0, error = 0;
        var parts = result.Stems.Select(s => WavIo.Read(s.Path)).ToArray();
        Assert.All(parts, p => Assert.Equal(mix.SampleRate, p.SampleRate));
        var n = parts.Min(p => p.Interleaved.Length);
        for (var i = 0; i < Math.Min(n, mix.Interleaved.Length); i++)
        {
            double sum = 0;
            foreach (var p in parts) sum += p.Interleaved[i];
            signal += (double)mix.Interleaved[i] * mix.Interleaved[i];
            error += (mix.Interleaved[i] - sum) * (mix.Interleaved[i] - sum);
        }

        var sumDb = 10 * Math.Log10(signal / Math.Max(error, 1e-30));
        report.Add($"stems sum to the mix at {sumDb:0.00} dB ({string.Join(", ", result.Stems.Select(s => s.Name))})");
        await File.WriteAllLinesAsync(Path.Combine(work, "sw13-e2e.txt"), report, ct);
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join(Environment.NewLine, report));
        Assert.True(sumDb > 20, $"the stems sum to the mix at only {sumDb:0.00} dB");
    }

    private static IEnumerable<string> Ancestors(string dir)
    {
        for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
            yield return d.FullName;
    }

    /// <summary>Thread-safe: the separation logs from worker threads.</summary>
    private sealed class CaptureLogger : IAppLogger
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => [.. _lines];

        public string CurrentLogPath => "<capture>";

        public event Action<LogEmittedArgs>? LogEmitted
        {
            add { }
            remove { }
        }

        public bool IsEnabled(AppLogCategory category) => true;

        public void Flush()
        {
        }

        public void ReloadSettings()
        {
        }

        public void Trace(AppLogCategory category, string message) => _lines.Enqueue(message);

        public void Debug(AppLogCategory category, string message) => _lines.Enqueue(message);

        public void Info(AppLogCategory category, string message) => _lines.Enqueue(message);

        public void Warning(AppLogCategory category, string message) => _lines.Enqueue("WARN " + message);

        public void Warning(AppLogCategory category, string message, Exception? exception) =>
            _lines.Enqueue($"WARN {message} {exception?.Message}");

        public void Error(AppLogCategory category, string message, Exception? exception = null) =>
            _lines.Enqueue($"ERROR {message} {exception?.Message}");

        public void External(string source, string message) => _lines.Enqueue(message);

        public IDisposable BeginTimedOperation(AppLogCategory category, string operationName, string? details = null) =>
            new Nothing();

        public void LogUnhandledException(string source, Exception exception) => _lines.Enqueue("UNHANDLED " + exception.Message);

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
