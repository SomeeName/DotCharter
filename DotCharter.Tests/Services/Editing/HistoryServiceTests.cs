using DotCharter.Services.Editing;

namespace DotCharter.Tests.Services.Editing;

public class HistoryServiceTests
{
    [Fact]
    public void Do_ExecutesAction_PushesToUndoStack_FiresEvent()
    {
        var svc = new HistoryService(new FakeAppLogger());
        var action = new RecordingAction();
        var eventCount = 0;
        svc.HistoryChanged += () => eventCount++;

        var result = svc.Do(action);

        Assert.True(result);
        Assert.Equal(1, action.ExecuteCount);
        Assert.Equal(1, eventCount);
    }

    [Fact]
    public void Do_WithNoEffect_Skips()
    {
        var svc = new HistoryService(new FakeAppLogger());
        var action = new RecordingAction { HasEffect = false };

        var result = svc.Do(action);

        Assert.False(result);
        Assert.Equal(0, action.ExecuteCount);
    }

    [Fact]
    public void UndoThenRedo_RestoresState()
    {
        var svc = new HistoryService(new FakeAppLogger());
        var action = new RecordingAction();

        svc.Do(action);
        svc.Undo();
        Assert.Equal(1, action.UndoCount);

        svc.Redo();
        Assert.Equal(2, action.ExecuteCount);
    }

    [Fact]
    public void Undo_OnEmptyStack_IsSafe()
    {
        var svc = new HistoryService(new FakeAppLogger());
        svc.Undo(); // should not throw
    }

    [Fact]
    public void PushAction_CapsUndoStackAt500()
    {
        var svc = new HistoryService(new FakeAppLogger());
        for (var i = 0; i < 600; i++) svc.PushAction(new RecordingAction());

        // We can't observe count directly, but Undo should still work for at most 500 steps.
        // Drain the stack by undoing until events stop firing.
        var undoCount = 0;

        void Handler() => undoCount++;

        svc.HistoryChanged += Handler;

        for (var i = 0; i < 600; i++) svc.Undo();

        svc.HistoryChanged -= Handler;
        Assert.InRange(undoCount, 1, 500);
    }

    [Fact]
    public void Clear_EmptiesBothStacks_AndLogs()
    {
        var logger = new FakeAppLogger();
        var svc = new HistoryService(logger);
        svc.Do(new RecordingAction());

        svc.Clear();

        Assert.True(logger.HasEntryContaining("INFO", "History cleared"));
        // After clear, undo should be a no-op (no crash, no new events beyond Clear's).
        svc.Undo();
    }

    // ---- Tree (branching) semantics -------------------------------------

    [Fact]
    public void UndoThenNewEdit_CreatesSiblingBranch_PreservesOldTail()
    {
        var svc = new HistoryService(new FakeAppLogger());
        var a = new RecordingAction();
        var b = new RecordingAction();
        var c = new RecordingAction();

        svc.Do(a);
        var idA = svc.CurrentNodeId;
        svc.Do(b);
        var idB = svc.CurrentNodeId;
        svc.Undo();
        svc.Do(c); // previously this discarded b; now it must branch
        var idC = svc.CurrentNodeId;

        var tree = svc.GetTree();
        Assert.Equal(4, tree.Count); // root + a + b + c
        Assert.Contains(tree, n => n.Id == idB); // old tail preserved
        Assert.Equal(idA, tree.Single(n => n.Id == idB).ParentId);
        Assert.Equal(idA, tree.Single(n => n.Id == idC).ParentId);
        Assert.Equal(idC, tree.Single(n => n.IsCurrent).Id);
        Assert.False(tree.Single(n => n.Id == idB).IsOnCurrentPath);
        Assert.True(tree.Single(n => n.Id == idC).IsOnCurrentPath);
        Assert.True(tree.Single(n => n.Id == idA).IsOnCurrentPath);
    }

    [Fact]
    public void Redo_AtFork_FollowsMostRecentBranch()
    {
        var svc = new HistoryService(new FakeAppLogger());
        var b = new RecordingAction();
        var c = new RecordingAction();

        svc.Do(new RecordingAction()); // a
        svc.Do(b);
        svc.Undo();
        svc.Do(c); // newest branch at the fork
        var idC = svc.CurrentNodeId;
        svc.Undo(); // back to a

        svc.Redo();

        Assert.Equal(idC, svc.CurrentNodeId); // followed c, not b
        Assert.Equal(2, c.ExecuteCount);
        Assert.Equal(1, b.ExecuteCount); // b untouched
    }

