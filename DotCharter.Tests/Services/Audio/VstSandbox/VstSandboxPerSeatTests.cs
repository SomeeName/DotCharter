using DotCharter.Tone.Audio.Vst.Sandbox;

namespace DotCharter.Tests.Services.Audio.VstSandbox;

public sealed class VstSandboxPerSeatTests
{
    private static readonly TimeSpan Detector = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Test01_TwoSeatsStartDistinctChildrenAndArmOnlyTheirSeats()
    {
        using var f = new VstSandboxPerSeatFixture();
        await f.Chain();
        Assert.Empty(f.Children);
        Assert.Empty(f.Bridge.Arms);
        await f.Chain(f.Entry(0), f.Entry(1));
        Assert.Equal(2, f.Children.Count);
        Assert.Equal([0, 1], f.Bridge.Arms.Select(a => a.Player));
        Assert.Equal(f.Children.Select(c => c.ShmName), f.Bridge.Arms.Select(a => a.Shm));
        Assert.Equal(f.Children.Select(c => c.RequestEventName), f.Bridge.Arms.Select(a => a.Request));
        Assert.Equal(f.Children.Select(c => c.ResponseEventName), f.Bridge.Arms.Select(a => a.Response));
    }

    [Fact]
    public async Task Test02_LastUnloadStopsAndDisarmsOnlyItsSeat()
    {
        using var f = new VstSandboxPerSeatFixture();
        var first = f.Entry(0);
        await f.Chain(first, f.Entry(1));
        f.Bridge.Arms.Clear();
        await f.Chain(first);
        Assert.Equal(2, f.Children.Count);
        Assert.Equal(0, f.Children[0].Stops);
        Assert.Equal(1, f.Children[1].Stops);
        var arm = Assert.Single(f.Bridge.Arms);
        Assert.Equal(1, arm.Player);
        Assert.False(arm.Enabled);
    }

    [Fact]
    public async Task Test03_InstrumentKeepsSeatZeroAliveUntilItsLastUnload()
    {
        using var f = new VstSandboxPerSeatFixture();
        await f.Instrument(true);
        var host = Assert.Single(f.Children);
        Assert.Equal(0, Assert.Single(f.Bridge.Arms).Player);
        await f.Chain(f.Entry(0));
        await f.Chain();
        Assert.Equal(0, host.Stops);
        await f.Instrument(false);
        Assert.Equal(1, host.Stops);
        Assert.False(f.Bridge.Arms[^1].Enabled);
    }

    [Fact]
    public async Task Test03_InstrumentUnloadDuringRecoveryTearsDownTheEmptySeat()
    {
        using var f = new VstSandboxPerSeatFixture();
        await f.Instrument(true);
        var gate = VstSandboxPerSeatFixture.Field<SemaphoreSlim>(f.Service, "_vstGate");
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            f.Bridge.Arms.Clear();
            f.Children[0].Crash();
            await f.Instrument(false);
        }
        finally { gate.Release(); }
        await f.Recovery(0).PendingRestart.WaitAsync(Detector, TestContext.Current.CancellationToken);

