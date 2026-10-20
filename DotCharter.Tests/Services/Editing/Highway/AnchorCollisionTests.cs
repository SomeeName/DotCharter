using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;

namespace DotCharter.Tests.Services.Editing.Highway;

/// <summary>
///     One beat holds ONE fret-hand position.
///     <para>
///         Notes, chords and hand shapes each had a collision rule in
///         <c>CommitHighwayDragDropCommand</c> from the start; anchors did not.
///         So every drop that carried anchors — a PASTE above all, which brings a
///         whole run of them at once — stacked them on whatever the destination
///         already had. Nothing failed at the time; the duplicate surfaced later
///         as the inspector's "Two anchors share the same position", once per
///         pasted anchor, and the only fix was deleting each one by hand.
///     </para>
///     <para>
///         The reported case: 22 notes, 7 hand shapes and 4 anchors copied from
///         beat 444 and pasted at 124, over a region that already had anchors —
///         four errors, one per pasted anchor.
///     </para>
/// </summary>
public sealed class AnchorCollisionTests
{
    [Fact]
    public void AnArrivingAnchor_DestroysTheOneAlreadyOnThatBeat()
    {
        var h = new Harness();
        var sitting = h.AddAnchor(4, fret: 2);

        h.Service.CommitHandShapeDragAndDrop(
            [], [], [], [], [], [],
            [], [h.Anchor(4, fret: 9)]);

        var survivor = Assert.Single(h.Level.Anchors);
        Assert.Equal(9, survivor.Fret);
        // REFERENCE identity on purpose: FretAnchor implements IComparable<T> on
        // POSITION alone, and xUnit's comparer honours that — so a plain
        // DoesNotContain would compare two anchors on beat 4 as equal and this
        // assertion would say nothing at all.
        Assert.DoesNotContain(h.Level.Anchors, a => ReferenceEquals(a, sitting));
    }

    [Fact]
    public void APasteOfSeveralAnchors_LeavesOnePerBeat()
    {
        // The reported shape, in miniature.
        var h = new Harness();
        foreach (var beat in new[] { 4, 8, 12, 16 }) h.AddAnchor(beat, fret: 2);

        h.Service.CommitHandShapeDragAndDrop(
            [], [], [], [], [], [],
            [], [h.Anchor(4, 7), h.Anchor(8, 9), h.Anchor(12, 7), h.Anchor(16, 9)]);

        Assert.Equal(4, h.Level.Anchors.Count);
        Assert.Equal(4, h.Level.Anchors.Select(a => a.Position).Distinct().Count());
        Assert.All(h.Level.Anchors, a => Assert.NotEqual(2, a.Fret));
    }

    [Fact]
    public void AnAnchorOnAFreeBeat_DestroysNothing()
    {
        var h = new Harness();
        var untouched = h.AddAnchor(4, fret: 2);

        h.Service.CommitHandShapeDragAndDrop(
            [], [], [], [], [], [],
            [], [h.Anchor(8, fret: 9)]);

        Assert.Equal(2, h.Level.Anchors.Count);
        Assert.Contains(h.Level.Anchors, a => ReferenceEquals(a, untouched));
    }

    /// <summary>
    ///     A MOVE is not a collision with itself. The anchor being dragged
    ///     arrives as a target/new pair, and treating its own old instance as a
    ///     squatter would delete the thing the gesture is moving.
    /// </summary>
    [Fact]
    public void DraggingAnAnchorOntoItsOwnBeat_KeepsIt()
    {
        var h = new Harness();
        var dragged = h.AddAnchor(4, fret: 2);

        h.Service.CommitHandShapeDragAndDrop(
            [], [], [], [], [], [],
            [dragged], [h.Anchor(4, fret: 5)]);

        var survivor = Assert.Single(h.Level.Anchors);
        Assert.Equal(5, survivor.Fret);
    }

    [Fact]
    public void DraggingAnAnchorOntoANEIGHBOURSBeat_TakesTheNeighbourOut()
    {
        var h = new Harness();
        var dragged = h.AddAnchor(4, fret: 2);
        var neighbour = h.AddAnchor(8, fret: 12);

        h.Service.CommitHandShapeDragAndDrop(
            [], [], [], [], [], [],
            [dragged], [h.Anchor(8, fret: 2)]);

        var survivor = Assert.Single(h.Level.Anchors);
        Assert.Equal(2, survivor.Fret);
        Assert.Equal(new FractionalPosition(8), survivor.Position);
        Assert.DoesNotContain(h.Level.Anchors, a => ReferenceEquals(a, neighbour));
    }

    [Fact]
    public void OneUndoPutsTheOverriddenAnchorBack()
    {
        var h = new Harness();
        h.AddAnchor(4, fret: 2);

        h.Service.CommitHandShapeDragAndDrop(
            [], [], [], [], [], [],
            [], [h.Anchor(4, fret: 9)]);
        h.History.Undo();

        var restored = Assert.Single(h.Level.Anchors);
        Assert.Equal(2, restored.Fret);
    }

    /// <summary>The pure rule, independent of the commit path.</summary>
    [Fact]
    public void TheRuleIsPositionAlone_NotPositionAndFret()
    {
        var service = new StrictCollisionService();
        var existing = new FretAnchor { Position = new FractionalPosition(4), Fret = 2, Width = 4 };
        var incoming = new FretAnchor { Position = new FractionalPosition(4), Fret = 12, Width = 4 };

        var result = service.CalculateAnchorCollisions([incoming], [existing], []);

        Assert.Same(existing, Assert.Single(result.AnchorsToDestroy));
    }

    private sealed class Harness
    {
        private readonly ChartProject _project = new();

        public Harness()
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var config = new FakeConfigurationService();
            var scope = new EditingScope(State);
            Service = new HighwayEditingService(State, Selection, History, new StrictCollisionService(),
                config, scope, new EditTargetService(Selection, scope, config), logger);

            for (var i = 0; i < 32; i++) _project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });

            Arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            Arrangement.Levels.Add(new ChartLevel());
            _project.Arrangements.Add(Arrangement);
            State.ActiveProject = _project;
            State.ActiveArrangement = Arrangement;
            State.ActiveLevel = Arrangement.Levels[0];
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public HistoryService History { get; }
        public HighwayEditingService Service { get; }
        public Arrangement Arrangement { get; }
        public ChartLevel Level => Arrangement.Levels[0];

        public FretAnchor Anchor(int beat, int fret) =>
            new() { Position = new FractionalPosition(beat), Fret = fret, Width = 4 };

        public FretAnchor AddAnchor(int beat, int fret)
        {
            var anchor = Anchor(beat, fret);
            Level.Anchors.Add(anchor);
            return anchor;
        }
    }
}
