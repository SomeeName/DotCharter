using Avalonia;
using Avalonia.Layout;
using DotCharter.Tone.UI.Controls.Fire;

namespace DotCharter.Tests.UI.Controls;

/// <summary>
///     Where a dragged rig unit would land: the insertion index inside a lane,
///     and the rect the 6 px marker occupies.
///     <para>
///         An insertion index is in <c>[0, count]</c> — <c>count + 1</c>
///         answers for <c>count</c> items — and it is decided by ITEM CENTRES,
///         not edges. Edges leave the gaps between cards ambiguous, which is
///         what makes an insertion marker flicker between two slots as the
///         pointer crosses one.
///     </para>
/// </summary>
public sealed class FireSlotHitTestTests
{
    // Three 40-wide cards, 10 apart: 0-40, 50-90, 100-140. Centres: 20/70/120.
    private static readonly Rect[] Row =
    [
        new(0, 0, 40, 60),
        new(50, 0, 40, 60),
        new(100, 0, 40, 60)
    ];

    private static readonly Rect Lane = new(0, 0, 200, 60);

    [Theory]
    [InlineData(-5.0, 0)]
    [InlineData(0.0, 0)]
    [InlineData(19.9, 0)]
    [InlineData(20.0, 1)]  // exactly the first card's centre → after it
    [InlineData(45.0, 1)]
    [InlineData(69.9, 1)]
    [InlineData(70.0, 2)]
    [InlineData(119.9, 2)]
    [InlineData(120.0, 3)] // past the last centre → the end slot
    [InlineData(400.0, 3)]
    public void TheInsertionIndexCountsTheCentresThePointerIsPast(double x, int expected)
        => Assert.Equal(expected,
            FireSlotHitTest.InsertionIndex(Row, new Point(x, 30), Orientation.Horizontal));

    [Fact]
    public void ThereAreCountPlusOneSlots()
    {
        var seen = new HashSet<int>();
        for (var x = -10.0; x < 210.0; x += 0.5)
            seen.Add(FireSlotHitTest.InsertionIndex(Row, new Point(x, 30), Orientation.Horizontal));

        Assert.Equal([0, 1, 2, 3], seen.OrderBy(i => i).ToArray());
    }

    [Fact]
    public void AnEmptyLaneIsAlwaysSlotZero()
        => Assert.Equal(0, FireSlotHitTest.InsertionIndex([], new Point(500, 5), Orientation.Horizontal));

    [Fact]
    public void AVerticalLaneReadsTheYAxis()
    {
        Rect[] column = [new(0, 0, 60, 40), new(0, 50, 60, 40)];
        Assert.Equal(0, FireSlotHitTest.InsertionIndex(column, new Point(30, 5), Orientation.Vertical));
        Assert.Equal(1, FireSlotHitTest.InsertionIndex(column, new Point(30, 25), Orientation.Vertical));
        Assert.Equal(2, FireSlotHitTest.InsertionIndex(column, new Point(30, 95), Orientation.Vertical));
    }

    // ── The marker ───────────────────────────────────────────────────────────

    [Fact]
    public void TheMarkerIsSixPixelsWideAndSitsInTheGapItWouldOpen()
    {
        // SIX, spelled out — comparing against the constant would pass at any
        // thickness, which is the vacuous shape this repo has shipped before.
        Assert.Equal(6.0, FireSlotHitTest.MarkerThickness);

        var between = FireSlotHitTest.MarkerBounds(Row, 1, Orientation.Horizontal, Lane);
        Assert.Equal(6.0, between.Width, 9);

        // Card 0 ends at 40, card 1 starts at 50 → the bar is centred on 45.
        Assert.Equal(45.0, between.Center.X, 9);
    }

    [Fact]
    public void TheFirstAndLastSlotsLandOnTheOuterEdges()
    {
        var first = FireSlotHitTest.MarkerBounds(Row, 0, Orientation.Horizontal, Lane);
        var last = FireSlotHitTest.MarkerBounds(Row, Row.Length, Orientation.Horizontal, Lane);

        Assert.Equal(Row[0].X, first.Center.X, 9);
        Assert.Equal(Row[^1].Right, last.Center.X, 9);
    }

    [Fact]
    public void TheMarkerSpansTheItemsPlusABitOfPadding()
    {
        var marker = FireSlotHitTest.MarkerBounds(Row, 1, Orientation.Horizontal, Lane);
        Assert.Equal(60.0 + 2 * FireSlotHitTest.MarkerPadding, marker.Height, 9);
        Assert.Equal(-FireSlotHitTest.MarkerPadding, marker.Y, 9);
    }

    [Fact]
    public void AVerticalLanesMarkerIsAHorizontalBar()
    {
        Rect[] column = [new(0, 0, 60, 40), new(0, 50, 60, 40)];
        var marker = FireSlotHitTest.MarkerBounds(column, 1, Orientation.Vertical, new Rect(0, 0, 60, 100));

        Assert.Equal(6.0, marker.Height, 9);
        Assert.Equal(45.0, marker.Center.Y, 9);
        Assert.Equal(60.0 + 2 * FireSlotHitTest.MarkerPadding, marker.Width, 9);
    }

    [Fact]
    public void AnEmptyLanesMarkerSitsAtItsLeadingEdgeAndStillHasSize()
    {
        var marker = FireSlotHitTest.MarkerBounds([], 0, Orientation.Horizontal, Lane);
        Assert.Equal(6.0, marker.Width, 9);
        Assert.True(marker.Height > 0, "an empty lane's marker is invisible — the drop target reads as dead");
    }

    [Fact]
    public void AnOutOfRangeIndexClampsRatherThanThrowing()
    {
        var low = FireSlotHitTest.MarkerBounds(Row, -4, Orientation.Horizontal, Lane);
        var high = FireSlotHitTest.MarkerBounds(Row, 99, Orientation.Horizontal, Lane);
        Assert.Equal(Row[0].X, low.Center.X, 9);
        Assert.Equal(Row[^1].Right, high.Center.X, 9);
    }

    // ── The slot itself ──────────────────────────────────────────────────────

    [Fact]
    public void ASlotIsALaneAndAnIndex_AndAnIndexAloneIsNotADropTarget()
    {
        Assert.False(FireDropSlot.None.IsValid);
        Assert.False(new FireDropSlot(string.Empty, 2).IsValid);  // an index with no lane
        Assert.False(new FireDropSlot("chain.a", -1).IsValid);    // a lane with no slot
        Assert.True(new FireDropSlot("chain.a", 0).IsValid);
    }

    [Fact]
    public void TheSameIndexInTwoLanes_IsTwoDifferentSlots()
        => Assert.NotEqual(new FireDropSlot("chain.a", 2), new FireDropSlot("chain.b", 2));
}
