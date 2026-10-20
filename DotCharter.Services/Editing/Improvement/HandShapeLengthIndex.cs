namespace DotCharter.Services.Editing.Improvement;

/// <summary>
///     Sorted position arrays for <c>ChartImprovementService.ImproveHandShapeLengths</c>,
///     built once per level.
///     <para>
///         That pass asked three questions PER HAND SHAPE, each by walking a
///         whole collection: the last chord inside the bar
///         (<c>Where(…).OrderByDescending(…).First()</c>), the first anchor after
///         it, and the first note-or-chord after it. On a dense level that is
///         O(N×M) on the UI thread behind Tools ▸ Improve Chart.
///     </para>
///     <para>
///         Every query reads only <c>Position</c>, and the pass writes only
///         <c>HandShape.EndPosition</c>, so nothing it does can invalidate these
///         arrays. The answers are EXACT, not approximate —
///         <c>ImproveHandShapeLengthsEquivalenceTests</c> pins the whole pass
///         against a verbatim copy of the old bodies.
///     </para>
/// </summary>
internal sealed class HandShapeLengthIndex
{
    private readonly FractionalPosition[] _chordPositions;
    private readonly FractionalPosition[] _anchorPositions;

    /// <summary>Note AND chord positions, merged — the old body concatenated the
    /// two before taking its minimum.</summary>
    private readonly FractionalPosition[] _contentPositions;

    public HandShapeLengthIndex(ChartLevel level)
    {
        _chordPositions = level.Chords.Select(c => c.Position).ToArray();
        Array.Sort(_chordPositions);

        _anchorPositions = level.Anchors.Select(a => a.Position).ToArray();
        Array.Sort(_anchorPositions);

        _contentPositions = level.Notes.Select(n => n.Position)
            .Concat(level.Chords.Select(c => c.Position))
            .ToArray();
        Array.Sort(_contentPositions);
    }

    /// <summary>The greatest chord position in <c>[from, to]</c>, or null when the
    /// span holds no chord. Both bounds INCLUSIVE, as the old filter was.</summary>
    public FractionalPosition? LastChordPositionIn(FractionalPosition from, FractionalPosition to)
    {
        var at = UpperBound(_chordPositions, to) - 1;
        if (at < 0) return null;
        return _chordPositions[at].CompareTo(from) >= 0 ? _chordPositions[at] : null;
    }

    public FractionalPosition? FirstAnchorAfter(FractionalPosition after) => FirstAfter(_anchorPositions, after);

    public FractionalPosition? FirstContentAfter(FractionalPosition after) => FirstAfter(_contentPositions, after);

    private static FractionalPosition? FirstAfter(FractionalPosition[] sorted, FractionalPosition after)
    {
        var at = UpperBound(sorted, after);
        return at < sorted.Length ? sorted[at] : null;
    }

    /// <summary>First index whose value is strictly greater than <paramref name="value" />.</summary>
    private static int UpperBound(FractionalPosition[] sorted, FractionalPosition value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (sorted[mid].CompareTo(value) <= 0) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }
}
