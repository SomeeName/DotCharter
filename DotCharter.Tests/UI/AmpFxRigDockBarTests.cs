using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The editor header from the tone-rig design — previous / next module,
///     the "Lane A / Cabinet" breadcrumb, the ENABLED switch — and the
///     workspace divider's persisted height. All projections over the rig;
///     none of them holds state the board does not already have.
/// </summary>
public sealed class AmpFxRigDockBarTests
{
    private static IReadOnlyList<DspModuleType> Catalog() =>
    [
        new DspModuleType("overdrive", "Overdrive", [new DspParamInfo("drive", "Drive", 0f, 1f, 0.5f)]),
        new DspModuleType("delay", "Delay", [new DspParamInfo("mix", "Mix", 0f, 1f, 0.3f)]),
        new DspModuleType("reverb", "Reverb", [new DspParamInfo("mix", "Mix", 0f, 1f, 0.3f)]),
        new DspModuleType("splitter", "Splitter", [new DspParamInfo("enabledBranches", "Branches", 2f, 3f, 2f)]),
        new DspModuleType("merger", "Merger", [new DspParamInfo("mode", "Mode", 0f, 1f, 0f)]),
    ];

    private static (PedalboardViewModel Rig, AmpFxRigViewModel Screen, FakeDspChainService Chain) Screen(IConfigurationService? config = null)
    {
        var chain = new FakeDspChainService(Catalog());
        var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), FakeLocalization.English(), config: config);
        return (rig, new AmpFxRigViewModel(rig), chain);
    }

    [Fact]
    public void PreviousAndNext_StepTheSelectionThroughTheChain_AndWrap()
    {
        var (rig, screen, chain) = Screen();
        foreach (var id in new[] { "overdrive", "delay", "reverb" })
            rig.AddGear(chain.Catalog.Single(t => t.Id == id));

        Assert.Null(rig.SelectedNode);
        screen.SelectNextUnitCommand.Execute(null);
        Assert.Equal("overdrive", rig.SelectedNode!.ModuleTypeId);
        screen.SelectNextUnitCommand.Execute(null);
        Assert.Equal("delay", rig.SelectedNode!.ModuleTypeId);
        screen.SelectPreviousUnitCommand.Execute(null);
        Assert.Equal("overdrive", rig.SelectedNode!.ModuleTypeId);
        screen.SelectPreviousUnitCommand.Execute(null);
        Assert.Equal("reverb", rig.SelectedNode!.ModuleTypeId); // wrapped
        screen.SelectNextUnitCommand.Execute(null);
        Assert.Equal("overdrive", rig.SelectedNode!.ModuleTypeId); // wrapped the other way
    }

    [Fact]
    public void TheBreadcrumb_NamesTheLane_TheSerialRun_OrRouting()
    {
        var (rig, screen, chain) = Screen();
        rig.AddGear(chain.Catalog.Single(t => t.Id == "overdrive"));
        rig.AddGear(chain.Catalog.Single(t => t.Id == "splitter"));
        var delay = rig.AddGear(chain.Catalog.Single(t => t.Id == "delay"))!;
        rig.MoveTo(rig.Nodes.Single(n => n.IsMerger), rig.Nodes.Count - 1); // the region closes after the delay
        rig.RoutingSplit2Command.Execute(null);
        rig.DropToRow(delay, rig.Nodes.IndexOf(delay), 2);

        rig.SelectNodeAt(0);
        Assert.Equal("Series", screen.DockLaneLabel);
        Assert.Equal("FireAccent", screen.DockLaneTokenKey);

        rig.SelectNodeAt(rig.Nodes.IndexOf(delay));
        Assert.Equal("Lane B", screen.DockLaneLabel);
        Assert.Equal("FireLaneB", screen.DockLaneTokenKey);

        rig.SelectNodeAt(rig.Nodes.IndexOf(rig.Nodes.Single(n => n.IsSplitter)));
        Assert.Equal("Routing", screen.DockLaneLabel);

        // re-branching the docked unit moves the breadcrumb with it
        rig.SelectNodeAt(rig.Nodes.IndexOf(delay));
        var raised = new List<string?>();
        screen.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        rig.DropToRow(delay, rig.Nodes.IndexOf(delay), 1);
        Assert.Equal("Lane A", screen.DockLaneLabel);
        Assert.Contains(nameof(AmpFxRigViewModel.DockLaneLabel), raised);
    }

    [Fact]
    public void TheEnabledSwitch_IsTheBypassInverted_AndNotifiesBothWays()
    {
        var (rig, screen, chain) = Screen();
        rig.AddGear(chain.Catalog.Single(t => t.Id == "overdrive"));
        rig.SelectNodeAt(0);
        var unit = screen.DockUnit!;
        Assert.True(unit.IsUnitEnabled);
        var raised = new List<string?>();
        unit.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        unit.IsUnitEnabled = false;
        Assert.True(unit.Node.Bypassed);
        Assert.Contains(nameof(RigDockUnit.IsUnitEnabled), raised);
        Assert.NotEqual(unit.ToggleEnabledLabel, string.Empty);
        var bypassedLabel = unit.ToggleEnabledLabel;

        unit.ToggleEnabledCommand.Execute(null);
        Assert.False(unit.Node.Bypassed);
        Assert.NotEqual(bypassedLabel, unit.ToggleEnabledLabel);
    }

    /// <summary>
    ///     THE TONES MODE AND THE MODULE EDITOR ARE NEVER BOTH ON SCREEN, and
    ///     which one is follows the LAST GESTURE (W1's rule, restated one
    ///     structure up by W4, 2026-09-16). The tone designer and the Tones list
    ///     used to be a DRAWER sharing the rig's fixed-height workspace with the
    ///     editor, each falling back to a header stub; they are a MODE now that
    ///     takes the whole body below the header, and picking a block LEAVES it.
    ///     <para>
    ///         This is the W1 case renamed from
    ///         <c>TheWorkspace_GivesTheRoomToWhicheverHalfWasAskedForLast</c>.
    ///         The gesture sequence is unchanged — browse, pick, close, HIDE —
    ///         only the word for "the tone surface has the room" is.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheTonesMode_AndTheEditor_FollowTheLastGesture()
    {
        var (rig, screen, chain) = Screen();
        rig.AddGear(chain.Catalog.Single(t => t.Id == "overdrive"));
        rig.AddGear(chain.Catalog.Single(t => t.Id == "delay"));

        rig.SelectNodeAt(0);
        Assert.True(screen.IsDockBodyVisible);
        Assert.False(screen.IsTonesMode);

        // Browsing tones is a MODE: it takes the whole body below the header.
        rig.ToggleBrowserCommand.Execute(null);
        Assert.True(screen.IsTonesMode);

        // …and picking a block LEAVES it, straight back into the editor.
        rig.SelectNodeAt(1);
        Assert.False(rig.IsBrowserOpen);
        Assert.False(screen.IsTonesMode);
        Assert.True(screen.IsDockBodyVisible);

        // Entering and leaving by the pill leaves the editor exactly as it was.
        rig.ToggleBrowserCommand.Execute(null);
        Assert.True(screen.IsTonesMode);
        rig.ToggleBrowserCommand.Execute(null);
        Assert.False(screen.IsTonesMode);
        Assert.True(screen.IsDockBodyVisible);

        // HIDE still collapses the editor to its bar on its own.
        screen.ToggleDockCommand.Execute(null);
        Assert.False(screen.IsDockBodyVisible);
    }

    /// <summary>
    ///     ONE persisted height for the whole region below the routing canvas.
    ///     Replaces <c>TheDockHeight_IsClampedAndPersisted_AndZeroMeansAutomatic</c>:
    ///     "zero means automatic" was the bug — an automatically sized editor row
    ///     is exactly what made the canvas above it resize when the editor opened
    ///     (W1). There is no automatic size any more, so there is no zero.
    /// </summary>
    [Fact]
    public void TheWorkspaceHeight_IsClampedAndPersisted_AndNeverSizesToItsContent()
    {
        var config = new FakeConfigurationService();
        var (rig, _, _) = Screen(config);
        Assert.Equal(RigWorkspaceViewModel.DefaultHeight, rig.Workspace.Height);

        rig.Workspace.Height = 5000;
        Assert.Equal(RigWorkspaceViewModel.MaxHeight, rig.Workspace.Height);
        Assert.Equal(RigWorkspaceViewModel.MaxHeight, config.LoadAppConfig().RigWorkspaceHeight);

        rig.Workspace.Height = 10;
        Assert.Equal(RigWorkspaceViewModel.MinHeight, rig.Workspace.Height);

        config.LoadAppConfig().RigWorkspaceHeight = 400;
        var (again, _, _) = Screen(config);
        Assert.Equal(400, again.Workspace.Height);
    }
}
