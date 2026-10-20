using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Anchors;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Targets;

namespace DotCharter.Tests.Services.Editing.Anchors;

/// <summary>
///     Nudging an FHP anchor without the dialog.
///     <para>
///         The dialog used to be the only route to an existing anchor. In the
///         owner's 2026-07-28 session that meant 16 modal round trips, 12 of them
///         a ±1 fret change — including 7→6 then 6→5 on the SAME anchor one second
///         apart, i.e. one intent spelled as two dialogs.
///     </para>
/// </summary>
public sealed class AnchorNudgeTests
{
    private sealed class Harness
    {
        public readonly ChartLevel Level;
        public readonly HighwayEditingService Service;
        public readonly SelectionService Selection = new();
        public readonly HistoryService History;

        public Harness(int capo = 0)
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var state = new ProjectState();
            var project = new ChartProject();
            for (var i = 0; i < 32; i++)
            {
                project.Beats.Add(new BeatMarker
                { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0 });
            }

            var arr = new Arrangement { Name = "Lead", Capo = capo };
            Level = new ChartLevel();
            arr.Levels.Add(Level);
            project.Arrangements.Add(arr);

            state.ActiveProject = project;
            state.ActiveArrangement = arr;
            state.ActiveLevel = Level;

            var config = new FakeConfigurationService();
            var scope = new EditingScope(state);
            Service = new HighwayEditingService(state, Selection, History, new StrictCollisionService(),
                config, scope, new EditTargetService(Selection, scope, config), logger);
        }

