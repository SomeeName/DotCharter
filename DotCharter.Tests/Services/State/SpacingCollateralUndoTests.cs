using DotCharter.Services.Editing.Collisions;

namespace DotCharter.Tests.Services.State;

/// <summary>
///     Undo must INVERT. Every collection action runs the spacing enforcer over
///     the whole active level in its post-execute, after Execute AND after Undo —
///     and that pass only ever SHORTENS. So an edit truncated a NEIGHBOUR the
///     action does not hold, and undoing the edit re-ran a pass that cannot
///     lengthen anything back: the neighbour stayed truncated and repeating the
///     gesture eroded the chart.
/// </summary>
public class SpacingCollateralUndoTests
{
    private static AppConfig Config() => new()
    {
        MinNoteDistanceMode = MinNoteDistanceMode.BeatFraction,
        MinNoteDistanceDivisor = 64
    };

    private static List<BeatMarker> Beats(int count)
    {
        var beats = new List<BeatMarker>();
        for (var i = 0; i < count; i++)
            beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0 });
        return beats;
    }

    private static FractionalPosition Pos(int beat) => new(beat);

    private static Action Enforcer(ChartLevel level, List<BeatMarker> beats)
        => () => ChartSpacingEnforcer.EnforceLevel(level, beats, Config());

    [Fact]
    public void AddingANoteTruncatesTheNeighbour_AndUndoGivesItBack()
    {
        var level = new ChartLevel();
        var beats = Beats(16);
        var sustained = new GuitarNote
        {
            Position = Pos(1), StringIndex = 0, Fret = 5, Sustain = new FractionalPosition(4)
        };
        level.Notes.Add(sustained);

        var placed = new GuitarNote { Position = Pos(2), StringIndex = 1, Fret = 7 };
        var action = new AddNotesAction(level.Notes, new[] { placed }, Enforcer(level, beats));

        action.Execute();
        Assert.True(sustained.Sustain < new FractionalPosition(4), "the placement should have truncated the neighbour");

        action.Undo();

        Assert.Equal(new FractionalPosition(4), sustained.Sustain);
        Assert.Single(level.Notes);
    }

    [Fact]
    public void RepeatedPlaceAndUndoCycles_DoNotErodeTheChart()
    {
        var level = new ChartLevel();
        var beats = Beats(16);
        var sustained = new GuitarNote
        {
            Position = Pos(1), StringIndex = 0, Fret = 5, Sustain = new FractionalPosition(4)
        };
        level.Notes.Add(sustained);

        for (var i = 0; i < 5; i++)
        {
            var placed = new GuitarNote { Position = Pos(2), StringIndex = 1, Fret = 7 };
            var action = new AddNotesAction(level.Notes, new[] { placed }, Enforcer(level, beats));
            action.Execute();
            action.Undo();
        }

        Assert.Equal(new FractionalPosition(4), sustained.Sustain);
    }

    [Fact]
    public void RedoAfterUndo_ReproducesTheExactSameTruncation()
    {
        var level = new ChartLevel();
        var beats = Beats(16);
        var sustained = new GuitarNote
        {
            Position = Pos(1), StringIndex = 0, Fret = 5, Sustain = new FractionalPosition(4)
        };
        level.Notes.Add(sustained);

        var placed = new GuitarNote { Position = Pos(2), StringIndex = 1, Fret = 7 };
        var action = new AddNotesAction(level.Notes, new[] { placed }, Enforcer(level, beats));

        action.Execute();
        var afterFirstExecute = sustained.Sustain;
        action.Undo();
        action.Execute();

        Assert.Equal(afterFirstExecute, sustained.Sustain);
    }

    [Fact]
    public void RemovingANoteAndUndoingIt_RestoresTheNeighboursSustain()
    {
        // The mirror case: Undo RE-INSERTS, which truncates — and the subsequent
        // Redo (remove again) must not leave the neighbour short.
        var level = new ChartLevel();
        var beats = Beats(16);
        var sustained = new GuitarNote
        {
            Position = Pos(1), StringIndex = 0, Fret = 5, Sustain = new FractionalPosition(4)
        };
        var crowding = new GuitarNote { Position = Pos(2), StringIndex = 1, Fret = 7 };
        level.Notes.Add(sustained);
        level.Notes.Add(crowding);

        // Settle: the chart as the enforcer would have left it.
        ChartSpacingEnforcer.EnforceLevel(level, beats, Config());
        var settled = sustained.Sustain;

        var action = new RemoveNotesAction(level.Notes, new[] { crowding }, Enforcer(level, beats));
        action.Execute();
        action.Undo();

        Assert.Equal(settled, sustained.Sustain);
        Assert.Equal(2, level.Notes.Count);
    }

    [Fact]
    public void MutatingANote_DoesNotLetTheCollateralUndoTheUndo()
    {
        // A MutateNotesAction's own target is captured by the recorder holding the
        // NEW state, so replaying it after copyFn(old) would undo the undo. Owned
        // elements are excluded for exactly that reason.
        var level = new ChartLevel();
        var beats = Beats(16);
        var target = new GuitarNote
        {
            Position = Pos(1), StringIndex = 0, Fret = 5, Sustain = new FractionalPosition(new Fraction(1, 2))
        };
        level.Notes.Add(target);
        level.Notes.Add(new GuitarNote { Position = Pos(4), StringIndex = 1, Fret = 7 });

        var oldState = target.Clone();
        var newState = target.Clone();
        newState.Sustain = new FractionalPosition(8); // longer than the room available

        var action = new MutateNotesAction(level.Notes, new[] { target },
            new[] { oldState }, new[] { newState }, Enforcer(level, beats));

        action.Execute();
        action.Undo();

        Assert.Equal(new FractionalPosition(new Fraction(1, 2)), target.Sustain);
    }

    [Fact]
    public void AddingASoundUnderAHandShape_RestoresTheShapesEndOnUndo()
    {
        var level = new ChartLevel();
        var beats = Beats(16);
        var shape = new HandShape { Position = Pos(1), EndPosition = Pos(6), TemplateId = 0 };
        level.HandShapes.Add(shape);
        level.Notes.Add(new GuitarNote { Position = Pos(1), StringIndex = 0, Fret = 5 });

        // A sound landing ON the shape's end forces the min-note-distance gap.
        var placed = new GuitarNote { Position = Pos(6), StringIndex = 1, Fret = 7 };
        var action = new AddNotesAction(level.Notes, new[] { placed }, Enforcer(level, beats));

        action.Execute();
        Assert.True(shape.EndPosition < Pos(6), "the placement should have clipped the hand shape");

        action.Undo();

        Assert.Equal(Pos(6), shape.EndPosition);
    }

    [Fact]
    public void AddingANoteCollapsesTheNeighboursBendCurve_AndUndoRestoresIt()
    {
        var level = new ChartLevel();
        var beats = Beats(16);
        var bent = new GuitarNote
        {
            Position = Pos(1), StringIndex = 0, Fret = 5, Sustain = new FractionalPosition(4)
        };
        bent.BendValues.Add(new BendValue(new FractionalPosition(3), 1.0));
        bent.BendValues.Add(new BendValue(new FractionalPosition(4), 2.0));
        level.Notes.Add(bent);

        var placed = new GuitarNote { Position = Pos(2), StringIndex = 1, Fret = 7 };
        var action = new AddNotesAction(level.Notes, new[] { placed }, Enforcer(level, beats));

        action.Execute();
        // The sustain shrank to under a beat, so the clamp collapsed both points
        // onto the new end and the dedupe dropped one of them.
        Assert.Single(bent.BendValues);

        action.Undo();

        Assert.Equal(2, bent.BendValues.Count);
        Assert.Equal(new FractionalPosition(3), bent.BendValues[0].Position);
        Assert.Equal(new FractionalPosition(4), bent.BendValues[1].Position);
        Assert.Equal(2.0, bent.BendValues[1].Step);
    }

    [Fact]
    public void WithNoPostExecutePass_NothingIsRecordedAndUndoStillWorks()
    {
        var level = new ChartLevel();
        var sustained = new GuitarNote { Position = Pos(1), StringIndex = 0, Sustain = new FractionalPosition(4) };
        level.Notes.Add(sustained);

        var placed = new GuitarNote { Position = Pos(2), StringIndex = 1 };
        var action = new AddNotesAction(level.Notes, new[] { placed });

        action.Execute();
        action.Undo();

        Assert.Single(level.Notes);
        Assert.Equal(new FractionalPosition(4), sustained.Sustain);
    }

    [Fact]
    public void NestedCompositeActions_EachRestoreTheirOwnCollateral()
    {
        var level = new ChartLevel();
        var beats = Beats(24);
        var first = new GuitarNote
        {
            Position = Pos(1), StringIndex = 0, Fret = 5, Sustain = new FractionalPosition(8)
        };
        level.Notes.Add(first);

        var a = new GuitarNote { Position = Pos(6), StringIndex = 1, Fret = 7 };
        var b = new GuitarNote { Position = Pos(3), StringIndex = 2, Fret = 9 };
        var composite = new CompositeAction(new IChartAction[]
        {
            new AddNotesAction(level.Notes, new[] { a }, Enforcer(level, beats)),
            new AddNotesAction(level.Notes, new[] { b }, Enforcer(level, beats))
        });

        composite.Execute();
        Assert.True(first.Sustain < new FractionalPosition(8));

        composite.Undo();

        Assert.Equal(new FractionalPosition(8), first.Sustain);
        Assert.Single(level.Notes);
    }

    /// <summary>
    ///     The hole gesture merging opened in every test above, and the one case
    ///     nothing covered: they all drive an action DIRECTLY, and
    ///     <c>HistoryGestureMergeTests</c> passes <c>null</c> for the post-execute
    ///     pass — so no test ever ran the enforcer through a MERGE.
    ///     <para>
    ///         <c>HistoryService.Do</c> executes the incoming tick first, which
    ///         records that tick's collateral on THAT instance, and only then folds
    ///         it into the head and DROPS the instance. So a run whose first tick
    ///         truncates one neighbour and whose later tick truncates another lands
    ///         as one undo node holding the first record only: Ctrl+Z puts the
    ///         dragged sound back and leaves the second neighbour permanently
    ///         short, with the node that could have restored it already merged
    ///         away. Exactly the erosion <c>SpacingCollateral</c> exists to stop.
    ///     </para>
    ///     <para>
    ///         <b>The ticks here move the target's POSITION, and that is
    ///         deliberate</b>: <c>EnforceSoundSpacing</c>'s shortening branch is a
    ///         pure function of ONSETS, so a position move is the only thing that
    ///         can truncate a different neighbour on different ticks. No shipped
    ///         keybind carries a merge verb on a position move today (the seven
    ///         verbs are sustain, hand-shape length, anchor fret/width, fret,
    ///         string and lyric length), so this pins the CONTRACT of
    ///         <c>MutateElementsAction.Absorb</c> rather than reproducing a live
    ///         user-visible fault. Recorded undo data may not be discarded by a
    ///         merge, whichever gesture starts carrying one next.
    ///     </para>
    /// </summary>
    [Fact]
    public void AMergedGestureRestoresTheCollateralOfEveryTick_NotJustTheFirst()
    {
        var level = new ChartLevel();
        var beats = Beats(24);
        var enforcer = Enforcer(level, beats);

        // Two long-sustained neighbours, far enough apart that neither crowds the
        // other, and a dragged sound out beyond both of them.
        var early = new GuitarNote
        {
            Position = Pos(0), StringIndex = 0, Fret = 3, Sustain = new FractionalPosition(4)
        };
        var late = new GuitarNote
        {
            Position = Pos(6), StringIndex = 1, Fret = 5, Sustain = new FractionalPosition(4)
        };
        var dragged = new GuitarNote { Position = Pos(12), StringIndex = 2, Fret = 7 };
        level.Notes.Add(early);
        level.Notes.Add(late);
        level.Notes.Add(dragged);

        var clock = new Clock();
        var history = new HistoryService(new FakeAppLogger(), clock: clock.Read,
            mergeWindow: TimeSpan.FromMilliseconds(900));

        // Tick 1 lands the drag inside `late`'s sustain; tick 2 carries it on past
        // `early`'s. One gesture, one node, two different neighbours truncated.
        history.Do(DragTick(level, dragged, 12, 9, enforcer));
        clock.Advance(TimeSpan.FromMilliseconds(50));
        history.Do(DragTick(level, dragged, 9, 3, enforcer));

        Assert.Single(history.GetUndoSteps());
        Assert.True(late.Sustain < new FractionalPosition(4), "tick 1 should have truncated `late`");
        Assert.True(early.Sustain < new FractionalPosition(4), "tick 2 should have truncated `early`");

        history.Undo();

        Assert.Equal(Pos(12), dragged.Position);
        Assert.Equal(new FractionalPosition(4), late.Sustain);
        // The one the merge used to throw away with the tick that recorded it.
        Assert.Equal(new FractionalPosition(4), early.Sustain);
    }

    /// <summary>
    ///     One tick of a held drag: the same target, further along the timeline.
    ///     The verb is a literal because no shipped verb moves a position — see the
    ///     test above; <c>HistoryService</c> only compares the string.
    /// </summary>
    private static MutateNotesAction DragTick(ChartLevel level, GuitarNote target,
        int fromBeat, int toBeat, Action enforcer)
    {
        var oldState = target.Clone();
        oldState.Position = Pos(fromBeat);
        var newState = target.Clone();
        newState.Position = Pos(toBeat);
        return new MutateNotesAction(level.Notes, [target], [oldState], [newState], enforcer, "drag");
    }

    private sealed class Clock
    {
        private DateTime _nowUtc = new(2026, 8, 3, 9, 12, 0, DateTimeKind.Utc);
        public DateTime Read() => _nowUtc;
        public void Advance(TimeSpan by) => _nowUtc += by;
    }
}
