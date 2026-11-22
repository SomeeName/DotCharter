using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;

namespace DotCharter.Tests.Services.Editing.Highway;

/// <summary>Nudging the selection earlier or later in time (#370), as ONE undo step.</summary>
public sealed class NudgeInTimeTests
{
    [Fact]
    public void NudgeTranslatesNoteAndChordBends_AndUndoRedoKeepsTheirOffsets()
    {
        var h = new Harness();
        var note = h.AddNote(At(4));
        note.Sustain = At(2);
        note.BendValues.Add(new BendValue { Position = At(5), Step = 2 });
        var chord = h.AddChord(At(8));
        chord.ChordNotes.Add(new ChordNote
        {
            StringIndex = 2, Fret = 5, Sustain = At(2),
            BendValues = { new BendValue { Position = At(9), Step = 2 } }
        });
        h.Select(note, chord);
        h.Service.MoveSelectedInTime(1, 0, 4);
        Assert.Equal(At(5, 1), Assert.Single(note.BendValues).Position);
        Assert.Equal(At(9, 1), Assert.Single(chord.ChordNotes[0].BendValues).Position);
        h.History.Undo();
        Assert.Equal(At(5), Assert.Single(note.BendValues).Position);
        Assert.Equal(At(9), Assert.Single(chord.ChordNotes[0].BendValues).Position);
        h.History.Redo();
        Assert.Equal(At(5, 1), Assert.Single(note.BendValues).Position);
        Assert.Equal(At(9, 1), Assert.Single(chord.ChordNotes[0].BendValues).Position);
    }

    private static FractionalPosition At(int beat, int num = 0, int den = 4) => new(beat, new Fraction(num, den));

