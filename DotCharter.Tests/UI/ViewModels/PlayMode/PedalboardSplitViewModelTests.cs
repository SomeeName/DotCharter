using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
/// VM tests for the ABI-v23 split/parallel board surface against the in-memory
/// fake chain: the BoardColumns projection, the auto-paired merger, drop-to-row
/// (move + set-branch), the auto-merge flag, and branch capture in BuildCurrentRig.
/// No Avalonia instance.
/// </summary>
public sealed class PedalboardSplitViewModelTests
{
    // A catalogue with the routing nodes so the split flow can be exercised.
    private static IReadOnlyList<DspModuleType> SplitCatalog() =>
    [
        new DspModuleType("overdrive", "Overdrive", [new DspParamInfo("drive", "Drive", 0f, 1f, 0.5f)]),
        new DspModuleType("distortion", "Distortion", [new DspParamInfo("gain", "Gain", 0f, 1f, 0.6f)]),
        new DspModuleType("chorus", "Chorus", [new DspParamInfo("mix", "Mix", 0f, 1f, 0.5f)]),
        new DspModuleType("reverb", "Reverb", [new DspParamInfo("mix", "Mix", 0f, 1f, 0.3f)]),
        new DspModuleType("splitter", "Splitter", [new DspParamInfo("enabledBranches", "Branches", 2f, 3f, 2f)]),
        new DspModuleType("merger", "Merger", [new DspParamInfo("mode", "Mode", 0f, 1f, 0f)]),
    ];

    private static PedalboardViewModel NewVm(out FakeDspChainService dsp)
    {
        dsp = new FakeDspChainService(SplitCatalog());
        return TestRigBuilders.Pedalboard(dsp, new FakeDspTonePresetService(), FakeLocalization.English());
    }

    private static PedalNodeViewModel Add(PedalboardViewModel vm, string id)
        => vm.AddGear(vm.AvailableModules.First(m => m.Id == id))!;

    [Fact]
    public void AddingSplitter_auto_pairs_a_merger_right_after_it()
    {
        var vm = NewVm(out var dsp);
        Add(vm, "overdrive");

        var splitter = Add(vm, "splitter");

        // splitter + merger both added; merger sits immediately after the splitter.
        Assert.Equal(new[] { "overdrive", "splitter", "merger" },
            vm.Nodes.Select(n => n.ModuleTypeId).ToArray());
        Assert.True(splitter.IsSplitter);
        Assert.Equal(new[] { "overdrive", "splitter", "merger" },
            dsp.GetNodes().Select(n => n.ModuleTypeId).ToArray());
    }

    [Fact]
    public void BoardColumns_projects_linear_chain_as_pedal_columns()
    {
        var vm = NewVm(out _);
        Add(vm, "overdrive");
        Add(vm, "reverb");

        Assert.False(vm.HasSplitRegion);
        Assert.All(vm.BoardColumns, c => Assert.IsType<PedalColumnViewModel>(c));
        Assert.Equal(2, vm.BoardColumns.Count);
    }

    [Fact]
    public void BoardColumns_projects_a_region_between_the_junctions()
    {
        var vm = NewVm(out _);
        Add(vm, "overdrive");
        Add(vm, "splitter"); // auto-adds merger after it
        // Put a pedal into the region body (between splitter and merger).
        var dist = Add(vm, "distortion");
        // dist landed at chain end (after merger) — move it before the merger.
        var mergerIndex = vm.Nodes.ToList().FindIndex(n => n.IsMerger);
        vm.MoveTo(dist, mergerIndex);

        // columns: overdrive | splitter(junction) | region | merger(junction)
        var kinds = vm.BoardColumns.Select(c => c is RegionColumnViewModel ? "region"
            : ((PedalColumnViewModel)c).IsJunction ? "junction" : "pedal").ToArray();
        Assert.Equal(new[] { "pedal", "junction", "region", "junction" }, kinds);
        Assert.True(vm.HasSplitRegion);

        var region = vm.BoardColumns.OfType<RegionColumnViewModel>().Single();
        Assert.Equal(2, region.Rows.Count);
        // dist defaults to branch 0 → row 1 (top).
        Assert.Contains(region.Rows[0].Pedals, p => p.ModuleTypeId == "distortion");
    }

    [Fact]
    public void DropToRow_moves_and_sets_the_branch_atomically()
    {
        var vm = NewVm(out var dsp);
        Add(vm, "splitter"); // splitter @0, merger @1
        var dist = Add(vm, "distortion"); // @2 (after merger)

        // Drop it into the region (index 1, before the merger) on branch 2.
        Assert.True(vm.DropToRow(dist, 1, branch: 2));

        Assert.Equal(2, dist.Branch);
        Assert.Equal(2, dsp.GetNodeBranch(dist.NodeId));
        // it now sits between splitter and merger
        Assert.Equal(new[] { "splitter", "distortion", "merger" },
            vm.Nodes.Select(n => n.ModuleTypeId).ToArray());

        var region = vm.BoardColumns.OfType<RegionColumnViewModel>().Single();
        Assert.Contains(region.Rows[1].Pedals, p => ReferenceEquals(p, dist)); // branch 2 = row index 1
        Assert.Empty(region.Rows[0].Pedals);
    }

