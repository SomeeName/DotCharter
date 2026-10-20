using System.Collections.ObjectModel;

namespace DotCharter.Tests.Services.State;

public class ProjectDirtyTrackerTests
{
    /// <summary>
    ///     <c>ChangeStamp</c> answers "has anything changed since X", which
    ///     "dirty" cannot: a project stays dirty while it sits untouched, so a
    ///     periodic worker gated on dirty repeats its work over identical
    ///     content forever (<c>AutoBackupGate</c> is the case this exists for).
    ///     Both halves have to move it — an edit moves the history node, and an
    ///     out-of-band change moves NO node, which is the whole reason
    ///     <see cref="ProjectDirtyTracker.MarkDirty" /> exists.
    /// </summary>
    [Fact]
    public void ChangeStamp_MovesForAnEdit_AndForAnOutOfBandChange_ButNotForIdling()
    {
        var history = new HistoryService(new FakeAppLogger());
        using var tracker = new ProjectDirtyTracker(history);

        var baseline = tracker.ChangeStamp;
        Assert.Equal(baseline, tracker.ChangeStamp); // idling moves nothing

        history.Do(new NoOpAction());
        var afterEdit = tracker.ChangeStamp;
        Assert.NotEqual(baseline, afterEdit);
        Assert.Equal(afterEdit, tracker.ChangeStamp); // ...and still nothing

        // Attaching song audio: the history node does not move, the stamp must.
        tracker.MarkDirty();
        Assert.NotEqual(afterEdit, tracker.ChangeStamp);
    }

    /// <summary>An UNDO back to a stamp the project has already been at reads as
    /// that same stamp — which is right: the content is byte-for-byte what was
    /// backed up there, so nothing is owed.</summary>
    [Fact]
    public void ChangeStamp_ReturnsToItsOldValue_WhenTheHistoryDoes()
    {
        var history = new HistoryService(new FakeAppLogger());
        using var tracker = new ProjectDirtyTracker(history);

        history.Do(new NoOpAction());
        var atEdit = tracker.ChangeStamp;
        history.Do(new NoOpAction());
        Assert.NotEqual(atEdit, tracker.ChangeStamp);

        history.Undo();
        Assert.Equal(atEdit, tracker.ChangeStamp);
    }

    [Fact]
    public void StartsClean()
    {
        var history = new HistoryService(new FakeAppLogger());
        using var tracker = new ProjectDirtyTracker(history);

        Assert.False(tracker.IsDirty);
    }

