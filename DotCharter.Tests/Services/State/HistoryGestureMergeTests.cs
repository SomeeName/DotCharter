using System.Collections.ObjectModel;

namespace DotCharter.Tests.Services.State;

/// <summary>
///     Pins gesture coalescing: a wheel run / held key must land as ONE undo node.
///     <para>
///         Everything here drives an INJECTED clock and asserts node COUNTS, never
///         elapsed time — the repo rule about wall-clock assertions in the default
///         gate. The window is exercised by moving the fake clock, so these stay
///         deterministic under a parallel suite.
///     </para>
/// </summary>
public sealed class HistoryGestureMergeTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(900);

    private static Chord ChordAt(int beat, int sustainBeats = 0) => new()
    {
        Position = new FractionalPosition(beat),
        ChordNotes =
        {
            new ChordNote
            {
                StringIndex = 0, Fret = 5,
                Sustain = new FractionalPosition(sustainBeats)
            }
        }
    };

    /// <summary>Builds the action a single sustain tick produces.</summary>
    private static MutateChordsAction SustainTick(
        ObservableCollection<Chord> collection, Chord target, int fromBeats, int toBeats)
    {
        var oldState = target.Clone();
        oldState.ChordNotes[0].Sustain = new FractionalPosition(fromBeats);
        var newState = target.Clone();
        newState.ChordNotes[0].Sustain = new FractionalPosition(toBeats);
        return new MutateChordsAction(collection, [target], [oldState], [newState], null, MergeVerbs.Sustain);
    }

    private sealed class Clock
    {
        public DateTime NowUtc = new(2026, 7, 28, 15, 43, 0, DateTimeKind.Utc);
        public DateTime Read() => NowUtc;
        public void Advance(TimeSpan by) => NowUtc += by;
    }

    [Fact]
    public void AWheelRunCollapsesToOneUndoNode()
    {
        var clock = new Clock();
        var history = new HistoryService(new FakeAppLogger(), clock: clock.Read, mergeWindow: Window);
        var chord = ChordAt(4);
        var collection = new ObservableCollection<Chord> { chord };

        // 20 ticks, each one grid step further, 50 ms apart — a wheel run.
        for (var i = 0; i < 20; i++)
        {
            history.Do(SustainTick(collection, chord, i, i + 1));
            clock.Advance(TimeSpan.FromMilliseconds(50));
        }

        Assert.Single(history.GetUndoSteps());
        Assert.Equal(new FractionalPosition(20), chord.ChordNotes[0].Sustain);

        // ...and ONE Ctrl+Z takes the whole run back to where it started.
        history.Undo();
        Assert.Equal(FractionalPosition.Zero, chord.ChordNotes[0].Sustain);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void APauseLongerThanTheWindowStartsANewNode()
    {
        var clock = new Clock();
        var history = new HistoryService(new FakeAppLogger(), clock: clock.Read, mergeWindow: Window);
        var chord = ChordAt(4);
        var collection = new ObservableCollection<Chord> { chord };

        history.Do(SustainTick(collection, chord, 0, 1));
        clock.Advance(TimeSpan.FromMilliseconds(100));
        history.Do(SustainTick(collection, chord, 1, 2));

        // The charter stops, thinks, and starts a second gesture.
        clock.Advance(Window + TimeSpan.FromMilliseconds(1));
        history.Do(SustainTick(collection, chord, 2, 3));

        Assert.Equal(2, history.GetUndoSteps().Count);

        // Undoing the SECOND gesture leaves the first one standing.
        history.Undo();
        Assert.Equal(new FractionalPosition(2), chord.ChordNotes[0].Sustain);
    }

    [Fact]
    public void EndGestureClosesTheRunImmediately()
    {
        var clock = new Clock();
        var history = new HistoryService(new FakeAppLogger(), clock: clock.Read, mergeWindow: Window);
        var chord = ChordAt(4);
        var collection = new ObservableCollection<Chord> { chord };

        history.Do(SustainTick(collection, chord, 0, 1));
        history.EndGesture();
        // Same instant, well inside the window — only the explicit close separates them.
        history.Do(SustainTick(collection, chord, 1, 2));

        Assert.Equal(2, history.GetUndoSteps().Count);
    }

    /// <summary>
    ///     The reason <see cref="IMergeableAction.MergeVerb" /> exists: two
    ///     DIFFERENT edits to the same chord are both a <c>MutateChordsAction</c>,
    ///     and folding them together would make one Ctrl+Z revert an edit the
    ///     charter made deliberately and separately.
    /// </summary>
    [Fact]
    public void DifferentVerbsOnTheSameTargetNeverMerge()
    {
        var clock = new Clock();
        var history = new HistoryService(new FakeAppLogger(), clock: clock.Read, mergeWindow: Window);
        var chord = ChordAt(4);
        var collection = new ObservableCollection<Chord> { chord };

        history.Do(SustainTick(collection, chord, 0, 1));

        var oldState = chord.Clone();
        var refretted = chord.Clone();
        refretted.ChordNotes[0].Fret = 7;
        history.Do(new MutateChordsAction(collection, [chord], [oldState], [refretted], null, "refret"));

        Assert.Equal(2, history.GetUndoSteps().Count);
    }

    /// <summary>An unlabelled mutation is the DEFAULT and must keep its own node.</summary>
    [Fact]
    public void AnActionWithNoMergeVerbNeverMerges()
    {
        var clock = new Clock();
        var history = new HistoryService(new FakeAppLogger(), clock: clock.Read, mergeWindow: Window);
        var chord = ChordAt(4);
        var collection = new ObservableCollection<Chord> { chord };

        for (var i = 0; i < 3; i++)
        {
            var oldState = chord.Clone();
            oldState.ChordNotes[0].Sustain = new FractionalPosition(i);
            var newState = chord.Clone();
            newState.ChordNotes[0].Sustain = new FractionalPosition(i + 1);
            history.Do(new MutateChordsAction(collection, [chord], [oldState], [newState]));
        }

        Assert.Equal(3, history.GetUndoSteps().Count);
    }

    /// <summary>
    ///     "Drag, Ctrl+Z, drag again" must not fold the second drag into a node the
    ///     user just reverted — that would silently re-apply the undone edit.
    /// </summary>
    [Fact]
    public void AGestureCannotMergeAcrossAnUndo()
    {
        var clock = new Clock();
        var history = new HistoryService(new FakeAppLogger(), clock: clock.Read, mergeWindow: Window);
        var chord = ChordAt(4);
        var collection = new ObservableCollection<Chord> { chord };

        history.Do(SustainTick(collection, chord, 0, 1));
        history.Do(SustainTick(collection, chord, 1, 2));
        Assert.Single(history.GetUndoSteps());

        history.Undo();
        Assert.Equal(FractionalPosition.Zero, chord.ChordNotes[0].Sustain);

        // A fresh gesture, still inside the merge window.
        history.Do(SustainTick(collection, chord, 0, 1));
        Assert.Single(history.GetUndoSteps());

        history.Undo();
        Assert.Equal(FractionalPosition.Zero, chord.ChordNotes[0].Sustain);
        Assert.False(history.CanUndo);
    }

    /// <summary>
    ///     Reference identity, not value equality: two actions can hold
    ///     equal-looking chords at the same position and belong to different
    ///     objects, and folding those would write one gesture's result onto the
    ///     other's items.
    /// </summary>
    [Fact]
    public void TheSameVerbOnDifferentTargetsDoesNotMerge()
    {
        var clock = new Clock();
        var history = new HistoryService(new FakeAppLogger(), clock: clock.Read, mergeWindow: Window);
        var first = ChordAt(4);
        var second = ChordAt(8);
        var collection = new ObservableCollection<Chord> { first, second };

        history.Do(SustainTick(collection, first, 0, 1));
        history.Do(SustainTick(collection, second, 0, 1));

        Assert.Equal(2, history.GetUndoSteps().Count);
    }

    /// <summary>
    ///     A mixed note+chord selection builds a two-child composite per tick.
    ///     Without composite merging the commonest gesture of all — select a run,
    ///     hold the key — still spends one node per tick.
    /// </summary>
    [Fact]
    public void AMixedSelectionCoalescesThroughTheComposite()
    {
        var clock = new Clock();
        var history = new HistoryService(new FakeAppLogger(), clock: clock.Read, mergeWindow: Window);

        var chord = ChordAt(4);
        var chords = new ObservableCollection<Chord> { chord };
        var note = new GuitarNote { Position = new FractionalPosition(6), StringIndex = 1, Fret = 3 };
        var notes = new ObservableCollection<GuitarNote> { note };

        for (var i = 0; i < 10; i++)
        {
            var oldNote = note.Clone();
            oldNote.Sustain = new FractionalPosition(i);
            var newNote = note.Clone();
            newNote.Sustain = new FractionalPosition(i + 1);

            history.Do(new CompositeAction([
                new MutateNotesAction(notes, [note], [oldNote], [newNote], null, MergeVerbs.Sustain),
                SustainTick(chords, chord, i, i + 1)
            ]));
            clock.Advance(TimeSpan.FromMilliseconds(40));
        }

        Assert.Single(history.GetUndoSteps());
        Assert.Equal(new FractionalPosition(10), note.Sustain);
        Assert.Equal(new FractionalPosition(10), chord.ChordNotes[0].Sustain);

        history.Undo();
        Assert.Equal(FractionalPosition.Zero, note.Sustain);
        Assert.Equal(FractionalPosition.Zero, chord.ChordNotes[0].Sustain);
    }

    /// <summary>
    ///     A composite merges only when EVERY child agrees. A mismatched pair must
    ///     leave the head node completely untouched — a half-merged composite would
    ///     describe a chart state that never existed.
    /// </summary>
    [Fact]
    public void APartlyCompatibleCompositeIsRefusedWhole()
    {
        var clock = new Clock();
        var history = new HistoryService(new FakeAppLogger(), clock: clock.Read, mergeWindow: Window);

        var chord = ChordAt(4);
        var other = ChordAt(8);
        var chords = new ObservableCollection<Chord> { chord, other };
        var note = new GuitarNote { Position = new FractionalPosition(6), StringIndex = 1, Fret = 3 };
        var notes = new ObservableCollection<GuitarNote> { note };

        MutateNotesAction NoteTick(int from, int to)
        {
            var oldNote = note.Clone();
            oldNote.Sustain = new FractionalPosition(from);
            var newNote = note.Clone();
            newNote.Sustain = new FractionalPosition(to);
            return new MutateNotesAction(notes, [note], [oldNote], [newNote], null, MergeVerbs.Sustain);
        }

        history.Do(new CompositeAction([NoteTick(0, 1), SustainTick(chords, chord, 0, 1)]));
        // Second tick's chord half targets a DIFFERENT chord, so the whole
        // composite must be refused rather than merging the note half only.
        history.Do(new CompositeAction([NoteTick(1, 2), SustainTick(chords, other, 0, 1)]));

        Assert.Equal(2, history.GetUndoSteps().Count);

        // Undo the second node: both of ITS halves revert, and the first node's
        // note edit survives intact at 1 — which is only true if nothing merged.
        history.Undo();
        Assert.Equal(new FractionalPosition(1), note.Sustain);
        Assert.Equal(new FractionalPosition(1), chord.ChordNotes[0].Sustain);
        Assert.Equal(FractionalPosition.Zero, other.ChordNotes[0].Sustain);
    }

    /// <summary>
    ///     The merge window is a wall-clock SUBTRACTION, and wall clocks step
    ///     backwards — an NTP correction, a VM resume, a user fixing the system
    ///     time. A bare <c>elapsed &gt; window</c> reads a backwards step as "no
    ///     time has passed", so the window never closes and two edits made an
    ///     hour apart fold into one undo node. The gesture here is a long,
    ///     deliberate pause with the clock jumping back across it.
    /// </summary>
    [Fact]
    public void AClockThatSteppedBackwardsDoesNotReopenAClosedGesture()
    {
        var clock = new Clock();
        var history = new HistoryService(new FakeAppLogger(), clock: clock.Read, mergeWindow: Window);
        var chord = ChordAt(4);
        var collection = new ObservableCollection<Chord> { chord };

        history.Do(SustainTick(collection, chord, 0, 1));
        clock.Advance(TimeSpan.FromHours(-1));
        history.Do(SustainTick(collection, chord, 1, 2));

        Assert.Equal(2, history.GetUndoSteps().Count);

        // ...and the two really are separable: one Ctrl+Z reverts only the second.
        history.Undo();
        Assert.Equal(new FractionalPosition(1), chord.ChordNotes[0].Sustain);
    }

    /// <summary>
    ///     The clock is injected so the type is DETERMINISTIC, which has to
    ///     include the one field the history UI displays. Node timestamps used to
    ///     read ambient <c>DateTime.UtcNow</c>, so every timestamp on
    ///     <see cref="IHistoryService.GetTree" /> / <c>GetUndoSteps</c> was
    ///     unassertable no matter what clock the caller handed in.
    /// </summary>
    [Fact]
    public void NodeTimestampsFollowTheInjectedClock()
    {
        var clock = new Clock();
        var history = new HistoryService(new FakeAppLogger(), clock: clock.Read, mergeWindow: Window);
        var chord = ChordAt(4);
        var collection = new ObservableCollection<Chord> { chord };

        var firstAt = clock.NowUtc;
        history.Do(SustainTick(collection, chord, 0, 1));
        history.EndGesture();
        clock.Advance(TimeSpan.FromMinutes(5));
        var secondAt = clock.NowUtc;
        history.Do(SustainTick(collection, chord, 1, 2));

        // GetUndoSteps is newest-first: element 0 is what the next Ctrl+Z reverts.
        var steps = history.GetUndoSteps();
        Assert.Equal(secondAt, steps[0].TimestampUtc);
        Assert.Equal(firstAt, steps[1].TimestampUtc);
        Assert.Equal(secondAt, history.GetTree().Single(n => n.IsCurrent).TimestampUtc);
    }

    /// <summary>
    ///     Redo has to replay the merged node as ONE step and land on the run's
    ///     final state, not on its first tick.
    /// </summary>
    [Fact]
    public void RedoReplaysTheWholeMergedRun()
    {
        var clock = new Clock();
        var history = new HistoryService(new FakeAppLogger(), clock: clock.Read, mergeWindow: Window);
        var chord = ChordAt(4);
        var collection = new ObservableCollection<Chord> { chord };

        for (var i = 0; i < 5; i++)
        {
            history.Do(SustainTick(collection, chord, i, i + 1));
            clock.Advance(TimeSpan.FromMilliseconds(40));
        }

        history.Undo();
        Assert.Equal(FractionalPosition.Zero, chord.ChordNotes[0].Sustain);

        history.Redo();
        Assert.Equal(new FractionalPosition(5), chord.ChordNotes[0].Sustain);
        Assert.False(history.CanRedo);
    }
}
