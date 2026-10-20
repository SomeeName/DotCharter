using System.Collections;
using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Eof;

/// <summary>
///     <c>EofExporter.ReportHandShapeShapeLoss</c> asked "which chord does a
///     reader re-bind this hand shape's shape to?" once per hand shape, with two
///     full LINQ scans of <c>level.Chords</c> plus an <c>OrderBy</c> of the
///     matches. A level of an RS-imported chart carries roughly one hand shape
///     per chord, so that is O(handShapes x chords log chords) per level, on
///     every project save — the <c>.eof</c> companion is written inside the save
///     worker.
///     <para>
///     Two claims are pinned here, and they are different claims. The
///     EQUIVALENCE cases say the binary-searched answer is the answer the two
///     LINQ branches gave, including the tie and the past-the-window cases. The
///     COST case counts element reads through an instrumented list, so it fails
///     on a linear scan without ever looking at a clock.
///     </para>
/// </summary>
public sealed class ChordPositionIndexTests
{
    private static Chord At(int beat, int templateId = 0) =>
        new() { Position = new FractionalPosition(beat, Fraction.Zero), TemplateId = templateId };

    /// <summary>The expression the index replaced, verbatim, as the
    /// specification the binary search has to reproduce.</summary>
    private static Chord? LegacyResolve(IReadOnlyList<Chord> chords, HandShape hs) =>
        chords
            .Where(c => c.Position >= hs.Position && c.Position <= hs.EndPosition)
            .OrderBy(c => c.Position)
            .FirstOrDefault()
        ?? chords.Where(c => c.Position >= hs.Position).OrderBy(c => c.Position).FirstOrDefault();

    private static HandShape Shape(int start, int end) => new()
    {
        Position = new FractionalPosition(start, Fraction.Zero),
        EndPosition = new FractionalPosition(end, Fraction.Zero)
    };

    [Fact]
    public void ItAnswersExactlyWhatTheTwoLinqBranchesAnswered()
    {
        // Deliberately unsorted, with a duplicate position (the tie the stable
        // OrderBy decided) and a gap wide enough that some windows hold nothing.
        List<Chord> chords =
        [
            At(12, templateId: 5), At(0, templateId: 1), At(4, templateId: 2),
            At(4, templateId: 3), At(30, templateId: 4)
        ];
        var index = new ChordPositionIndex(chords);

        var windows = new List<HandShape>();
        for (var start = 0; start <= 34; start++)
        {
            for (var length = 0; length <= 6; length++)
                windows.Add(Shape(start, start + length));
        }

        var sawInside = false;
        var sawFallback = false;
        var sawNothing = false;
        foreach (var hs in windows)
        {
            var expected = LegacyResolve(chords, hs);
            Assert.Same(expected, index.FirstAtOrAfter(hs.Position));

            if (expected == null) sawNothing = true;
            else if (expected.Position <= hs.EndPosition) sawInside = true;
            else sawFallback = true;
        }

        // Non-vacuity: all three shapes of answer really occurred, so a
        // comparison that only ever met the easy case is ruled out.
        Assert.True(sawInside, "no window ever contained a chord");
        Assert.True(sawFallback, "no window ever fell back to a chord past its end");
        Assert.True(sawNothing, "no window ever ran off the end of the level");
    }

    [Fact]
    public void ItReadsTheChordListOncePerLevelAndNotOncePerHandShape()
    {
        const int chordCount = 2_000;
        const int handShapeCount = 2_000;

        var chords = new List<Chord>(chordCount);
        for (var i = 0; i < chordCount; i++) chords.Add(At(i, templateId: i));

        var counting = new CountingList<Chord>(chords);
        var index = new ChordPositionIndex(counting);

        // The build is allowed exactly one pass; nothing after it may touch the
        // source list at all.
        Assert.Equal(chordCount, counting.Reads);
        var afterBuild = counting.Reads;

        for (var i = 0; i < handShapeCount; i++)
            Assert.NotNull(index.FirstAtOrAfter(new FractionalPosition(i, Fraction.Zero)));

        Assert.Equal(afterBuild, counting.Reads);

        // …and the lookup itself stays logarithmic. Counted over an ALREADY
        // ordered instrumented list, so the number is the search's own cost.
        // The linear form this replaced touched ~2,000 x 2,000 / 2 = 2,000,000
        // elements; a binary search over 2,000 touches at most 11 per query.
        var orderedCounting = new CountingList<Chord>(chords);
        for (var i = 0; i < handShapeCount; i++)
        {
            Assert.NotNull(ChordPositionIndex.FirstAtOrAfter(
                orderedCounting, new FractionalPosition(i, Fraction.Zero)));
        }

        Assert.True(orderedCounting.Reads <= handShapeCount * 16,
            $"the lookups touched {orderedCounting.Reads} elements for {handShapeCount} queries over " +
            $"{chordCount} chords — that is not a binary search");
    }

    /// <summary>An <see cref="IReadOnlyList{T}" /> that counts every element
    /// read, so "reads the source once" is an assertion rather than a claim.</summary>
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
