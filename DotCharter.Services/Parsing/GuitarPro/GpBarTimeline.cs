namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     The barlines of one imported track and the WRITTEN onset of every beat a triplet
///     feel moved, both in the parser's quarter-note positions. The let-ring hand-shape pass
///     reads it: a let-ring run never crosses a barline, and a run's bar ends at its last
///     note's written end (the swung onset plus the written length overshoots by the swing
///     shift), never past the next barline. The chord hand-shape pass reads it too: a sound's
///     written end lands where the beat written there was placed (<see cref="PlacedEnd" />).
/// </summary>
public sealed class GpBarTimeline
{
    /// <summary>No barlines, no swing: the hand-shape pass splits and clips nothing.</summary>
    public static readonly GpBarTimeline None = new();

    private readonly List<FractionalPosition> _barlines = [];
    private readonly Dictionary<FractionalPosition, FractionalPosition> _writtenOnsets = [];
    private readonly Dictionary<FractionalPosition, FractionalPosition> _placedOnsets = [];

    /// <summary>A timeline over <paramref name="barStarts" />, in ascending order.</summary>
    public static GpBarTimeline FromBarStarts(IEnumerable<FractionalPosition> barStarts)
    {
        var timeline = new GpBarTimeline();
        foreach (var start in barStarts) timeline.AddBarline(start);
        return timeline;
    }

    /// <summary>The barlines added so far, in ascending order.</summary>
    public IReadOnlyList<FractionalPosition> Barlines => _barlines;

    /// <summary>Forgets every barline and onset, for the next track.</summary>
    public void Clear()
    {
        _barlines.Clear();
        _writtenOnsets.Clear();
        _placedOnsets.Clear();
    }

    /// <summary>Adds a barline; the parser adds them in ascending order, the end of the last bar included.</summary>
    public void AddBarline(FractionalPosition position)
    {
        if (_barlines.Count == 0 || _barlines[^1].CompareTo(position) < 0) _barlines.Add(position);
    }

    /// <summary>Records that the beat placed at <paramref name="placed" /> was written at
    /// <paramref name="written" />; an unswung beat records nothing.</summary>
    public void RecordOnset(FractionalPosition placed, FractionalPosition written)
    {
        if (placed.Equals(written)) return;
        _writtenOnsets[placed] = written;
        _placedOnsets[written] = placed;
    }

    /// <summary>Where the beat placed at <paramref name="placed" /> was written: itself unless a feel moved it.</summary>
    public FractionalPosition WrittenOnset(FractionalPosition placed)
        => _writtenOnsets.TryGetValue(placed, out var written) ? written : placed;

    /// <summary>
    ///     Where a sound placed at <paramref name="placed" /> and written <paramref name="length" /> long
    ///     ends on the placed grid: its written end, moved as far as the feel moved the beat written
    ///     there. Under a triplet feel a downbeat eighth ends on the swung off-beat (2/3), not 1/6 beat
    ///     before it, and a swung off-beat eighth ends on the next beat, not 1/6 beat past it.
    /// </summary>
    public FractionalPosition PlacedEnd(FractionalPosition placed, FractionalPosition length)
    {
        var writtenEnd = WrittenOnset(placed) + length;
        return _placedOnsets.TryGetValue(writtenEnd, out var moved) ? moved : writtenEnd;
    }

    /// <summary>The first barline strictly after <paramref name="position" />, or null past the last one.</summary>
    public FractionalPosition? NextBarline(FractionalPosition position)
    {
        int lo = 0, hi = _barlines.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (_barlines[mid].CompareTo(position) <= 0) lo = mid + 1;
            else hi = mid;
        }

        return lo < _barlines.Count ? _barlines[lo] : null;
    }

    /// <summary>Whether a barline lies in <c>(<paramref name="from" />, <paramref name="to" />]</c>:
    /// the beat at <paramref name="to" /> opens a later bar than the one at <paramref name="from" />.</summary>
    public bool CrossesBarline(FractionalPosition from, FractionalPosition to)
        => NextBarline(from) is { } barline && barline.CompareTo(to) <= 0;
}