    [Fact]
    public void A_mixed_selection_moves_together_and_one_undo_restores_all_of_it()
    {
        var h = new Harness();
        var note = h.AddNote(At(4));
        var chord = h.AddChord(At(5));
        var shape = h.AddHandShape(At(4), At(6));
        var anchor = h.AddAnchor(At(4), 5);
        h.Select(note, chord, shape, anchor);

        var result = h.Service.MoveSelectedInTime(1, 0, 4);

        Assert.Equal(TimeMoveOutcome.Moved, result.Outcome);
        Assert.Equal(4, result.ItemCount);
        Assert.Equal(125, result.DeltaMs, 0.5);
        Assert.Equal(At(4, 1), note.Position);
        Assert.Equal(At(5, 1), chord.Position);
        Assert.Equal(At(4, 1), shape.Position);
        Assert.Equal(At(6, 1), shape.EndPosition);
        Assert.Equal(At(4, 1), anchor.Position);

        h.History.Undo();

        Assert.Equal(At(4), note.Position);
        Assert.Equal(At(5), chord.Position);
        Assert.Equal(At(4), shape.Position);
        Assert.Equal(At(6), shape.EndPosition);
        Assert.Equal(At(4), anchor.Position);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void Redo_moves_the_whole_selection_again()
    {
        var h = new Harness();
        var note = h.AddNote(At(4));
        var anchor = h.AddAnchor(At(4), 5);
        h.Select(note, anchor);
        h.Service.MoveSelectedInTime(-1, 0, 4);
        h.History.Undo();

        h.History.Redo();

        Assert.Equal(At(3, 3), note.Position);
        Assert.Equal(At(3, 3), anchor.Position);
    }

    [Theory]
    [InlineData(1, 1, 501)]
    [InlineData(-1, 10, 490)]
    public void A_millisecond_nudge_moves_by_that_many_ms(int direction, int ms, double expectedMs)
    {
        var h = new Harness();
        var note = h.AddNote(At(1));
        h.Select(note);

        var result = h.Service.MoveSelectedInTime(direction, ms, 4);

        Assert.Equal(expectedMs, note.Position.GetPositionMs(h.Beats), 0.05);
        Assert.Equal(direction * ms, result.DeltaMs, 0.05);
    }

    [Fact]
    public void A_move_across_the_first_beat_is_clamped_to_it_and_reported()
    {
        var h = new Harness();
        var early = h.AddNote(At(0, 1, 8));
        var late = h.AddNote(At(1));
        h.Select(early, late);

        var result = h.Service.MoveSelectedInTime(-1, 0, 4);

        Assert.Equal(TimeMoveOutcome.Clamped, result.Outcome);
        Assert.Equal(FractionalPosition.Zero, early.Position);
        Assert.Equal(At(0, 7, 8), late.Position);
    }

    [Fact]
    public void A_clamped_millisecond_nudge_keeps_the_same_ms_delta_across_a_tempo_change()
    {
        var h = new Harness();
        h.Beats[0].Time = 100;
        h.Beats[0].Bpm = 120;
        for (var i = 1; i < h.Beats.Count; i++)
        {
            h.Beats[i].Time = 600 + (i - 1) * 1000;
            h.Beats[i].Bpm = 60;
        }

        var early = h.AddNote(At(0, 1, 2));
        var late = h.AddNote(At(2));
        var chord = h.AddChord(At(3));
        var shape = h.AddHandShape(At(4), At(5));
        var anchor = h.AddAnchor(At(6), 7);
        h.Select(early, late, chord, shape, anchor);

        var result = h.Service.MoveSelectedInTime(-1, 500, 4);

        Assert.Equal(TimeMoveOutcome.Clamped, result.Outcome);
        Assert.Equal(-250.0, result.DeltaMs, 6);
        Assert.Equal(FractionalPosition.Zero, early.Position);
        Assert.Equal(1350.0, late.Position.GetPositionMs(h.Beats), 6);
        Assert.Equal(2350.0, chord.Position.GetPositionMs(h.Beats), 6);
        Assert.Equal(3350.0, shape.Position.GetPositionMs(h.Beats), 6);
        Assert.Equal(4350.0, shape.EndPosition.GetPositionMs(h.Beats), 6);
        Assert.Equal(5350.0, anchor.Position.GetPositionMs(h.Beats), 6);

        h.History.Undo();

        Assert.Equal(At(0, 1, 2), early.Position);
        Assert.Equal(At(2), late.Position);
        Assert.Equal(At(3), chord.Position);
        Assert.Equal(At(4), shape.Position);
        Assert.Equal(At(5), shape.EndPosition);
        Assert.Equal(At(6), anchor.Position);
        Assert.False(h.History.CanUndo);

        h.History.Redo();

        Assert.Equal(FractionalPosition.Zero, early.Position);
        Assert.Equal(1350.0, late.Position.GetPositionMs(h.Beats), 6);
        Assert.Equal(4350.0, shape.EndPosition.GetPositionMs(h.Beats), 6);
    }

    [Fact]
    public void An_earlier_move_from_the_first_beat_is_refused_and_changes_nothing()
    {
        var h = new Harness();
        var note = h.AddNote(FractionalPosition.Zero);
        h.Select(note);

        var result = h.Service.MoveSelectedInTime(-1, 0, 4);

        Assert.Equal(TimeMoveOutcome.AtStart, result.Outcome);
        Assert.Equal(FractionalPosition.Zero, note.Position);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void A_move_onto_an_unselected_item_is_refused_and_changes_nothing()
    {
        var h = new Harness();
        var moving = h.AddNote(At(4));
        var neighbour = h.AddNote(At(4, 1));
        h.Select(moving);

        var result = h.Service.MoveSelectedInTime(1, 0, 4);

        Assert.Equal(TimeMoveOutcome.Collision, result.Outcome);
        Assert.Equal(At(4), moving.Position);
        Assert.Equal(At(4, 1), neighbour.Position);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void A_selected_neighbour_is_not_a_collision_because_it_moves_too()
    {
        var h = new Harness();
        var a = h.AddNote(At(4));
        var b = h.AddNote(At(4, 1));
        h.Select(a, b);

        var result = h.Service.MoveSelectedInTime(1, 0, 4);

        Assert.Equal(TimeMoveOutcome.Moved, result.Outcome);
        Assert.Equal(At(4, 1), a.Position);
        Assert.Equal(At(4, 2), b.Position);
    }

    [Fact]
    public void An_anchor_landing_on_an_unselected_anchor_is_refused()
    {
        var h = new Harness();
        var moving = h.AddAnchor(At(4), 5);
        h.AddAnchor(At(4, 1), 7);
        h.Select(moving);

        Assert.Equal(TimeMoveOutcome.Collision, h.Service.MoveSelectedInTime(1, 0, 4).Outcome);
    }

    [Fact]
    public void A_hand_shape_nudged_into_an_unselected_hand_shape_is_refused_and_changes_nothing()
    {
        var h = new Harness();
        var moving = h.AddHandShape(At(4), At(5));
        var neighbour = h.AddHandShape(At(5), At(6));
        h.Select(moving);

        var result = h.Service.MoveSelectedInTime(1, 0, 4);

        Assert.Equal(TimeMoveOutcome.HandShapeOverlap, result.Outcome);
        Assert.Equal(At(4), moving.Position);
        Assert.Equal(At(5), moving.EndPosition);
        Assert.Equal(At(5), neighbour.Position);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void A_hand_shape_that_only_touches_an_unselected_one_may_move()
    {
        var h = new Harness();
        var moving = h.AddHandShape(At(4), At(5));
        h.AddHandShape(At(5, 1), At(6));
        h.Select(moving);

        Assert.Equal(TimeMoveOutcome.Moved, h.Service.MoveSelectedInTime(1, 0, 4).Outcome);
    }

    [Fact]
    public void A_selected_neighbour_hand_shape_is_not_an_overlap_because_it_moves_too()
    {
        var h = new Harness();
        var a = h.AddHandShape(At(4), At(5));
        var b = h.AddHandShape(At(5), At(6));
        h.Select(a, b);

        Assert.Equal(TimeMoveOutcome.Moved, h.Service.MoveSelectedInTime(1, 0, 4).Outcome);
    }

    [Fact]
    public void A_chord_may_cross_into_an_unselected_hand_shape_because_binding_is_positional()
    {
        var h = new Harness();
        var chord = h.AddChord(At(4, 3));
        h.AddHandShape(At(5), At(6));
        h.Select(chord);

        Assert.Equal(TimeMoveOutcome.Moved, h.Service.MoveSelectedInTime(1, 0, 4).Outcome);
        Assert.Equal(At(5), chord.Position);
    }

    [Fact]
    public void With_nothing_selected_the_command_is_disabled_and_the_call_reports_it()
    {
        var h = new Harness();
        h.AddNote(At(4));

        Assert.False(h.Service.HasTimeMovableSelection);
        Assert.Equal(TimeMoveOutcome.NothingSelected, h.Service.MoveSelectedInTime(1, 0, 4).Outcome);
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

            var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            arrangement.Levels.Add(new ChartLevel());
            _project.Arrangements.Add(arrangement);
            State.ActiveProject = _project;
            State.ActiveArrangement = arrangement;
            State.ActiveLevel = arrangement.Levels[0];
            Level = arrangement.Levels[0];
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public HistoryService History { get; }
        public HighwayEditingService Service { get; }
        public ChartLevel Level { get; }
        public IList<BeatMarker> Beats => _project.Beats;

        public GuitarNote AddNote(FractionalPosition at)
        {
            var note = new GuitarNote { Position = at, StringIndex = 2, Fret = 3 };
            Level.Notes.Add(note);
            return note;
        }

        public Chord AddChord(FractionalPosition at)
        {
            var chord = new Chord { Position = at };
            Level.Chords.Add(chord);
            return chord;
        }

        public HandShape AddHandShape(FractionalPosition at, FractionalPosition end)
        {
            var shape = new HandShape { Position = at, EndPosition = end };
            Level.HandShapes.Add(shape);
            return shape;
        }

        public FretAnchor AddAnchor(FractionalPosition at, int fret)
        {
            var anchor = new FretAnchor(at, fret);
            Level.Anchors.Add(anchor);
            return anchor;
        }

        public void Select(params object[] items)
        {
            foreach (var item in items)
            {
                switch (item)
                {
                    case GuitarNote n:
                        Selection.SelectedNotes.Add(n);
                        break;
                    case Chord c:
                        Selection.SelectedChords.Add(c);
                        break;
                    case HandShape s:
                        Selection.SelectedHandShapes.Add(s);
                        break;
                    case FretAnchor a:
                        Selection.SelectedAnchors.Add(a);
                        break;
                }
            }
        }
    }
}
