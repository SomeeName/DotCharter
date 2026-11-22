using DotCharter.Tone.Audio.Vst;
using DotCharter.Tone.Audio.Vst.Sandbox;

namespace DotCharter.Tests.Services.Audio.VstSandbox;

/// <summary>
/// #387: a sandbox restart replayed the chain but never the plugin state, so presets and
/// knobs came back at defaults. The fake child below keeps a blob per slot, like the real
/// <c>getState</c>/<c>setState</c> ops.
/// </summary>
public sealed class VstSandboxStateRestoreTests
{
    [Fact]
    public async Task CaptureAsksEverySlotAndKeepsOnlyTheAnswers()
    {
        var child = new FakeStateChannel();
        child.Blobs["slot-a"] = "QUJD";
        child.Blobs[VstSandboxProtocol.InstrumentSlotId] = "REVG";
        var failures = new List<string>();

        var captured = await VstSandboxStateSync.CaptureAsync(
            child, ["slot-a", VstSandboxProtocol.InstrumentSlotId, "slot-gone"], (slot, _) => failures.Add(slot));

        Assert.Equal("QUJD", captured["slot-a"]);
        Assert.Equal("REVG", captured[VstSandboxProtocol.InstrumentSlotId]);
        Assert.False(captured.ContainsKey("slot-gone"));
        Assert.Equal(["slot-gone"], failures);
    }

    [Fact]
    public async Task ReplaySendsTheCapturedBlobToEachReloadedSlotAndTheInstrument()
    {
        var states = new Dictionary<string, string>
        {
            ["slot-a"] = "QUJD",
            [VstSandboxProtocol.InstrumentSlotId] = "REVG",
            ["slot-unloaded"] = "R0hJ",
        };
        var child = new FakeStateChannel();

        var restored = await VstSandboxStateSync.ReplayAsync(
            child, states, ["slot-a", VstSandboxProtocol.InstrumentSlotId, "slot-no-blob"], (_, _) => { });

        Assert.Equal(2, restored);
        Assert.Equal("QUJD", child.Blobs["slot-a"]);
        Assert.Equal("REVG", child.Blobs[VstSandboxProtocol.InstrumentSlotId]);
        Assert.DoesNotContain(child.Requests, r => r.Slot is "slot-unloaded" or "slot-no-blob");
        Assert.All(child.Requests, r => Assert.Equal("setState", r.Op));
    }

    [Fact]
    public async Task ARejectedSetStateIsReportedAndNotCounted()
    {
        var child = new FakeStateChannel { RejectSetState = true };
        var failures = new List<string>();

        var restored = await VstSandboxStateSync.ReplayAsync(
            child, new Dictionary<string, string> { ["slot-a"] = "QUJD" }, ["slot-a"], (slot, _) => failures.Add(slot));

        Assert.Equal(0, restored);
        Assert.Equal(["slot-a"], failures);
    }

    [Fact]
    public async Task StateSurvivesACaptureThenCrashThenReplayRoundTrip()
    {
        var before = new FakeStateChannel();
        before.Blobs["slot-a"] = "dHdlYWtlZA==";
        var captured = await VstSandboxStateSync.CaptureAsync(before, ["slot-a"], (_, _) => { });

        var desired = new VstSandboxChainState();
        foreach (var (slot, state) in captured) desired.PluginStates[slot] = state;
        var after = new FakeStateChannel(); // the restarted child: defaults
        await VstSandboxStateSync.ReplayAsync(after, desired.Clone().PluginStates, ["slot-a"], (_, _) => { });

        Assert.Equal("dHdlYWtlZA==", after.Blobs["slot-a"]);
    }

    [Fact]
    public async Task TheRecoveryStoresDesiredLiveSlotsAndDropsRemovedOnesWhenItCapturesState()
    {
        var (recovery, desired, live) = NewRecovery();
        desired.PluginStates["slot-stale"] = "R0hJ";
        var id = Guid.NewGuid();
        var slot = id.ToString();
        desired.Chain.Add(new VstChainEntry { Id = id });
        var child = new FakeStateChannel();
        child.Blobs[slot] = "QUJD";
        live.Add(slot);

        await recovery.CaptureStateAsync(child);

        Assert.Equal("QUJD", desired.PluginStates[slot]);
        Assert.False(desired.PluginStates.ContainsKey("slot-stale"));
    }

    [Fact]
    public async Task TheRecoveryReplaysOnlyIntoSlotsThatAreLoadedAgain()
    {
        var (recovery, _, live) = NewRecovery();
        live.Add("slot-a");
        var child = new FakeStateChannel();

        await recovery.ReplayStateAsync(child, new Dictionary<string, string> { ["slot-a"] = "QUJD", ["slot-b"] = "REVG" });

        Assert.Equal("QUJD", child.Blobs["slot-a"]);
        Assert.False(child.Blobs.ContainsKey("slot-b"));
    }

    private static (VstSandboxRecovery Recovery, VstSandboxChainState Desired, List<string> Live) NewRecovery()
    {
        var desired = new VstSandboxChainState();
        var live = new List<string>();
        var gate = new object();
        var recovery = new VstSandboxRecovery(gate, desired, new SemaphoreSlim(1, 1), null, FakeLocalization.KeysOnly(), null,
            new VstSandboxRecoveryLink
            {
                IsActive = () => true,
                IsCurrentHost = _ => true,
                MarkGaveUp = () => { },
                NotifySlotsUnloaded = () => { },
                LiveSlotIds = () => [.. live],
                CurrentHost = () => null,
                ReadHeartbeat = () => 0,
                ShutdownLocked = () => Task.CompletedTask,
                RebuildLocked = () => Task.FromResult<(IVstSandboxHost, IReadOnlyDictionary<string, string>)?>(null),
            });
        return (recovery, desired, live);
    }

    private sealed class FakeStateChannel : IVstSandboxChannel
    {
        public Dictionary<string, string> Blobs { get; } = [];
        public List<VstSandboxRequest> Requests { get; } = [];
        public bool RejectSetState { get; init; }

        public bool IsRunning => true;
        public int? ChildProcessId => null;

        public Task<VstSandboxMessage> SendAsync(VstSandboxRequest request, TimeSpan? timeout = null)
        {
            Requests.Add(request);
            var slot = request.Slot ?? string.Empty;
            switch (request.Op)
            {
                case "getState":
                    return Task.FromResult(Blobs.TryGetValue(slot, out var s)
                        ? new VstSandboxMessage { Ok = true, State = s }
                        : new VstSandboxMessage { Ok = false, Error = "no such slot" });
                case "setState" when !RejectSetState:
                    Blobs[slot] = request.State ?? string.Empty;
                    return Task.FromResult(new VstSandboxMessage { Ok = true });
                default:
                    return Task.FromResult(new VstSandboxMessage { Ok = false, Error = "rejected" });
            }
        }

        public Task StopAsync(TimeSpan? gracePeriod = null) => Task.CompletedTask;

        public void Kill() { }
    }
}