    [Fact]
    public void BypassingSplitter_collapses_the_region()
    {
        var vm = NewVm(out _);
        Add(vm, "splitter");
        var dist = Add(vm, "distortion");
        vm.MoveTo(dist, vm.Nodes.ToList().FindIndex(n => n.IsMerger)); // into region
        Assert.True(vm.HasSplitRegion);

        var splitter = vm.Nodes.First(n => n.IsSplitter);
        splitter.Bypassed = true; // fires StructureChanged → re-lay

        Assert.False(vm.HasSplitRegion);
        Assert.DoesNotContain(vm.BoardColumns, c => c is RegionColumnViewModel);
    }

    [Fact]
    public void AutoMergeActive_when_region_has_no_merger()
    {
        var vm = NewVm(out _);
        var splitter = Add(vm, "splitter"); // adds merger too
        var merger = vm.Nodes.First(n => n.IsMerger);
        // Remove the merger → the region auto-merges at the chain end.
        vm.RemoveCommand.Execute(merger);
        var dist = Add(vm, "distortion"); // into the (now open) region body

        Assert.True(vm.HasSplitRegion);
        Assert.True(vm.AutoMergeActive);
    }

    [Fact]
    public void BuildCurrentRig_captures_the_branch_tag()
    {
        var vm = NewVm(out _);
        Add(vm, "splitter");
        var dist = Add(vm, "distortion");
        vm.DropToRow(dist, 1, branch: 2);

        var rig = vm.BuildCurrentRig();
        var distMod = rig.Modules.Single(m => m.ModuleTypeId == "distortion");
        Assert.Equal(2, distMod.Branch);
        // linear modules stay branch 0.
        Assert.Equal(0, rig.Modules.Single(m => m.ModuleTypeId == "splitter").Branch);
    }

    // ── routing-mode toolbar (the chain builder's left rail) ───────────────────

    [Fact]
    public void RoutingMode_is_serial_on_a_plain_chain()
    {
        var vm = NewVm(out _);
        Add(vm, "overdrive");

        Assert.Equal(1, vm.RoutingBranches);
        Assert.True(vm.IsRoutingSerial);
        Assert.False(vm.IsRoutingSplit2);
        Assert.False(vm.IsRoutingSplit3);
    }

    [Fact]
    public void RoutingSplit2_wraps_the_SELECTED_pedal_in_a_new_region()
    {
        var vm = NewVm(out var dsp);
        Add(vm, "overdrive");
        Add(vm, "distortion");
        Add(vm, "reverb");
        vm.SelectNodeAt(1); // the distortion

        vm.RoutingSplit2Command.Execute(null);

        // The splitter lands ahead of the selection and the merger right after it.
        Assert.Equal(new[] { "overdrive", "splitter", "distortion", "merger", "reverb" },
            vm.Nodes.Select(n => n.ModuleTypeId).ToArray());
        Assert.Equal(new[] { "overdrive", "splitter", "distortion", "merger", "reverb" },
            dsp.GetNodes().Select(n => n.ModuleTypeId).ToArray());

        Assert.True(vm.HasSplitRegion);
        Assert.Equal(2, vm.RoutingBranches);
        Assert.True(vm.IsRoutingSplit2);
    }

    [Fact]
    public void RoutingSplit3_rewidths_an_existing_region_without_adding_junctions()
    {
        var vm = NewVm(out _);
        Add(vm, "splitter"); // auto-pairs a merger
        Assert.Equal(2, vm.RoutingBranches);

        vm.RoutingSplit3Command.Execute(null);

        Assert.Equal(new[] { "splitter", "merger" },
            vm.Nodes.Select(n => n.ModuleTypeId).ToArray()); // no new junctions
        Assert.Equal(3, vm.RoutingBranches);
        Assert.True(vm.IsRoutingSplit3);
        Assert.Equal(3, vm.Nodes[0].EnabledBranches);
    }

    [Fact]
    public void RoutingSplit_with_no_selection_appends_the_pair_at_the_chain_end()
    {
        var vm = NewVm(out _);
        Add(vm, "overdrive");

        vm.RoutingSplit2Command.Execute(null);

        Assert.Equal(new[] { "overdrive", "splitter", "merger" },
            vm.Nodes.Select(n => n.ModuleTypeId).ToArray());
        Assert.True(vm.IsRoutingSplit2);
    }

    [Fact]
    public void RoutingSerial_removes_the_junction_pair_and_keeps_the_body_pedals()
    {
        var vm = NewVm(out var dsp);
        Add(vm, "overdrive");
        Add(vm, "distortion");
        vm.SelectNodeAt(1);
        vm.RoutingSplit2Command.Execute(null);
        Assert.True(vm.HasSplitRegion);

        vm.RoutingSerialCommand.Execute(null);

        Assert.Equal(new[] { "overdrive", "distortion" },
            vm.Nodes.Select(n => n.ModuleTypeId).ToArray());
        Assert.Equal(new[] { "overdrive", "distortion" },
            dsp.GetNodes().Select(n => n.ModuleTypeId).ToArray());
        Assert.False(vm.HasSplitRegion);
        Assert.True(vm.IsRoutingSerial);
    }

