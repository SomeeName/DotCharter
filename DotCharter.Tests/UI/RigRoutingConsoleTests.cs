using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The routing console a splitter or merger opens is a projection over the
///     rig's own routing commands and the board's live lanes — it owns no
///     state of its own, so the console, the footer strip and the board can
///     never disagree about the mode or a lane's level.
/// </summary>
public sealed class RigRoutingConsoleTests
{
    // The default fake catalogue has no routing nodes; the split flow needs them.
    private static IReadOnlyList<DspModuleType> SplitCatalog() =>
    [
        new DspModuleType("overdrive", "Overdrive", [new DspParamInfo("drive", "Drive", 0f, 1f, 0.5f)]),
        new DspModuleType("delay", "Delay", [new DspParamInfo("mix", "Mix", 0f, 1f, 0.3f)]),
        new DspModuleType("splitter", "Splitter", [new DspParamInfo("enabledBranches", "Branches", 2f, 3f, 2f)]),
        new DspModuleType("merger", "Merger", [new DspParamInfo("mode", "Mode", 0f, 1f, 0f)]),
    ];

    private static (PedalboardViewModel Rig, AmpFxRigViewModel Screen) SplitRig()
    {
        var chain = new FakeDspChainService(SplitCatalog());
        var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), FakeLocalization.English());
        rig.AddGear(chain.Catalog.Single(t => t.Id == "overdrive"));
        rig.AddGear(chain.Catalog.Single(t => t.Id == "splitter")); // auto-pairs the merger
        rig.AddGear(chain.Catalog.Single(t => t.Id == "delay"));
        rig.RoutingSplit3Command.Execute(null);
        return (rig, new AmpFxRigViewModel(rig));
    }

    [Fact]
    public void AJunction_OpensTheConsole_OverTheBoardsLanes()
    {
        var (rig, screen) = SplitRig();
        var splitter = rig.Nodes.Single(n => n.IsSplitter);
        rig.SelectNodeAt(rig.Nodes.IndexOf(splitter));

        var unit = screen.DockUnit!;
        Assert.True(unit.HasRouting);
        Assert.Same(unit.Node, splitter);
        var console = unit.Routing!;
        Assert.Equal(3, console.LaneCount);
        Assert.True(console.IsTriple);
        Assert.Same(screen.BoardSegments.OfType<RigSplitSegment>().Single().Lanes, console.Lanes);
        Assert.Same(rig.RoutingSplit2Command, console.RoutingSplit2Command);

        var merger = rig.Nodes.Single(n => n.IsMerger);
        rig.SelectNodeAt(rig.Nodes.IndexOf(merger));
        Assert.True(screen.DockUnit!.HasRouting);
    }

    [Fact]
    public void ThePedalsDoNotGetAConsole()
    {
        var (rig, screen) = SplitRig();
        rig.SelectNodeAt(rig.Nodes.IndexOf(rig.Nodes.First(n => n.ModuleTypeId == "overdrive")));
        Assert.False(screen.DockUnit!.HasRouting);
        Assert.Null(screen.DockUnit.Routing);
    }

    [Fact]
    public void TheMode_FollowsTheRig_AndNotifies()
    {
        var (rig, screen) = SplitRig();
        rig.SelectNodeAt(rig.Nodes.IndexOf(rig.Nodes.Single(n => n.IsSplitter)));
        var console = screen.DockUnit!.Routing!;
        var raised = new List<string?>();
        console.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        console.RoutingSplit2Command.Execute(null);
        Assert.True(console.IsDual);
        Assert.False(console.IsTriple);
        Assert.Contains(nameof(RigRoutingConsoleViewModel.ModeName), raised);
        Assert.Contains(nameof(RigRoutingConsoleViewModel.LaneSummary), raised);
        Assert.NotEqual(console.ModeName, string.Empty);
    }
}
