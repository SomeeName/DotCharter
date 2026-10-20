using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     The incremental board sync (0.59). <c>PedalboardViewModel.Sync</c> used
///     to be a full teardown — <c>Nodes.Clear()</c> plus a fresh
///     <c>PedalNodeViewModel</c> per native node — which made every add,
///     remove and preset apply re-inflate every unit card in BOTH rig hosts
///     (measured ~280 ms of UI-thread work per gesture on a 7-module board).
///     These pin the diff: a surviving native node KEEPS its view model (and
///     therefore its realized card), with its state mirrored from the service
///     rather than re-pushed to it. The one deliberate exception — a seat
///     repoint rebuilds everything — is pinned here too.
/// </summary>
public sealed class PedalboardIncrementalSyncTests
{
    private static PedalboardViewModel NewVm(out FakeDspChainService dsp)
    {
        dsp = new FakeDspChainService();
        return TestRigBuilders.Pedalboard(dsp, new FakeDspTonePresetService(), FakeLocalization.English());
    }

    // ── tone switch: shared modules keep their view models ───────────────────

    [Fact]
    public void ApplyingAPresetOverAnother_ReusesTheSurvivingNodesAndMirrorsTheirParams()
    {
        var vm = NewVm(out var dsp);
        var applier = new DcRigApplier(dsp);

        applier.Apply(new DcRigPreset(new List<DcRigModule>
        {
            new("overdrive", new Dictionary<string, float> { ["drive"] = 0.2f }, false),
            new("amp", new Dictionary<string, float> { ["gain"] = 0.3f }, false),
            new("cab", new Dictionary<string, float> { ["level"] = 0.8f }, false),
            new("octaver", new Dictionary<string, float> { ["sub"] = 0.5f }, false),
        }));
        vm.Sync();
        var before = vm.Nodes.ToDictionary(n => n.NodeId);

        // B shares overdrive/amp/cab (new params), drops the octaver, adds a delay.
        applier.Apply(new DcRigPreset(new List<DcRigModule>
        {
            new("overdrive", new Dictionary<string, float> { ["drive"] = 0.9f }, true),
            new("amp", new Dictionary<string, float> { ["gain"] = 0.7f }, false),
            new("cab", new Dictionary<string, float> { ["level"] = 0.4f }, false),
            new("delay", new Dictionary<string, float> { ["time"] = 500f }, false),
        }));
        var paramPushes = dsp.ParamSets.Count;
        var bypassPushes = dsp.BypassSets.Count;
        vm.Sync();

        // Surviving native nodes kept their VIEW MODEL INSTANCES…
        Assert.Equal(4, vm.Nodes.Count);
        foreach (var node in vm.Nodes.Where(n => before.ContainsKey(n.NodeId)))
            Assert.Same(before[node.NodeId], node);
        Assert.Equal(3, vm.Nodes.Count(n => before.ContainsKey(n.NodeId)));

        // …with the NEW tone mirrored onto them (knob, bypass)…
        var od = vm.Nodes.Single(n => n.ModuleTypeId == "overdrive");
        Assert.Equal(0.9f, od.Params.Single(p => p.Id == "drive").Value);
        Assert.True(od.Bypassed);

        // …and the mirror pushed NOTHING back to native.
        Assert.Equal(paramPushes, dsp.ParamSets.Count);
        Assert.Equal(bypassPushes, dsp.BypassSets.Count);
    }

    [Fact]
    public void AddingAPedal_CreatesExactlyOneNewViewModel()
    {
        var vm = NewVm(out _);
        vm.AddGear(vm.AvailableModules.First(m => m.Id == "overdrive"));
        vm.AddGear(vm.AvailableModules.First(m => m.Id == "amp"));
        var before = vm.Nodes.ToList();

        vm.AddGear(vm.AvailableModules.First(m => m.Id == "reverb"));

        Assert.Equal(3, vm.Nodes.Count);
        Assert.Same(before[0], vm.Nodes[0]);
        Assert.Same(before[1], vm.Nodes[1]);
        Assert.DoesNotContain(vm.Nodes[2], before);
    }

    [Fact]
    public void RemovingAPedal_LeavesTheOtherViewModelsUntouched()
    {
        var vm = NewVm(out _);
        vm.AddGear(vm.AvailableModules.First(m => m.Id == "overdrive"));
        var middle = vm.AddGear(vm.AvailableModules.First(m => m.Id == "amp"))!;
        vm.AddGear(vm.AvailableModules.First(m => m.Id == "cab"));
        var first = vm.Nodes[0];
        var last = vm.Nodes[2];

        vm.RemoveCommand.Execute(middle);

        Assert.Equal(2, vm.Nodes.Count);
        Assert.Same(first, vm.Nodes[0]);
        Assert.Same(last, vm.Nodes[1]);
    }

