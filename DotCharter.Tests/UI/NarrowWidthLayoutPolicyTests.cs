using DotCharter.UI.Controls;

namespace DotCharter.Tests.UI;

/// <summary>
///     The pure decision behind the work-queue bar's narrow-pane layout (#134): when its last part
///     wraps (<see cref="TrailingWrapLayout" />). The real tree is laid out at 400 and 320 px by
///     <c>Perf/InspectorNarrowWidthUiPerfSmokeTests</c>. The tab strip's rule (#573) is
///     <c>UI/InspectorTabStripLayoutTests</c>.
/// </summary>
public sealed class NarrowWidthLayoutPolicyTests
{
    [Theory]
    // filter floor 150, stepper 100 + readout 190, 6 px between parts
    [InlineData(500, false)] // 500 - 290 - 12 = 198 ≥ 150: one row
    [InlineData(452, false)] // exactly the floor
    [InlineData(451, true)]
    [InlineData(400, true)]  // the ~400 px a 1280 px window gives the pane
    [InlineData(320, true)]  // the dock's MinWidth
    public void TheLastPart_WrapsOnlyBelowTheFirstPartsFloor(double available, bool wraps) =>
        Assert.Equal(wraps, TrailingWrapLayout.Wraps(available, 150, 290, 6, 3));

    [Fact]
    public void ALoneChild_OrAnUnconstrainedRow_NeverWraps()
    {
        Assert.False(TrailingWrapLayout.Wraps(10, 150, 0, 6, 1));
        Assert.False(TrailingWrapLayout.Wraps(double.PositiveInfinity, 150, 290, 6, 3));
    }
}
