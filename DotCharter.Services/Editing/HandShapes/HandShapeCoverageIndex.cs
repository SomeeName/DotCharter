namespace DotCharter.Services.Editing.HandShapes;

/// <summary>
///     "Is any hand shape already covering this position?" — the idempotency
///     question <see cref="ChordHandShapePlanner" /> asks once per chord, and
///     again for every candidate follower of a repeated-strum run.
///     <para>
///         A linear scan of <c>level.HandShapes</c>, a list the
///         same pass APPENDS TO as it walks, would grow with the work and
///         make one import O(C²) in the chord count. This answers it in
///         O(log&#160;n) from a starts-sorted array plus a prefix maximum of the
///         ends.
///     </para>
///     <para>
///         <b>It is EXACT, including nesting.</b> The predicate is
///         <c>hs.Position == p || (hs.Position &lt; p &amp;&amp; p &lt; hs.EndPosition)</c>,
///         so a zero-width shape still matches by its start and a shape whose
///         span is swallowed by a longer one is never lost: the prefix maximum
///         over every start &#8804; <c>p</c> answers "does ANY of them reach past
///         p", which is exactly the disjunction the scan computed.
///         <c>HandShapeCoverageIndexTests</c> pins that against the naive
///         predicate over generated shape sets — keep that copy dumb.
///     </para>
/// </summary>
internal sealed class HandShapeCoverageIndex
{
    private readonly List<FractionalPosition> _starts = [];
    private readonly List<FractionalPosition> _ends = [];

    /// <summary>Running maximum of <see cref="_ends" /> over <c>[0..i]</c>.</summary>
    private readonly List<FractionalPosition> _maxEndThrough = [];

    public HandShapeCoverageIndex(IEnumerable<HandShape> existing)
    {
        foreach (var hs in existing) Add(hs.Position, hs.EndPosition);
    }

    public void Add(FractionalPosition start, FractionalPosition end)
    {
        // The builder appends in nondecreasing start order, so this is an O(1)
        // tail insert in practice; a pre-existing shape list in any order still
        // lands correctly, just more expensively.
        var at = UpperBound(start);
        _starts.Insert(at, start);
        _ends.Insert(at, end);
        _maxEndThrough.Insert(at, end);

        for (var i = at; i < _starts.Count; i++)
        {
            _maxEndThrough[i] = i == 0
                ? _ends[0]
                : Max(_maxEndThrough[i - 1], _ends[i]);
        }
    }

    public bool Covers(FractionalPosition position)
    {
        var last = UpperBound(position) - 1; // last shape starting at or before `position`
        if (last < 0) return false;
        // A shape starting exactly here covers it whatever its end — that is how
        // a zero-width clamp leftover stays idempotent.
        if (_starts[last].CompareTo(position) == 0) return true;
        return _maxEndThrough[last].CompareTo(position) > 0;
    }

    /// <summary>First index whose start is strictly greater than <paramref name="value" />.</summary>
    private int UpperBound(FractionalPosition value)
    {
        int lo = 0, hi = _starts.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (_starts[mid].CompareTo(value) <= 0) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    private static FractionalPosition Max(FractionalPosition a, FractionalPosition b) =>
        a.CompareTo(b) >= 0 ? a : b;
}
