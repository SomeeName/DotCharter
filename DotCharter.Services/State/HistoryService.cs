using DotCharter.Services.Diagnostics;
using DotCharter.Services.Editing;

namespace DotCharter.Services.State;

/// <summary>
/// Tree-based (branching) undo history. Undoing and then making a new edit no
/// longer discards the redo tail: the new edit starts a sibling branch and the
/// old tail is preserved. Ctrl+Z / Ctrl+Y stay strictly linear along the
/// current path — <see cref="Undo"/> steps to the parent, <see cref="Redo"/>
/// follows the most recently taken branch at each fork.
///
/// Memory is bounded (actions may hold full copies of modified notes/chords,
/// so an uncapped tree can grow into hundreds of MB over long sessions): past
/// the node budget the coldest leaves off the current path are pruned first;
/// if the tree is a pure chain the oldest step is collapsed into the root
/// sentinel (the classic linear-cap behavior). Actions capture live object
/// references and closures, so nodes cannot be serialized to a spill file —
/// pruning is the bounding strategy.
/// </summary>
public class HistoryService : IHistoryService
{
    public const int DefaultMaxNodes = 500;

    /// <summary>
    /// The memory the history may keep alive in whole-project snapshots. The node cap
    ///     counts a one-note nudge and a two-clone power tool alike, and a power-tool step on a
    ///     base-game song imported with all its levels holds 20-67 MiB (two clones, median 10 MiB
    ///     each, the largest 33 MiB; measured). 256 MiB keeps a dozen such steps on a
    ///     typical song and four on the heaviest, beside a ~1.1 GiB working set.
    /// </summary>
    public const long DefaultMaxBytes = 256L * 1024 * 1024;

    /// <summary>
    ///     How long a gesture stays open for merging after its last tick.
    ///     <para>
    ///         <b>Measured against the real gesture it exists to catch.</b> In one real
    ///         session the 111-tick hand-shape resize had internal gaps
    ///         up to ~750 ms (the user pausing mid-wheel) but a clear 3.3 s break
    ///         where they stopped and restarted. 900 ms sits above the first and
    ///         far below the second, so one wheel run collapses to one node while
    ///         a genuine pause still starts a new one.
    ///     </para>
    ///     <para>
    ///         Erring long is the cheap direction: two deliberate taps merging into
    ///         one undo step still undoes something the user recognises ("I made it
    ///         longer"), whereas erring short brings back the 111 nodes. Gestures
    ///         that know their own end call <see cref="EndGesture" /> and never
    ///         depend on this at all.
    ///     </para>
    /// </summary>
    public static readonly TimeSpan MergeWindow = TimeSpan.FromMilliseconds(900);

    private readonly IAppLogger _logger;
    private readonly int _maxNodes;
    private readonly long _maxBytes;
    private readonly TimeSpan _mergeWindow;
    private readonly Dictionary<int, Node> _nodes = [];
    private readonly Func<DateTime> _now;
    private readonly ChartingSessionLog? _session;
    private readonly IEditingScope? _scope;
    private Node _current;

    /// <summary>
    ///     The node an in-flight gesture is folding into, or null when no gesture
    ///     is open. Cleared by every history NAVIGATION — undo, redo, jump, clear —
    ///     so a gesture can never merge across one. Without that, "drag, Ctrl+Z,
    ///     drag again" would fold the second drag into a node the user had just
    ///     reverted and silently re-apply it.
    /// </summary>
    private Node? _mergeAnchor;

    private DateTime _mergeAnchorUtc;
    private int _nextId;
    private Node _root;

    /// <summary>The sum of <see cref="IChartAction.EstimatedBytes" /> over every node's action.</summary>
    private long _bytes;

    public HistoryService(IAppLogger logger, IEditingScope? scope = null, int maxNodes = DefaultMaxNodes,
        Func<DateTime>? clock = null, TimeSpan? mergeWindow = null, ChartingSessionLog? session = null,
        long maxBytes = DefaultMaxBytes)
    {
        _logger = logger;
        _session = session;
        _scope = scope;
        _maxNodes = Math.Max(1, maxNodes);
        _maxBytes = Math.Max(0, maxBytes);
        _now = clock ?? (() => DateTime.UtcNow);
        _mergeWindow = mergeWindow ?? MergeWindow;
        _root = CreateRoot();
        _current = _root;
    }

