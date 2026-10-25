using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Catalog;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Tests.Services.Editing.Highway;

/// <summary>
///     Every timeline, highway and delete path that can leave a chord template with no user takes it
///     out in the SAME undo node, and one undo brings the template, its id and every user back. Each
///     test names the path, then asserts the removal, the single undo step, the undo and the redo.
///     The template editor, the wheel resize and the shape list's merge have their own classes.
/// </summary>
public sealed class StrandedShapePathTests
{
    [Fact]
    public void ATimelineFretVerb_TakesTheShapeTheChordLeftOutWithIt()
    {
        var h = new Harness();
        var old = h.AddTemplate(0, (0, 3), (1, 5));
        var chord = h.AddChord(2, 0, (0, 3), (1, 5));
        h.Selection.SelectedChords.Add(chord);

        h.Service.MoveSelectedFretUp();

        var survivor = Assert.Single(h.Arrangement.ChordTemplates);
        Assert.NotSame(old, survivor);
        Assert.Equal([(0, 4), (1, 6)], survivor.Frets.OrderBy(f => f.Key).Select(f => (f.Key, f.Value)));
        Assert.Equal(survivor.Id, chord.TemplateId);
        Assert.Single(h.History.GetUndoSteps());

        h.History.Undo();
        Assert.Same(old, Assert.Single(h.Arrangement.ChordTemplates));
        Assert.Equal((0, 0), (old.Id, chord.TemplateId));
        Assert.Equal([3, 5], chord.ChordNotes.Select(n => n.Fret));

        h.History.Redo();
        Assert.NotSame(old, Assert.Single(h.Arrangement.ChordTemplates));
        Assert.Equal([4, 6], chord.ChordNotes.Select(n => n.Fret));
    }

    [Fact]
    public void ATimelineFretVerb_KeepsTheShapeWhileAnotherChordStillUsesIt()
    {
        var h = new Harness();
        var old = h.AddTemplate(0, (0, 3), (1, 5));
        var chord = h.AddChord(2, 0, (0, 3), (1, 5));
        var sibling = h.AddChord(6, 0, (0, 3), (1, 5));
        h.Selection.SelectedChords.Add(chord);

        h.Service.MoveSelectedFretUp();

        Assert.Equal(2, h.Arrangement.ChordTemplates.Count);
        Assert.Same(old, h.Arrangement.ChordTemplates[0]);
        Assert.Equal(0, sibling.TemplateId);
        Assert.Equal(1, chord.TemplateId);
    }

