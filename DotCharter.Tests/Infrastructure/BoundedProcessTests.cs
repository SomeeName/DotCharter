using System.Diagnostics;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     <see cref="BoundedProcess" />, the one runner the script and fixture tests
///     use for a child whose output they read (#608), against REAL children: a
///     child that never exits must end the test at the cap, with its tree killed,
///     instead of holding it inside a pipe read.
/// </summary>
public sealed class BoundedProcessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-bounded-" + Guid.NewGuid().ToString("N"));

    public BoundedProcessTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public async Task AFinishedChild_ReturnsItsExitCodeAndBothOutputs()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Uses cmd.exe.");
        var ct = TestContext.Current.CancellationToken;

        var result = await BoundedWait.ForAsync(Task.Run(() => BoundedProcess.Run(
            new ProcessStartInfo("cmd.exe", ["/c", "echo out& echo err 1>&2& exit /b 3"]),
            TimeSpan.FromMinutes(2), "cmd (echo)"), ct), "the finished child's result", ct);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("out", result.StandardOutput);
        Assert.Contains("err", result.StandardError);
    }

    /// <summary>The hang the runners had: the child never exits and keeps its pipes
    /// open. The cap is what is under test; the test asserts that it ENDED the run,
    /// never how long the run took. It is long enough that the grandchild is seen
    /// running BEFORE the cap fires, so the kill has a live tree to take down: with a
    /// cap shorter than cmd's start-up the test passed with no tree at all.</summary>
    [Fact]
    public async Task AChildThatNeverExits_FailsAtTheCap_WithItsTreeKilled()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Uses a .cmd script.");
        var ct = TestContext.Current.CancellationToken;
        var tree = OwnedChildTree.Create(_dir);

        var run = Task.Run(() => BoundedProcess.Run(
            new ProcessStartInfo("cmd.exe", ["/c", tree.Script]), TimeSpan.FromSeconds(30), "the never-ending script"), ct);
        await tree.WaitUntilRunningAsync(ct);
        Assert.False(run.IsCompleted, "the run ended before its child tree was running, so no live tree was killed");
        var failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            BoundedWait.ForAsync(run, "BoundedProcess.Run to give up on a child that never exits", ct));
        Assert.Contains("the never-ending script did not finish", failure.Message);
        // A tree the runner failed to kill dies on its own after five minutes.
        await tree.WaitUntilGoneAsync("BoundedProcess left the child tree running after its cap", ct);
    }
}