    [Fact]
    public void RoutingSerial_on_a_serial_chain_is_a_no_op()
    {
        var vm = NewVm(out _);
        Add(vm, "overdrive");

        vm.RoutingSerialCommand.Execute(null);

        Assert.Single(vm.Nodes);
        Assert.True(vm.IsRoutingSerial);
    }

    // ── the owner's "TWO Mergers" bug: the toolbar must NEVER build a second
    //    junction pair when the chain already carries one, however it got there. ──

    [Fact]
    public void RoutingSplit_on_a_BYPASSED_splitter_rewidths_it_instead_of_adding_a_second_pair()
    {
        var vm = NewVm(out var dsp);
        Add(vm, "overdrive");
        Add(vm, "splitter"); // + auto-paired merger
        var splitter = vm.Nodes.First(n => n.IsSplitter);

        // Bypassing the splitter collapses the region — the toolbar reads "serial",
        // but the junctions are STILL in the chain.
        splitter.Bypassed = true;
        Assert.True(vm.IsRoutingSerial);

        vm.RoutingSplit3Command.Execute(null);

        Assert.Equal(new[] { "overdrive", "splitter", "merger" },
            vm.Nodes.Select(n => n.ModuleTypeId).ToArray());
        Assert.Equal(new[] { "overdrive", "splitter", "merger" },
            dsp.GetNodes().Select(n => n.ModuleTypeId).ToArray());
        Assert.Single(vm.Nodes, n => n.IsSplitter);
        Assert.Single(vm.Nodes, n => n.IsMerger);
        Assert.False(splitter.Bypassed); // re-armed
        Assert.Equal(3, vm.RoutingBranches);
    }

    [Fact]
    public void RoutingSplit_reuses_a_STRAY_merger_instead_of_adding_a_second_one()
    {
        var vm = NewVm(out _);
        Add(vm, "overdrive");
        Add(vm, "merger"); // a lone merger (dragged in from the rail)

        vm.RoutingSplit2Command.Execute(null);

        Assert.Single(vm.Nodes, n => n.IsMerger);
        Assert.Single(vm.Nodes, n => n.IsSplitter);
        // …and it closes the region: the merger sits AFTER the splitter.
        var ids = vm.Nodes.Select(n => n.ModuleTypeId).ToList();
        Assert.True(ids.IndexOf("merger") > ids.IndexOf("splitter"));
        Assert.True(vm.HasSplitRegion);
    }

    [Fact]
    public void Adding_a_second_splitter_from_the_rail_never_adds_a_second_merger()
    {
        var vm = NewVm(out _);
        Add(vm, "splitter"); // splitter + merger
        Add(vm, "splitter"); // a second one — must NOT bring its own merger

        Assert.Single(vm.Nodes, n => n.IsMerger);
    }

    [Fact]
    public void RoutingSerial_removes_exactly_the_one_junction_pair_and_keeps_the_body()
    {
        var vm = NewVm(out var dsp);
        Add(vm, "overdrive");
        Add(vm, "distortion");
        Add(vm, "reverb");
        vm.SelectNodeAt(1);
        vm.RoutingSplit2Command.Execute(null); // od | S | dist | M | reverb

        vm.RoutingSerialCommand.Execute(null);

        Assert.Equal(new[] { "overdrive", "distortion", "reverb" },
            vm.Nodes.Select(n => n.ModuleTypeId).ToArray());
        Assert.Equal(new[] { "overdrive", "distortion", "reverb" },
            dsp.GetNodes().Select(n => n.ModuleTypeId).ToArray());
        Assert.True(vm.IsRoutingSerial);
        Assert.False(vm.HasSplitRegion);
    }

    [Fact]
    public void RoutingSerial_also_sweeps_a_BYPASSED_junction_so_the_next_split_stays_single()
    {
        var vm = NewVm(out _);
        Add(vm, "overdrive");
        Add(vm, "splitter");
        vm.Nodes.First(n => n.IsMerger).Bypassed = true; // a bypassed leftover

        vm.RoutingSerialCommand.Execute(null);
        Assert.Equal(new[] { "overdrive" }, vm.Nodes.Select(n => n.ModuleTypeId).ToArray());

        vm.RoutingSplit2Command.Execute(null);
        Assert.Single(vm.Nodes, n => n.IsSplitter);
        Assert.Single(vm.Nodes, n => n.IsMerger);
    }

    [Fact]
    public void Split_serial_split_round_trip_never_accumulates_junctions()
    {
        var vm = NewVm(out _);
        Add(vm, "overdrive");

        for (var i = 0; i < 3; i++)
        {
            vm.RoutingSplit2Command.Execute(null);
            vm.RoutingSplit3Command.Execute(null);
            vm.RoutingSerialCommand.Execute(null);
        }

        vm.RoutingSplit2Command.Execute(null);

        Assert.Single(vm.Nodes, n => n.IsSplitter);
        Assert.Single(vm.Nodes, n => n.IsMerger);
        Assert.Equal(3, vm.Nodes.Count);
    }
}
