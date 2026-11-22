namespace DotCharter.Services.State;

/// <summary>
/// Read-only snapshot of one node in the undo tree.
/// </summary>
/// <param name="Id">Stable id of the node (monotonic creation order).</param>
/// <param name="ParentId">Id of the parent node; <c>null</c> for the root sentinel.</param>
/// <param name="Description">Human-readable label of the action that created this
/// state (empty for the pristine root sentinel).</param>
/// <param name="TimestampUtc">When the action was recorded.</param>
/// <param name="IsCurrent">True for exactly one node: the state the chart is in now.</param>
/// <param name="IsOnCurrentPath">True for every node on the root→current path,
/// i.e. the linear history plain Ctrl+Z walks backwards through.</param>
public sealed record HistoryNodeSnapshot(
    int Id,
    int? ParentId,
    string Description,
    DateTime TimestampUtc,
    bool IsCurrent,
    bool IsOnCurrentPath);

public interface IHistoryService
{
    event Action? HistoryChanged;
    /// <summary>
    /// Raised before an undo/redo runs so the UI can refocus the timeline
    /// panel that owns the action being reverted/replayed. The argument is the
    /// arrangement captured at <see cref="Do"/> / <see cref="PushAction"/>
    /// time (null = action wasn't tagged, leave focus alone).
    /// </summary>
    event Action<Arrangement?>? FocusRequested;

    /// <summary>
    ///     Raised when an action THREW inside <see cref="Do" />, <see cref="Undo" />
    ///     or <see cref="Redo" /> — i.e. the chart may have been partially mutated
    ///     and the history does NOT describe the result.
    ///     <para>
    ///         This exists because the unsaved-changes state is DERIVED from the
    ///         history (<see cref="ProjectDirtyTracker" /> compares node ids), and a
    ///         failed apply moves no node: the chart changed, the title bar showed
    ///         no `*`, and closing the app never prompted. <c>ProjectDirtyTracker</c>
    ///         subscribes and marks the project dirty, which is the conservative
    ///         direction — a spurious prompt costs a click, a missing one costs the
    ///         work.
    ///     </para>
    ///     <para>The exception is still rethrown to the caller; this is a
    ///     notification, not a handler.</para>
    /// </summary>
    event Action? ActionFailed;

    /// <summary>
    ///     Raised once when an outermost <see cref="Undo" />, <see cref="Redo" /> or <see cref="NavigateTo" />
    ///     step is over, on EVERY exit: <c>true</c> when the step applied (after its
    ///     <see cref="HistoryChanged" />), <c>false</c> when it threw — a failed apply (after
    ///     <see cref="ActionFailed" />), a failed link rebuild or a throwing <see cref="FocusRequested" />
    ///     listener, none of which raises <see cref="HistoryChanged" /> for the step. The steps a jump walks
    ///     raise it once, for the jump. <see cref="IsApplyingStep" /> is false inside it. Whatever was
    ///     deferred to the step's end settles here; a listener that throws after a failed step is logged,
    ///     and the step's own exception surfaces.
    /// </summary>
    event Action<bool>? StepEnded;

    /// <summary>
    ///     Raised after a step copied an edit into other linked placements, with every group the
    ///     step (the whole gesture so far) propagated into. The editor shows the "Linked" toast.
    /// </summary>
    event Action<IReadOnlyList<LinkPropagationReport>>? LinksPropagated;

    /// <summary>
    ///     "Only here": while the step that propagated <paramref name="report" /> is still the
    ///     current one, keeps its edit, restores the placements it was copied to and unlinks the
    ///     placement it was made in, as one new undo step. False when that step is not the current one.
    /// </summary>
    bool OnlyHere(LinkPropagationReport report);

