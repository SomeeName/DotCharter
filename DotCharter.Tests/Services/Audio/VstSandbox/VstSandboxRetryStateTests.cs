namespace DotCharter.Tests.Services.Audio.VstSandbox;

public sealed class VstSandboxRetryStateTests
{
    private static readonly TimeSpan Detector = TimeSpan.FromSeconds(60);
    private const string State = "cHJlc2V0LVM=";

    [Fact]
    public async Task TheSameChainRetriesAfterAStartFailure()
    {
        using var f = new VstSandboxPerSeatFixture();
        var entry = f.Entry(2);
        var fail = true;
        f.ConfigureChild = child =>
        {
            if (!fail) return;
            fail = false;
            child.StartFailure = new TimeoutException("start failed once");
        };
        await Assert.ThrowsAsync<AggregateException>(() => f.Chain(entry));
        Assert.False(f.Service.IsGuitarVstSlotLoaded(entry.Id));

        await f.Chain(entry);

        Assert.Equal(2, f.Children.Count);
        Assert.True(f.Service.IsGuitarVstSlotLoaded(entry.Id));
        Assert.Contains(f.Children[1].Requests, request => request.Op == "load" && request.Slot == entry.Id.ToString());
    }

    [Fact]
    public async Task TheSameChainRetriesOnlyTheUnacknowledgedSlot()
    {
        using var f = new VstSandboxPerSeatFixture();
        var loaded = f.Entry(0);
        var rejected = f.Entry(0);
        f.ConfigureChild = child => child.BeforeReply = request => request.Op == "load" && request.Slot == rejected.Id.ToString() ? Task.FromException(new IOException("load was not acknowledged")) : Task.CompletedTask;
        await f.Chain(loaded, rejected);
        Assert.True(f.Service.IsGuitarVstSlotLoaded(loaded.Id));
        Assert.False(f.Service.IsGuitarVstSlotLoaded(rejected.Id));
        f.Children[0].BeforeReply = null;

        await f.Chain(loaded, rejected);

        Assert.True(f.Service.IsGuitarVstSlotLoaded(rejected.Id));
        Assert.Single(f.Children);
        Assert.Single(f.Children[0].Requests, request => request.Op == "load" && request.Slot == loaded.Id.ToString());
        Assert.Equal(2, f.Children[0].Requests.Count(request => request.Op == "load" && request.Slot == rejected.Id.ToString()));
    }

    [Fact]
    public async Task AHealthCaptureBetweenMoveAndDestinationLoadKeepsTheMovedState()
    {
        using var f = new VstSandboxPerSeatFixture();
        var moved = f.Entry(0);
        var existing = f.Entry(1);
        var earlierLoad = f.Entry(0);
        await f.Chain(moved, existing);
        f.Children[0].States[moved.Id.ToString()] = State;
        var captured = false;
        f.Children[0].BeforeReply = async request =>
        {
            if (request.Op != "load" || request.Slot != earlierLoad.Id.ToString()) return;
            Assert.False(f.Service.IsGuitarVstSlotLoaded(moved.Id));
            await CaptureByHealthTickAsync(f, 1);
            captured = true;
        };

        await f.Chain(earlierLoad, existing, f.Entry(1, moved.Id));

        Assert.True(captured);
        Assert.True(f.Children[1].States.TryGetValue(moved.Id.ToString(), out var restored));
        Assert.Equal(State, restored);
        Assert.Equal(State, f.Seat(1).Desired.PluginStates[moved.Id.ToString()]);
    }

    [Fact]
    public async Task AFailedDestinationLoadKeepsStateThroughHealthCaptureAndSameChainRetry()
    {
        using var f = new VstSandboxPerSeatFixture();
        var moved = f.Entry(0);
        var existing = f.Entry(1);
        await f.Chain(moved, existing);
        f.Children[0].States[moved.Id.ToString()] = State;
        f.Children[1].BeforeReply = request => request.Op == "load" && request.Slot == moved.Id.ToString() ? Task.FromException(new IOException("load was not acknowledged")) : Task.CompletedTask;
        var destination = f.Entry(1, moved.Id);
        await f.Chain(existing, destination);
        Assert.False(f.Service.IsGuitarVstSlotLoaded(moved.Id));

        await CaptureByHealthTickAsync(f, 1);

        Assert.True(f.Seat(1).Desired.PluginStates.TryGetValue(moved.Id.ToString(), out var retained));
        Assert.Equal(State, retained);
        f.Children[1].BeforeReply = null;
        await f.Chain(existing, destination);
        Assert.True(f.Service.IsGuitarVstSlotLoaded(moved.Id));
        Assert.Equal(State, f.Children[1].States[moved.Id.ToString()]);
        Assert.Single(f.Children[1].Requests, request => request.Op == "load" && request.Slot == existing.Id.ToString());
    }

    [Fact]
    public async Task ASourceCrashDuringUnloadMovesCapturedStateWithoutFailureOrReplayOnTheOldSeat()
    {
        using var f = new VstSandboxPerSeatFixture();
        var moved = f.Entry(0);
        var staying = f.Entry(0);
        var existing = f.Entry(1);
        await f.Chain(moved, staying, existing);
        f.Children[0].States[moved.Id.ToString()] = State;
        f.Children[0].BeforeReply = request =>
        {
            if (request.Op != "unload" || request.Slot != moved.Id.ToString()) return Task.CompletedTask;
            f.Children[0].Crash();
            return Task.FromException(new IOException("old owner exited during unload"));
        };
        var gate = VstSandboxPerSeatFixture.Field<SemaphoreSlim>(f.Service, "_vstGate");
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        Exception? error;
        try { error = await Record.ExceptionAsync(() => f.Chain(staying, existing, f.Entry(1, moved.Id))); }
        finally { gate.Release(); }
        await f.Recovery(0).PendingRestart.WaitAsync(Detector, TestContext.Current.CancellationToken);

        Assert.Null(error);
        Assert.True(f.Service.IsGuitarVstSlotLoaded(moved.Id));
        Assert.Equal(State, f.Children[1].States[moved.Id.ToString()]);
        Assert.DoesNotContain(f.Seat(0).Desired.Chain, entry => entry.Id == moved.Id);
        Assert.False(f.Seat(0).Slots.ContainsKey(moved.Id));
        Assert.DoesNotContain(f.Children.Skip(2).SelectMany(child => child.Requests), request => request.Op == "load" && request.Slot == moved.Id.ToString());
    }

    private static async Task CaptureByHealthTickAsync(VstSandboxPerSeatFixture f, int player)
    {
        var recovery = f.Recovery(player);
        recovery.StopHealthTimer();
        recovery.ResetHeartbeat();
        VstSandboxPerSeatFixture.SetField(recovery, "_healthTicks", 9);
        var beat = 0u;
        f.Bridge.Heartbeat = _ => ++beat;
        var before = recovery.PendingCapture;
        VstSandboxPerSeatFixture.Invoke(recovery, "HealthTick");
        VstSandboxPerSeatFixture.Invoke(recovery, "HealthTick");
        Assert.NotSame(before, recovery.PendingCapture);
        await recovery.PendingCapture.WaitAsync(Detector, TestContext.Current.CancellationToken);
    }
}
