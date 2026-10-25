using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;

namespace DotCharter.Tests.Services.Editing.Highway;

/// <summary>
///     A chord and the hand shape drawn over it are ONE authored decision, so an edit that
///     moves the chord onto another template has to take its hand shape along, in the same
///     undo step, unless <c>AppConfig.SyncHandShapeToChordTemplate</c> is off. Before, the
///     fret verbs, the string toggles and the drag-drop merge repointed only the chord: the
///     3D highway kept drawing the old grip, and the bar stopped matching its chord.
/// </summary>
public sealed class ChordHandShapeSyncTests
{
    [Fact]
    public void AFretChange_MovesTheHandShapeOntoTheChordsNewTemplate()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 3), (1, 5));
        var chord = h.AddChord(2, 0, (0, 3), (1, 5));
        var bar = h.AddHandShape(2, 4, 0);
        h.KeepInUse(0);
        h.Selection.SelectedChords.Add(chord);

        h.Service.MoveSelectedFretUp();

        Assert.NotEqual(0, chord.TemplateId);
        Assert.Equal(chord.TemplateId, bar.TemplateId);
        Assert.Equal(new[] { 4, 6 }, h.Arrangement.ChordTemplates.Single(t => t.Id == bar.TemplateId)
            .Frets.OrderBy(f => f.Key).Select(f => f.Value));
    }

    [Fact]
    public void AFretChange_AndTheHandShapeFollowing_AreOneUndoStep()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 3), (1, 5));
        var chord = h.AddChord(2, 0, (0, 3), (1, 5));
        var bar = h.AddHandShape(2, 4, 0);
        h.KeepInUse(0);
        h.Selection.SelectedChords.Add(chord);

        h.Service.MoveSelectedFretUp();
        Assert.Single(h.History.GetUndoSteps());

        h.History.Undo();

        Assert.Equal(0, chord.TemplateId);
        Assert.Equal(0, bar.TemplateId);
        Assert.Single(h.Arrangement.ChordTemplates);

        h.History.Redo();

        Assert.Equal(chord.TemplateId, bar.TemplateId);
        Assert.NotEqual(0, bar.TemplateId);
    }

    [Fact]
    public void AFretChange_LeavesTheHandShapeAloneWhenTheSyncIsOff()
    {
        var h = new Harness();
        h.Config.AppConfig.SyncHandShapeToChordTemplate = false;
        h.AddTemplate(0, (0, 3), (1, 5));
        var chord = h.AddChord(2, 0, (0, 3), (1, 5));
        var bar = h.AddHandShape(2, 4, 0);
        h.Selection.SelectedChords.Add(chord);

        h.Service.MoveSelectedFretUp();

        Assert.NotEqual(0, chord.TemplateId);
        Assert.Equal(0, bar.TemplateId);
    }

    [Fact]
    public void AFretChange_ReusingAnExistingTemplate_PointsTheHandShapeAtThatTemplate()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 3), (1, 5));
        h.AddTemplate(1, (0, 4), (1, 6));
        var chord = h.AddChord(2, 0, (0, 3), (1, 5));
        var bar = h.AddHandShape(2, 4, 0);
        h.KeepInUse(0);
        h.Selection.SelectedChords.Add(chord);

        h.Service.MoveSelectedFretUp();

        Assert.Equal(1, chord.TemplateId);
        Assert.Equal(1, bar.TemplateId);
        Assert.Equal(2, h.Arrangement.ChordTemplates.Count);
    }

    [Fact]
    public void AHandShapeOnAnotherTemplate_IsNotDraggedOntoTheChord()
    {
        // The bar was deliberately not in step with its chord (the spacing pass leaves a
        // bar on the full shape of a reduced chord): an edit must not overwrite that.
        var h = new Harness();
        h.AddTemplate(0, (0, 3), (1, 5));
        h.AddTemplate(1, (0, 3), (1, 5), (2, 5));
        var chord = h.AddChord(2, 0, (0, 3), (1, 5));
        var bar = h.AddHandShape(2, 4, 1);
        h.KeepInUse(0);
        h.Selection.SelectedChords.Add(chord);

        h.Service.MoveSelectedFretUp();

        Assert.Equal(1, bar.TemplateId);
    }

    [Fact]
    public void ABarOverTwoChords_StaysPutWhenOnlyOneOfThemChanges()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 3), (1, 5));
        var first = h.AddChord(2, 0, (0, 3), (1, 5));
        var second = h.AddChord(3, 0, (0, 3), (1, 5));
        var bar = h.AddHandShape(2, 4, 0);
        h.Selection.SelectedChords.Add(first);

        h.Service.MoveSelectedFretUp();

        Assert.NotEqual(0, first.TemplateId);
        Assert.Equal(0, second.TemplateId);
        Assert.Equal(0, bar.TemplateId);
    }

    [Fact]
    public void ABarOverTwoChords_FollowsWhenBothChange()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 3), (1, 5));
        var first = h.AddChord(2, 0, (0, 3), (1, 5));
        var second = h.AddChord(3, 0, (0, 3), (1, 5));
        var bar = h.AddHandShape(2, 4, 0);
        h.KeepInUse(0);
        h.Selection.SelectedChords.Add(first);
        h.Selection.SelectedChords.Add(second);

        h.Service.MoveSelectedFretUp();

        Assert.Equal(first.TemplateId, second.TemplateId);
        Assert.Equal(first.TemplateId, bar.TemplateId);
        Assert.NotEqual(0, bar.TemplateId);
    }

    [Fact]
    public void AHandShapeOutsideTheChord_IsNotTouched()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 3), (1, 5));
        var chord = h.AddChord(2, 0, (0, 3), (1, 5));
        var elsewhere = h.AddHandShape(6, 8, 0);
        h.Selection.SelectedChords.Add(chord);

        h.Service.MoveSelectedFretUp();

        Assert.Equal(0, elsewhere.TemplateId);
    }

    [Fact]
    public void AnOctaveShift_AndAFretValue_BothTakeTheHandShapeAlong()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 3), (1, 5));
        var chord = h.AddChord(2, 0, (0, 3), (1, 5));
        var bar = h.AddHandShape(2, 4, 0);
        h.KeepInUse(0);
        h.Selection.SelectedChords.Add(chord);

        h.Service.SetSelectedFret(7);

        Assert.Equal(chord.TemplateId, bar.TemplateId);
        Assert.NotEqual(0, bar.TemplateId);

        h.Service.MoveSelectedFretByOctave(1);

        Assert.Equal(chord.TemplateId, bar.TemplateId);
    }

    [Fact]
    public void AStringShift_MovesTheHandShapeOntoTheChordsNewTemplate()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 3), (1, 5));
        var chord = h.AddChord(2, 0, (0, 3), (1, 5));
        var bar = h.AddHandShape(2, 4, 0);
        h.KeepInUse(0);
        h.Selection.SelectedChords.Add(chord);

        h.Service.MoveSelectedStringUp(6);

        Assert.NotEqual(0, chord.TemplateId);
        Assert.Equal(chord.TemplateId, bar.TemplateId);
    }

    [Fact]
    public void PlacingAStringOnAChord_MovesTheHandShapeOntoTheChordsNewTemplate()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 5), (1, 7));
        var chord = h.AddChord(2, 0, (0, 5), (1, 7));
        var bar = h.AddHandShape(2, 4, 0);
        h.KeepInUse(0);
        h.State.PreviewNoteFret = 7;

        h.Service.ApplyPlacement(new FractionalPosition(2), 2);

        Assert.NotEqual(0, chord.TemplateId);
        Assert.Equal(chord.TemplateId, bar.TemplateId);
        Assert.Single(h.History.GetUndoSteps());
    }

    [Fact]
    public void PlacingAStringOnAChord_LeavesTheHandShapeWhenTheSyncIsOff()
    {
        var h = new Harness();
        h.Config.AppConfig.SyncHandShapeToChordTemplate = false;
        h.AddTemplate(0, (0, 5), (1, 7));
        h.AddChord(2, 0, (0, 5), (1, 7));
        var bar = h.AddHandShape(2, 4, 0);
        h.State.PreviewNoteFret = 7;

        h.Service.ApplyPlacement(new FractionalPosition(2), 2);

        Assert.Equal(0, bar.TemplateId);
    }

    [Fact]
    public void ARightClickStringEdit_MovesTheHandShapeOntoTheChordsNewTemplate()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 5), (1, 7), (2, 7));
        var chord = h.AddChord(2, 0, (0, 5), (1, 7), (2, 7));
        var bar = h.AddHandShape(2, 4, 0);
        h.KeepInUse(0);

        h.Service.HandleRightClickChord(chord, 2);

        Assert.Equal(2, chord.ChordNotes.Count);
        Assert.NotEqual(0, chord.TemplateId);
        Assert.Equal(chord.TemplateId, bar.TemplateId);
        Assert.Single(h.History.GetUndoSteps());

        h.History.Undo();

        Assert.Equal(0, bar.TemplateId);
    }

    [Fact]
    public void ADroppedNoteMergedIntoAChord_MovesTheHandShapeOntoTheChordsNewTemplate()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 5), (1, 7));
        var chord = h.AddChord(2, 0, (0, 5), (1, 7));
        var bar = h.AddHandShape(2, 4, 0);
        h.KeepInUse(0);
        var dropped = new GuitarNote { Position = new FractionalPosition(2), StringIndex = 2, Fret = 7 };

        h.Service.CommitHandShapeDragAndDrop([], [dropped], [], [], [], [], [], []);

        var merged = Assert.Single(h.State.PlacedChords);
        Assert.Equal(3, merged.ChordNotes.Count);
        Assert.NotEqual(0, merged.TemplateId);
        Assert.Equal(merged.TemplateId, bar.TemplateId);
        Assert.Single(h.History.GetUndoSteps());

        h.History.Undo();

        Assert.Same(chord, Assert.Single(h.State.PlacedChords));
        Assert.Equal(0, bar.TemplateId);
    }

    [Fact]
    public void ADroppedNoteMergedIntoAChord_LeavesTheHandShapeWhenTheSyncIsOff()
    {
        var h = new Harness();
        h.Config.AppConfig.SyncHandShapeToChordTemplate = false;
        h.AddTemplate(0, (0, 5), (1, 7));
        h.AddChord(2, 0, (0, 5), (1, 7));
        var bar = h.AddHandShape(2, 4, 0);
        var dropped = new GuitarNote { Position = new FractionalPosition(2), StringIndex = 2, Fret = 7 };

        h.Service.CommitHandShapeDragAndDrop([], [dropped], [], [], [], [], [], []);

        Assert.Equal(0, bar.TemplateId);
    }

    internal sealed class Harness
    {
        public Harness()
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var collisions = new StrictCollisionService();
            Scope = new EditingScope(State);
            Service = new HighwayEditingService(State, Selection, History, collisions, Config, Scope,
                new EditTargetService(Selection, Scope, Config), logger);

            var project = new ChartProject();
            for (var i = 0; i < 16; i++)
                project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
            Arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            Arrangement.Levels.Add(new ChartLevel());
            project.Arrangements.Add(Arrangement);

            State.ActiveProject = project;
            State.ActiveArrangement = Arrangement;
            State.ActiveLevel = Arrangement.Levels[0];
        }

        public FakeConfigurationService Config { get; } = new();
        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public HistoryService History { get; }
        public EditingScope Scope { get; }
        public HighwayEditingService Service { get; }
        public Arrangement Arrangement { get; }

        public ChordTemplate AddTemplate(int id, params (int stringIdx, int fret)[] frets)
        {
            var t = new ChordTemplate { Id = id };
            foreach (var (stringIdx, fret) in frets) t.Frets[stringIdx] = fret;
            Arrangement.ChordTemplates.Add(t);
            return t;
        }

        public Chord AddChord(int beat, int templateId, params (int stringIdx, int fret)[] notes)
        {
            var c = new Chord { Position = new FractionalPosition(beat), TemplateId = templateId };
            foreach (var (stringIdx, fret) in notes)
                c.ChordNotes.Add(new ChordNote { StringIndex = stringIdx, Fret = fret });
            State.PlacedChords.Add(c);
            return c;
        }

        /// <summary>A bar far from every edit that keeps <paramref name="templateId" /> in use, so the edit does not strand it.</summary>
        public HandShape KeepInUse(int templateId) => AddHandShape(12, 13, templateId);

        public HandShape AddHandShape(int start, int end, int templateId)
        {
            var bar = new HandShape
            {
                Position = new FractionalPosition(start),
                EndPosition = new FractionalPosition(end),
                TemplateId = templateId
            };
            Arrangement.Levels[0].HandShapes.Add(bar);
            return bar;
        }
    }
}
