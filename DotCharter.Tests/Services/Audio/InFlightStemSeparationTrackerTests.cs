using DotCharter.Services.Audio.StemSeparation;

namespace DotCharter.Tests.Services.Audio;

public class InFlightStemSeparationTrackerTests
{
    [Fact]
    public void NewTracker_HasZeroInFlight()
    {
        var t = new InFlightStemSeparationTracker();
        Assert.Equal(0, t.InFlight);
    }

    [Fact]
    public void BeginRun_IncrementsCount_DisposeDecrements()
    {
        var t = new InFlightStemSeparationTracker();
        var s = t.BeginRun();
        Assert.Equal(1, t.InFlight);
        s.Dispose();
        Assert.Equal(0, t.InFlight);
    }

    [Fact]
    public void MultipleConcurrentRuns_Tracked()
    {
        var t = new InFlightStemSeparationTracker();
        var a = t.BeginRun();
        var b = t.BeginRun();
        var c = t.BeginRun();
        Assert.Equal(3, t.InFlight);
        a.Dispose(); b.Dispose();
        Assert.Equal(1, t.InFlight);
        c.Dispose();
        Assert.Equal(0, t.InFlight);
    }

    [Fact]
    public void DoubleDispose_IsSafe_DoesNotDoubleCount()
    {
        var t = new InFlightStemSeparationTracker();
        var s = t.BeginRun();
        s.Dispose();
        s.Dispose();
        Assert.Equal(0, t.InFlight);
    }

    [Fact]
    public async Task WaitForCompletion_WhenIdle_CompletesImmediately()
    {
        var t = new InFlightStemSeparationTracker();
        var task = t.WaitForCompletionAsync(TimeSpan.FromSeconds(60));
        Assert.True(task.IsCompletedSuccessfully);
        await task;
    }

    [Fact]
    public async Task WaitForCompletion_ReturnsAfterAllRunsEnd()
    {
        var t = new InFlightStemSeparationTracker();
        var s = t.BeginRun();
        var wait = t.WaitForCompletionAsync(TimeSpan.FromSeconds(60));
        Assert.False(wait.IsCompleted);

        s.Dispose();
        await wait.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal(0, t.InFlight);
    }

    [Fact]
    public async Task WaitForCompletion_TimesOut_WhenRunNeverEnds()
    {
        var t = new InFlightStemSeparationTracker();
        using var _ = t.BeginRun();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await t.WaitForCompletionAsync(TimeSpan.FromMilliseconds(80));
        sw.Stop();
        // Returned by the timeout, not because the run completed.
        Assert.True(sw.ElapsedMilliseconds >= 60,
            $"Expected timeout ~80 ms, got {sw.ElapsedMilliseconds} ms");
        Assert.Equal(1, t.InFlight);
    }

    [Fact]
    public async Task SecondCycle_AfterIdle_StillWaits()
    {
        var t = new InFlightStemSeparationTracker();
        // First cycle.
        using (t.BeginRun()) { }
        Assert.True(t.WaitForCompletionAsync(TimeSpan.FromMilliseconds(50)).IsCompleted);

        // Second cycle resets the TCS so a new wait actually waits.
        var s = t.BeginRun();
        var wait = t.WaitForCompletionAsync(TimeSpan.FromSeconds(60));
        Assert.False(wait.IsCompleted);
        s.Dispose();
        await wait.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
    }
}
