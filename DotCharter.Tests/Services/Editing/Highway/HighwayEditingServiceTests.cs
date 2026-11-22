using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;

namespace DotCharter.Tests.Services.Editing.Highway;

public sealed class HighwayEditingServiceTests
{
    [Fact]
    public void SpacingRedo_PrunesTheRestoredAnchorSelection()
    {
        var h = new Harness();
        var project = h.State.ActiveProject!;
        var level = h.State.ActiveLevel!;
        level.Anchors.Add(new FretAnchor(new FractionalPosition(0), 5));
        var redundant = new FretAnchor(new FractionalPosition(1), 5);
        level.Anchors.Add(redundant);
        h.History.Do(ProjectSpacingEdit.Plan(project, h.Config.AppConfig, () => { }));
        Assert.Single(level.Anchors);
        h.History.Undo();
        Assert.Same(redundant, level.Anchors[1]);
        h.Selection.SelectedAnchors.Add(redundant);

        h.History.Redo();

        Assert.DoesNotContain(redundant, level.Anchors);
        Assert.Empty(h.Selection.SelectedAnchors);
        var oldFret = redundant.Fret;
        h.Service.MoveSelectedFretUp();
        Assert.Equal(oldFret, redundant.Fret);
        h.Service.Dispose();
    }

    [Fact]
    public void OpeningAnotherProject_ReleasesTheCachedEditingGraph()
    {
        var h = new Harness();
        h.AddNote(1, 0, 5);
        _ = h.Service.GetLinkNextUpdater();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        Assert.NotNull(typeof(HighwayEditingService).GetField("_linkNextUpdater", flags)!.GetValue(h.Service));

        h.State.ActiveProject = new ChartProject();
        h.State.ActiveArrangement = null;
        h.History.Clear();

        foreach (var name in new[] { "_linkNextUpdater", "_spacingLevel", "_spacingBeats" })
            Assert.Null(typeof(HighwayEditingService).GetField(name, flags)!.GetValue(h.Service));
    }

    [Fact]
    public void RemoveNote_RemovesFromPlacedNotesAndIsUndoable()
    {
        var h = new Harness();
        var note = h.AddNote(2, 2, 5);
        Assert.Single(h.State.PlacedNotes);

        h.Service.RemoveNote(note);
        Assert.Empty(h.State.PlacedNotes);

        h.History.Undo();
        Assert.Single(h.State.PlacedNotes);
    }

    [Fact]
    public void MoveSelectedFretUp_IncrementsFret()
    {
        var h = new Harness();
        var note = h.AddNote(2, 2, 5);
        h.Selection.SelectedNotes.Add(note);

        h.Service.MoveSelectedFretUp();

        Assert.Equal(6, h.State.PlacedNotes[0].Fret);
    }

    [Fact]
    public void MoveSelectedFretDown_DecrementsFret_ButNotBelowZero()
    {
        var h = new Harness();
        var note = h.AddNote(2, 2, 1);
        h.Selection.SelectedNotes.Add(note);

        h.Service.MoveSelectedFretDown();
        Assert.Equal(0, h.State.PlacedNotes[0].Fret);

        h.Service.MoveSelectedFretDown();
        Assert.Equal(0, h.State.PlacedNotes[0].Fret);
    }

    [Fact]
    public void MoveSelectedStringUp_AdjustsStringIndex()
    {
        var h = new Harness();
        var note = h.AddNote(2, 3, 5);
        h.Selection.SelectedNotes.Add(note);

        h.Service.MoveSelectedStringUp(6);

        // "Up" on the highway corresponds to a higher string index in this codebase.
        Assert.Equal(4, h.State.PlacedNotes[0].StringIndex);
    }

    [Fact]
    public void MoveSelectedStringDown_AdjustsStringIndex()
    {
        var h = new Harness();
        var note = h.AddNote(2, 3, 5);
        h.Selection.SelectedNotes.Add(note);

        h.Service.MoveSelectedStringDown();

        Assert.Equal(2, h.State.PlacedNotes[0].StringIndex);
    }

    [Fact]
    public void ToggleSelectedHopo_TogglesHopoFlag()
    {
        var h = new Harness();
        var note = h.AddNote(2, 2, 5);
        h.Selection.SelectedNotes.Add(note);
        var initial = note.Hopo;

        h.Service.ToggleSelectedHopo();

        Assert.NotEqual(initial, h.State.PlacedNotes[0].Hopo);
    }

    [Fact]
    public void ToggleSelectedVibrato_TogglesVibratoFlag()
    {
        var h = new Harness();
        var note = h.AddNote(2, 2, 5);
        h.Selection.SelectedNotes.Add(note);
        Assert.False(note.Vibrato);

        h.Service.ToggleSelectedVibrato();
        Assert.True(h.State.PlacedNotes[0].Vibrato);

        h.Service.ToggleSelectedVibrato();
        Assert.False(h.State.PlacedNotes[0].Vibrato);
    }