    /// <summary>A reorder behind the VM's back (a preset whose modules moved)
    /// is mirrored with Moves, not rebuilds.</summary>
    [Fact]
    public void AReorderedChain_KeepsEveryViewModel()
    {
        var vm = NewVm(out var dsp);
        var a = vm.AddGear(vm.AvailableModules.First(m => m.Id == "overdrive"))!;
        var b = vm.AddGear(vm.AvailableModules.First(m => m.Id == "amp"))!;
        var c = vm.AddGear(vm.AvailableModules.First(m => m.Id == "cab"))!;

        Assert.True(dsp.MoveNode(c.NodeId, 0));
        vm.Sync();

        Assert.Equal([c, a, b], vm.Nodes.ToArray());
    }

    /// <summary>A neural amp whose CAPTURE changed must rebuild its card — the
    /// skin (amp head vs pedal chassis) and the faceplate identity are resolved
    /// at construction from the loaded file.</summary>
    [Fact]
    public void ANeuralAmpWhoseCaptureChanged_IsRebuilt_WhileItsNeighboursSurvive()
    {
        var catalog = FakeDspChainService.DefaultCatalog()
            .Append(new DspModuleType("namamp", "NAM Amp", [])
            { StringParams = [new DspStringParamInfo("modelPath", "NAM Model", "*.nam;*.aidax")] })
            .ToList();
        var dsp = new FakeDspChainService(catalog);
        var vm = TestRigBuilders.Pedalboard(dsp, new FakeDspTonePresetService(), FakeLocalization.English());
        var od = vm.AddGear(vm.AvailableModules.First(m => m.Id == "overdrive"))!;
        var nam = vm.AddGear(vm.AvailableModules.First(m => m.Id == "namamp"))!;

        Assert.True(dsp.SetNodeString(nam.NodeId, "modelPath", @"C:\captures\plexi.nam"));
        vm.Sync();

        Assert.Same(od, vm.Nodes[0]);
        Assert.NotSame(nam, vm.Nodes[1]);
        Assert.Equal(@"C:\captures\plexi.nam", vm.Nodes[1].StringParams.Single().Value);
    }

    /// <summary>An engine rebuild can hand out the SAME node id for a different
    /// module type — that is never the same pedal, so the card rebuilds.</summary>
    [Fact]
    public void ASameIdNodeOfAnotherType_IsRebuiltNotMirrored()
    {
        var vm = NewVm(out var dsp);
        var od = vm.AddGear(vm.AvailableModules.First(m => m.Id == "overdrive"))!;

        // Simulate the id being re-issued to a different module: a fresh fake
        // chain re-numbers from 1, exactly like a rebuilt engine's chain.
        Assert.True(dsp.RemoveNode(od.NodeId));
        var replacement = dsp.AddNode("delay")!;
        Assert.NotEqual(od.NodeId, replacement.NodeId); // the fake never re-issues…
        Assert.True(dsp.RemoveNode(replacement.NodeId));

        // …so drive CanMirror directly for the id-collision case.
        Assert.False(od.CanMirror(new DspNode(od.NodeId, "delay", false,
            new Dictionary<string, float>())));
    }

    // ── the seat repoint stays a FULL rebuild ────────────────────────────────

    private sealed class TwoChains : IPlayerDspChains
    {
        private readonly IDspChainService[] _all =
        [
            new FakeDspChainService(), new FakeDspChainService()
        ];

        public int MaxPlayers => _all.Length;
        public IReadOnlyList<IDspChainService> All => _all;
        public IDspChainService For(int player) => _all[Math.Clamp(player, 0, _all.Length - 1)];
        public void AttachEngineToAll(nint engine) { }
    }

    /// <summary>
    ///     The two per-seat fakes both number their nodes from 1, exactly like
    ///     two seats' native chains after an engine rebuild — so an id-keyed
    ///     diff across the repoint would false-match seat 1's amp onto seat 0's
    ///     card, and the reused card (which holds seat 0's chain) would go on
    ///     editing the wrong player's tone. The repoint therefore rebuilds
    ///     every view model, deliberately.
    /// </summary>
    [Fact]
    public void ASeatRepoint_RebuildsEveryViewModel()
    {
        var chains = new TwoChains();
        var rig = TestRigBuilders.Pedalboard(chains.For(0), new FakeDspTonePresetService(), FakeLocalization.English(),
            chains: chains);
        rig.SetSeats([new RigSeatOption(0, "Ada"), new RigSeatOption(1, "Bo")]);

        rig.AddGear(rig.AvailableModules.First(m => m.Id == "amp"));
        chains.For(1).AddNode("amp"); // seat 1's own amp — SAME node id as seat 0's
        var seat0Amp = rig.Nodes.Single();

        rig.SelectedSeat = rig.Seats[1];

        Assert.Equal(seat0Amp.NodeId, rig.Nodes.Single().NodeId);
        Assert.NotSame(seat0Amp, rig.Nodes.Single());
    }
}
