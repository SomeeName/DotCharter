using System.Collections.Specialized;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The Fire rig screen's chain mirrors are RECONCILED, never cleared and
///     refilled (0.59). <c>Chain</c> and <c>SignalChips</c> feed non-virtualized
///     ItemsControls (the board's unit cards, the signal strip), so a
///     clear+refill re-inflated every card template on every chain event — and,
///     because a cleared collection resets the board ScrollViewer's extent,
///     snapped a scrolled board back to the chain start on every add. The gear
///     panel's rows are the same story one panel over: their structure depends
///     only on the catalogue and the filter, so a chain change updates the
///     "IN RIG" badges in place instead of rebuilding ~57 row templates.
/// </summary>
public sealed class AmpFxRigChainMirrorTests
{
    private static (AmpFxRigViewModel Screen, PedalboardViewModel Rig) NewScreen()
    {
        var rig = TestRigBuilders.Pedalboard(new FakeDspChainService(), new FakeDspTonePresetService(), FakeLocalization.English());
        return (new AmpFxRigViewModel(rig), rig);
    }

    private static void Add(PedalboardViewModel rig, string id) =>
        rig.AddGear(rig.AvailableModules.First(m => m.Id == id));

    [Fact]
    public void AChainChange_NeverResetsTheChainMirror_AndSurvivingChipsKeepTheirInstances()
    {
        var (screen, rig) = NewScreen();
        Add(rig, "overdrive");
        Add(rig, "amp");
        var firstChip = screen.SignalChips[0];
        var resets = 0;
        screen.Chain.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) resets++;
        };
        screen.SignalChips.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) resets++;
        };

        Add(rig, "cab");
        rig.RemoveCommand.Execute(rig.Nodes[1]);
        rig.Sync();

        Assert.Equal(0, resets);
        Assert.Same(firstChip, screen.SignalChips[0]);
        Assert.Equal(rig.Nodes.ToArray(), screen.Chain.ToArray());
    }

    /// <summary>The strip's → separator rule survives the head changing: only
    /// the FIRST chip hides its arrow, whichever node that now is.</summary>
    [Fact]
    public void RemovingTheHead_PromotesTheNextChipToFirst()
    {
        var (screen, rig) = NewScreen();
        Add(rig, "overdrive");
        Add(rig, "amp");
        Assert.False(screen.SignalChips[0].ShowsArrow);
        Assert.True(screen.SignalChips[1].ShowsArrow);

        rig.RemoveCommand.Execute(rig.Nodes[0]);

        Assert.False(screen.SignalChips[0].ShowsArrow);
        Assert.Same(rig.Nodes[0], screen.SignalChips[0].Node);
    }

    [Fact]
    public void AChainChange_UpdatesTheGearBadgesInPlace_WithoutRebuildingTheRows()
    {
        var (screen, rig) = NewScreen();
        var rowsBefore = screen.GearGroups.SelectMany(g => g.Items).ToList();
        var odRow = rowsBefore.Single(i => i.Type.Id == "overdrive");
        Assert.False(odRow.IsInRig);

        Add(rig, "overdrive");

        var rowsAfter = screen.GearGroups.SelectMany(g => g.Items).ToList();
        Assert.Equal(rowsBefore, rowsAfter); // same instances, same order
        Assert.True(odRow.IsInRig);
        Assert.True(odRow.CanAdd);

        rig.RemoveCommand.Execute(rig.Nodes[0]);
        Assert.False(odRow.IsInRig);
        Assert.True(odRow.CanAdd);
    }

    [Fact]
    public void TheDock_StaysOnItsUnit_AcrossAChainChange_AndClosesWhenItsUnitGoes()
    {
        var (screen, rig) = NewScreen();
        Add(rig, "overdrive");
        Add(rig, "amp");
        rig.SelectNodeAt(1);
        var dock = screen.DockUnit;
        Assert.NotNull(dock);

        Add(rig, "cab");
        Assert.Same(dock, screen.DockUnit); // survived — no editor re-inflation

        rig.RemoveCommand.Execute(rig.Nodes[1]); // the docked amp
        Assert.Null(rig.SelectedNode);
        Assert.False(screen.HasDockUnit);
        Assert.Null(screen.DockUnit);
    }
}