    public event Action? HistoryChanged;
    public event Action<Arrangement?>? FocusRequested;
    public event Action? ActionFailed;

    /// <summary>
    ///     Runs one apply/revert and, if it throws, tells the world the chart and
    ///     the history have parted company before letting the exception out. See
    ///     <see cref="IHistoryService.ActionFailed" /> for why a silent rethrow was
    ///     the bug: the dirty state is derived from the node id, and a failed apply
    ///     moves no node.
    /// </summary>
    private void RunOrReportFailure(Action apply, string what)
    {
        try
        {
            apply();
        }
        catch (Exception ex)
        {
            // A partial apply edited something; nobody knows which arrangement.
            MarkEdited(null);
            _logger.Error(AppLogCategory.History,
                $"{what} threw; the chart may be partially edited and the history does not describe it. " +
                "Marking the project dirty so the work is not silently discarded.", ex);
            ActionFailed?.Invoke();
            throw;
        }
    }

    /// <summary>What the history's actions keep alive, as they estimate it.</summary>
    public long RetainedBytes => _bytes;

    public bool CanUndo => _current != _root;
    public bool CanRedo => NextRedoChild() != null;
    public int CurrentNodeId => _current.Id;

    public void Clear()
    {
        _nodes.Clear();
        _root = CreateRoot();
        _current = _root;
        _bytes = 0;
        EndGesture();
        _logger.Info(AppLogCategory.History, "History cleared.");
        HistoryChanged?.Invoke();
    }

    public void EndGesture() => _mergeAnchor = null;

    public bool Do(IChartAction action)
    {
        if (!action.HasEffect) return false;

        RunOrReportFailure(action.Execute, $"Do {DescribeAction(action)}");
        PushAction(action);
        HistoryChanged?.Invoke();
        return true;
    }

    public bool PushAction(IChartAction action)
    {
        if (!action.HasEffect) return false;
        // Do has applied it, and a direct push comes after the caller applied it: either way the
        // chart moved, merged into the open gesture or not.
        MarkEdited(action);
        if (TryMergeIntoOpenGesture(action)) return true;

        // Tag the action with whichever arrangement the editing scope is
        // currently pointing at so undo/redo can refocus the right panel.
        var node = new Node(_nextId++, action, _scope?.Arrangement, DescribeAction(action), _now())
        {
            Parent = _current
        };
        _nodes[node.Id] = node;
        _bytes += action.EstimatedBytes;
        _current.Children.Add(node);
        // Redo follows the most recent branch by default.
        _current.PreferredChild = node;
        var branched = _current.Children.Count > 1;
        _current = node;
        OpenGesture(node);
        _session?.CountEdit();
        EnforceBudget();
        _logger.Info(AppLogCategory.History,
            $"Executed {node.Description} (target={node.Target?.Name ?? "<none>"}, node={node.Id}{(branched ? ", new branch" : "")}). Nodes={_nodes.Count - 1}, retained≈{_bytes / (1024 * 1024)} MiB.");
        return true;
    }

