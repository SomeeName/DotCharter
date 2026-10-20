using DotCharter.Services.Editing.Collisions;

namespace DotCharter.Tests.Services.State;

/// <summary>
///     A composite runs the whole-level spacing pass ONCE per gesture, not once
///     per child. A drag-drop hands the enforcer to up to six effective children
///     and a Delete to three or four; each used to run the pass inside its own
///     Execute and again inside its own Undo (47 ms per pass on a 20k-sound
///     level — ~280 ms for one drop, then the same for Ctrl+Z). The pass is
///     idempotent, so the extra runs re-derived an answer the level already met.
///     <para>
///         The collateral contract moves with it: the one pass runs under one
///         recorder whose exclusion set is the union of the children's and the
///         composite holds the result. On undo NO pass runs — inverses + that
///         record restore the exact pre-edit chart, and the per-child flow's
///         undo-time pass could only re-shorten it (a trimmed tail restored to a
///         length the enforcer caps came back capped).
///     </para>
/// </summary>
public sealed class SpacingPostPassCoalescingTests
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

    private static GuitarNote Note(int beat, int str) => new() { Position = Pos(beat), StringIndex = str, Fret = 3 };

    [Fact]
    public void AComposite_RunsASharedPostPassOnce_OnExecuteAndRedo_AndNotOnUndo()
    {
        var level = new ChartLevel();
        var runs = 0;
        Action pass = () => runs++;
        var composite = new CompositeAction(
        [
            new AddNotesAction(level.Notes, [Note(1, 0)], pass),
            new AddNotesAction(level.Notes, [Note(2, 1)], pass),
            new AddNotesAction(level.Notes, [Note(3, 2)], pass),
            new AddNotesAction(level.Notes, [Note(4, 3)], pass)
        ]);

        composite.Execute();
        Assert.Equal(1, runs);
        Assert.Equal(4, level.Notes.Count);

        composite.Undo();
        Assert.Equal(1, runs);
        Assert.Empty(level.Notes);

        composite.Execute();
        Assert.Equal(2, runs);
        Assert.Equal(4, level.Notes.Count);
    }

    /// <summary>The service hands out its enforcer many times per gesture;
    /// delegate equality is what folds them, so two instances that are the same
    /// target + method count once, and a genuinely different pass counts too.</summary>
    [Fact]
    public void DistinctPasses_EachRunOnce_SameTargetAndMethodFold()
    {
        var level = new ChartLevel();
        var a = 0;
        var b = 0;
        var counter = new Counter();
        var composite = new CompositeAction(
        [
            new AddNotesAction(level.Notes, [Note(1, 0)], () => a++),
            new AddNotesAction(level.Notes, [Note(2, 1)], () => b++),
            new AddNotesAction(level.Notes, [Note(3, 2)], counter.Bump),
            new AddNotesAction(level.Notes, [Note(4, 3)], counter.Bump)
        ]);

        composite.Execute();
        Assert.Equal(1, a);
        Assert.Equal(1, b);
        Assert.Equal(1, counter.Runs);
    }

    private sealed class Counter
    {
        public int Runs;
        public void Bump() => Runs++;
    }

    /// <summary>
    ///     The real enforcer, the real collateral: two placements next to a long
    ///     sustain truncate it once; one Ctrl+Z gives the whole sustain back; redo
    ///     truncates identically. Before the change each child recorded its own
    ///     collateral — this pins that the composite's single record is complete.
    /// </summary>
    [Fact]
    public void TheGesturesCollateral_IsRestoredByOneUndo_AndRedoReproducesIt()
    {
        var level = new ChartLevel();
        var beats = Beats(16);
        var sustained = new GuitarNote { Position = Pos(1), StringIndex = 0, Fret = 5, Sustain = new FractionalPosition(4) };
        level.Notes.Add(sustained);
        Action enforcer = () => ChartSpacingEnforcer.EnforceLevel(level, beats, Config());

        var composite = new CompositeAction(
        [
            new AddNotesAction(level.Notes, [Note(3, 1)], enforcer),
            new AddNotesAction(level.Notes, [Note(2, 2)], enforcer)
        ]);

        composite.Execute();
        var truncated = sustained.Sustain;
        Assert.True(truncated < new FractionalPosition(4), "the placements should have truncated the neighbour");

        composite.Undo();
        Assert.Equal(new FractionalPosition(4), sustained.Sustain);
        Assert.Single(level.Notes);

        composite.Execute();
        Assert.Equal(truncated, sustained.Sustain);
        Assert.Equal(3, level.Notes.Count);

        // …and the cycle does not erode (the SpacingCollateralUndoTests claim,
        // through the composite path).
        for (var i = 0; i < 5; i++)
        {
            composite.Undo();
            composite.Execute();
        }
        composite.Undo();
        Assert.Equal(new FractionalPosition(4), sustained.Sustain);
    }

    [Fact]
    public void ANestedComposite_DefersIntoTheOuterOne_SoTheGestureStillPaysOnce()
    {
        var level = new ChartLevel();
        var runs = 0;
        Action pass = () => runs++;
        var inner = new CompositeAction(
        [
            new AddNotesAction(level.Notes, [Note(1, 0)], pass),
            new AddNotesAction(level.Notes, [Note(2, 1)], pass)
        ]);
        var outer = new CompositeAction(
        [
            inner,
            new AddNotesAction(level.Notes, [Note(3, 2)], pass)
        ]);

        outer.Execute();
        Assert.Equal(1, runs);
        outer.Undo();
        Assert.Equal(1, runs);
        Assert.Empty(level.Notes);
    }

    /// <summary>
    ///     The shape that decided the rule: a tail the gesture trimmed (a mutate
    ///     child with no pass of its own) sits at a length the enforcer would
    ///     cap. The per-child flow re-ran the pass on undo and the restored tail
    ///     came back capped; with no undo-time pass it comes back exact.
    /// </summary>
    [Fact]
    public void ARestoredTail_TheEnforcerWouldCap_ComesBackExact()
    {
        var level = new ChartLevel();
        var beats = Beats(16);
        var tail = new GuitarNote { Position = Pos(0), StringIndex = 0, Fret = 3, Sustain = new FractionalPosition(2) };
        var next = new GuitarNote { Position = Pos(2), StringIndex = 1, Fret = 5 };
        level.Notes.Add(tail);
        level.Notes.Add(next);
        Action enforcer = () => ChartSpacingEnforcer.EnforceLevel(level, beats, Config());

        var trimmed = tail.Clone();
        trimmed.Sustain = FractionalPosition.Zero;
        var composite = new CompositeAction(
        [
            new AddHandShapesAction(level.HandShapes,
                [new HandShape { Position = Pos(0), EndPosition = Pos(4) }], enforcer),
            new MutateNotesAction(level.Notes, [tail], [tail.Clone()], [trimmed])
        ]);

        composite.Execute();
        Assert.Equal(FractionalPosition.Zero, tail.Sustain);

        composite.Undo();
        Assert.Empty(level.HandShapes);
        Assert.Equal(new FractionalPosition(2), tail.Sustain);
    }

    /// <summary>A standalone action is untouched by the scope: it still runs its
    /// own pass in Execute and in Undo, exactly as before.</summary>
    [Fact]
    public void AStandaloneAction_StillRunsItsOwnPass()
    {
        var level = new ChartLevel();
        var runs = 0;
        var action = new AddNotesAction(level.Notes, [Note(1, 0)], () => runs++);
        action.Execute();
        Assert.Equal(1, runs);
        action.Undo();
        Assert.Equal(2, runs);
        Assert.Null(DeferredPostPassScope.Current);
    }

    /// <summary>A child that throws leaves no scope behind, and since the pass
    /// runs only after every child succeeded, the rollback restored the exact
    /// pre-edit chart with no pass at all.</summary>
    [Fact]
    public void AFailingChild_RollsBack_RunsNoPass_AndLeavesNoScopeOpen()
    {
        var level = new ChartLevel();
        var runs = 0;
        Action pass = () => runs++;
        var composite = new CompositeAction(
        [
            new AddNotesAction(level.Notes, [Note(1, 0)], pass),
            new ThrowingAction(),
            new AddNotesAction(level.Notes, [Note(3, 2)], pass)
        ]);

        Assert.Throws<InvalidOperationException>(composite.Execute);
        Assert.Empty(level.Notes);
        Assert.Equal(0, runs);
        Assert.Null(DeferredPostPassScope.Current);
    }

    private sealed class ThrowingAction : IChartAction
    {
        public bool HasEffect => true;
        public void Execute() => throw new InvalidOperationException("boom");
        public void Undo() { }
    }
}
