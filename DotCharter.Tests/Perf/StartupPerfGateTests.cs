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

        // The child gets a data root of its own and only ITS log is read (#606): the
        // gate used to run the app on the user's real data and to accept the newest
        // StartupPhases line in the shared log folder, whoever had written it.
        using var launch = new StartupGateLaunch();
        using var process = Process.Start(launch.StartInfo(exe));
        Assert.NotNull(process);

        try
        {
            var line = WaitForStartupPhasesLine(launch, TimeSpan.FromSeconds(60));
            var interactiveMs = ParseKey(line, "interactive");

            Assert.True(interactiveMs <= budgetMs,
                $"Time to interactive {interactiveMs} ms exceeds the {budgetMs} ms budget. Phase line: {line}");
        }
        finally
        {
            try
            {
                process!.Kill(entireProcessTree: true);
                // Reaped before the data root is removed: the app holds its log open.
                process.WaitForExit(TimeSpan.FromSeconds(30));
            }
            catch (InvalidOperationException)
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
    ///     Polls the child's OWN log folder for its StartupPhases line. The cap is the
    ///     gate's deadlock detector for an app that never reaches first render.
    /// </summary>
    private static string WaitForStartupPhasesLine(StartupGateLaunch launch, TimeSpan timeout)
    {
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < timeout)
        {
            if (launch.FindStartupPhasesLine() is { } line) return line;
            Thread.Sleep(250);
        }

        Assert.Fail($"No StartupPhases line appeared in '{launch.LogsDirectory}' within {timeout.TotalSeconds:F0} s: " +
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
