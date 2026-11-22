using DotCharter.Tone.Audio.Tonelab;

namespace DotCharter.Tests.Audio.Tonelab;

/// <summary>The 32-bit helper call: its arguments, its exit-code contract, its timeout, and the hidden runner itself.</summary>
public sealed class TonelabInjectHelperTests
{
    private const string Helper = @"C:\DC\tools\tonelab\tonelab-launch.exe";
    private const string Dll = @"C:\DC\tools\tonelab\tonelab.dll";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheHelperGetsInjectThePidAndTheDll_AndNothingElse()
    {
        var runner = FakeHelperRunner.Ok();
        var result = await new TonelabInjectHelper(Helper, Dll, runner).InjectAsync(1234, Ct);

        Assert.True(result.IsOk);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(Helper, call.Exe);
        Assert.Equal(["--inject", "1234", "--dll", Dll], call.Arguments);
        Assert.Equal(TonelabInjectHelper.Timeout, call.Timeout);
    }

    [Theory]
    [InlineData(0, "ok", TonelabInjectOutcome.Ok, "ok")]
    [InlineData(2, "err usage", TonelabInjectOutcome.Usage, "usage")]
    [InlineData(3, "err no-process", TonelabInjectOutcome.NoProcess, "no-process")]
    [InlineData(4, "err not-rocksmith", TonelabInjectOutcome.NotRocksmith, "not-rocksmith")]
    [InlineData(5, "err open-failed", TonelabInjectOutcome.OpenFailed, "open-failed")]
    [InlineData(6, "err dll-not-found", TonelabInjectOutcome.DllNotFound, "dll-not-found")]
    [InlineData(7, "err loader-timeout", TonelabInjectOutcome.GameExited, "loader-timeout")]
    [InlineData(8, "err inject-failed", TonelabInjectOutcome.InjectFailed, "inject-failed")]
    [InlineData(1, "", TonelabInjectOutcome.Failed, "")]
    [InlineData(100, "err ?", TonelabInjectOutcome.Failed, "?")]
    [InlineData(-1, "", TonelabInjectOutcome.Failed, "")]
    public void TheExitCodeDecides_AndTheLastLineExplains(int exitCode, string lastLine, TonelabInjectOutcome outcome, string reason)
    {
        var result = TonelabInjectHelper.Parse(new HelperProcessRun(exitCode, lastLine.Length == 0 ? "" : "starting\r\n" + lastLine + "\r\n\r\n", false));

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(lastLine, result.LastLine);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(outcome == TonelabInjectOutcome.Ok, result.IsOk);
    }

    [Fact]
    public void TheLastNonEmptyLineIsRead()
    {
        var result = TonelabInjectHelper.Parse(new HelperProcessRun(7, "waiting\n  err exited  \r\n\n", false));
        Assert.Equal("err exited", result.LastLine);
        Assert.Equal("exited", result.Reason);
    }

    [Fact]
    public async Task AHelperThatHangs_IsATimeout_AndOneThatCannotStart_AFailure()
    {
        var hung = await new TonelabInjectHelper(Helper, Dll, new FakeHelperRunner(new HelperProcessRun(null, "", true))).InjectAsync(1, Ct);
        Assert.Equal(TonelabInjectOutcome.Timeout, hung.Outcome);
        Assert.False(hung.IsOk);

        var missing = new FakeHelperRunner(new HelperProcessRun(0, "", false)) { Throw = new System.ComponentModel.Win32Exception(2, "not found") };
        var failed = await new TonelabInjectHelper(Helper, Dll, missing).InjectAsync(1, Ct);
        Assert.Equal(TonelabInjectOutcome.Failed, failed.Outcome);
        Assert.Contains("not found", failed.LastLine, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A real child: the timeout is not the subject here, so it is a deadlock detector of minutes
    ///     a loaded box cannot reach, and the run is asserted to have finished on its own.
    /// </summary>
    [Fact]
    public async Task TheHiddenRunner_ReadsExitCodeAndOutput()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "the runner's live case drives cmd.exe.");
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        var run = await HiddenHelperProcessRunner.Instance.RunAsync(cmd, ["/c", "echo err no-process& exit 3"], TimeSpan.FromMinutes(5), Ct);

        Assert.False(run.TimedOut, "cmd.exe did not exit within five minutes: a hang, or a starved box");
        Assert.Equal(3, run.ExitCode);
        Assert.Equal(TonelabInjectOutcome.NoProcess, TonelabInjectHelper.Parse(run).Outcome);
        Assert.Equal("no-process", TonelabInjectHelper.Parse(run).Reason);
    }

    /// <summary>The timeout IS the subject: a helper that would run for half a minute is ended at its deadline.</summary>
    [Fact]
    public async Task TheHiddenRunner_EndsAHelperPastItsTimeout()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "the runner's live case drives cmd.exe.");
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        var slow = await HiddenHelperProcessRunner.Instance.RunAsync(cmd, ["/c", "ping -n 30 127.0.0.1 >nul"], TimeSpan.FromMilliseconds(300), Ct);
        Assert.True(slow.TimedOut);
        Assert.Null(slow.ExitCode);
    }

    [Fact]
    public async Task TheHiddenRunner_EndsTheHelper_WhenTheCallerCancels()
    {
        if (!OperatingSystem.IsWindows()) return;
        var helper = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new HiddenHelperProcessRunner { Started = started.SetResult };
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        // A timeout far past the test's life: only the caller's cancel can end this helper.
        var run = runner.RunAsync(helper,
            ["-NoProfile", "-NonInteractive", "-Command", "[Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset).WaitOne()"],
            TimeSpan.FromMinutes(10), caller.Token);
        var pid = await started.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        using var process = System.Diagnostics.Process.GetProcessById(pid);
        try
        {
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.True(process.HasExited, "Caller cancellation returned while the helper was still alive.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
