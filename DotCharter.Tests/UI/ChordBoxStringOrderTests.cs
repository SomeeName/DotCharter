using DotCharter.UI.Controls;

namespace DotCharter.Tests.UI;

/// <summary>
///     The chord box lays its strings out the way the HIGHWAY does.
///     <para>
///         It did not. <c>ChartRenderContext.LaneTopFor</c> is
///         <c>flip ? count - 1 - index : index</c> with lane 0 at the TOP, so
///         unflipped the highway puts string 0 — the LOWEST, the red E — at the
///         top. The chord box drew string 0 at the BOTTOM, and its own comment
///         claimed that matched the highway. The two were mirror images:
///         "instead of red (low E) being the top the purple (high E) is on the
///         top".
///     </para>
///     <para>
///         And the flip toggle has to move BOTH: "generally there is a
///         flip/invert toggle and that should invert these as well so they are
///         always consistent".
///     </para>
/// </summary>
public sealed class ChordBoxStringOrderTests
{
    /// <summary>
    ///     The claim, stated against the renderer's own formula so the two cannot
    ///     drift apart.
    /// </summary>
    [Theory]
    [InlineData(false, 0, 0)] // unflipped: the LOW string is lane 0, the top
    [InlineData(false, 5, 5)]
    [InlineData(true, 0, 5)]  // flipped: the low string goes to the bottom
    [InlineData(true, 5, 0)]
    public void TheLaneMatchesTheHighwaysFormula(bool flip, int stringIndex, int expectedLane)
    {
        const int count = 6;
        var geometry = new ChordBoxGeometry(400, 200, count, 0, 5, flip);

        Assert.Equal(expectedLane, geometry.LaneOf(stringIndex));
        // …and that is exactly what ChartRenderContext.LaneTopFor computes.
        Assert.Equal(flip ? count - 1 - stringIndex : stringIndex, geometry.LaneOf(stringIndex));
    }

    [Fact]
    public void UnflippedTheLowStringIsDrawnAtTheTop()
    {
        var geometry = new ChordBoxGeometry(400, 200, 6, 0, 5);

        var low = geometry.StringY(0);
        var high = geometry.StringY(5);

        Assert.True(low < high, $"string 0 is at y={low} and string 5 at y={high} — the low string must be on top");
        Assert.Equal(ChordBoxGeometry.TopPadding, low, 3);
    }

    [Fact]
    public void FlippedItIsTheOtherWayRound()
    {
        var geometry = new ChordBoxGeometry(400, 200, 6, 0, 5, FlipStrings: true);

        Assert.True(geometry.StringY(0) > geometry.StringY(5));
        Assert.Equal(ChordBoxGeometry.TopPadding, geometry.StringY(5), 3);
    }

    /// <summary>
    ///     Hit-testing follows the drawing, or a click lands on the wrong string —
    ///     the same rule <c>ChartHitTester</c> states about the highway.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheStringUnderAPointIsTheOneDrawnThere(bool flip)
    {
        var geometry = new ChordBoxGeometry(400, 200, 6, 0, 5, flip);

        for (var stringIndex = 0; stringIndex < 6; stringIndex++)
            Assert.Equal(stringIndex, geometry.HitString(geometry.StringY(stringIndex)));
    }

    [Fact]
    public void TheLanesAreEvenlySpacedAndCoverTheBoard()
    {
        var geometry = new ChordBoxGeometry(400, 200, 6, 0, 5);
        var spacing = geometry.StringSpacing;

        for (var stringIndex = 1; stringIndex < 6; stringIndex++)
            Assert.Equal(spacing, geometry.StringY(stringIndex) - geometry.StringY(stringIndex - 1), 3);
    }
}
