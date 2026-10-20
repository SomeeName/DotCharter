using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.UI;

/// <summary>
///     #211: the editor's top area (timeline + song map) and its bottom dock (Inspector + 3D preview)
///     share <c>MainSplitGrid</c>. On a short window the timeline's 360 px floor used to win outright
///     and leave the dock 190 px at 1280x720 — an Inspector with no body. The split heights below are
///     the ones the shell measured (<c>Perf/EditorShortWindowUiPerfSmokeTests</c>): 608 at 1280x720,
///     688 at 1280x800, 968 at 1920x1080.
/// </summary>
public sealed class EditorRowFloorsTests
{
    private static readonly double Comfortable =
        TimelineAreaMinHeight.For(sheetVisible: false, songMapShown: true, gameplayPresentation: false);

    [Fact]
    public void ATallWindow_KeepsTheTimelinesComfortableFloor()
    {
        var floors = EditorRowFloors.For(968, Comfortable, dockVisible: true);

        Assert.Equal(Comfortable, floors.TopAreaMinPx);
        Assert.Equal(TimelineAreaMinHeight.TimelineMinPx, floors.TimelineRowMinPx);
        Assert.Equal(EditorRowFloors.DockFloorPx, floors.DockMinPx);
    }

    [Fact]
    public void At1280x800_TheTimelineGivesWay_UntilTheDockHasItsFloor()
    {
        var floors = EditorRowFloors.For(688, Comfortable, dockVisible: true);

        Assert.Equal(EditorRowFloors.DockFloorPx, floors.DockMinPx);
        Assert.Equal(688 - EditorRowFloors.SplitterPx - EditorRowFloors.DockFloorPx, floors.TopAreaMinPx);
        Assert.Equal(330, floors.TimelineRowMinPx);
    }

    [Fact]
    public void At1280x720_TheTimelineStopsAtItsCompactFloor_AndTheDockTakesTheRest()
    {
        var floors = EditorRowFloors.For(608, Comfortable, dockVisible: true);

        Assert.Equal(EditorRowFloors.TimelineCompactPx, floors.TimelineRowMinPx);
        Assert.Equal(Comfortable - (TimelineAreaMinHeight.TimelineMinPx - EditorRowFloors.TimelineCompactPx), floors.TopAreaMinPx);
        Assert.Equal(608 - EditorRowFloors.SplitterPx - floors.TopAreaMinPx, floors.DockMinPx);
        Assert.True(floors.DockMinPx > 190, "the dock got no more than it had before #211");
    }

    /// <summary>The three floors always add up to no more than the split while both can be honoured.</summary>
    [Theory]
    [InlineData(608)]
    [InlineData(688)]
    [InlineData(750)]
    [InlineData(968)]
    public void TheFloorsFitTheSplit(double split)
    {
        var floors = EditorRowFloors.For(split, Comfortable, dockVisible: true);
        Assert.True(floors.TopAreaMinPx + EditorRowFloors.SplitterPx + floors.DockMinPx <= split + 0.001,
            $"at {split} px the floors ask for {floors.TopAreaMinPx} + 6 + {floors.DockMinPx}");
    }

    [Fact]
    public void ATinyWindow_NeverTakesTheDockUnderItsOldFloor()
    {
        var floors = EditorRowFloors.For(300, Comfortable, dockVisible: true);
        Assert.Equal(EditorRowFloors.DockLegacyMinPx, floors.DockMinPx);
        Assert.Equal(EditorRowFloors.TimelineCompactPx, floors.TimelineRowMinPx);
    }

    [Fact]
    public void AHiddenDock_HasNoFloor_AndTheTimelineKeepsItsOwn()
    {
        var floors = EditorRowFloors.For(608, Comfortable, dockVisible: false);
        Assert.Equal(0, floors.DockMinPx);
        Assert.Equal(TimelineAreaMinHeight.TimelineMinPx, floors.TimelineRowMinPx);
        Assert.Equal(Comfortable, floors.TopAreaMinPx);
    }

    /// <summary>Before the first layout the split measures 0: the markup's own values, not a squeeze.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AnUnmeasuredSplit_GetsTheMarkupsValues(double split)
    {
        var floors = EditorRowFloors.For(split, Comfortable, dockVisible: true);
        Assert.Equal(Comfortable, floors.TopAreaMinPx);
        Assert.Equal(TimelineAreaMinHeight.TimelineMinPx, floors.TimelineRowMinPx);
        Assert.Equal(EditorRowFloors.DockLegacyMinPx, floors.DockMinPx);
    }

    /// <summary>The open sheet strip keeps its own minimum: only the TIMELINE row gives way.</summary>
    [Fact]
    public void WithTheSheetStripOpen_OnlyTheTimelineRowGivesWay()
    {
        var withSheet = TimelineAreaMinHeight.For(sheetVisible: true, songMapShown: true, gameplayPresentation: false);
        var floors = EditorRowFloors.For(608, withSheet, dockVisible: true);

        Assert.Equal(EditorRowFloors.TimelineCompactPx, floors.TimelineRowMinPx);
        Assert.Equal(withSheet - (TimelineAreaMinHeight.TimelineMinPx - EditorRowFloors.TimelineCompactPx), floors.TopAreaMinPx);
    }

    /// <summary>The dock row's old floor restates the markup.</summary>
    [Fact]
    public void TheLegacyDockFloor_MatchesTheMarkup()
    {
        var window = MainWindowSource.ReadAllText();
        var grid = window[window.IndexOf("x:Name=\"MainSplitGrid\"", StringComparison.Ordinal)..];
        Assert.Contains($"<RowDefinition Height=\"{{x:Static shellViews:EditorSplitDefaults.DockRow}}\" MinHeight=\"{EditorRowFloors.DockLegacyMinPx}\" />", grid, StringComparison.Ordinal);
    }
}