    [Fact]
    public void UndoRedo_StayLinearAlongCurrentPath()
    {
        var svc = new HistoryService(new FakeAppLogger());
        var a = new RecordingAction();
        var b = new RecordingAction();

        svc.Do(a);
        svc.Do(b);
        var idB = svc.CurrentNodeId;
        svc.Undo();
        svc.Undo();
        Assert.False(svc.CanUndo);

        svc.Redo();
        svc.Redo();

        Assert.Equal(idB, svc.CurrentNodeId);
        Assert.Equal(2, a.ExecuteCount);
        Assert.Equal(2, b.ExecuteCount);
        Assert.False(svc.CanRedo);
    }

    [Fact]
    public void NavigateTo_CrossBranch_WalksViaCommonAncestor()
    {
        var svc = new HistoryService(new FakeAppLogger());
        var a = new RecordingAction();
        var b = new RecordingAction();
        var c = new RecordingAction();

        svc.Do(a);
        svc.Do(b);
        var idB = svc.CurrentNodeId;
        svc.Undo();
        svc.Do(c); // current = c, sibling of b under a

        Assert.True(svc.NavigateTo(idB));

        Assert.Equal(idB, svc.CurrentNodeId);
        Assert.Equal(1, c.UndoCount); // stepped back over c
        Assert.Equal(2, b.ExecuteCount); // replayed b
        Assert.Equal(1, a.ExecuteCount); // common ancestor untouched
        Assert.Equal(0, a.UndoCount);
    }

    [Fact]
    public void NavigateTo_AncestorAndBack_ReplaysExactly()
    {
        var svc = new HistoryService(new FakeAppLogger());
        var b = new RecordingAction();
        var c = new RecordingAction();

        svc.Do(new RecordingAction()); // a
        var idA = svc.CurrentNodeId;
        svc.Do(b);
        svc.Do(c);
        var idC = svc.CurrentNodeId;

        Assert.True(svc.NavigateTo(idA));
        Assert.Equal(idA, svc.CurrentNodeId);
        Assert.Equal(1, b.UndoCount);
        Assert.Equal(1, c.UndoCount);

        Assert.True(svc.NavigateTo(idC));
        Assert.Equal(idC, svc.CurrentNodeId);
        Assert.Equal(2, b.ExecuteCount);
        Assert.Equal(2, c.ExecuteCount);
    }

    [Fact]
    public void NavigateTo_CurrentNode_IsNoOp()
    {
        var svc = new HistoryService(new FakeAppLogger());
        var a = new RecordingAction();
        svc.Do(a);

        Assert.True(svc.NavigateTo(svc.CurrentNodeId));
        Assert.Equal(1, a.ExecuteCount);
        Assert.Equal(0, a.UndoCount);
    }

    [Fact]
    public void NavigateTo_UnknownId_ReturnsFalse_ChangesNothing()
    {
        var svc = new HistoryService(new FakeAppLogger());
        var a = new RecordingAction();
        svc.Do(a);
        var current = svc.CurrentNodeId;

        Assert.False(svc.NavigateTo(9999));
        Assert.Equal(current, svc.CurrentNodeId);
        Assert.Equal(1, a.ExecuteCount);
        Assert.Equal(0, a.UndoCount);
    }

    [Fact]
    public void FocusRequested_RaisedOnUndoAndRedo_WithTaggedArrangement()
    {
        var scope = new FakeEditingScope { Arrangement = new Arrangement() };
        var svc = new HistoryService(new FakeAppLogger(), scope);
        svc.Do(new RecordingAction());

        var raised = new List<Arrangement?>();
        svc.FocusRequested += t => raised.Add(t);

        svc.Undo();
        svc.Redo();

        Assert.Equal(2, raised.Count);
        Assert.Same(scope.Arrangement, raised[0]);
        Assert.Same(scope.Arrangement, raised[1]);
    }

    // ---- Memory bounding (configurable budget, prune-based) -------------

