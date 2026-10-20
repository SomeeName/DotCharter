using DotCharter.Services.Editing.HandShapes;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     <c>ChordHandShapeBuilder</c> asked "is any hand shape already covering
///     this position?" with a linear scan of <c>level.HandShapes</c> — a list the
///     same pass appends to as it walks, so the scan grew with the work and an
///     import was O(C²) in the chord count.
///     <para>
///         The replacement is only allowed to be faster, never different: it must
///         answer <c>hs.Position == p || (hs.Position &lt; p &amp;&amp; p &lt;
///         hs.EndPosition)</c> for every input, NESTED and zero-width shapes
///         included — a naive "furthest end so far" would lose a shape swallowed
///         by a longer one. The reference below is the old predicate verbatim;
///         keep it dumb.
///     </para>
/// </summary>
public sealed class HandShapeCoverageIndexTests
{
    /// <summary>The old body, verbatim.</summary>
    private static bool Naive(List<(int Start, int End)> shapes, int position)
    {
        foreach (var (start, end) in shapes)
        {
            if (start == position || (start < position && position < end))
                return true;
        }

        return false;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(99)]
    [InlineData(20260903)]
    public void TheIndexAgreesWithTheScan_OnGeneratedShapeSets(int seed)
    {
        var rng = new Random(seed);
        var shapes = new List<(int Start, int End)>();
        var index = new HandShapeCoverageIndex([]);

        // Interleave adds and queries the way the builder does — the index has to
        // stay correct while it is being appended to.
        for (var round = 0; round < 200; round++)
        {
            var start = rng.Next(0, 60);
            // Zero-width (a clamp leftover), ordinary, and long spans that nest
            // over the shorter ones — all three shapes the builder produces.
            var end = start + rng.Next(0, 4) switch
            {
                0 => 0,
                1 => 1,
                2 => rng.Next(2, 6),
                _ => rng.Next(6, 30)
            };

            shapes.Add((start, end));
            index.Add(Pos(start), Pos(end));

            for (var p = 0; p <= 95; p++)
                Assert.Equal(Naive(shapes, p), index.Covers(Pos(p)));
        }
    }

    [Fact]
    public void ANestedShape_IsNotHiddenByALongerOneAroundIt()
    {
        // The failure mode a "max end so far" without the start ordering has:
        // the long bar answers for everything and the short one is never asked.
        var index = new HandShapeCoverageIndex([]);
        index.Add(Pos(0), Pos(40));
        index.Add(Pos(10), Pos(12));

        Assert.True(index.Covers(Pos(11)));
        Assert.True(index.Covers(Pos(39)));
        Assert.False(index.Covers(Pos(40)));  // end is EXCLUSIVE
        Assert.True(index.Covers(Pos(10)));
    }

    [Fact]
    public void AZeroWidthShape_StillMatchesByItsStart()
    {
        var index = new HandShapeCoverageIndex([]);
        index.Add(Pos(8), Pos(8));

        Assert.True(index.Covers(Pos(8)));
        Assert.False(index.Covers(Pos(7)));
        Assert.False(index.Covers(Pos(9)));
    }

    [Fact]
    public void ShapesAddedOutOfOrder_AreStillFound()
    {
        // The builder appends in nondecreasing order, but a level imported into
        // may carry pre-existing shapes in any order at all.
        var index = new HandShapeCoverageIndex([]);
        index.Add(Pos(30), Pos(34));
        index.Add(Pos(4), Pos(9));
        index.Add(Pos(18), Pos(19));

        Assert.True(index.Covers(Pos(5)));
        Assert.True(index.Covers(Pos(18)));
        Assert.True(index.Covers(Pos(33)));
        Assert.False(index.Covers(Pos(12)));
        Assert.False(index.Covers(Pos(29)));
    }

    private static FractionalPosition Pos(int beat) => new(beat);
}
