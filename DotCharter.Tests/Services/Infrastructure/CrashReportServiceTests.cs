using DotCharter.Services.Infrastructure.CrashReporting;
using DotCharter.Tests.Services.Diagnostics;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Infrastructure;

public sealed class CrashReportServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _reportsDir;
    private readonly string _logPath;

    public CrashReportServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"dc_crash_{Guid.NewGuid():N}");
        _reportsDir = Path.Combine(_root, "crash-reports");
        _logPath = Path.Combine(_root, "dotcharter.log");
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => BestEffortDelete.Directory(_root);

    private CrashReportService Build(AppConfig config, DateTimeOffset? clock = null)
    {
        return new CrashReportService(
            () => config,
            logger: null,
            reportsDirectory: _reportsDir,
            logFilePath: _logPath,
            clock: () => clock ?? new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void TryCapture_OptedOut_WritesNothing()
    {
        var svc = Build(new AppConfig { CrashReportOptIn = false });

        var path = svc.TryCapture("AppDomain", new InvalidOperationException("boom"));

        Assert.Null(path);
        Assert.False(Directory.Exists(_reportsDir) && Directory.GetFiles(_reportsDir).Length > 0);
    }

    [Fact]
    public void TryCapture_OptedIn_WritesRedactedBundle()
    {
        File.WriteAllText(_logPath,
            "2026-07-10 line A\r\n2026-07-10 signed in as Alex\r\n2026-07-10 password=hunter2\r\n");
        var svc = Build(new AppConfig { CrashReportOptIn = true });

        var path = svc.TryCapture("AppDomain", new InvalidOperationException("boom"));

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        var content = File.ReadAllText(path!);

        Assert.Contains(CrashReportBuilder.Header, content);
        Assert.Contains("Source: AppDomain", content);
        Assert.Contains("System.InvalidOperationException", content);
        // Log tail is bundled...
        Assert.Contains("line A", content);
        // ...but credentials in it are scrubbed.
        Assert.DoesNotContain("hunter2", content);
        // The OS user name is never present verbatim (the service scrubs it as a
        // defence-in-depth pass), when it is long enough to safely match.
        if (Environment.UserName.Trim().Length >= 2)
            Assert.DoesNotContain(Environment.UserName, content);
    }

    [Fact]
    public void TryCapture_NoLogFile_StillWritesBundle()
    {
        var svc = Build(new AppConfig { CrashReportOptIn = true });

        var path = svc.TryCapture("TaskScheduler", new Exception("no log around"));

        Assert.NotNull(path);
        var content = File.ReadAllText(path!);
        Assert.Contains("--- Recent log (last 0 line(s)) ---", content);
    }

    [Fact]
    public void TryCapture_ConfigProviderThrows_ReturnsNull()
    {
        var svc = new CrashReportService(
            () => throw new InvalidOperationException("config unavailable"),
            logger: null,
            reportsDirectory: _reportsDir,
            logFilePath: _logPath);

        var path = svc.TryCapture("AppDomain", new Exception("boom"));

        Assert.Null(path);
    }

    /// <summary>
    ///     A crash bundle is a file the user is invited to attach to a bug
    ///     report, so this is the shape that actually matters: a REAL log tail,
    ///     with the mixture of identity and credential shapes a session leaves
    ///     behind. Each line here is a real logging pattern from this app —
    ///     project paths under the user's home, the GitHub PAT the updater uses,
    ///     an Authorization header, the TONE3000 OAuth exchange, the online
    ///     server's session. None of the VALUES may survive into the bundle.
    /// </summary>
    [Fact]
    public void TryCapture_RealisticBundle_CarriesNoSecretAndNoUserPath()
    {
        var secrets = new Dictionary<string, string>
        {
            ["github pat"] = FakeSecrets.GitHubPat,
            ["jwt"] = FakeSecrets.Jwt,
            ["password"] = "hunter2",
            ["client secret"] = "cs_FAKE_client_secret_0000",
            ["client id"] = "FAKE-tone3000-client-0000",
            ["api key"] = "0123456789abcdef0123456789abcdef01234567"
        };

        File.WriteAllText(_logPath, string.Join("\r\n",
        [
            @"12:00:01 INF Opened C:\Users\Alex\Music\Charts\song.rscp",
            @"12:00:02 INF Home is /home/alex/.config/dotcharter",
            $"12:00:03 INF GitHub sign-in ok, token={secrets["github pat"]}",
            $"12:00:04 DBG GET /releases Authorization: Bearer {secrets["jwt"]}",
            $"12:00:05 DBG POST /oauth/token client_id={secrets["client id"]}&client_secret={secrets["client secret"]}",
            $"12:00:06 WRN retry with password={secrets["password"]}",
            $"12:00:07 INF apiKey: {secrets["api key"]}",
            "12:00:08 INF Playback started"
        ]) + "\r\n");

        var svc = Build(new AppConfig { CrashReportOptIn = true });
        var path = svc.TryCapture("AppDomain", new InvalidOperationException("boom"));

        Assert.NotNull(path);
        var content = File.ReadAllText(path!);

        foreach (var (_, value) in secrets)
            Assert.DoesNotContain(value, content);

        // Identity, not just secrets: no home-directory user segment survives,
        // on either platform's shape.
        Assert.DoesNotContain(@"Users\Alex", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/home/alex", content, StringComparison.OrdinalIgnoreCase);

        // …and the bundle is still USEFUL — over-redacting to an empty file would
        // pass every assertion above.
        Assert.Contains("Playback started", content);
        Assert.Contains("System.InvalidOperationException", content);
    }

    /// <summary>
    ///     The log tail must stay bounded no matter how large the log grew (the
    ///     rolling sink allows 10 MB), and the seek must not hand a HALF LINE to
    ///     the bundle. This is why the reader is shared with the diagnostics
    ///     bundle and the Session Health panel rather than hand-rolled a fourth
    ///     time — this copy had already drifted from the other three.
    /// </summary>
    [Fact]
    public void TryCapture_HugeLog_BundlesABoundedTailOfWholeLines()
    {
        // ~2 MB, comfortably past the 512 KB read window.
        var line = new string('x', 400);
        using (var writer = new StreamWriter(_logPath))
        {
            for (var i = 0; i < 5000; i++)
                writer.WriteLine($"{i:0000} {line} END{i:0000}");
        }

        var svc = Build(new AppConfig { CrashReportOptIn = true });
        var path = svc.TryCapture("AppDomain", new Exception("boom"));

        Assert.NotNull(path);
        var content = File.ReadAllText(path!);

        // The tail is the END of the log, not the start.
        Assert.Contains("END4999", content);
        Assert.DoesNotContain("0000 " + line, content);

        // Every bundled log line is a WHOLE line: the seek lands mid-line and the
        // partial first one must be dropped, not shipped as a fragment.
        var body = content[content.IndexOf("--- Recent log", StringComparison.Ordinal)..];
        foreach (var bundled in body.Split('\n').Skip(1))
        {
            var trimmed = bundled.TrimEnd('\r');
            if (trimmed.Length == 0 || !trimmed.Contains("END", StringComparison.Ordinal)) continue;
            Assert.Matches(@"^\d{4} x{400} END\d{4}$", trimmed);
        }
    }

    [Fact]
    public void TryCapture_PrunesToRetentionCap()
    {
        var svc = Build(new AppConfig { CrashReportOptIn = true });

        // Pre-seed more than the retention cap (20) of stale reports.
        Directory.CreateDirectory(_reportsDir);
        for (var i = 0; i < 30; i++)
        {
            var stale = Path.Combine(_reportsDir, $"crash-old-{i:00}.txt");
            File.WriteAllText(stale, "old");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddMinutes(-100 + i));
        }

        var path = svc.TryCapture("AppDomain", new Exception("newest"));
        Assert.NotNull(path);

        var remaining = Directory.GetFiles(_reportsDir, "crash-*.txt");
        Assert.True(remaining.Length <= 20, $"expected <= 20 reports, found {remaining.Length}");
        Assert.Contains(remaining, f => f == path);
    }
}
