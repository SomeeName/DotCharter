namespace DotCharter.Services.Infrastructure.Recovery;

/// <summary>
///     Pure decision logic for the crash-recovery autosave cadence. A caller
///     (a UI timer) feeds it "history changed" and "tick" moments; it answers
///     whether a snapshot is due. Two triggers, whichever comes first:
///     <list type="bullet">
///         <item>Debounce: the edit burst went quiet — <see cref="DebounceDefault"/>
///         (10 s) elapsed since the LAST change.</item>
///         <item>Interval: edits keep streaming without a pause — snapshot at
///         least every configured interval (default 60 s) anyway, so an
///         uninterrupted editing marathon is never more than one interval
///         behind on disk.</item>
///     </list>
///     Cheap when idle: while nothing changed since the last snapshot,
///     <see cref="ShouldSnapshot"/> is a single bool check.
///     Not thread-safe — callers synchronize (the coordinator drives it from
///     the UI thread only).
/// </summary>
public sealed class RecoverySnapshotScheduler
{
    public static readonly TimeSpan DebounceDefault = TimeSpan.FromSeconds(10);

    private bool _dirty;
    private DateTimeOffset _lastChangeUtc;
    private DateTimeOffset _lastSnapshotUtc;

    public RecoverySnapshotScheduler(DateTimeOffset nowUtc) => _lastSnapshotUtc = nowUtc;

    /// <summary>True when at least one change arrived since the last snapshot/reset.</summary>
    public bool IsDirty => _dirty;

    /// <summary>Record that the project mutated (history changed) at <paramref name="nowUtc"/>.</summary>
    public void NotifyChanged(DateTimeOffset nowUtc)
    {
        _dirty = true;
        _lastChangeUtc = nowUtc;
    }

    /// <summary>Whether a snapshot should be captured right now.</summary>
    public bool ShouldSnapshot(DateTimeOffset nowUtc, TimeSpan interval, TimeSpan debounce)
    {
        if (!_dirty) return false;
        if (nowUtc - _lastChangeUtc >= debounce) return true;
        return nowUtc - _lastSnapshotUtc >= interval;
    }

    /// <summary>
    ///     Record that a snapshot was captured. Call this at CAPTURE time (when
    ///     the immutable clone is taken), not when the file write finishes —
    ///     edits landing during the async write must re-mark the state dirty.
    /// </summary>
    public void NotifySnapshotCaptured(DateTimeOffset nowUtc)
    {
        _dirty = false;
        _lastSnapshotUtc = nowUtc;
    }

    /// <summary>
    ///     Forget all pending changes (project saved, switched or closed —
    ///     nothing left worth snapshotting until the next edit).
    /// </summary>
    public void Reset(DateTimeOffset nowUtc)
    {
        _dirty = false;
        _lastChangeUtc = default;
        _lastSnapshotUtc = nowUtc;
    }
}