    [Fact]
    public void ToggleSelectedTremolo_TogglesTremoloFlag()
    {
        var h = new Harness();
        var note = h.AddNote(2, 2, 5);
        h.Selection.SelectedNotes.Add(note);

        h.Service.ToggleSelectedTremolo();
        Assert.True(h.State.PlacedNotes[0].Tremolo);
    }

    [Fact]
    public void Mutation_WithEmptySelection_DoesNothing()
    {
        var h = new Harness();
        h.AddNote(2, 2, 5);

        h.Service.MoveSelectedFretUp();

        Assert.Equal(5, h.State.PlacedNotes[0].Fret);
    }

    [Fact]
    public void AddHandShape_AddsToHandShapesAndIsUndoable()
    {
        var h = new Harness();
        var hs = new HandShape
        {
            Position = new FractionalPosition(2),
            EndPosition = new FractionalPosition(4)
        };

        h.Service.AddHandShape(hs);

        Assert.Single(h.State.HandShapes);

        h.History.Undo();
        Assert.Empty(h.State.HandShapes);
    }

    [Fact]
    public void RemoveHandShape_RemovesFromCollection()
    {
        var h = new Harness();
        var hs = new HandShape
        {
            Position = new FractionalPosition(2),
            EndPosition = new FractionalPosition(4)
        };
        h.State.HandShapes.Add(hs);

        h.Service.RemoveHandShape(hs);

        Assert.Empty(h.State.HandShapes);
    }

    [Fact]
    public void ApplyPlacement_AddsNoteAtTargetPosition()
    {
        var h = new Harness();

        h.Service.ApplyPlacement(new FractionalPosition(2), 1);

        Assert.Single(h.State.PlacedNotes);
        Assert.Equal(1, h.State.PlacedNotes[0].StringIndex);
        Assert.Equal(new FractionalPosition(2), h.State.PlacedNotes[0].Position);
    }

    [Fact]
    public void GetLinkNextUpdater_ReturnsCallableAction()
    {
        var h = new Harness();
        var updater = h.Service.GetLinkNextUpdater();

        // Should not throw with an empty/clean state.
        var ex = Record.Exception(() => updater());
        Assert.Null(ex);
    }

    /// <summary>
    ///     Right-click delete used to remove the shape from the level and leave
    ///     it SELECTED: the inspector's Hand Shape tab kept editing a detached
    ///     object, and the next Delete named it again — whose Ctrl+Z put it back.
    /// </summary>
    [Fact]
    public void RemoveHandShape_DropsItFromTheSelection_SoDeleteThenUndoCannotResurrectIt()
    {
        var h = new Harness();
        var hs = new HandShape
        {
            Position = new FractionalPosition(2),
            EndPosition = new FractionalPosition(4)
        };
        h.State.HandShapes.Add(hs);
        h.Selection.SelectedHandShapes.Add(hs);

        h.Service.RemoveHandShape(hs);

        Assert.Empty(h.State.HandShapes);
        Assert.Empty(h.Selection.SelectedHandShapes);

        // What the coordinator's Delete would have built over the stale selection.
        var stale = new RemoveHandShapesAction(h.State.HandShapes, new[] { hs }, null);
        Assert.False(h.History.Do(stale));
        h.History.Undo(); // undoes the real removal
        Assert.Single(h.State.HandShapes);
        h.History.Redo();
        Assert.Empty(h.State.HandShapes);
    }

    /// <summary>
    ///     <c>RemoveNote</c> deselects before it deletes and
    ///     <c>GlobalEditingCoordinator.BuildDeleteActions</c> clears every
    ///     collection it deletes from — <c>RemoveHandShape</c>, the verb behind
    ///     the hand-shape band's right-click delete, did not.
    ///     <para>
    ///         A deleted bar left in <c>SelectedHandShapes</c> is live state:
    ///         <c>TimelineWheelAdjuster.TryAdjustHandShapeLength</c> reports
    ///         "not targeted" only when the selection AND the hover are empty, so
    ///         every bare wheel notch is CLAIMED by the hand-shape rung and
    ///         applied to a detached object instead of falling through the
    ///         documented adjust chain — and the rung's grip rebuild can mint a
    ///         ChordTemplate for a bar that no longer exists.
    ///     </para>
    /// </summary>
    [Fact]
    public void RemoveHandShape_DropsTheBarFromTheSelection()
    {
        var h = new Harness();
        var shape = h.AddHandShape(2, 4);
        h.Selection.SelectedHandShapes.Add(shape);

        h.Service.RemoveHandShape(shape);

        Assert.Empty(h.State.HandShapes);
        Assert.Empty(h.Selection.SelectedHandShapes);
    }