    /// <summary>
    ///     Folds a tick of an in-flight gesture into the node the gesture already
    ///     owns, instead of pushing a new one.
    ///     <para>
    ///         <paramref name="action" /> has already been applied to the chart —
    ///         both entry points (<see cref="Do" /> after <c>Execute</c>, and a
    ///         direct <see cref="PushAction" /> from a caller that applied the
    ///         change itself) leave the chart in the post-action state — so a
    ///         successful merge must only rewrite the head action's record, never
    ///         the chart.
    ///     </para>
    /// </summary>
    private bool TryMergeIntoOpenGesture(IChartAction action)
    {
        if (_mergeAnchor == null || _mergeAnchor != _current) return false;
        if (_current.Action is not IMergeableAction head) return false;
        if (action is not IMergeableAction incoming) return false;
        if (head.MergeVerb.Length == 0) return false;
        if (!string.Equals(head.MergeVerb, incoming.MergeVerb, StringComparison.Ordinal)) return false;
        // A NEGATIVE gap closes the window just like an over-long one. The clock
        // is wall-clock (`DateTime.UtcNow` by default), and wall-clock is not
        // monotonic: an NTP correction, a VM resume or a user changing the system
        // time can move it BACKWARDS mid-session. A bare `> _mergeWindow` reads a
        // backwards step as "no time has passed", so the window stops closing and
        // two edits the charter made minutes apart fold into one undo node —
        // silently, and in the direction that LOSES an undo step. Refusing is
        // always safe (it costs one node, never correctness), so refuse.
        var sinceLastTick = _now() - _mergeAnchorUtc;
        if (sinceLastTick < TimeSpan.Zero || sinceLastTick > _mergeWindow) return false;
        if (!head.CanAbsorb(action)) return false;

        head.Absorb(action);
        _mergeAnchorUtc = _now();
        _current.MergedTicks++;
        _session?.CountGestureTick();
        _logger.Trace(AppLogCategory.History,
            $"Merged a '{head.MergeVerb}' tick into node {_current.Id} ({_current.MergedTicks + 1} total).");
        return true;
    }

    private void OpenGesture(Node node)
    {
        _mergeAnchor = node.Action is IMergeableAction { MergeVerb.Length: > 0 } ? node : null;
        _mergeAnchorUtc = _now();
    }

    /// <summary>
    /// The history node's description TOKEN, resolved to words at display
    ///     time by <see cref="HistoryActionText.Describe" /> (so a language switch
    ///     relabels the existing history): a composite's own
    ///     <see cref="CompositeAction.LabelKey" /> when the gesture that built it
    ///     named itself; else the merge verb a generic action carries ("fret",
    ///     "sustain"), which says what the edit WAS; else the action's type name.
    ///     An unlabelled composite is described by its first effective child.
    /// </summary>
    private static string DescribeAction(IChartAction action)
    {
        if (action is CompositeAction { LabelKey.Length: > 0 } labelled)
            return HistoryActionText.KeyToken + labelled.LabelKey;
        while (action is CompositeAction { LabelKey: null or "", Actions.Count: > 0 } composite)
            action = composite.Actions[0];
        if (action is CompositeAction { LabelKey.Length: > 0 } inner)
            return HistoryActionText.KeyToken + inner.LabelKey;
        if (action is IMergeableAction { MergeVerb.Length: > 0 } verb)
            return HistoryActionText.VerbToken + verb.MergeVerb;
        return action.GetType().Name;
    }

    public void Undo()
    {
        if (_current == _root)
        {
            _logger.Trace(AppLogCategory.History, "Undo requested with nothing to undo.");
            return;
        }

        var node = _current;
        EndGesture();
        _session?.CountUndo();
        FocusRequested?.Invoke(node.Target);
        RunOrReportFailure(node.Action!.Undo, $"Undo {node.Description}");
        MarkEdited(node.Action);
        // Plain Redo right after this Undo must return to the same node, even
        // if a sibling branch was taken more recently in the past.
        node.Parent!.PreferredChild = node;
        _current = node.Parent;
        _logger.Info(AppLogCategory.History,
            $"Undo {node.Action.GetType().Name} (target={node.Target?.Name ?? "<none>"}). Node {node.Id} -> {_current.Id}.");
        HistoryChanged?.Invoke();
    }

    public void Redo()
    {
        var next = NextRedoChild();
        if (next == null)
        {
            _logger.Trace(AppLogCategory.History, "Redo requested with nothing to redo.");
            return;
        }

        EndGesture();
        _session?.CountRedo();
        FocusRequested?.Invoke(next.Target);
        RunOrReportFailure(next.Action!.Execute, $"Redo {next.Description}");
        MarkEdited(next.Action);
        _current = next;
        _logger.Info(AppLogCategory.History,
            $"Redo {next.Action.GetType().Name} (target={next.Target?.Name ?? "<none>"}). Current={next.Id}.");
        HistoryChanged?.Invoke();
    }