    [Fact]
    public void AnOctaveShiftAndAFretValue_TakeTheShapeTheyLeaveOutWithIt()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 3), (1, 5));
        var chord = h.AddChord(2, 0, (0, 3), (1, 5));
        h.Selection.SelectedChords.Add(chord);

        h.Service.MoveSelectedFretByOctave(1);
        Assert.Single(h.Arrangement.ChordTemplates);
        Assert.Equal([15, 17], chord.ChordNotes.Select(n => n.Fret));

        h.Service.SetSelectedFret(7);
        var survivor = Assert.Single(h.Arrangement.ChordTemplates);
        Assert.Equal(survivor.Id, chord.TemplateId);

        h.History.Undo();
        Assert.Equal([15, 17], Assert.Single(h.Arrangement.ChordTemplates).Frets.Values.OrderBy(f => f));
    }

    [Fact]
    public void AStringShift_TakesTheShapeTheChordLeftOutWithIt()
    {
        var h = new Harness();
        var old = h.AddTemplate(0, (0, 3), (1, 5));
        var chord = h.AddChord(2, 0, (0, 3), (1, 5));
        h.Selection.SelectedChords.Add(chord);

        h.Service.MoveSelectedStringUp(6);

        var survivor = Assert.Single(h.Arrangement.ChordTemplates);
        Assert.NotSame(old, survivor);
        Assert.Equal(survivor.Id, chord.TemplateId);

        h.History.Undo();
        Assert.Same(old, Assert.Single(h.Arrangement.ChordTemplates));
    }

    [Fact]
    public void APlacementIntoAChord_TakesTheShapeTheReshapeLeftOutWithIt()
    {
        var h = new Harness();
        var old = h.AddTemplate(0, (0, 5), (1, 7));
        var chord = h.AddChord(2, 0, (0, 5), (1, 7));
        h.State.PreviewNoteFret = 7;

        h.Service.ApplyPlacement(new FractionalPosition(2), 2);

        var survivor = Assert.Single(h.Arrangement.ChordTemplates);
        Assert.Equal(3, survivor.Frets.Count);
        Assert.Equal(survivor.Id, chord.TemplateId);
        Assert.Single(h.History.GetUndoSteps());

        h.History.Undo();
        Assert.Same(old, Assert.Single(h.Arrangement.ChordTemplates));
        Assert.Equal(2, chord.ChordNotes.Count);

        h.History.Redo();
        Assert.Equal(3, Assert.Single(h.Arrangement.ChordTemplates).Frets.Count);
    }

    [Fact]
    public void ARightClickStringEdit_TakesTheShapeTheReshapeLeftOutWithIt()
    {
        var h = new Harness();
        var old = h.AddTemplate(0, (0, 5), (1, 7), (2, 7));
        var chord = h.AddChord(2, 0, (0, 5), (1, 7), (2, 7));

        h.Service.HandleRightClickChord(chord, 2);

        var survivor = Assert.Single(h.Arrangement.ChordTemplates);
        Assert.NotSame(old, survivor);
        Assert.Equal(2, survivor.Frets.Count);
        Assert.Single(h.History.GetUndoSteps());

        h.History.Undo();
        Assert.Same(old, Assert.Single(h.Arrangement.ChordTemplates));
        Assert.Equal(3, chord.ChordNotes.Count);
    }

    [Fact]
    public void AChordThatCollapsesIntoANote_TakesItsShapeOutWithIt()
    {
        var h = new Harness();
        var old = h.AddTemplate(0, (0, 5), (1, 7));
        var chord = h.AddChord(2, 0, (0, 5), (1, 7));

        h.Service.HandleRightClickChord(chord, 1);

        Assert.Empty(h.Arrangement.ChordTemplates);
        Assert.Empty(h.Level.Chords);
        Assert.Single(h.Level.Notes);
        Assert.Single(h.History.GetUndoSteps());

        h.History.Undo();
        Assert.Same(old, Assert.Single(h.Arrangement.ChordTemplates));
        Assert.Same(chord, Assert.Single(h.Level.Chords));
        Assert.Empty(h.Level.Notes);

        h.History.Redo();
        Assert.Empty(h.Arrangement.ChordTemplates);
    }

    [Fact]
    public void ACollapsingPlacement_TakesTheShapeOutWithIt()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 5), (1, 7));
        h.AddChord(2, 0, (0, 5), (1, 7));
        h.State.PreviewNoteFret = 7;

        h.Service.ApplyPlacement(new FractionalPosition(2), 1);

        Assert.Empty(h.Arrangement.ChordTemplates);
        h.History.Undo();
        Assert.Single(h.Arrangement.ChordTemplates);
    }

    [Fact]
    public void ADroppedNoteMergedIntoAChord_TakesTheOldShapeOutWithIt()
    {
        var h = new Harness();
        var old = h.AddTemplate(0, (0, 5), (1, 7));
        h.AddChord(2, 0, (0, 5), (1, 7));
        var dropped = new GuitarNote { Position = new FractionalPosition(2), StringIndex = 2, Fret = 7 };

        h.Service.CommitHandShapeDragAndDrop([], [dropped], [], [], [], [], [], []);

        var merged = Assert.Single(h.Level.Chords);
        var survivor = Assert.Single(h.Arrangement.ChordTemplates);
        Assert.Equal(3, survivor.Frets.Count);
        Assert.Equal(survivor.Id, merged.TemplateId);
        Assert.Single(h.History.GetUndoSteps());

        h.History.Undo();
        Assert.Same(old, Assert.Single(h.Arrangement.ChordTemplates));
        Assert.Equal(0, h.Level.Chords.Single().TemplateId);
    }

    [Fact]
    public void ADragDropThatRunsAChordOver_TakesItsShapeOutWithIt()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 5), (1, 7));
        h.AddTemplate(1, (0, 2), (1, 4));
        var victim = h.AddChord(2, 0, (0, 5), (1, 7));
        var dragged = h.AddChord(8, 1, (0, 2), (1, 4));
        var landed = dragged.Clone();
        landed.Position = new FractionalPosition(2);

        h.Service.CommitHandShapeDragAndDrop([], [], [dragged], [landed], [], [], [], []);

        Assert.DoesNotContain(victim, h.Level.Chords);
        var survivor = Assert.Single(h.Arrangement.ChordTemplates);
        Assert.Equal([(0, 2), (1, 4)], survivor.Frets.OrderBy(f => f.Key).Select(f => (f.Key, f.Value)));
        Assert.Equal(survivor.Id, Assert.Single(h.Level.Chords).TemplateId);

        h.History.Undo();
        Assert.Equal(2, h.Arrangement.ChordTemplates.Count);
        Assert.Equal([0, 1], h.Level.Chords.OrderBy(c => c.Position).Select(c => c.TemplateId));
    }

    [Fact]
    public void ADraggedChord_KeepsItsShapeWhenItMovesToAnotherBeat()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 5), (1, 7));
        var chord = h.AddChord(2, 0, (0, 5), (1, 7));
        var landed = chord.Clone();
        landed.Position = new FractionalPosition(6);

        h.Service.CommitHandShapeDragAndDrop([], [], [chord], [landed], [], [], [], []);

        Assert.Single(h.Arrangement.ChordTemplates);
        Assert.Equal(0, Assert.Single(h.Level.Chords).TemplateId);
    }

    [Fact]
    public void ADragDropThatReplacesAHandShape_TakesTheShapeItOwnedOutWithIt()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 5), (1, 7));
        h.AddTemplate(1, (0, 2), (1, 4));
        h.AddChord(2, 0, (0, 5), (1, 7));
        var bar = h.AddHandShape(2, 4, 1);
        var replacement = new HandShape
        {
            Position = new FractionalPosition(2), EndPosition = new FractionalPosition(4), TemplateId = 0
        };

        h.Service.CommitHandShapeDragAndDrop([], [], [], [], [bar], [replacement], [], []);

        var survivor = Assert.Single(h.Arrangement.ChordTemplates);
        Assert.Equal([(0, 5), (1, 7)], survivor.Frets.OrderBy(f => f.Key).Select(f => (f.Key, f.Value)));
        Assert.Equal((0, 0), (survivor.Id, Assert.Single(h.Level.HandShapes).TemplateId));

        h.History.Undo();
        Assert.Equal(2, h.Arrangement.ChordTemplates.Count);
        Assert.Same(bar, Assert.Single(h.Level.HandShapes));
        Assert.Equal(1, bar.TemplateId);
    }

    [Fact]
    public void ARemovedHandShape_TakesItsPrivateShapeOutWithIt()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 5), (1, 7));
        h.AddTemplate(1, (0, 2), (1, 4));
        h.AddChord(2, 0, (0, 5), (1, 7));
        var bar = h.AddHandShape(8, 10, 1);

        h.Service.RemoveHandShape(bar);

        Assert.Empty(h.Level.HandShapes);
        var survivor = Assert.Single(h.Arrangement.ChordTemplates);
        Assert.Equal(5, survivor.Frets[0]);
        Assert.Single(h.History.GetUndoSteps());

        h.History.Undo();
        Assert.Equal([0, 1], h.Arrangement.ChordTemplates.Select(t => t.Id));
        Assert.Same(bar, Assert.Single(h.Level.HandShapes));
        Assert.Equal(1, bar.TemplateId);

        h.History.Redo();
        Assert.Single(h.Arrangement.ChordTemplates);
    }

    [Fact]
    public void ARemovedHandShape_KeepsAShapeItsChordStillUses()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 5), (1, 7));
        h.AddChord(2, 0, (0, 5), (1, 7));
        var bar = h.AddHandShape(2, 4, 0);

        h.Service.RemoveHandShape(bar);

        Assert.Single(h.Arrangement.ChordTemplates);
    }

    [Fact]
    public void ASplit_NeverTakesAShapeThatIsStillInUse()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 3), (1, 5));
        h.AddTemplate(1, (0, 3), (1, 5));
        h.AddChord(1, 0, (0, 3), (1, 5));
        var pivot = h.AddChord(4, 1, (0, 3), (1, 5));
        h.AddHandShape(0, 8, 0);
        h.Selection.SelectedChords.Add(pivot);

        h.Splitter.SplitAtSelection(h.Track);

        Assert.Equal(2, h.Level.HandShapes.Count);
        Assert.Equal(2, h.Arrangement.ChordTemplates.Count);
        Assert.Single(h.History.GetUndoSteps());
        h.History.Undo();
        Assert.Equal(2, h.Arrangement.ChordTemplates.Count);
    }

    [Fact]
    public void ASplitWhoseTailMintsAGrip_KeepsEveryShape_AndUndoRemovesOnlyTheMintedOne()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 3), (1, 5));
        h.AddChord(1, 0, (0, 3), (1, 5));
        h.AddNote(6, 2, 7);
        var pivot = h.AddChord(4, 0, (0, 3), (1, 5));
        h.AddHandShape(0, 8, 0);
        h.Selection.SelectedChords.Add(pivot);

        h.Splitter.SplitAtSelection(h.Track);

        Assert.Equal(2, h.Arrangement.ChordTemplates.Count);
        h.History.Undo();
        Assert.Single(h.Arrangement.ChordTemplates);
    }

    [Fact]
    public void DeletingAChord_TakesItsShapeOutWithIt()
    {
        var h = new Harness();
        var kept = h.AddTemplate(0, (0, 5), (1, 7));
        var gone = h.AddTemplate(1, (0, 2), (1, 4));
        h.AddChord(2, 0, (0, 5), (1, 7));
        var doomed = h.AddChord(6, 1, (0, 2), (1, 4));
        h.Selection.SelectedChords.Add(doomed);

        h.Coordinator.DeleteSelected(EditorFocusArea.NoteHighway);

        Assert.Same(kept, Assert.Single(h.Arrangement.ChordTemplates));
        Assert.Single(h.Level.Chords);
        Assert.Single(h.History.GetUndoSteps());

        h.History.Undo();
        Assert.Equal([kept, gone], h.Arrangement.ChordTemplates);
        Assert.Equal([0, 1], h.Arrangement.ChordTemplates.Select(t => t.Id));
        Assert.Equal(1, doomed.TemplateId);

        h.History.Redo();
        Assert.Single(h.Arrangement.ChordTemplates);
    }

    [Fact]
    public void DeletingAChord_RenumbersTheShapesAfterItForEveryUser()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 5), (1, 7));
        h.AddTemplate(1, (0, 2), (1, 4));
        h.AddTemplate(2, (0, 9), (1, 9));
        var doomed = h.AddChord(2, 0, (0, 5), (1, 7));
        var onLast = h.AddChord(6, 2, (0, 9), (1, 9));
        var bar = h.AddHandShape(6, 8, 2);
        var onMiddle = h.AddChord(10, 1, (0, 2), (1, 4));
        h.Selection.SelectedChords.Add(doomed);

        h.Coordinator.DeleteSelected(EditorFocusArea.NoteHighway);

        Assert.Equal([0, 1], h.Arrangement.ChordTemplates.Select(t => t.Id));
        Assert.Equal((0, 1, 1), (onMiddle.TemplateId, onLast.TemplateId, bar.TemplateId));

        h.History.Undo();
        Assert.Equal((1, 2, 2), (onMiddle.TemplateId, onLast.TemplateId, bar.TemplateId));
    }

    [Fact]
    public void DeletingAChord_KeepsTheShapeItsHandShapeStillUses()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 5), (1, 7));
        var doomed = h.AddChord(2, 0, (0, 5), (1, 7));
        h.AddHandShape(2, 4, 0);
        h.Selection.SelectedChords.Add(doomed);

        h.Coordinator.DeleteSelected(EditorFocusArea.NoteHighway);

        Assert.Single(h.Arrangement.ChordTemplates);
    }

    [Fact]
    public void DeletingTheLastUseAcrossTwoLevels_NeedsBothToGo()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 5), (1, 7));
        var first = h.AddChord(2, 0, (0, 5), (1, 7));
        var otherLevel = new ChartLevel { DifficultyIndex = 1 };
        otherLevel.Chords.Add(new Chord { TemplateId = 0, Position = new FractionalPosition(2) });
        h.Arrangement.Levels.Add(otherLevel);
        h.Selection.SelectedChords.Add(first);

        h.Coordinator.DeleteSelected(EditorFocusArea.NoteHighway);

        Assert.Single(h.Arrangement.ChordTemplates);
        Assert.Equal(0, otherLevel.Chords[0].TemplateId);
    }

    [Fact]
    public void DeletingNotesAndTheirHandShapes_TakesThePrivateShapeOutWithThem()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 5), (1, 7));
        h.AddTemplate(1, (0, 2));
        h.AddChord(10, 0, (0, 5), (1, 7));
        var note = h.AddNote(2, 0, 2);
        var bar = h.AddHandShape(2, 4, 1);
        h.Selection.SelectedNotes.Add(note);

        h.Coordinator.DeleteSelectedRelated(EditorFocusArea.NoteHighway);

        Assert.Empty(h.Level.HandShapes);
        Assert.Equal(0, Assert.Single(h.Arrangement.ChordTemplates).Id);
        Assert.Single(h.History.GetUndoSteps());

        h.History.Undo();
        Assert.Equal(2, h.Arrangement.ChordTemplates.Count);
        Assert.Same(bar, Assert.Single(h.Level.HandShapes));
        Assert.Equal(1, bar.TemplateId);
        Assert.Same(note, Assert.Single(h.Level.Notes));
    }

    [Fact]
    public void DeletingJustANote_LeavesEveryShapeAlone()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 5), (1, 7));
        var note = h.AddNote(2, 0, 2);
        h.AddHandShape(2, 4, 0);
        h.Selection.SelectedNotes.Add(note);

        h.Coordinator.DeleteSelected(EditorFocusArea.NoteHighway);

        Assert.Single(h.Arrangement.ChordTemplates);
        Assert.Single(h.Level.HandShapes);
    }

    private sealed class Harness
    {
        private readonly ChartProject _project = new();

        public Harness()
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            // Min note distance off, so the spacing pass never moves a bar the test placed.
            Config.AppConfig.MinNoteDistanceMode = MinNoteDistanceMode.Milliseconds;
            Config.AppConfig.MinNoteDistanceMs = 0;
            var scope = new EditingScope(State);
            Service = new HighwayEditingService(State, Selection, History, new StrictCollisionService(),
                Config, scope, new EditTargetService(Selection, scope, Config), logger);

            for (var i = 0; i < 32; i++)
                _project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });

            Arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            Arrangement.Levels.Add(new ChartLevel());
            _project.Arrangements.Add(Arrangement);
            State.ActiveProject = _project;
            State.ActiveArrangement = Arrangement;
            State.ActiveLevel = Arrangement.Levels[0];

            Splitter = new HandShapeSplitter(new FingeringGenerationService(), Service, Selection, Config, logger);
            var tracks = new TrackContextRegistry(State, Selection);
            Track = new TrackEditorContext(Arrangement, Selection) { Project = _project, Level = Level };
            var catalogDir = Path.Combine(Path.GetTempPath(), "dotcharter-stranded-shape-tests-no-such-dir");
            Coordinator = new GlobalEditingCoordinator(State, Selection, History,
                new ClipboardService(logger), new TempoService(logger), new BeatScaleService(logger),
                Service, Config, new SelectLikeService(logger), new FingeringGenerationService(),
                tracks, new PhraseCatalogService(catalogDir, new RscpChartSerializer(), logger), logger,
                FakeLocalization.English());
        }

        public FakeConfigurationService Config { get; } = new();
        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public HistoryService History { get; }
        public HighwayEditingService Service { get; }
        public HandShapeSplitter Splitter { get; }
        public GlobalEditingCoordinator Coordinator { get; }
        public Arrangement Arrangement { get; }
        public TrackEditorContext Track { get; }
        public ChartLevel Level => Arrangement.Levels[0];

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

        public GuitarNote AddNote(int beat, int stringIndex, int fret)
        {
            var note = new GuitarNote { Position = new FractionalPosition(beat), StringIndex = stringIndex, Fret = fret };
            Level.Notes.Add(note);
            return note;
        }

        public HandShape AddHandShape(int start, int end, int templateId)
        {
            var bar = new HandShape
            {
                Position = new FractionalPosition(start),
                EndPosition = new FractionalPosition(end),
                TemplateId = templateId
            };
            Level.HandShapes.Add(bar);
            return bar;
        }
    }
}
