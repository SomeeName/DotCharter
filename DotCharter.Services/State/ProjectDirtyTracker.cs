namespace DotCharter.Services.State;

/// <summary>
///     Derives the project's unsaved-changes state from the undo history
///     instead of tracking mutations in parallel: the project is dirty exactly
///     when the history's current node differs from the node that was current
///     at the last save/load baseline (<see cref="IHistoryService.CurrentNodeId" />).
///     Undoing back to the saved node therefore flips the state clean again.
///     If the baseline node gets pruned from the history tree the id can never
///     match again and the project simply stays dirty until the next save —
///     conservative in the "never lose work" direction.
/// </summary>
public sealed class ProjectDirtyTracker : IDisposable
{
    private readonly IHistoryService _history;
    private int _baselineNodeId;
    private bool _lastRaisedDirty;
    private int _outOfBandChanges;

    public ProjectDirtyTracker(IHistoryService history)
    {
        _history = history;
        _baselineNodeId = history.CurrentNodeId;
        _history.HistoryChanged += OnHistoryChanged;
        // An action that THREW mid-apply moves no node, so the id comparison below
        // reads CLEAN over a chart that was partially edited — no `*`, no close
        // prompt, and the work goes out with the window. See
        // IHistoryService.ActionFailed.
        _history.ActionFailed += MarkDirty;
    }

    /// <summary>Computed live so callers (e.g. a window-close gate) always see
    /// the truth even if a mutation path skipped the HistoryChanged event.</summary>
    public bool IsDirty => _history.CurrentNodeId != _baselineNodeId;

    /// <summary>
    ///     A stamp of the project's CURRENT content state, not of its
    ///     dirtiness: equal stamps mean nothing has changed in between, and a
    ///     different stamp means something has. It is the undo history's node
    ///     id — the id IS the change stamp, which is what makes
    ///     <see cref="IsDirty" /> a plain comparison above — folded with a
    ///     counter for the out-of-band changes <see cref="MarkDirty" /> records,
    ///     because those move no node.
    ///     <para>
    ///         "Dirty" alone cannot answer "has anything changed since X": a
    ///         project stays dirty while it sits untouched, so a periodic worker
    ///         gated on dirty repeats its work forever over identical content.
    ///         <c>AutoBackupGate</c> is the case this exists for — it wrote one
    ///         more identical zip every interval for a dirty-but-idle project.
    ///     </para>
    ///     <para>
    ///         Comparable only WITHIN one project session: node ids restart per
    ///         history, so a holder must forget its stamp when the project is
    ///         replaced.
    ///     </para>
    /// </summary>
    public long ChangeStamp => ((long)_outOfBandChanges << 32) | (uint)_history.CurrentNodeId;

    /// <summary>Raised when <see cref="IsDirty" /> flips (edge-triggered).</summary>
    public event Action? IsDirtyChanged;

    /// <summary>
    ///     The current state was just persisted (save) or freshly loaded from
    ///     disk — it IS the on-disk state, so it becomes the clean baseline.
    ///     <para>
    ///         <b>A save is a gesture BOUNDARY, so it closes the open gesture
    ///         first.</b> Without that, the next tick of a still-open gesture
    ///         MERGES into the node that was just baselined: the merge rewrites
    ///         the head action in place and leaves
    ///         <see cref="IHistoryService.CurrentNodeId" /> exactly where it was,
    ///         so the tracker reads CLEAN while the chart has moved on — the
    ///         title-bar `*` never appears and the close prompt never fires.
    ///         The second, worse half is that the on-disk state stops being an
    ///         undo target at all: the node that stood for it now also carries
    ///         every tick made after the save, so Ctrl+Z steps straight PAST what
    ///         is on disk. Ending the gesture costs at most one undo node.
    ///     </para>
    /// </summary>
    public void MarkClean()
    {
        _history.EndGesture();
        _baselineNodeId = _history.CurrentNodeId;
        CleanMarked?.Invoke();
        RaiseIfChanged();
    }

    /// <summary>
    ///     Raised on EVERY <see cref="MarkClean" />, the exact mirror of
    ///     <see cref="DirtyMarked" /> — and, like it, NOT the edge-triggered
    ///     <see cref="IsDirtyChanged" />, which stays silent when the session was
    ///     already clean.
    ///     <para>
    ///         Crash recovery needs this one too. <c>MarkClean</c> is the
    ///         declaration every REPLACE path makes ("what is in memory is what a
    ///         fresh load produces"), so it is the one signal that covers all of
    ///         them: the project open funnel, the PSARC/library song load that
    ///         Play Mode's song select drives, the importers and the default
    ///         project. Only the open funnel ever told the recovery service, so a
    ///         library song load left the snapshot scheduler armed by the
    ///         <c>HistoryChanged</c> that <c>ApplyProject(resetHistory: true)</c>
    ///         raises — and ten seconds later the app wrote a full crash snapshot
    ///         for a chart nobody had edited, under the <c>untitled</c> key a
    ///         PSARC session always has (it has no project path), which
    ///         <c>RecoverySnapshotStore.IsRecoverable</c> then offered back on the
    ///         next launch.
    ///     </para>
    /// </summary>
    public event Action? CleanMarked;

    /// <summary>
    ///     Records an unsaved change that the undo history cannot represent —
    ///     project-level state that lives beside the chart graph (attaching or
    ///     replacing the song audio is the case this exists for). The baseline
    ///     moves to a sentinel no real node can ever carry (<see cref="HistoryService" />
    ///     hands out ids from 0 upward), so the session reads dirty until the
    ///     next save/load, exactly as it would after an unsaved edit.
    /// </summary>
    public void MarkDirty()
    {
        _baselineNodeId = NeverAssignedNodeId;
        // …and it is a CHANGE, so the stamp has to move too — the history's node
        // id does not, which is the whole reason this method exists.
        _outOfBandChanges++;
        DirtyMarked?.Invoke();
        RaiseIfChanged();
    }

    /// <summary>
    ///     Raised on EVERY <see cref="MarkDirty" />, unlike the edge-triggered
    ///     <see cref="IsDirtyChanged" />.
    ///     <para>
    ///         Crash recovery needs this one. It arms its snapshot off a CHANGE
    ///         signal, and it only had the undo history's — so project-level state
    ///         that the history cannot represent (attaching or replacing the song
    ///         audio is the case this class exists for) was never snapshotted at
    ///         all. Subscribing to the edge would only half-fix it: a second
    ///         <c>MarkDirty</c> on an already-dirty project raises nothing, which is
    ///         exactly the "edit, autosave, then attach audio" order.
    ///     </para>
    /// </summary>
    public event Action? DirtyMarked;

    /// <summary>Sentinel baseline: node ids start at 0 and only increase, so no
    /// history state can ever equal this — i.e. "dirty until the next save".</summary>
    private const int NeverAssignedNodeId = -1;

    public void Dispose()
    {
        _history.HistoryChanged -= OnHistoryChanged;
        _history.ActionFailed -= MarkDirty;
    }

    private void OnHistoryChanged() => RaiseIfChanged();

    private void RaiseIfChanged()
    {
        var dirty = IsDirty;
        if (dirty == _lastRaisedDirty) return;
        _lastRaisedDirty = dirty;
        IsDirtyChanged?.Invoke();
    }
}