    [Fact]
    public void Edit_MakesDirty_AndRaisesOnce()
    {
        var history = new HistoryService(new FakeAppLogger());
        using var tracker = new ProjectDirtyTracker(history);
        var raised = 0;
        tracker.IsDirtyChanged += () => raised++;

        history.Do(new NoOpAction());
        Assert.True(tracker.IsDirty);
        Assert.Equal(1, raised);

        // A second edit keeps it dirty — edge-triggered event only.
        history.Do(new NoOpAction());
        Assert.True(tracker.IsDirty);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void UndoBackToBaseline_ReadsCleanAgain()
    {
        var history = new HistoryService(new FakeAppLogger());
        using var tracker = new ProjectDirtyTracker(history);

        history.Do(new NoOpAction());
        Assert.True(tracker.IsDirty);

        history.Undo();
        Assert.False(tracker.IsDirty);

        history.Redo();
        Assert.True(tracker.IsDirty);
    }

    [Fact]
    public void MarkClean_AfterSave_ResetsBaseline_UndoDirtiesAgain()
    {
        var history = new HistoryService(new FakeAppLogger());
        using var tracker = new ProjectDirtyTracker(history);
        history.Do(new NoOpAction());

        tracker.MarkClean();
        Assert.False(tracker.IsDirty);

        // Undoing PAST the saved state is a change relative to disk.
        history.Undo();
        Assert.True(tracker.IsDirty);

        history.Redo();
        Assert.False(tracker.IsDirty);
    }

    [Fact]
    public void MarkClean_RaisesWhenFlippingCleanOnly()
    {
        var history = new HistoryService(new FakeAppLogger());
        using var tracker = new ProjectDirtyTracker(history);
        history.Do(new NoOpAction());

        var raised = 0;
        tracker.IsDirtyChanged += () => raised++;

        tracker.MarkClean();
        Assert.Equal(1, raised);

        tracker.MarkClean(); // already clean — no edge, no event
        Assert.Equal(1, raised);
    }

    [Fact]
    public void HistoryClear_ReadsDirty_UntilMarkedClean()
    {
        // Clear() replaces the root sentinel with a NEW node id, so a load
        // path that clears history must re-baseline via MarkClean — until it
        // does the tracker stays conservative (dirty).
        var history = new HistoryService(new FakeAppLogger());
        using var tracker = new ProjectDirtyTracker(history);

        history.Clear();
        Assert.True(tracker.IsDirty);

        tracker.MarkClean();
        Assert.False(tracker.IsDirty);
    }

    [Fact]
    public void SiblingBranch_IsDirty_EvenAtSameDepth()
    {
        var history = new HistoryService(new FakeAppLogger());
        using var tracker = new ProjectDirtyTracker(history);

        history.Do(new NoOpAction());
        tracker.MarkClean();
        history.Undo();
        history.Do(new NoOpAction()); // sibling branch of the saved node

        Assert.True(tracker.IsDirty);
    }

    /// <summary>
    ///     A save lands mid-gesture (autosave, or Ctrl+S while a wheel run is
    ///     still inside the 900 ms merge window) and the charter keeps dragging.
    ///     <para>
    ///         The tracker derives dirty from <c>CurrentNodeId</c>, and a merged
    ///         tick rewrites the head action IN PLACE without moving the current
    ///         node — so every tick after the save was invisible: the chart moved,
    ///         the title-bar `*` stayed off and the close prompt never fired.
    ///     </para>
    /// </summary>
    [Fact]
    public void ASaveMidGesture_DoesNotSwallowTheTicksThatFollowIt()
    {
        var clock = new FakeClock();
        var history = new HistoryService(new FakeAppLogger(), clock: clock.Read,
            mergeWindow: TimeSpan.FromMilliseconds(900));
        using var tracker = new ProjectDirtyTracker(history);
        var chord = ChordWithSustain();
        var chords = new ObservableCollection<Chord> { chord };

        history.Do(SustainTick(chords, chord, 0, 1));
        tracker.MarkClean(); // the save
        Assert.False(tracker.IsDirty);

        clock.Advance(TimeSpan.FromMilliseconds(50)); // still well inside the window
        history.Do(SustainTick(chords, chord, 1, 2));

        Assert.Equal(new FractionalPosition(2), chord.ChordNotes[0].Sustain);
        Assert.True(tracker.IsDirty);

        // ...and the SAVED state is still an undo target: one Ctrl+Z lands back
        // on what is on disk rather than stepping straight past it.
        history.Undo();
        Assert.Equal(new FractionalPosition(1), chord.ChordNotes[0].Sustain);
        Assert.False(tracker.IsDirty);
    }

    /// <summary>
    ///     Crash recovery arms its snapshot off a CHANGE signal and only had the
    ///     undo history's, so project-level state the history cannot represent
    ///     (attaching or replacing the song audio, a checkpoint or recovery
    ///     restore) was never snapshotted. The edge-triggered
    ///     <see cref="ProjectDirtyTracker.IsDirtyChanged" /> is not enough for that:
    ///     a second <c>MarkDirty</c> on an already-dirty project raises nothing,
    ///     which is exactly the "edit, autosave, then attach audio" order.
    /// </summary>
    [Fact]
    public void DirtyMarked_FiresOnEveryMarkDirty_EvenWhenAlreadyDirty()
    {
        var history = new HistoryService(new FakeAppLogger());
        using var tracker = new ProjectDirtyTracker(history);
        var marked = 0;
        var edges = 0;
        tracker.DirtyMarked += () => marked++;
        tracker.IsDirtyChanged += () => edges++;

        history.Do(new NoOpAction()); // dirty via the history: one edge, no mark
        Assert.Equal(0, marked);
        Assert.Equal(1, edges);

        tracker.MarkDirty(); // already dirty: the edge stays silent, this must not
        tracker.MarkDirty();
        Assert.Equal(2, marked);
        Assert.Equal(1, edges);
    }

    /// <summary>
    ///     The exact mirror, and for the exact mirror reason. Crash recovery
    ///     RETIRES its snapshot off the clean declaration every replace path
    ///     makes, and the edge is silent on the case that matters most: browsing
    ///     one library song after another leaves the session clean throughout, so
    ///     an edge-triggered subscriber would disarm the snapshotter on the first
    ///     load and never again — which is the "asks to restore a PSARC song
    ///     nobody edited" report.
    /// </summary>
    [Fact]
    public void CleanMarked_FiresOnEveryMarkClean_EvenWhenAlreadyClean()
    {
        var history = new HistoryService(new FakeAppLogger());
        using var tracker = new ProjectDirtyTracker(history);
        var marked = 0;
        var edges = 0;
        tracker.CleanMarked += () => marked++;
        tracker.IsDirtyChanged += () => edges++;

        Assert.False(tracker.IsDirty); // already clean: the edge cannot fire
        tracker.MarkClean();
        tracker.MarkClean();

        Assert.Equal(2, marked);
        Assert.Equal(0, edges);
    }

    // ---------- an apply that THREW ----------

    /// <summary>
    ///     `Do` pushes the undo node only after `Execute` returns, so a throw
    ///     halfway leaves the chart edited with no node describing it — and because
    ///     the dirty state is DERIVED from the node id, the project read CLEAN.
    ///     No `*`, no close prompt, and the charter's work left with the window.
    /// </summary>
    [Fact]
    public void AComposite_ThatThrowsMidApply_RevertsWhatItDid_LeavesTheHistoryAlone_AndReadsDIRTY()
    {
        var history = new HistoryService(new FakeAppLogger());
        using var tracker = new ProjectDirtyTracker(history);
        var log = new List<string>();
        var baseline = history.CurrentNodeId;

        var composite = new CompositeAction([
            new RecordingAction("a", log),
            new RecordingAction("b", log),
            new RecordingAction("c", log, throwOnExecute: true)
        ]);

        Assert.Throws<InvalidOperationException>(() => history.Do(composite));

        // 1–2 applied then rolled back in reverse; 3 never applied.
        Assert.Equal(["a:do", "b:do", "b:undo", "a:undo"], log);
        // No node was pushed for an action that never landed.
        Assert.Equal(baseline, history.CurrentNodeId);
        Assert.False(history.CanUndo);
        // …and the project is dirty anyway, which is the conservative direction.
        Assert.True(tracker.IsDirty);
    }

    [Fact]
    public void AnUndo_ThatThrowsMidRevert_LeavesTheProjectDIRTY()
    {
        var history = new HistoryService(new FakeAppLogger());
        var log = new List<string>();
        var action = new RecordingAction("only", log, throwOnUndo: true);
        history.Do(action);

        using var tracker = new ProjectDirtyTracker(history);
        Assert.False(tracker.IsDirty); // the baseline is the post-edit node

        Assert.Throws<InvalidOperationException>(history.Undo);
        Assert.True(tracker.IsDirty);
    }

    private sealed class RecordingAction(
        string name, List<string> log, bool throwOnExecute = false, bool throwOnUndo = false) : IChartAction
    {
        public bool HasEffect => true;

        public void Execute()
        {
            if (throwOnExecute) throw new InvalidOperationException($"{name} refused to apply");
            log.Add($"{name}:do");
        }

        public void Undo()
        {
            if (throwOnUndo) throw new InvalidOperationException($"{name} refused to revert");
            log.Add($"{name}:undo");
        }
    }

    private static Chord ChordWithSustain()
    {
        var chord = new Chord { Position = new FractionalPosition(4) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 5 });
        return chord;
    }

    private static MutateChordsAction SustainTick(
        ObservableCollection<Chord> collection, Chord target, int fromBeats, int toBeats)
    {
        var oldState = target.Clone();
        oldState.ChordNotes[0].Sustain = new FractionalPosition(fromBeats);
        var newState = target.Clone();
        newState.ChordNotes[0].Sustain = new FractionalPosition(toBeats);
        return new MutateChordsAction(collection, [target], [oldState], [newState], null, MergeVerbs.Sustain);
    }

    private sealed class FakeClock
    {
        public DateTime NowUtc = new(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        public DateTime Read() => NowUtc;
        public void Advance(TimeSpan by) => NowUtc += by;
    }

    private sealed class NoOpAction : IChartAction
    {
        public bool HasEffect => true;

        public void Execute()
        {
        }

        public void Undo()
        {
        }
    }
}
