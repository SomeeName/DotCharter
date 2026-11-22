namespace DotCharter.Services.Editing.Collisions;

/// <summary>
///     A beat edit can run the project-wide compatibility pass, which changes more than
///     sustains: templates, derived flags and anchor membership also change. Capture each
///     arrangement before and after the first pass. Undo restores the entrance state after
///     readout callbacks; redo reattaches the recorded result before idempotent enforcement.
///     Ordinary note edits keep their cheap recorder.
/// </summary>
internal sealed class BeatSpacingSnapshot : IDisposable
{
    [ThreadStatic] private static BeatSpacingSnapshot? _current;

    private readonly BeatSpacingSnapshot? _previous;
    private readonly List<(Arrangement Live, Arrangement Before, Arrangement? After)> _arrangements = [];
    private readonly HashSet<Arrangement> _captured = new(ReferenceEqualityComparer.Instance);

    private BeatSpacingSnapshot(BeatSpacingSnapshot? previous) => _previous = previous;

    public static BeatSpacingSnapshot? Current => _current;

    public long EstimatedBytes => _arrangements.Sum(a => ChartProjectFootprint.EstimateHistoryBytes(a.Before) +
        (a.After == null ? 0 : ChartProjectFootprint.EstimateHistoryBytes(a.After)));

    public static BeatSpacingSnapshot Begin()
    {
        var snapshot = new BeatSpacingSnapshot(_current);
        _current = snapshot;
        return snapshot;
    }

    public void Capture(Arrangement arrangement)
    {
        if (!_captured.Add(arrangement)) return;
        _arrangements.Add((arrangement, ChartProjectCloner.CloneArrangement(arrangement, retainIdentities: true), null));
    }

    public void Complete()
    {
        for (var i = 0; i < _arrangements.Count; i++)
        {
            var (live, before, _) = _arrangements[i];
            _arrangements[i] = (live, before, ChartProjectCloner.CloneArrangement(live, retainIdentities: true));
        }
    }

    public void Restore()
    {
        foreach (var (live, before, _) in _arrangements) ChartProjectRestorer.RestoreArrangement(live, before);
    }

    public void RestoreAfter()
    {
        foreach (var (live, _, after) in _arrangements)
        {
            if (after != null) ChartProjectRestorer.RestoreArrangement(live, after);
        }
    }

    public void Dispose() => _current = _previous;
}
