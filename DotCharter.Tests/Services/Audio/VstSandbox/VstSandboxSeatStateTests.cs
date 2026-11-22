using DotCharter.Tone.Audio.Vst.Sandbox;

namespace DotCharter.Tests.Services.Audio.VstSandbox;

public sealed class VstSandboxSeatStateTests
{
    private static readonly TimeSpan Detector = TimeSpan.FromSeconds(60);
    private const string State = "cHJlc2V0LVM=";

    [Fact]
    public async Task AMoveCapturesBeforeUnloadRestoresAfterLoadAndRetainsTheStateForCrashReplay()
    {
        using var f = new VstSandboxPerSeatFixture();
        var a = f.Entry(0);
        var slot = a.Id.ToString();
        await f.Chain(a);
        f.Children[0].States[slot] = State;
        f.Traffic.Clear();

        await f.Chain(f.Entry(1, a.Id));

        var ops = f.Traffic.Where(item => item.EndsWith(":" + slot, StringComparison.Ordinal)).ToArray();
        Assert.Equal([$"0:getState:{slot}", $"0:unload:{slot}", $"1:load:{slot}", $"1:setState:{slot}"], ops);
        Assert.Equal(State, f.Children[1].States[slot]);
        Assert.Equal(State, f.Seat(1).Desired.PluginStates[slot]);
        Assert.False(f.Seat(0).Desired.PluginStates.ContainsKey(slot));

        f.Children[1].Crash();
        await f.Recovery(1).PendingRestart.WaitAsync(Detector, TestContext.Current.CancellationToken);
        Assert.Equal(State, f.Children[2].States[slot]);
    }

    [Fact]
    public async Task AFailedMoveCaptureKeepsTheLastCapturedStateAndLogsTheFallback()
    {
        using var f = new VstSandboxPerSeatFixture();
        var a = f.Entry(0);
        var slot = a.Id.ToString();
        await f.Chain(a);
        f.Children[0].States[slot] = State;
        await f.Recovery(0).CaptureStateAsync(f.Children[0]);
        f.Children[0].RejectGetState = true;

        await f.Chain(f.Entry(1, a.Id));

        Assert.Equal(State, f.Children[1].States[slot]);
        Assert.Equal(State, f.Seat(1).Desired.PluginStates[slot]);
        Assert.Contains(f.Logger.Entries, entry => entry.Message.Contains("last captured", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedRearmRecoversOnlyItsSeatAndReplaysItsSlots()
    {
        using var f = new VstSandboxPerSeatFixture();
        var a = f.Entry(0);
        var b = f.Entry(2);
        await f.Chain(a, b);
        f.Bridge.Arms.Clear();
        f.Bridge.EnabledSeats.Clear();
        f.Bridge.ArmResult = (player, call) => player != 2 || call != 2;
        var gate = VstSandboxPerSeatFixture.Field<SemaphoreSlim>(f.Service, "_vstGate");
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        try { VstSandboxPerSeatFixture.Invoke(f.Service, "RearmSandboxAfterEngineSwapLocked", f.Engine); }
        finally { gate.Release(); }
        await f.Recovery(2).PendingRestart.WaitAsync(Detector, TestContext.Current.CancellationToken);

        Assert.Equal(3, f.Children.Count);
        Assert.Equal(1, f.Children[1].Kills);
        Assert.Equal(1, f.Children[1].DisposeCalls);
        Assert.Contains(2, f.Bridge.EnabledSeats);
        Assert.Contains(f.Children[2].Requests, request => request.Op == "load" && request.Slot == b.Id.ToString());
        Assert.Same(f.Children[0], f.Seat(0).Host);
        Assert.Equal(0, f.Children[0].Stops);
        Assert.Single(f.Bridge.Arms, arm => arm.Player == 0 && arm.Enabled);
        Assert.Equal(0, VstSandboxPerSeatFixture.Field<VstSandboxRestartPolicy>(f.Recovery(0), "_policy").RecentCrashCount(TimeSpan.Zero));
    }
}
