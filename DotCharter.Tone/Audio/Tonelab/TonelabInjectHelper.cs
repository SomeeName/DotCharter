using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DotCharter.Tone.Audio.Tonelab;

/// <summary>One finished (or abandoned) helper run: its exit code (null when it never started or timed out), its stdout, whether it timed out.</summary>
public sealed record HelperProcessRun(int? ExitCode, string Output, bool TimedOut);

/// <summary>Runs a console helper with no window and collects its output, bounded by a timeout.</summary>
public interface IHelperProcessRunner
{
    Task<HelperProcessRun> RunAsync(string exe, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
///     The real runner: <c>CreateNoWindow</c>, no shell, stdout and stderr redirected; past the
///     timeout the helper (never the game) is ended.
/// </summary>
public sealed class HiddenHelperProcessRunner : IHelperProcessRunner
{
    public static HiddenHelperProcessRunner Instance { get; } = new();

    public async Task<HelperProcessRun> RunAsync(string exe, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            // The helper prints ASCII (its parsed line is "pid <n>"); UTF-8 reads that unchanged.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? ""
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info);
        if (process is null) return new HelperProcessRun(null, "", false);
        var output = process.StandardOutput.ReadToEndAsync(ct);
        _ = process.StandardError.ReadToEndAsync(ct);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return new HelperProcessRun(null, "", true);
        }

        return new HelperProcessRun(process.ExitCode, await output.ConfigureAwait(false), false);
    }
}

/// <summary>What <c>tonelab-launch.exe --inject &lt;pid&gt;</c> reported, by its exit code.</summary>
public enum TonelabInjectOutcome
{
    Ok = 0,
    Usage = 2,
    NoProcess = 3,
    NotRocksmith = 4,
    OpenFailed = 5,
    DllNotFound = 6,
    GameExited = 7,
    InjectFailed = 8,

    /// <summary>The helper did not finish within <see cref="TonelabInjectHelper.Timeout" /> and was ended.</summary>
    Timeout = 100,

    /// <summary>The helper did not start, or exited with a code its contract does not have.</summary>
    Failed = 101
}

/// <summary>The helper's verdict and its last stdout line (<c>ok</c> or <c>err &lt;reason&gt;</c>).</summary>
public sealed record TonelabInjectResult(TonelabInjectOutcome Outcome, int? ExitCode, string LastLine)
{
    public bool IsOk => Outcome == TonelabInjectOutcome.Ok;

    /// <summary>The reason token of an <c>err &lt;reason&gt;</c> line, else the whole line.</summary>
    public string Reason => LastLine.StartsWith("err ", StringComparison.Ordinal) ? LastLine[4..].Trim() : LastLine;
}

/// <summary>
///     The 32-bit injection helper: <c>tools/tonelab/tonelab-launch.exe --inject &lt;pid&gt; --dll &lt;tonelab.dll&gt;</c>.
///     The game is 32-bit and DotCharter 64-bit, so the injection runs in a helper of the game's bitness;
///     it touches nothing else and exits. Exit codes (tonelab's contract): 0 ok (also when already
///     loaded), 2 usage, 3 no-process, 4 not-rocksmith, 5 open-failed, 6 dll-not-found,
///     7 exited / loader-timeout, 8 inject-failed.
/// </summary>
public sealed class TonelabInjectHelper
{
    private readonly IHelperProcessRunner _runner;

    public TonelabInjectHelper(string helperPath, string dllPath, IHelperProcessRunner runner)
    {
        HelperPath = helperPath;
        DllPath = dllPath;
        _runner = runner;
    }

    /// <summary>The helper waits up to 30 s for the game's loader itself; this leaves it room.</summary>
    public static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(45);

    public string HelperPath { get; }

    public string DllPath { get; }

    public IReadOnlyList<string> ArgumentsFor(int processId) =>
        ["--inject", processId.ToString(CultureInfo.InvariantCulture), "--dll", DllPath];

    public async Task<TonelabInjectResult> InjectAsync(int processId, CancellationToken ct)
    {
        HelperProcessRun run;
        try
        {
            run = await _runner.RunAsync(HelperPath, ArgumentsFor(processId), Timeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return new TonelabInjectResult(TonelabInjectOutcome.Failed, null, ex.Message);
        }

        return Parse(run);
    }

    /// <summary>The verdict of one run: the exit code decides, the last non-empty stdout line explains.</summary>
    public static TonelabInjectResult Parse(HelperProcessRun run)
    {
        var last = run.Output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
        if (run.TimedOut) return new TonelabInjectResult(TonelabInjectOutcome.Timeout, null, last);
        var outcome = run.ExitCode is { } code && Enum.IsDefined(typeof(TonelabInjectOutcome), code) && code < (int)TonelabInjectOutcome.Timeout
            ? (TonelabInjectOutcome)code
            : TonelabInjectOutcome.Failed;
        return new TonelabInjectResult(outcome, run.ExitCode, last);
    }
}
