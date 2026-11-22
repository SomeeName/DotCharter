using DotCharter.Tone.Audio.Vst.Sandbox;

namespace DotCharter.Tests.Services.Audio.VstSandbox;

public sealed class VstSandboxSeatIsolationTests
{
    private static readonly TimeSpan Detector = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task AStartFailureDoesNotPreventLaterLoadsOrEmptySeatTeardown()
    {
        using var f = new VstSandboxPerSeatFixture();
        var a = f.Entry(0);
        await f.Chain(a, f.Entry(3));
        f.ConfigureChild = child =>
        {
            if (child.Number == 2) child.StartFailure = new TimeoutException("seat 1 start");
        };
        var b = f.Entry(1);
        var c = f.Entry(2);
        var error = await Assert.ThrowsAsync<AggregateException>(() => f.Chain(a, b, c));

        Assert.Contains(error.InnerExceptions, ex => ex is TimeoutException);
        Assert.Contains(f.Children.SelectMany(child => child.Requests), request => request.Op == "load" && request.Slot == c.Id.ToString());
        Assert.Equal(c.Id, Assert.Single(f.Seat(2).Desired.Chain).Id);
        Assert.False(f.Service.IsGuitarVstSlotLoaded(b.Id));
        Assert.Equal(1, f.Children[1].Stops);
        Assert.Contains(f.Bridge.Arms, arm => arm.Player == 3 && !arm.Enabled);
        Assert.Equal(1, f.Children[2].DisposeCalls);

        await f.Chain(a, b, c);
        Assert.True(f.Service.IsGuitarVstSlotLoaded(b.Id));
        Assert.Single(f.Children[3].Requests, request => request.Op == "load" && request.Slot == c.Id.ToString());
    }

    [Fact]
    public async Task ARejectedUnloadDoesNotPreventAnotherSeatsEdit()
    {
        using var f = new VstSandboxPerSeatFixture();
        var a = f.Entry(0);
        var b = f.Entry(2);
        var c = f.Entry(2);
        await f.Chain(a, b);
        f.Children[0].RejectUnload = true;

        var error = await Assert.ThrowsAsync<AggregateException>(() => f.Chain(b, c));

        Assert.Contains(error.InnerExceptions, ex => ex is IOException);
        Assert.True(f.Service.IsGuitarVstSlotLoaded(a.Id));
        Assert.Contains(f.Children[1].Requests, request => request.Op == "load" && request.Slot == c.Id.ToString());
    }

    [Fact]
    public async Task EditingSeatZeroDoesNotRetryOrResetAGivenUpSeat()
    {
        using var f = new VstSandboxPerSeatFixture();
        var a = f.Entry(0);
        var b = f.Entry(1);
        await f.Chain(a, b);
        var policy = VstSandboxPerSeatFixture.Field<VstSandboxRestartPolicy>(f.Recovery(1), "_policy");
        for (var i = 0; i < policy.MaxCrashesPerWindow; i++) policy.OnCrash(TimeSpan.Zero);
        f.Children[1].Crash();
        await f.Recovery(1).PendingRestart.WaitAsync(Detector, TestContext.Current.CancellationToken);
        var crashes = policy.RecentCrashCount(TimeSpan.Zero);
        f.Bridge.Arms.Clear();

        await f.Chain(f.Entry(0, a.Id, true), b);

        Assert.Equal(2, f.Children.Count);
        Assert.True(f.Seat(1).GaveUp);
        Assert.Null(f.Seat(1).Host);
        Assert.Equal(crashes, policy.RecentCrashCount(TimeSpan.Zero));
        Assert.DoesNotContain(f.Bridge.Arms, arm => arm.Player == 1);
    }

    [Fact]
    public async Task EditingAnotherSeatLeavesOnePendingRestartAndDisposesTheDeadHostOnce()
    {
        using var f = new VstSandboxPerSeatFixture();
        var a = f.Entry(0);
        var b = f.Entry(1);
        await f.Chain(a, b);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.RestartDelay = _ => { entered.TrySetResult(); return release.Task; };
        VstSandboxPerSeatFixture.Field<VstSandboxRestartPolicy>(f.Recovery(1), "_policy").OnCrash(TimeSpan.Zero);
        f.Children[1].Crash();
        var pending = f.Recovery(1).PendingRestart;
        try
        {
            await entered.Task.WaitAsync(Detector, TestContext.Current.CancellationToken);
            await f.Chain(f.Entry(0, a.Id, true), b);
            Assert.Equal(2, f.Children.Count);
            Assert.Equal(0, f.Children[1].DisposeCalls);
        }
        finally { release.TrySetResult(); }
        await pending.WaitAsync(Detector, TestContext.Current.CancellationToken);

        Assert.Equal(3, f.Children.Count);
        Assert.Equal(1, f.Children[1].DisposeCalls);
        Assert.Same(f.Children[2], f.Seat(1).Host);
        Assert.Equal(0, f.Children[0].Stops);
    }

    [Fact]
    public async Task AnObsoleteRestartCannotStopAReplacementCreatedByAnExplicitEdit()
    {
        using var f = new VstSandboxPerSeatFixture();
        var b = f.Entry(1);
        await f.Chain(b);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.RestartDelay = _ => { entered.TrySetResult(); return release.Task; };
        VstSandboxPerSeatFixture.Field<VstSandboxRestartPolicy>(f.Recovery(1), "_policy").OnCrash(TimeSpan.Zero);
        f.Children[0].Crash();
        var pending = f.Recovery(1).PendingRestart;
        try
        {
            await entered.Task.WaitAsync(Detector, TestContext.Current.CancellationToken);
            await f.Chain(f.Entry(1, b.Id, true));
            Assert.Equal(2, f.Children.Count);
        }
        finally { release.TrySetResult(); }
        await pending.WaitAsync(Detector, TestContext.Current.CancellationToken);

        Assert.Equal(1, f.Children[0].DisposeCalls);
        Assert.Equal(0, f.Children[1].Stops);
        Assert.True(f.Children[1].IsRunning);
        Assert.Equal(2, f.Children.Count);
    }
}
