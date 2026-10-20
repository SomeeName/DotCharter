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

    [Fact]
    public async Task TheHiddenRunner_ReadsExitCodeAndOutput_AndEndsAHelperPastItsTimeout()
    {
        if (!OperatingSystem.IsWindows()) return;
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        var run = await HiddenHelperProcessRunner.Instance.RunAsync(cmd, ["/c", "echo err no-process& exit 3"], TimeSpan.FromSeconds(20), Ct);
        Assert.False(run.TimedOut);
        Assert.Equal(TonelabInjectOutcome.NoProcess, TonelabInjectHelper.Parse(run).Outcome);
        Assert.Equal("no-process", TonelabInjectHelper.Parse(run).Reason);

        var slow = await HiddenHelperProcessRunner.Instance.RunAsync(cmd, ["/c", "ping -n 30 127.0.0.1 >nul"], TimeSpan.FromMilliseconds(300), Ct);
        Assert.True(slow.TimedOut);
        Assert.Null(slow.ExitCode);
    }
}
