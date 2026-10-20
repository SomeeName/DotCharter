using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Tempo;

/// <inheritdoc cref="IBeatResyncService" />
public sealed class BeatResyncService : IBeatResyncService
{
    private readonly IAppLogger? _logger;

    public BeatResyncService(IAppLogger? logger = null) => _logger = logger;

    public BeatResyncResult BuildResyncAction(
        ChartProject project, IReadOnlyList<BeatMarker> syncedBeats, Action? onPostExecute = null)
    {
        var n = project.Beats.Count;
        var m = syncedBeats.Count;

        if (project.IsBeatMapLocked)
        {
            _logger?.Info(AppLogCategory.Events, "Beat resync skipped — beat map is locked.");
            return new BeatResyncResult(BeatResyncStatus.Locked, null, n, m);
        }

        if (m < 2)
            return new BeatResyncResult(BeatResyncStatus.NoSyncData, null, n, m);

        if (n == 0)
            return new BeatResyncResult(BeatResyncStatus.NoExistingBeats, null, n, m);

        // MutateBeatsAction copies min(n, m) indices in place. When the sync is
        // shorter than the chart the trailing beats would keep their old (now
        // earlier) times, breaking monotonicity — refuse rather than corrupt the
        // grid. Equal or longer sync is safe (its prefix is monotonic).
        if (m < n)
        {
            _logger?.Warning(AppLogCategory.Events,
                $"Beat resync refused — sync covers {m} beats but the chart has {n}.");
            return new BeatResyncResult(BeatResyncStatus.CountMismatch, null, n, m);
        }

        var oldBeats = project.Beats.Select(beat => beat.Clone()).ToList();
        var action = new MutateBeatsAction(project.Beats, oldBeats, syncedBeats, onPostExecute);
        _logger?.Info(AppLogCategory.Events,
            $"Beat resync prepared — re-timing {n} beats from {m} synced anchors.");
        return new BeatResyncResult(BeatResyncStatus.Ok, action, n, m);
    }
}
