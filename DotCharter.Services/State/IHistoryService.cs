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

    bool CanUndo { get; }
    bool CanRedo { get; }
    /// <summary>Id of the node the chart state currently corresponds to
    /// (the root sentinel's id when there is nothing to undo).</summary>
    int CurrentNodeId { get; }
    void Clear();
    bool Do(IChartAction action);
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
    /// state. Returns false if the id is unknown (e.g. already pruned).
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
