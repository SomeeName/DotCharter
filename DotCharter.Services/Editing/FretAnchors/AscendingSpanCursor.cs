namespace DotCharter.Services.Editing.FretAnchors;

/// <summary>
///     "Which span covers this position?", answered in amortised O(1) for a
///     caller that asks in ascending position order.
/// </summary>
/// <remarks>
///     <para>
///     Written for <c>FretAnchorGenerationService.FindActiveHandShape</c>, which
///     asked it once per candidate anchor position by scanning the hand-shape
///     list from index 0 every time. The walk visits every note, chord,
///     slide landing and shape start of a level, so that is O(positions x
///     shapes / 2): ~3,000 positions and ~600 shapes on a typical four-minute
///     arrangement is ~0.9M iterations per level, and a dense Guitar Pro import
///     (~20,000 positions, ~5,000 shapes) is ~50M — per level, per arrangement,
///     inside every tab import (<c>GpArrangementParser</c>,
///     <c>GpLegacyImporter</c>, <c>SongsterrConverter</c>) and every refret.
///     Every other shape lookup in that file already binary-searched.
///     </para>
///     <para>
///     The cursor reproduces the scan EXACTLY rather than approximating it. The
///     linear form returned the first span whose END is past the position (and
///     then required its START to be at or before it), so the cursor advances
///     over precisely the spans that scan skipped — no assumption that the ENDS
///     are ordered, only that the caller's positions ascend. A query that goes
///     backwards rewinds the cursor, so the answer never depends on call order.
///     </para>
/// </remarks>
internal sealed class AscendingSpanCursor
{
    private readonly IReadOnlyList<(FractionalPosition Start, FractionalPosition End)> _spans;
    private int _cursor;
    private FractionalPosition _lastQuery;
    private bool _queried;

    public AscendingSpanCursor(IReadOnlyList<(FractionalPosition Start, FractionalPosition End)> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        _spans = spans;
    }

    /// <summary>Index of the span covering <paramref name="position" />
    /// (start inclusive, end EXCLUSIVE), or -1 when none does.</summary>
    public int IndexAt(FractionalPosition position)
    {
        if (_queried && position < _lastQuery) _cursor = 0;
        _lastQuery = position;
        _queried = true;

        while (_cursor < _spans.Count && _spans[_cursor].End <= position) _cursor++;
        if (_cursor >= _spans.Count) return -1;
        return _spans[_cursor].Start <= position ? _cursor : -1;
    }

    /// <summary>Rewinds to the start of the list. Call when the underlying
    /// list is rebuilt.</summary>
    public void Reset()
    {
        _cursor = 0;
        _queried = false;
    }
}