    /// <summary>
    ///     True while an <see cref="Undo" /> / <see cref="Redo" /> step is being applied: from before
    ///     <see cref="FocusRequested" /> is raised until the step has applied (or
    ///     <see cref="ActionFailed" /> has been raised for it), and false again before
    ///     <see cref="HistoryChanged" /> is raised. A <see cref="NavigateTo" /> jump is ONE step: true
    ///     from its first focus request to its last state change, with one <see cref="HistoryChanged" />
    ///     at the end. Anything that would record an edit in reaction to
    ///     the step (a selection cleared by the focus request, say) asks this and defers to
    ///     <see cref="StepEnded" />.
    /// </summary>
    bool IsApplyingStep { get; }

    bool CanUndo { get; }
    bool CanRedo { get; }
    /// <summary>Id of the node the chart state currently corresponds to
    /// (the root sentinel's id when there is nothing to undo).</summary>
    int CurrentNodeId { get; }
    /// <summary>Identity of the current content, renewed when a gesture tick rewrites a node.
    /// Undo/redo restores the target node's content identity.</summary>
    int CurrentContentId { get; }
    void Clear();
    /// <summary>
    ///     Applies <paramref name="action" /> and records it as one step (or merges it into the open
    ///     gesture). Returns false, applying nothing, for an action without effect and for one requested
    ///     while <see cref="IsApplyingStep" />: run then, it would land under the node being undone and
    ///     the step would move the history past it, so the chart kept the edit and the history lost it.
    ///     A caller with an edit to make at that moment defers it to <see cref="HistoryChanged" />.
    /// </summary>
    bool Do(IChartAction action);

    /// <summary>
    ///     Records an action the caller has ALREADY applied, as one step (or merged into the open gesture),
    ///     without executing it and without raising <see cref="HistoryChanged" />. Returns false, recording
    ///     nothing, for an action without effect and, as <see cref="Do" />, for one pushed while
    ///     <see cref="IsApplyingStep" />.
    /// </summary>
    bool PushAction(IChartAction action);
    void Undo();
    void Redo();

    /// <summary>
    ///     Closes the current gesture, so the next <see cref="Do" /> starts a new
    ///     undo node instead of merging into the last one.
    ///     <para>
    ///         Call this when an input gesture ENDS deterministically — a pointer
    ///         release, a dialog closing, a selection change. It exists because
    ///         the merge window (see <see cref="IMergeableAction" />) can only
    ///         guess where a wheel run or a key repeat stopped, and guessing is
    ///         only acceptable where nothing better is available. A pointer drag
    ///         knows exactly when it ended and should say so.
    ///     </para>
    ///     <para>
    ///         Always safe: at worst it costs one extra undo node. Never call it
    ///         DURING a gesture.
    ///     </para>
    /// </summary>
    void EndGesture();
    /// <summary>
    /// Read-only snapshot of the whole undo tree in creation order (root
    /// sentinel first). Undoing and then editing keeps the old
    /// redo tail — it becomes a sibling branch that stays visible here.
    /// </summary>
    IReadOnlyList<HistoryNodeSnapshot> GetTree();
    /// <summary>
    /// Walks undo/redo along the tree (up to the common ancestor, then down
    /// the target branch) until <paramref name="nodeId"/> is the current
    /// state, as ONE step: <see cref="IsApplyingStep"/> holds for the whole walk and
    /// <see cref="HistoryChanged"/> is raised once, when the chosen node is current (or,
    /// when a step throws, once for the node the walk stopped on).
    /// Returns false if the id is unknown (e.g. already pruned).
    /// </summary>
    bool NavigateTo(int nodeId);

    /// <summary>
    /// The undo chain from the current state back toward the initial state —
    /// element 0 is what the NEXT Undo reverts. Capped at <paramref name="max"/>.
    /// Drives the toolbar's hover history preview.
    /// </summary>
    IReadOnlyList<HistoryStepInfo> GetUndoSteps(int max = 64);

    /// <summary>
    /// The redo chain forward along the preferred branch — element 0 is what
    /// the NEXT Redo replays. Capped at <paramref name="max"/>.
    /// </summary>
    IReadOnlyList<HistoryStepInfo> GetRedoSteps(int max = 64);
}

/// <summary>One step of the undo/redo chain, for display.</summary>
public readonly record struct HistoryStepInfo(string Description, DateTime TimestampUtc);