    [Fact]
    public void Budget_PrunesColdestOffPathBranchFirst()
    {
        var svc = new HistoryService(new FakeAppLogger(), maxNodes: 3);
        var a = new RecordingAction();
        var b = new RecordingAction();
        var c = new RecordingAction();
        var d = new RecordingAction();

        svc.Do(a);
        var idA = svc.CurrentNodeId;
        svc.Do(b);
        var idB = svc.CurrentNodeId;
        svc.Undo();
        svc.Do(c); // 3 nodes: a -> {b, c}; budget not exceeded yet
        var idC = svc.CurrentNodeId;
        svc.Do(d); // 4th node: the cold off-path leaf (b) must be pruned

        var tree = svc.GetTree();
        Assert.DoesNotContain(tree, n => n.Id == idB);
        Assert.Equal(4, tree.Count); // root + a + c + d
        Assert.Contains(tree, n => n.Id == idA);
        Assert.Contains(tree, n => n.Id == idC);

        // Pruning must not run any Do/Undo on the affected actions.
        Assert.Equal(1, b.ExecuteCount);
        Assert.Equal(1, b.UndoCount);

        // A pruned node can no longer be navigated to.
        Assert.False(svc.NavigateTo(idB));

        // The current path is fully intact: d, c, a undo in order.
        svc.Undo();
        svc.Undo();
        svc.Undo();
        Assert.False(svc.CanUndo);
        Assert.Equal(1, d.UndoCount);
        Assert.Equal(1, c.UndoCount);
        Assert.Equal(1, a.UndoCount);
    }

    [Fact]
    public void Budget_LinearChain_CollapsesOldestIntoRoot()
    {
        var svc = new HistoryService(new FakeAppLogger(), maxNodes: 2);
        var a = new RecordingAction();
        var b = new RecordingAction();
        var c = new RecordingAction();

        svc.Do(a);
        svc.Do(b);
        svc.Do(c); // over budget; a collapses into the root sentinel

        var tree = svc.GetTree();
        Assert.Equal(3, tree.Count); // a (as root) + b + c
        Assert.Single(tree, n => n.ParentId == null); // exactly one root

        // Only b and c stay undoable; a's state is the oldest retained one.
        svc.Undo();
        svc.Undo();
        Assert.False(svc.CanUndo);
        Assert.Equal(1, c.UndoCount);
        Assert.Equal(1, b.UndoCount);
        Assert.Equal(0, a.UndoCount);

        // And the collapsed chain still redoes cleanly.
        svc.Redo();
        svc.Redo();
        Assert.Equal(2, b.ExecuteCount);
        Assert.Equal(2, c.ExecuteCount);
    }

    [Fact]
    public void Budget_KeepsDeepCurrentPath_OverColdBranches()
    {
        var svc = new HistoryService(new FakeAppLogger(), maxNodes: 4);
        var branch = new RecordingAction();

        svc.Do(new RecordingAction()); // a
        svc.Do(branch); // b
        var idBranch = svc.CurrentNodeId;
        svc.Undo(); // back to a; b becomes a cold branch
        svc.Do(new RecordingAction()); // c
        svc.Do(new RecordingAction()); // d  -> 4 nodes, at budget
        svc.Do(new RecordingAction()); // e  -> over budget: prune b, keep path

        var tree = svc.GetTree();
        Assert.DoesNotContain(tree, n => n.Id == idBranch);
        Assert.Equal(5, tree.Count); // root + a + c + d + e
        Assert.All(tree, n => Assert.True(n.IsOnCurrentPath));
    }

    private sealed class FakeEditingScope : IEditingScope
    {
        public ChartProject? Project => null;
        public Arrangement? Arrangement { get; set; }
        public ChartLevel? Level => null;
        public System.Collections.ObjectModel.ObservableCollection<GuitarNote> Notes { get; } = [];
        public System.Collections.ObjectModel.ObservableCollection<Chord> Chords { get; } = [];
        public System.Collections.ObjectModel.ObservableCollection<HandShape> HandShapes { get; } = [];
        public System.Collections.ObjectModel.ObservableCollection<FretAnchor> Anchors { get; } = [];
        public System.Collections.ObjectModel.ObservableCollection<EventPoint> EventPoints { get; } = [];
        public System.Collections.ObjectModel.ObservableCollection<BeatMarker> Beats { get; } = [];

        public void SetResolver(Func<EditingTarget?> resolver)
        {
        }
    }

    private sealed class RecordingAction : IChartAction
    {
        public int ExecuteCount;
        public int UndoCount;
        public bool HasEffect { get; init; } = true;

        public void Execute() => ExecuteCount++;

        public void Undo() => UndoCount++;
    }
}
