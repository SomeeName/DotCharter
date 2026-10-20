using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     The results-screen timing histogram's bar geometry. The interesting cases are
///     the degenerate ones — an empty run, and a long tail next to a tall peak.
/// </summary>
public class TimingHistogramMathTests
{
    [Fact]
    public void The_tallest_bin_fills_the_track_and_the_rest_scale_against_it()
    {
        var heights = TimingHistogramMath.BarHeights([0, 5, 10, 0]);

        Assert.Equal(0, heights[0]);
        Assert.Equal(TimingHistogramMath.MaxBarHeight / 2, heights[1]);
        Assert.Equal(TimingHistogramMath.MaxBarHeight, heights[2]);
        Assert.Equal(0, heights[3]);
    }

    [Fact]
    public void A_single_hit_beside_a_tall_peak_stays_visible()
    {
        // The tails are the whole point of the chart — the odd wildly-early hit is
        // what tells you something went wrong. Scaled strictly, 1 against 400 would
        // round to a sub-pixel bar and read as "no hits here".
        var heights = TimingHistogramMath.BarHeights([1, 400]);

        Assert.True(heights[0] >= TimingHistogramMath.MinVisibleBarHeight,
            $"a lone hit rendered {heights[0]}px and would be invisible");
        Assert.Equal(TimingHistogramMath.MaxBarHeight, heights[1]);
    }

    [Fact]
    public void An_all_zero_histogram_does_not_divide_by_zero()
    {
        // Happens for real: a run whose hits were all CQT pull-verified is untimed,
        // so every bin is empty.
        var heights = TimingHistogramMath.BarHeights([0, 0, 0, 0]);

        Assert.Equal(4, heights.Count);
        Assert.All(heights, h => Assert.Equal(0, h));
    }

    [Fact]
    public void An_absent_or_empty_histogram_yields_no_bars()
    {
        Assert.Empty(TimingHistogramMath.BarHeights(null));
        Assert.Empty(TimingHistogramMath.BarHeights([]));
    }

    [Fact]
    public void Bars_never_exceed_the_track()
    {
        var heights = TimingHistogramMath.BarHeights([3, 9, 27, 81]);
        Assert.All(heights, h => Assert.True(h <= TimingHistogramMath.MaxBarHeight));
    }

    [Fact]
    public void The_zero_line_falls_between_the_two_middle_bins()
    {
        // 30 bins of 10 ms across +/-150 ms: there is no single "zero" bin — the
        // boundary sits between bins 14 and 15, so both count as centre.
        const int bins = 30;

        Assert.True(TimingHistogramMath.IsCentreBin(14, bins));
        Assert.True(TimingHistogramMath.IsCentreBin(15, bins));
        Assert.False(TimingHistogramMath.IsCentreBin(13, bins));
        Assert.False(TimingHistogramMath.IsCentreBin(16, bins));
    }

    [Fact]
    public void Early_bins_are_the_left_half()
    {
        const int bins = 30;

        Assert.True(TimingHistogramMath.IsEarlyBin(0, bins));
        Assert.True(TimingHistogramMath.IsEarlyBin(14, bins));
        Assert.False(TimingHistogramMath.IsEarlyBin(15, bins));
        Assert.False(TimingHistogramMath.IsEarlyBin(29, bins));
    }

    [Fact]
    public void Centre_bars_are_drawn_solid_and_the_tails_dimmed()
    {
        Assert.Equal(1.0, new TimingBarRow(10, IsCentre: true, IsEarly: false).Opacity);
        Assert.True(new TimingBarRow(10, IsCentre: false, IsEarly: true).Opacity < 1.0);
    }
}
