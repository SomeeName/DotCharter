namespace DotCharter.Services.Parsing.Eof;

/// <summary>
///     One level's chords, ordered by position, answering "which chord is the
///     first at or after this position?" in O(log n).
/// </summary>
/// <remarks>
///     <para>
///     Built for <c>EofExporter.ReportHandShapeShapeLoss</c>, which asked that
///     question once per hand shape with two full LINQ scans of
///     <c>level.Chords</c> plus an <c>OrderBy</c> of the matches:
///     O(handShapes x chords log chords) per level, and a level of an
///     RS-imported chart carries roughly one hand shape per chord. On a dense
///     top level (~1,500 of each) that is ~2.25M predicate evaluations and
///     ~1,500 sorts, repeated for every difficulty level of every arrangement —
///     on every project save, because the <c>.eof</c> companion is written
///     inside the save worker and was described as "cheap".
///     </para>
///     <para>
///     The two branches it replaces collapse into one question. The first took
///     the earliest chord inside <c>[start, end]</c>; the second, its fallback,
///     took the earliest chord at or after <c>start</c> with no upper bound. If
///     the earliest chord at or after <c>start</c> lies within the window the
///     two agree, and if it does not, the first branch finds nothing and the
///     second returns exactly that chord — so "the first chord at or after the
///     hand shape's start" is the whole of it.
///     </para>
///     <para>
///     Ties are broken by the source order of equal positions, which is what
///     <c>OrderBy</c> (a stable sort) did.
///     </para>
/// </remarks>
internal sealed class ChordPositionIndex
{
    private readonly Chord[] _ordered;

    /// <summary>Reads <paramref name="chords" /> exactly once and orders a
    /// private copy; the source collection is never touched.</summary>
    public ChordPositionIndex(IReadOnlyList<Chord> chords)
    {
        ArgumentNullException.ThrowIfNull(chords);

        var count = chords.Count;
        _ordered = new Chord[count];
        for (var i = 0; i < count; i++) _ordered[i] = chords[i];
        // Stable, so equal positions keep source order exactly as OrderBy did.
        _ordered = [.. _ordered.OrderBy(static c => c.Position)];
    }

    public int Count => _ordered.Length;

    /// <summary>The earliest chord whose position is at or after
    /// <paramref name="position" />, or null when there is none.</summary>
    public Chord? FirstAtOrAfter(FractionalPosition position) => FirstAtOrAfter(_ordered, position);

    /// <summary>
    ///     The search itself, over any position-ordered list. Exposed so the
    ///     cost guard can count the elements a lookup actually touches through
    ///     an instrumented list, rather than instrument this class.
    /// </summary>
    internal static Chord? FirstAtOrAfter(IReadOnlyList<Chord> ordered, FractionalPosition position)
    {
        var lo = 0;
        var hi = ordered.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (ordered[mid].Position < position) lo = mid + 1;
            else hi = mid;
        }

        return lo < ordered.Count ? ordered[lo] : null;
    }
}