    [Fact]
    public void RemoveHandShape_LeavesTheOtherSelectedBarsAlone()
    {
        var h = new Harness();
        var doomed = h.AddHandShape(2, 4);
        var keeper = h.AddHandShape(6, 8);
        h.Selection.SelectedHandShapes.Add(doomed);
        h.Selection.SelectedHandShapes.Add(keeper);

        h.Service.RemoveHandShape(doomed);

        Assert.Equal([keeper], h.Selection.SelectedHandShapes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UndoingADragOrPaste_DropsDetachedSelectionsBeforeTheNextFretEdit(bool paste)
    {
        var h = new Harness();
        var source = h.AddNote(1, 0, 5);
        var moved = new GuitarNote { Position = new FractionalPosition(4), StringIndex = 0, Fret = 7 };
        Assert.True(h.Service.CommitHandShapeDragAndDrop(
            paste ? [] : [source], [moved], [], [], [], [], [], []));
        var result = Assert.Single(h.Selection.SelectedNotes);

        h.History.Undo();

        Assert.Empty(h.Selection.SelectedNotes);
        var node = h.History.CurrentNodeId;
        h.Service.MoveSelectedFretUp();
        Assert.Equal(node, h.History.CurrentNodeId);
        Assert.Equal(5, source.Fret);
        Assert.Equal(7, result.Fret);
        Assert.True(h.History.CanRedo);

        h.Selection.SelectedNotes.Add(source);
        h.History.Redo();
        Assert.All(h.Selection.SelectedNotes, n => Assert.Contains(n, h.State.PlacedNotes));
    }

    [Fact]
    public void UndoingAPaste_PrunesAllFourResultSelections()
    {
        var h = new Harness();
        var note = new GuitarNote { Position = new FractionalPosition(2), StringIndex = 0, Fret = 5 };
        var chord = new Chord { Position = new FractionalPosition(4) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 7 });
        var shape = new HandShape { Position = new FractionalPosition(6), EndPosition = new FractionalPosition(8) };
        var anchor = new FretAnchor { Position = new FractionalPosition(2), Fret = 5, Width = 4 };

        Assert.True(h.Service.CommitHandShapeDragAndDrop([], [note], [], [chord], [], [shape], [], [anchor]));
        Assert.Single(h.Selection.SelectedNotes);
        Assert.Single(h.Selection.SelectedChords);
        Assert.Single(h.Selection.SelectedHandShapes);
        Assert.Single(h.Selection.SelectedAnchors);

        h.History.Undo();

        Assert.Empty(h.Selection.SelectedNotes);
        Assert.Empty(h.Selection.SelectedChords);
        Assert.Empty(h.Selection.SelectedHandShapes);
        Assert.Empty(h.Selection.SelectedAnchors);
    }

    [Fact]
    public void ASpacingUpdater_KeepsTheEditedLevelAndBeatGridAcrossAFocusChange()
    {
        var h = new Harness();
        h.Config.AppConfig.MinNoteDistanceMode = MinNoteDistanceMode.Milliseconds;
        h.Config.AppConfig.MinNoteDistanceMs = 100;
        var originalLevel = h.State.ActiveLevel!;
        var first = h.AddNote(1, 0, 5);
        first.Sustain = new FractionalPosition(4);
        h.AddNote(2, 1, 7);
        var updater = h.Service.GetLinkNextUpdater();
        Assert.Same(updater, h.Service.GetLinkNextUpdater());
        var ladderUpdater = h.Service.GetLevelSpacingUpdater(originalLevel);

        var other = new ChartLevel();
        var otherNote = new GuitarNote { Position = new FractionalPosition(1), Sustain = new FractionalPosition(4) };
        other.Notes.Add(otherNote);
        other.Notes.Add(new GuitarNote { Position = new FractionalPosition(2), StringIndex = 1 });
        h.Scope.SetResolver(() => new EditingTarget(h.State.ActiveArrangement!, other));
        // Replacing the project also changes the grid resolved by the scope.
        h.State.ActiveProject = new ChartProject();

        updater();
        Assert.True(first.Sustain < new FractionalPosition(1));
        Assert.Equal(new FractionalPosition(4), otherNote.Sustain);
        first.Sustain = new FractionalPosition(4);
        ladderUpdater();
        Assert.True(first.Sustain < new FractionalPosition(1));
    }

    private sealed class Harness
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
            var arr = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            arr.Levels.Add(new ChartLevel());
            project.Arrangements.Add(arr);

            State.ActiveProject = project;
            State.ActiveArrangement = arr;
            State.ActiveLevel = arr.Levels[0];
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public FakeConfigurationService Config { get; } = new();
        public HistoryService History { get; }
        public EditingScope Scope { get; }
        public HighwayEditingService Service { get; }

        public HandShape AddHandShape(int startBeat, int endBeat)
        {
            var hs = new HandShape
            {
                Position = new FractionalPosition(startBeat),
                EndPosition = new FractionalPosition(endBeat)
            };
            State.HandShapes.Add(hs);
            return hs;
        }

        public GuitarNote AddNote(int beat, int stringIdx, int fret)
        {
            var n = new GuitarNote
            {
                Position = new FractionalPosition(beat),
                StringIndex = stringIdx,
                Fret = fret
            };
            State.PlacedNotes.Add(n);
            return n;
        }
    }
}
