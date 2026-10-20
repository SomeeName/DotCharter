using System.Diagnostics;
using System.Globalization;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Dev-machine startup gate (absolute wall-clock is meaningless on hosted
///     CI, so this is env-gated like the live-updater and real-VST tests).
///     Launches the built app, waits for the machine-readable
///     "StartupPhases: ... interactive=N" line (App.axaml.cs emits it after
///     the first window's render/layout queues drain) and asserts a budget.
///     Run: DOTCHARTER_PERF_GATE=1 dotnet test --filter StartupPerfGate
///     Optional: DOTCHARTER_PERF_GATE_EXE, DOTCHARTER_PERF_GATE_INTERACTIVE_MS.
/// </summary>
public sealed class StartupPerfGateTests
{
    // 0.14 target is 800 ms; the gate starts as a regression tripwire above
    // today's measured baseline (~2.3 s Debug) and ratchets DOWN as the
    // cold-start work lands. Never raise it.
    private const int DefaultBudgetMs = 4000;

    [Fact]
    public void App_ReachesInteractive_WithinBudget()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("DOTCHARTER_PERF_GATE") != "1",
            "Dev-machine wall-clock gate — set DOTCHARTER_PERF_GATE=1 to run.");

        var exe = ResolveExe();
        var budgetMs = int.TryParse(Environment.GetEnvironmentVariable("DOTCHARTER_PERF_GATE_INTERACTIVE_MS"),
            out var b)
            ? b
            : DefaultBudgetMs;

        var launchedAt = DateTime.Now.AddSeconds(-1); // log-clock skew slack
        using var process = Process.Start(new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false
        });
        Assert.NotNull(process);

        try
        {
            var line = WaitForStartupPhasesLine(launchedAt, TimeSpan.FromSeconds(60));
            var interactiveMs = ParseKey(line, "interactive");

            Assert.True(interactiveMs <= budgetMs,
                $"Time to interactive {interactiveMs} ms exceeds the {budgetMs} ms budget. Phase line: {line}");
        }
        finally
        {
            try
            {
                process!.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already exited.
            }
        }
    }

    private static string ResolveExe()
    {
        var overridePath = Environment.GetEnvironmentVariable("DOTCHARTER_PERF_GATE_EXE");
        if (!string.IsNullOrEmpty(overridePath)) return overridePath;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DotCharter.sln"))) dir = dir.Parent;
        Assert.SkipWhen(dir == null, "Repo root not found from the test base directory.");

        // Newest build wins — a stale sibling configuration otherwise gets
        // launched and (predating the StartupPhases line) times the gate out.
        var newest = new[] { "Release", "Debug" }
            .Select(config =>
                UiSourceTree.Resolve(Path.Combine(dir!.FullName, "DotCharter.UI", "bin", config, "net10.0", "DotCharter.exe")))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        Assert.SkipWhen(newest == null,
            "No built DotCharter.exe found — build DotCharter.UI first or set DOTCHARTER_PERF_GATE_EXE.");
        return newest!;
    }

    /// <summary>
    ///     Polls the app's log directory (a bin build is not a portable
    ///     install, so logs land under %APPDATA%/DotCharter/logs) for a
    ///     StartupPhases line stamped after launch. The log file is open in
    ///     the app, so read with full sharing.
    /// </summary>
    private static string WaitForStartupPhasesLine(DateTime launchedAt, TimeSpan timeout)
    {
        var logsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DotCharter", "logs");
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var newest = new DirectoryInfo(logsDir).GetFiles("*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest != null)
            {
                using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    var idx = line.IndexOf("StartupPhases:", StringComparison.Ordinal);
                    if (idx < 0) continue;
                    if (DateTime.TryParseExact(line.AsSpan(0, Math.Min(23, line.Length)), "yyyy-MM-dd HH:mm:ss.fff",
                            CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var stamp) &&
                        stamp >= launchedAt)
                    {
                        return line[idx..];
                    }
                }
            }

            Thread.Sleep(250);
        }

        Assert.Fail($"No StartupPhases line appeared within {timeout.TotalSeconds:F0} s — " +
                    "did the app crash before first render?");
        return null!; // unreachable
    }

    private static long ParseKey(string line, string key)
    {
        foreach (var token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!token.StartsWith(key + "=", StringComparison.Ordinal)) continue;
            return long.Parse(token.AsSpan(key.Length + 1), CultureInfo.InvariantCulture);
        }

        Assert.Fail($"Key '{key}' not found in: {line}");
        return 0; // unreachable
    }
}
