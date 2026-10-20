using DotCharter.Tone.UI.Views.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The board's wheel-to-horizontal translation (<see cref="RigBoardWheelMath" />).
///     Pure on purpose: direction and clamping are the whole contract, and a
///     wrong sign here reads as "scrolling is backwards" on every mouse.
/// </summary>
public sealed class RigBoardWheelMathTests
{
    /// <summary>Wheel DOWN (negative delta) travels RIGHT — the chain's reading
    /// direction — and wheel UP travels back left.</summary>
    [Fact]
    public void WheelDown_MovesRight_AndWheelUp_MovesLeft()
    {
        Assert.Equal(RigBoardWheelMath.NotchPx,
            RigBoardWheelMath.NextOffsetX(0, -1, 1000));
        Assert.Equal(400 - RigBoardWheelMath.NotchPx,
            RigBoardWheelMath.NextOffsetX(400, +1, 1000));
    }

    [Fact]
    public void TheOffset_ClampsAtBothEnds()
    {
        Assert.Equal(0, RigBoardWheelMath.NextOffsetX(20, +1, 1000));
        Assert.Equal(1000, RigBoardWheelMath.NextOffsetX(980, -1, 1000));
    }

    /// <summary>A viewport wider than the content (max ≤ 0) pins the offset to
    /// zero rather than producing a negative scroll range.</summary>
    [Fact]
    public void ANonScrollableViewport_PinsToZero()
    {
        Assert.Equal(0, RigBoardWheelMath.NextOffsetX(0, -1, 0));
        Assert.Equal(0, RigBoardWheelMath.NextOffsetX(0, -1, -50));
    }
    [Theory]
    [InlineData(1, 600, 400, 1000, 400, .6)]
    [InlineData(1, 1000, 200, 1000, 400, .5)]
    [InlineData(.5, 1000, 400, 500, 200, 1)]
    [InlineData(1, 0, 400, 1000, 400, 1)]
    public void FitUsesBothAxesAndPreservesReadableZoom(double current, double width, double height,
        double extentWidth, double extentHeight, double expected)
        => Assert.Equal(expected, RigBoardWheelMath.FitZoom(current, width, height, extentWidth, extentHeight), 5);
}