    public IReadOnlyList<HistoryNodeSnapshot> GetTree()
    {
        var onPath = new HashSet<Node>();
        for (var n = _current; n != null; n = n.Parent) onPath.Add(n);

        return _nodes.Values
            .OrderBy(n => n.Id)
            .Select(n => new HistoryNodeSnapshot(
                n.Id, n.Parent?.Id, n.Description, n.TimestampUtc,
                n == _current, onPath.Contains(n)))
            .ToList();
    }

    public bool NavigateTo(int nodeId)
    {
        if (!_nodes.TryGetValue(nodeId, out var target))
        {
            _logger.Warning(AppLogCategory.History, $"NavigateTo unknown history node {nodeId} (pruned?).");
            return false;
        }

        if (target == _current) return true;

        // Common ancestor: mark the target's ancestry, then walk up from the
        // current node until we hit it.
        var targetAncestry = new HashSet<Node>();
        for (var n = target; n != null; n = n.Parent) targetAncestry.Add(n);

        var undoSteps = 0;
        var ancestor = _current;
        while (!targetAncestry.Contains(ancestor))
        {
            ancestor = ancestor.Parent!;
            undoSteps++;
        }

        _logger.Info(AppLogCategory.History,
            $"Navigate to history node {nodeId} via ancestor {ancestor.Id}: {undoSteps} undo step(s).");

        // Walk up first (Undo rewrites PreferredChild along the way), THEN
        // point every fork on the descent path at the branch to take.
        for (var i = 0; i < undoSteps; i++) Undo();

        var redoSteps = 0;
        for (var n = target; n != ancestor; n = n.Parent!)
        {
            n.Parent!.PreferredChild = n;
            redoSteps++;
        }

        for (var i = 0; i < redoSteps; i++) Redo();
        return true;
    }

    /// <summary>Bumps <see cref="Arrangement.GetRevision" /> on every arrangement <paramref name="action" /> can
    /// have changed; null marks them all.</summary>
    private void MarkEdited(IChartAction? action) => ChartEditRevisions.MarkEdited(action, _scope?.Project, _scope?.Arrangement);

    private Node CreateRoot()
    {
        var root = new Node(_nextId++, null, null, string.Empty, _now());
        _nodes[root.Id] = root;
        return root;
    }

    private Node? NextRedoChild()
    {
        if (_current.Children.Count == 0) return null;
        var preferred = _current.PreferredChild;
        if (preferred != null && preferred.Parent == _current) return preferred;
        return _current.Children[^1];
    }

    public IReadOnlyList<HistoryStepInfo> GetUndoSteps(int max = 64)
    {
        var steps = new List<HistoryStepInfo>();
        for (var n = _current; n != _root && steps.Count < max; n = n.Parent!)
            steps.Add(new HistoryStepInfo(n.Description, n.TimestampUtc));
        return steps;
    }

    public IReadOnlyList<HistoryStepInfo> GetRedoSteps(int max = 64)
    {
        var steps = new List<HistoryStepInfo>();
        var n = NextRedoChild();
        while (n != null && steps.Count < max)
        {
            steps.Add(new HistoryStepInfo(n.Description, n.TimestampUtc));
            // Mirror NextRedoChild: the preferred branch, else the newest.
            var preferred = n.PreferredChild;
            n = preferred != null && preferred.Parent == n
                ? preferred
                : n.Children.Count > 0 ? n.Children[^1] : null;
        }

        return steps;
    }

