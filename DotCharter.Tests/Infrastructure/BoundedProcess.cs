using System.Diagnostics;
using System.Text;

namespace DotCharter.Tests.Infrastructure;

/// <summary>What a child run through <see cref="BoundedProcess" /> left behind.</summary>
internal sealed record BoundedProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
///     The ONE way a test runs a child process whose output it reads (#608).
///     <para>
///         The runners this replaces drained a pipe with a synchronous
///         <c>ReadToEnd()</c> and only then called <c>WaitForExit(cap)</c>. A read
///         to the end returns when every write handle of the pipe is closed, so a
///         child that hangs with its pipe open kept the test inside the read for
///         as long as it lived, and the cap after it was never reached.
///     </para>
///     <para>
///         Here both pipes are drained concurrently from the start, the wait for
///         the exit AND for the end of both pipes shares one cap, and a child that
///         overruns it has its whole process tree killed and reaped before the
///         failure, which names what was being waited for. The cap is a deadlock
///         detector (minutes), never a budget. An optional idle cap instead tracks
///         silence across both pipes; use an infinite total cap to allow a slow,
///         talking child to continue (#664).
///     </para>
/// </summary>
internal static class BoundedProcess
{
    /// <summary>How long a killed tree gets to be reaped and to let go of its pipes
    /// before the failure is reported without its output.</summary>
    private static readonly TimeSpan ReapCap = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     <see cref="Run" />, or null when the executable cannot be started at all
    ///     (not installed, not on PATH): the caller's reason to skip.
    /// </summary>
    public static BoundedProcessResult? TryRun(ProcessStartInfo info, TimeSpan cap, string what, TimeSpan? idleCap = null, TimeProvider? timeProvider = null, Action<string>? onOutput = null)
    {
        try
        {
            return Run(info, cap, what, idleCap, timeProvider, onOutput);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    ///     Runs <paramref name="info" /> with both pipes redirected and no window.
    ///     Returns the exit code and both outputs once the child has exited and
    ///     both pipes have ended. Fails the test, after killing and reaping the
    ///     tree, when that takes longer than <paramref name="cap" />.
    ///     <paramref name="what" /> names the child in that failure, e.g.
    ///     "pwsh (PinnedDownload.psm1)".
    /// </summary>
    public static BoundedProcessResult Run(ProcessStartInfo info, TimeSpan cap, string what, TimeSpan? idleCap = null, TimeProvider? timeProvider = null, Action<string>? onOutput = null)
    {
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;
        info.CreateNoWindow = true;

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{what} did not start.");

        // No token on the reads: a cancelled read leaves a still-writing child
        // blocked on a full pipe with nobody to reap it.
        var clock = timeProvider ?? TimeProvider.System;
        var started = clock.GetTimestamp();
        var guard = idleCap is { } silence ? new ProcessIdleGuard(silence, clock) : null;
        var stdout = guard is null ? process.StandardOutput.ReadToEndAsync() : DrainAsync(process.StandardOutput, guard, onOutput);
        var stderr = guard is null ? process.StandardError.ReadToEndAsync() : DrainAsync(process.StandardError, guard, onOutput);
        var drained = Task.WhenAll(stdout, stderr);

        if (Complete(process, drained, cap, guard, clock, started))
            return new BoundedProcessResult(process.ExitCode, stdout.Result, stderr.Result);

        var exited = process.HasExited;
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The child itself is gone; a descendant is what holds the pipe.
        }

        var reaped = process.WaitForExit(ReapCap);
        var closed = Wait(drained, ReapCap);
        var waitedFor = exited
            ? "it exited, but something it started kept its output pipe open"
            : "it never exited";
        var limit = DescribeLimit(cap, idleCap);
        Assert.Fail(
            $"{what} did not finish within {limit}: {waitedFor}. Its process tree was killed" +
            (reaped ? "." : $" but was not reaped within {ReapCap.TotalSeconds:0} s.") + Environment.NewLine +
            (closed ? $"{Text(stderr)}{Environment.NewLine}{Text(stdout)}" : "(the pipes never closed, so there is no output to show)"));
        throw new UnreachableException();
    }

    private static string DescribeLimit(TimeSpan cap, TimeSpan? idleCap)
    {
        if (idleCap is not { } idle) return $"{cap.TotalSeconds:0} s";
        if (cap != Timeout.InfiniteTimeSpan) return $"{cap.TotalSeconds:0} s total or {idle.TotalSeconds:0} s of silence";
        return $"{idle.TotalSeconds:0} s of silence";
    }

    // Existing callers keep the total cap. Export callers opt into silence tracking and
    // use Timeout.InfiniteTimeSpan for the total cap: progress may continue indefinitely.
    private static bool Complete(Process process, Task drained, TimeSpan cap, ProcessIdleGuard? guard, TimeProvider clock, long started)
    {
        if (guard is null)
        {
            var elapsed = Stopwatch.StartNew();
            var exited = process.WaitForExit(cap);
            var remaining = cap - elapsed.Elapsed;
            return exited && remaining > TimeSpan.Zero && Wait(drained, remaining);
        }

        var poll = TimeSpan.FromMilliseconds(100);
        while (true)
        {
            if (process.HasExited && drained.IsCompleted) return true;
            if (guard.Expired || (cap != Timeout.InfiniteTimeSpan && clock.GetElapsedTime(started) >= cap)) return false;
            if (process.HasExited) Wait(drained, poll);
            else process.WaitForExit(poll);
        }
    }

    // Read chunks rather than lines so partial output also renews the idle guard.
    private static async Task<string> DrainAsync(StreamReader reader, ProcessIdleGuard guard, Action<string>? onOutput)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (count == 0) break;
            var chunk = new string(buffer, 0, count);
            guard.ReportActivity();
            text.Append(chunk);
            onOutput?.Invoke(chunk);
        }

        return text.ToString();
    }

    private static string Text(Task<string> drain) => drain.IsCompletedSuccessfully ? drain.Result : string.Empty;

    private static bool Wait(Task task, TimeSpan cap)
    {
        try
        {
            return task.Wait(cap);
        }
        catch (AggregateException)
        {
            // A pipe that broke when the tree was killed is a finished drain.
            return true;
        }
    }
}
