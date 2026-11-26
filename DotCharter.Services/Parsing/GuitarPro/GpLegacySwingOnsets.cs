namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     The per-track <see cref="GpBarTimeline" /> of a GP3-5 file: the unfolded barlines plus the WRITTEN
///     onset of every beat a triplet feel moved, so the let-ring hand-shape pass ends a shape at
///     the last note's written end, as the gpif path does. One timeline per track because a swung beat of
///     one track must not rewrite an unswung beat of another that lands on the same placed position.
/// </summary>
internal sealed class GpLegacySwingOnsets
{
    private readonly Dictionary<int, GpBarTimeline> _byTrack = [];
    private readonly List<(int TrackId, FractionalPosition Placed, FractionalPosition Written)> _measure = [];

    /// <summary>Forgets every track, for the next file.</summary>
    public void Clear()
    {
        _byTrack.Clear();
        _measure.Clear();
    }

    /// <summary>Starts a measure: the beats recorded so far belong to the previous one.</summary>
    public void BeginMeasure() => _measure.Clear();

    /// <summary>Records one playable beat's placed and written onset on <paramref name="trackId" />.</summary>
    public void Record(int trackId, FractionalPosition placed, FractionalPosition written, IReadOnlyList<FractionalPosition> barStarts)
    {
        if (placed.Equals(written)) return;
        TimelineFor(trackId, barStarts).RecordOnset(placed, written);
        _measure.Add((trackId, placed, written));
    }

    /// <summary>Repeats this track's swung beats of the measure on a later pass, shifted by <paramref name="delta" />.</summary>
    public void Replay(int trackId, FractionalPosition delta, IReadOnlyList<FractionalPosition> barStarts)
    {
        var timeline = TimelineFor(trackId, barStarts);
        foreach (var (track, placed, written) in _measure)
            if (track == trackId) timeline.RecordOnset(placed + delta, written + delta);
    }

    /// <summary>The track's timeline; barlines only when no beat of it swung.</summary>
    public GpBarTimeline TimelineFor(int trackId, IReadOnlyList<FractionalPosition> barStarts)
    {
        if (_byTrack.TryGetValue(trackId, out var timeline)) return timeline;
        timeline = GpBarTimeline.FromBarStarts(barStarts);
        _byTrack[trackId] = timeline;
        return timeline;
    }
}