    /// <summary>
    /// Keeps the tree at or below the node budget AND the byte budget. Cold branches go first:
    /// the oldest leaf that is NOT on the current path is removed, repeatedly
    /// (this eats abandoned branches bottom-up; under byte pressure a leaf that holds bytes
    /// goes before one that holds none). When no such leaf exists the
    /// tree is a pure chain ending at the current node, so the oldest step is
    /// collapsed into the root sentinel exactly like the old linear cap.
    /// <para>The byte budget only sheds what can help: the CURRENT step is never collapsed for
    /// bytes (the tool the charter just ran stays undoable, whatever it weighs), so the loop stops
    /// once every byte left is that step's.</para>
    /// </summary>
    private void EnforceBudget()
    {
        while (_nodes.Count - 1 > _maxNodes || OverByteBudget())
        {
            var leaf = (OverByteBudget() ? FindColdestPrunableLeaf(holdingBytes: true) : null) ??
                       FindColdestPrunableLeaf(holdingBytes: false);
            if (leaf != null)
            {
                RemoveLeaf(leaf);
                continue;
            }

            if (_current == _root) break; // defensive; cannot happen while over budget
            AdvanceRoot();
        }
    }

    /// <summary>Over the byte budget with bytes held somewhere other than the current step.</summary>
    private bool OverByteBudget() => _bytes > _maxBytes && _bytes - (_current.Action?.EstimatedBytes ?? 0) > 0;

    private Node? FindColdestPrunableLeaf(bool holdingBytes)
    {
        var onPath = new HashSet<Node>();
        for (var n = _current; n != null; n = n.Parent) onPath.Add(n);

        Node? best = null;
        foreach (var n in _nodes.Values)
        {
            if (n.Children.Count != 0 || onPath.Contains(n)) continue;
            if (holdingBytes && (n.Action?.EstimatedBytes ?? 0) <= 0) continue;
            if (best == null || n.Id < best.Id) best = n;
        }

        return best;
    }

    private void RemoveLeaf(Node leaf)
    {
        var parent = leaf.Parent!;
        parent.Children.Remove(leaf);
        if (parent.PreferredChild == leaf)
            parent.PreferredChild = parent.Children.Count > 0 ? parent.Children[^1] : null;
        _nodes.Remove(leaf.Id);
        _bytes -= leaf.Action?.EstimatedBytes ?? 0;
        _logger.Trace(AppLogCategory.History,
            $"Pruned cold history node {leaf.Id} ({leaf.Description}). Nodes={_nodes.Count - 1}.");
    }

    private void AdvanceRoot()
    {
        // Only reached when the tree is a pure chain below the root: promote
        // the first real node to root sentinel. Its action cannot be
        // undone (the state it produced becomes the oldest retained state),
        // but its description stays visible in the tree.
        var first = _root.Children[0];
        _nodes.Remove(_root.Id);
        first.Parent = null;
        _bytes -= first.Action?.EstimatedBytes ?? 0;
        first.Action = null;
        first.Target = null;
        _root = first;
        _logger.Trace(AppLogCategory.History,
            $"History budget reached; oldest step collapsed into root (now node {first.Id}). Nodes={_nodes.Count - 1}.");
    }

    private sealed class Node(
        int id, IChartAction? action, Arrangement? target, string description, DateTime timestampUtc)
    {
        public readonly List<Node> Children = [];
        public int Id { get; } = id;
        public IChartAction? Action { get; set; } = action; // null only for the root sentinel
        public Arrangement? Target { get; set; } = target;
        public string Description { get; } = description;

        /// <summary>
        ///     Stamped from the SERVICE's clock, not from ambient
        ///     <see cref="DateTime.UtcNow" />. The clock is injectable precisely so
        ///     the type is deterministic under test, and a node that read the
        ///     ambient time made the one field the history UI actually displays
        ///     (<see cref="HistoryNodeSnapshot.TimestampUtc" />,
        ///     <see cref="HistoryStepInfo.TimestampUtc" />) the one field no test
        ///     could assert.
        /// </summary>
        public DateTime TimestampUtc { get; } = timestampUtc;
        public Node? Parent { get; set; }
        public Node? PreferredChild { get; set; } // the branch Redo follows at this fork

        /// <summary>
        ///     How many further gesture ticks this node absorbed after the first.
        ///     Diagnostic only — it is what tells the log "this one node was a
        ///     111-tick wheel run" instead of leaving that invisible.
        /// </summary>
        public int MergedTicks { get; set; }
    }
}