        public FretAnchor Add(int beat, int fret, int width = 4)
        {
            var anchor = new FretAnchor { Position = new FractionalPosition(beat), Fret = fret, Width = width };
            Level.Anchors.Add(anchor);
            return anchor;
        }
    }

    [Fact]
    public void NudgingTheFretMovesTheSelectedAnchor()
    {
        var h = new Harness();
        var anchor = h.Add(4, 7);
        h.Selection.SelectedAnchors.Add(anchor);

        Assert.True(h.Service.AdjustSelectedAnchors(-1, AnchorNudgeTarget.Fret));

        Assert.Equal(6, anchor.Fret);
    }

    /// <summary>
    ///     The whole point of the merge verb: 7→6→5 in one gesture is ONE Ctrl+Z,
    ///     not two dialogs and two undo nodes.
    /// </summary>
    [Fact]
    public void ARunOfNudgesIsOneUndoStep()
    {
        var h = new Harness();
        var anchor = h.Add(4, 7);
        h.Selection.SelectedAnchors.Add(anchor);

        h.Service.AdjustSelectedAnchors(-1, AnchorNudgeTarget.Fret);
        h.Service.AdjustSelectedAnchors(-1, AnchorNudgeTarget.Fret);
        Assert.Equal(5, anchor.Fret);

        Assert.Single(h.History.GetUndoSteps());
        h.History.Undo();
        Assert.Equal(7, anchor.Fret);
    }

    /// <summary>Fret and width are different gestures and must not fold together.</summary>
    [Fact]
    public void AFretNudgeAndAWidthNudgeStayApart()
    {
        var h = new Harness();
        var anchor = h.Add(4, 7);
        h.Selection.SelectedAnchors.Add(anchor);

        h.Service.AdjustSelectedAnchors(-1, AnchorNudgeTarget.Fret);
        h.Service.AdjustSelectedAnchors(1, AnchorNudgeTarget.Width);

        Assert.Equal(2, h.History.GetUndoSteps().Count);
        h.History.Undo();
        Assert.Equal(4, anchor.Width);
        Assert.Equal(6, anchor.Fret);
    }

    [Fact]
    public void EverySelectedAnchorMovesTogether()
    {
        var h = new Harness();
        var first = h.Add(4, 7);
        var second = h.Add(8, 12);
        h.Selection.SelectedAnchors.Add(first);
        h.Selection.SelectedAnchors.Add(second);

        h.Service.AdjustSelectedAnchors(1, AnchorNudgeTarget.Fret);

        Assert.Equal(8, first.Fret);
        Assert.Equal(13, second.Fret);
        Assert.Single(h.History.GetUndoSteps());
    }

    /// <summary>
    ///     An anchor may not sit on or below the capo — the capo already stops the
    ///     string there, and such an anchor is inspector rule L02.
    /// </summary>
    [Fact]
    public void TheFretFloorIsCapoPlusOne()
    {
        var h = new Harness(capo: 3);
        var anchor = h.Add(4, 5);
        h.Selection.SelectedAnchors.Add(anchor);

        h.Service.AdjustSelectedAnchors(-1, AnchorNudgeTarget.Fret, capo: 3);
        Assert.Equal(4, anchor.Fret);

        // Already at the floor: refuses rather than pushing an empty node.
        Assert.False(h.Service.AdjustSelectedAnchors(-1, AnchorNudgeTarget.Fret, capo: 3));
        Assert.Equal(4, anchor.Fret);
    }

    [Fact]
    public void WithNoCapoTheFloorIsFretOne()
    {
        var h = new Harness();
        var anchor = h.Add(4, 1);
        h.Selection.SelectedAnchors.Add(anchor);

        Assert.False(h.Service.AdjustSelectedAnchors(-1, AnchorNudgeTarget.Fret));
        Assert.Equal(1, anchor.Fret);
    }

    [Fact]
    public void NothingSelectedIsARefusal()
    {
        var h = new Harness();
        h.Add(4, 7);
        Assert.False(h.Service.AdjustSelectedAnchors(-1, AnchorNudgeTarget.Fret));
    }

    /// <summary>
    ///     A clamped anchor contributes nothing, so a mixed selection where one is
    ///     already at the ceiling still moves the others and still records one node.
    /// </summary>
    [Fact]
    public void AClampedAnchorDoesNotBlockItsNeighbours()
    {
        var h = new Harness();
        var atFloor = h.Add(4, 1);
        var movable = h.Add(8, 9);
        h.Selection.SelectedAnchors.Add(atFloor);
        h.Selection.SelectedAnchors.Add(movable);

        Assert.True(h.Service.AdjustSelectedAnchors(-1, AnchorNudgeTarget.Fret));

        Assert.Equal(1, atFloor.Fret);
        Assert.Equal(8, movable.Fret);

        h.History.Undo();
        Assert.Equal(1, atFloor.Fret);
        Assert.Equal(9, movable.Fret);
    }

    [Fact]
    public void WidthIsBoundedToAPlayableHandSpan()
    {
        var h = new Harness();
        var anchor = h.Add(4, 7, 3);
        h.Selection.SelectedAnchors.Add(anchor);

        Assert.False(h.Service.AdjustSelectedAnchors(-1, AnchorNudgeTarget.Width));
        Assert.Equal(3, anchor.Width);

        for (var i = 0; i < 10; i++) h.Service.AdjustSelectedAnchors(1, AnchorNudgeTarget.Width);
        Assert.Equal(7, anchor.Width);
    }

    [Fact]
    public void AZeroDeltaDoesNothing()
    {
        var h = new Harness();
        var anchor = h.Add(4, 7);
        h.Selection.SelectedAnchors.Add(anchor);

        Assert.False(h.Service.AdjustSelectedAnchors(0, AnchorNudgeTarget.Fret));
        Assert.Equal(7, anchor.Fret);
        Assert.False(h.History.CanUndo);
    }

    /// <summary>
    ///     The anchor floor is <c>capo + 1</c>, and it must be CLAMPED to the
    ///     neck: the arrangement dialog's capo spinner goes to 24, so an
    ///     unclamped floor is 25 — above <c>MaxAnchorFret</c>. <c>Math.Clamp</c>
    ///     THROWS <c>ArgumentException</c> when min &gt; max, so one press of the
    ///     nudge keybind took down the UI thread. The two anchor-minting sites
    ///     (<c>ChartImprovementService.MinAnchorFret</c>,
    ///     <c>FretAnchorGenerationService.MinAnchorFret</c>) already clamp; these
    ///     two did not.
    /// </summary>
    [Theory]
    [InlineData(23)]
    [InlineData(24)]
    public void NudgingTheFretUnderAHighCapo_ClampsToTheNeckInsteadOfThrowing(int capo)
    {
        var h = new Harness(capo);
        var anchor = h.Add(4, 20);
        h.Selection.SelectedAnchors.Add(anchor);

        h.Service.AdjustSelectedAnchors(1, AnchorNudgeTarget.Fret, capo);

        Assert.InRange(anchor.Fret, 1, 24);
    }
}
