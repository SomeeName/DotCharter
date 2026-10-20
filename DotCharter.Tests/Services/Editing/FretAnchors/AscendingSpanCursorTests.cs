using System.Collections;
using DotCharter.Services.Editing.FretAnchors;

namespace DotCharter.Tests.Services.Editing.FretAnchors;

/// <summary>
///     <c>FretAnchorGenerationService.FindActiveHandShape</c> scanned the whole
///     hand-shape list from index 0 for EVERY candidate anchor position — every
///     note, chord, slide landing and shape start of a level. O(positions x
///     shapes / 2): ~0.9M iterations for a typical four-minute arrangement,
///     ~50M for a dense Guitar Pro import, per level, per arrangement, inside
///     every tab import and every refret. Every other shape lookup in that file
///     already binary-searched.
///     <para>
///     Two claims, and they are different. The EQUIVALENCE cases replay the
///     linear scan verbatim as the specification, over abutting, gapped, nested
///     and out-of-order queries. The COST case counts element reads through an
///     instrumented list, so a scan that went back to index 0 fails without any
///     clock being read.
///     </para>
/// </summary>
public sealed class AscendingSpanCursorTests
{
    private static FractionalPosition P(int beat) => new(beat, Fraction.Zero);

    /// <summary>The loop the cursor replaced, verbatim, as the specification.</summary>
    private static int LinearIndexAt(
        IReadOnlyList<(FractionalPosition Start, FractionalPosition End)> spans, FractionalPosition position)
    {
        for (var i = 0; i < spans.Count; i++)
        {
            var span = spans[i];
            if (position < span.Start) return -1;
            if (position < span.End) return i;
        }

        return -1;
    }

    /// <summary>Abutting shapes (what the per-chord import produces), a gap, and
    /// a long shape — the three shapes a level really contains.</summary>
    private static List<(FractionalPosition Start, FractionalPosition End)> Spans() =>
    [
        (P(0), P(4)), (P(4), P(8)), (P(8), P(9)), (P(20), P(21)), (P(21), P(40))
    ];

    [Fact]
    public void AnAscendingWalkAnswersExactlyWhatTheLinearScanAnswered()
    {
        var spans = Spans();
        var cursor = new AscendingSpanCursor(spans);

        var sawHit = false;
        var sawGap = false;
        for (var beat = 0; beat <= 45; beat++)
        {
            var position = P(beat);
            var expected = LinearIndexAt(spans, position);
            Assert.Equal(expected, cursor.IndexAt(position));
            if (expected >= 0) sawHit = true;
            else sawGap = true;
        }

        // Not vacuous: the walk really crossed both covered and uncovered beats.
        Assert.True(sawHit, "no queried position ever landed inside a span");
        Assert.True(sawGap, "no queried position ever landed in the gap");
    }

    /// <summary>The cursor is only allowed to be a cursor because the walk asks
    /// in ascending order. A backwards query must still be right, or the
    /// optimisation is a landmine for the next caller.</summary>
    [Fact]
    public void AQueryThatGoesBackwardsRewindsInsteadOfMissing()
    {
        var spans = Spans();
        var cursor = new AscendingSpanCursor(spans);

        Assert.Equal(LinearIndexAt(spans, P(30)), cursor.IndexAt(P(30)));
        Assert.Equal(LinearIndexAt(spans, P(2)), cursor.IndexAt(P(2)));
        Assert.Equal(LinearIndexAt(spans, P(9)), cursor.IndexAt(P(9)));
        Assert.Equal(LinearIndexAt(spans, P(5)), cursor.IndexAt(P(5)));

        // Sanity: those four are not all the same answer.
        Assert.Equal(4, cursor.IndexAt(P(30)));
        Assert.Equal(0, cursor.IndexAt(P(2)));
    }

    [Fact]
    public void AnEmptySpanListAnswersNothingAndDoesNotThrow()
    {
        var cursor = new AscendingSpanCursor([]);
        Assert.Equal(-1, cursor.IndexAt(P(0)));
        Assert.Equal(-1, cursor.IndexAt(P(1_000)));
    }

    [Fact]
    public void AWholeAscendingWalkTouchesTheSpanListOnce()
    {
        const int spanCount = 2_000;
        const int positionCount = 6_000;

        var spans = new List<(FractionalPosition Start, FractionalPosition End)>(spanCount);
        for (var i = 0; i < spanCount; i++) spans.Add((P(i * 3), P(i * 3 + 2)));

        var counting = new CountingList<(FractionalPosition Start, FractionalPosition End)>(spans);
        var cursor = new AscendingSpanCursor(counting);

        var hits = 0;
        for (var beat = 0; beat < positionCount; beat++)
        {
            if (cursor.IndexAt(P(beat)) >= 0)
                hits++;
        }

        // The fixture covers two beats in three, so the walk really did resolve
        // spans rather than run off the end.
        Assert.Equal(spanCount * 2, hits);

        // Each span is passed once and read a bounded number of times per
        // query; the linear scan this replaced touched ~6,000 x 2,000 / 2 =
        // 6,000,000 elements.
        Assert.True(counting.Reads <= positionCount * 4,
            $"the walk touched {counting.Reads} elements for {positionCount} queries over " +
            $"{spanCount} spans — that is not a single pass");
    }

    /// <summary>An <see cref="IReadOnlyList{T}" /> that counts every element
    /// read, so "one pass" is an assertion rather than a claim.</summary>
    private sealed class CountingList<T>(IReadOnlyList<T> inner) : IReadOnlyList<T>
    {
        public int Reads { get; private set; }

        public int Count => inner.Count;

        public T this[int index]
        {
            get
            {
                Reads++;
                return inner[index];
            }
        }

        public IEnumerator<T> GetEnumerator()
        {
            for (var i = 0; i < inner.Count; i++) yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