        var context = VstSandboxPerSeatFixture.Field<VstSandboxSeat[]>(f.Service, "_sandboxSeats")[0];
        Assert.Null(context.Host);
        Assert.Null(VstSandboxPerSeatFixture.Field<object?>(f.Recovery(0), "_healthTimer"));
        Assert.Equal(1, f.Children[0].Stops);
        var arm = Assert.Single(f.Bridge.Arms);
        Assert.Equal(0, arm.Player);
        Assert.False(arm.Enabled);
    }

    [Fact]
    public async Task Test04_ControlAndStateCallsReachOnlyTheSlotOwner()
    {
        using var f = new VstSandboxPerSeatFixture();
        var a = f.Entry(0);
        var b = f.Entry(1);
        await f.Chain(a, b);
        Assert.Equal(2, f.Children.Count);
        Assert.Equal(a.Id.ToString(), Assert.Single(f.Children[0].Requests, r => r.Op == "load").Slot);
        Assert.Equal(b.Id.ToString(), Assert.Single(f.Children[1].Requests, r => r.Op == "load").Slot);
        f.Children[0].Requests.Clear();
        f.Children[1].Requests.Clear();
        await f.Chain(a, f.Entry(1, b.Id, true));
        await f.Recovery(1).CaptureStateAsync(f.Children[1]);
        await f.Recovery(1).ReplayStateAsync(f.Children[1], new Dictionary<string, string> { [b.Id.ToString()] = "c3RhdGU=" });
        foreach (var child in f.Children)
            child.NextRequest = new TaskCompletionSource<VstSandboxRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Service.ToggleGuitarVstSlotEditor(b.Id, false);
        var editor = await Task.WhenAny(f.Children.Select(c => c.NextRequest!.Task))
            .WaitAsync(Detector, TestContext.Current.CancellationToken);
        Assert.Same(f.Children[1].NextRequest!.Task, editor);
        Assert.Equal("hideEditor", (await editor).Op);
        foreach (var child in f.Children)
            child.NextRequest = new TaskCompletionSource<VstSandboxRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Service.SetGuitarVstSlotBypass(b.Id, false);
        var bypass = await Task.WhenAny(f.Children.Select(c => c.NextRequest!.Task))
            .WaitAsync(Detector, TestContext.Current.CancellationToken);
        Assert.Same(f.Children[1].NextRequest!.Task, bypass);
        Assert.Equal("bypass", (await bypass).Op);
        await f.Chain(a);
        Assert.DoesNotContain(f.Children[0].Requests, r => r.Slot == b.Id.ToString());
        foreach (var op in new[] { "bypass", "hideEditor", "getState", "setState", "unload" })
            Assert.Contains(f.Children[1].Requests, r => r.Op == op && r.Slot == b.Id.ToString());
        Assert.Empty(f.Children[0].Requests);
    }

    [Fact]
    public async Task Test05_MoveAwaitsOldSeatUnloadBeforeNewSeatLoad()
    {
        using var f = new VstSandboxPerSeatFixture();
        var a = f.Entry(0);
        await f.Chain(a);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Children[0].UnloadReply = released;
        var move = f.Chain(f.Entry(1, a.Id));
        Assert.False(move.IsCompleted);
        Assert.Single(f.Children);
        released.SetResult();
        await move;
        Assert.Equal(2, f.Children.Count);
        Assert.Contains(f.Children[1].Requests, r => r.Op == "load" && r.Slot == a.Id.ToString());
        Assert.True(f.Traffic.IndexOf($"0:unload:{a.Id}") < f.Traffic.IndexOf($"1:load:{a.Id}"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Test05_UnacknowledgedUnloadPreservesOldOwnerAndPreventsDestinationLoad(bool throws)
    {
        using var f = new VstSandboxPerSeatFixture();
        var moving = f.Entry(0);
        var staying = f.Entry(0);
        await f.Chain(moving, staying);
        f.Children[0].RejectUnload = !throws;
        f.Children[0].ThrowOnUnload = throws;
        f.Notifications.Clear();

        var error = await Assert.ThrowsAsync<AggregateException>(() => f.Chain(f.Entry(1, moving.Id), staying));
        Assert.Contains(error.InnerExceptions, ex => ex is IOException);

        Assert.Single(f.Children);
        Assert.True(f.Service.IsGuitarVstSlotLoaded(moving.Id));
        Assert.Empty(f.Notifications);
        Assert.DoesNotContain(f.Children[0].Requests, r => r.Op == "load" && r.Seat == 1);
        f.Children[0].RejectUnload = false;
        f.Children[0].ThrowOnUnload = false;
        await f.Chain(f.Entry(1, moving.Id), staying);
        Assert.Equal(2, f.Children.Count);
        Assert.Contains(f.Children[1].Requests, r => r.Op == "load" && r.Slot == moving.Id.ToString());
    }

    [Fact]
    public async Task Test06_CrashReportsAndReplaysOnlyTheFailedSeat()
    {
        using var f = new VstSandboxPerSeatFixture();
        var a = f.Entry(0);
        var b = f.Entry(1);
        await f.Chain(a, b);
        Assert.Equal(2, f.Children.Count);
        await f.Recovery(1).CaptureStateAsync(f.Children[1]);
        f.Children[0].Requests.Clear();
        f.Notifications.Clear();
        f.Bridge.Arms.Clear();
        f.Children[1].Crash();
        await f.Recovery(1).PendingRestart.WaitAsync(Detector, TestContext.Current.CancellationToken);
        Assert.Equal([(b.Id, false), (b.Id, true)], f.Notifications);
        Assert.Empty(f.Children[0].Requests);
        Assert.Equal(0, f.Children[0].Stops);
        Assert.All(f.Bridge.Arms, arm => Assert.Equal(1, arm.Player));
        Assert.Contains(f.Children[2].Requests, r => r.Op == "setState" && r.Slot == b.Id.ToString());
        Assert.Equal(0, VstSandboxPerSeatFixture.Field<VstSandboxRestartPolicy>(f.Recovery(0), "_policy").RecentCrashCount(TimeSpan.Zero));
    }

    [Fact]
    public async Task Test06_FailedChainReplayKeepsTheRecoveredInstrumentAndCurrentHost()
    {
        using var f = new VstSandboxPerSeatFixture();
        await f.Chain(f.Entry(0));
        await f.Instrument(true);
        f.RejectChainLoads = true;
        var context = VstSandboxPerSeatFixture.Field<VstSandboxSeat[]>(f.Service, "_sandboxSeats")[0];
        var replay = (Task<(IVstSandboxHost Host, IReadOnlyDictionary<string, string> States)?>)
            VstSandboxPerSeatFixture.Invoke(f.Service, "RebuildSandboxLockedAsync", context)!;
        var result = await replay;

        Assert.NotNull(result);
        Assert.True(result.Value.Host.IsRunning);
        Assert.Same(context.Host, result.Value.Host);
        Assert.True(f.Service.IsInstrumentVstLoaded);
        Assert.Equal(2, f.Children.Count);
        Assert.Contains(f.Children[1].Requests, request => request.Op == "loadInstrument");
    }

    [Fact]
    public async Task Test07_ExhaustedSeatDisarmsWithoutTouchingOtherChildren()
    {
        using var f = new VstSandboxPerSeatFixture();
        var a = f.Entry(0);
        var b = f.Entry(1);
        await f.Chain(a, b);
        Assert.Equal(2, f.Children.Count);
        var policy = VstSandboxPerSeatFixture.Field<VstSandboxRestartPolicy>(f.Recovery(1), "_policy");
        for (var i = 0; i < policy.MaxCrashesPerWindow; i++) policy.OnCrash(TimeSpan.Zero);
        f.Notifications.Clear();
        f.Bridge.Arms.Clear();
        f.Children[0].Requests.Clear();
        f.Children[1].Crash();
        await f.Recovery(1).PendingRestart.WaitAsync(Detector, TestContext.Current.CancellationToken);
        Assert.Contains((b.Id, false), f.Notifications);
        Assert.DoesNotContain(f.Notifications, n => n.Id == a.Id);
        Assert.True(f.Children[0].IsRunning);
        Assert.Equal(0, f.Children[0].Stops);
        Assert.Empty(f.Children[0].Requests);
        Assert.Equal(1, Assert.Single(f.Bridge.Arms).Player);
        Assert.False(f.Bridge.Arms[0].Enabled);
    }

    [Fact]
    public async Task Test08_RebuildRearmsEverySurvivingChildOnItsSeat()
    {
        using var f = new VstSandboxPerSeatFixture();
        await f.Chain(f.Entry(0), f.Entry(2));
        f.Bridge.Arms.Clear();
        foreach (var child in f.Children)
            child.NextRequest = new TaskCompletionSource<VstSandboxRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        VstSandboxPerSeatFixture.Invoke(f.Service, "RearmSandboxAfterEngineSwapLocked", f.Engine);
        var prepared = await Task.WhenAll(f.Children.Select(c => c.NextRequest!.Task))
            .WaitAsync(Detector, TestContext.Current.CancellationToken);
        Assert.All(prepared, request => Assert.Equal("prepare", request.Op));
        Assert.Equal([0, 2], f.Bridge.Arms.Select(a => a.Player));
        Assert.Equal(f.Children.Select(c => c.ShmName), f.Bridge.Arms.Select(a => a.Shm));
    }

    [Fact]
    public async Task Test09_HealthMonitorReadsItsSeatsStatisticsAndRejectsStaleHost()
    {
        using var f = new VstSandboxPerSeatFixture();
        await f.Chain(f.Entry(0), f.Entry(1));
        VstSandboxPerSeatFixture.Invoke(f.Recovery(1), "HealthTick");
        Assert.Equal([1], f.Bridge.Stats);
        Assert.Equal(2, f.Children.Count);
        f.Children[1].Crash();
        await f.Recovery(1).PendingRestart.WaitAsync(Detector, TestContext.Current.CancellationToken);
        var count = f.Children.Count;
        f.Recovery(1).OnCrashed(f.Children[1]);
        await f.Recovery(1).PendingRestart.WaitAsync(Detector, TestContext.Current.CancellationToken);
        Assert.Equal(count, f.Children.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Test10_ShutdownAndDisposeStopEveryChildAndDisarmEverySeat(bool dispose)
    {
        using var f = new VstSandboxPerSeatFixture();
        await f.Chain(f.Entry(0), f.Entry(2));
        Assert.Equal(2, f.Children.Count);
        f.Bridge.Arms.Clear();
        if (dispose)
        {
            f.Service.Dispose();
        }
        else
        {
            VstSandboxPerSeatFixture.Invoke(f.Service, "ApplySandboxMode", false);
            await Task.WhenAll(f.Children.Select(c => c.Stopped.Task)).WaitAsync(Detector, TestContext.Current.CancellationToken);
            var gate = VstSandboxPerSeatFixture.Field<SemaphoreSlim>(f.Service, "_vstGate");
            await gate.WaitAsync(TestContext.Current.CancellationToken);
            gate.Release();
        }
        Assert.All(f.Children, c => Assert.Equal(1, c.Stops));
        Assert.Equal([0, 1, 2, 3], f.Bridge.Arms.Select(a => a.Player));
        Assert.All(f.Bridge.Arms, a => Assert.False(a.Enabled));
    }
}
