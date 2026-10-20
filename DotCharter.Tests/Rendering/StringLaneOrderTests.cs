using DotCharter.Rendering.Data;
using DotCharter.UI.Controls.Layers;

namespace DotCharter.Tests.Rendering;

// Pins the inverted-string layout: the 3D string→row helper and the 2D lane
// map must flip together, and the 2D flip must COMPOSE with lefty (XOR) so a
// lefty player who also inverts ends up back at the default order.
public class StringLaneOrderTests
{
    [Theory]
    // default (not inverted): StringIndex 0 (low E) sits at the TOP row (count-1).
    [InlineData(0, 6, false, 5)]
    [InlineData(5, 6, false, 0)]
    // inverted (tab layout): low E drops to the bottom row (0).
    [InlineData(0, 6, true, 0)]
    [InlineData(5, 6, true, 5)]
    public void StringLaneIndex_FlipsTheVerticalOrder(int stringIndex, int stringCount, bool invert, int expected) => Assert.Equal(expected, FretboardConstants.StringLaneIndex(stringIndex, stringCount, invert));

    [Fact]
    public void StringLaneIndex_Capo0Default_IsAStrictNoOp_ForEveryString()
    {
        // The whole 3D stack must be byte-identical to the historical
        // (stringCount - 1 - i) formula when the flag is off.
        for (var s = 0; s < 6; s++)
            Assert.Equal(6 - 1 - s, FretboardConstants.StringLaneIndex(s, 6, invertStrings: false));
    }

    [Theory]
    // Default (no flip): low E (string 0) sits at the TOP 2D lane (row 0),
    // matching the 3D highway. Lefty OR Invert flips it; both together cancel.
    [InlineData(false, false, 0)] // neither: low E at row 0 = top (Rocksmith)
    [InlineData(true, false, 5)]  // lefty only: flipped
    [InlineData(false, true, 5)]  // invert only: tab layout (low E at the bottom)
    [InlineData(true, true, 0)]   // both: cancel back to default
    public void LaneTopFor_ComposesLeftyAndInvert(bool lefty, bool invert, int expectedRow)
    {
        var rc = new ChartRenderContext
        {
            TopMargin = 0f,
            LaneHeight = 10f,
            StringCount = 6,
            LeftyFlip = lefty,
            InvertStrings = invert
        };

        // LaneTopFor(0) = TopMargin + row * LaneHeight, so row = LaneTopFor(0) / LaneHeight.
        Assert.Equal(expectedRow, (int)(rc.LaneTopFor(0) / rc.LaneHeight));
    }

    [Theory]
    [InlineData(false)] // default: Rocksmith order — low E on top in BOTH views
    [InlineData(true)]  // inverted: tab order — low E on the bottom in BOTH views
    public void TwoDAndThreeD_ShowTheSameStringAtTheTop(bool invert)
    {
        // THE regression guard for the 2D/3D mismatch: the string drawn at the top
        // of the 2D timeline must be the same string drawn at the top of the 3D
        // highway. The views have opposite vertical axes (2D screen-Y down, 3D
        // world-Y up), so their row formulas are mirror images — this proves they
        // still land the same string on top.
        const int n = 6;

        // 3D top = the highest world Y = the highest row index (n - 1).
        var threeDTop = -1;
        for (var s = 0; s < n; s++)
        {
            if (FretboardConstants.StringLaneIndex(s, n, invert) == n - 1)
                threeDTop = s;
        }

        // 2D top = the smallest lane top = row 0.
        var rc = new ChartRenderContext
        {
            TopMargin = 0f, LaneHeight = 10f, StringCount = n, LeftyFlip = false, InvertStrings = invert
        };
        var twoDTop = -1;
        for (var s = 0; s < n; s++)
        {
            if ((int)(rc.LaneTopFor(s) / rc.LaneHeight) == 0)
                twoDTop = s;
        }

        Assert.Equal(threeDTop, twoDTop);
    }
}
