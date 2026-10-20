using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Tempo;

public enum BeatResyncStatus
{
    /// <summary>An action was produced; applying it re-times the chart.</summary>
    Ok,

    /// <summary>The beat map is locked — nothing was changed.</summary>
    Locked,

    /// <summary>The sync source has too few anchor points to be usable.</summary>
    NoSyncData,

    /// <summary>The chart has no beat map to re-time.</summary>
    NoExistingBeats,

    /// <summary>The sync covers fewer beats than the chart — re-timing would
    /// leave the chart's trailing beats stale (non-monotonic). Refused.</summary>
    CountMismatch
}

/// <summary>Outcome of a resync request. <see cref="Action" /> is non-null only
/// when <see cref="Status" /> is <see cref="BeatResyncStatus.Ok" />.</summary>
public sealed record BeatResyncResult(
    BeatResyncStatus Status,
    IChartAction? Action,
    int ProjectBeatCount,
    int SyncedBeatCount);

/// <summary>
///     Re-times a chart's beat grid onto an externally-supplied set of beat
///     times (e.g. Align to recording's aligned grid) WITHOUT moving any note:
///     notes are grid-relative, so rewriting <see cref="BeatMarker.Time" /> in
///     place makes every note follow automatically. Undoable, and refuses when
///     the beat map is locked.
/// </summary>
public interface IBeatResyncService
{
    /// <summary>
    ///     Builds an undoable action that copies <paramref name="syncedBeats" />'
    ///     times/tempo onto the project's existing beats (index by index).
    /// </summary>
    BeatResyncResult BuildResyncAction(
        ChartProject project, IReadOnlyList<BeatMarker> syncedBeats, Action? onPostExecute = null);
}
