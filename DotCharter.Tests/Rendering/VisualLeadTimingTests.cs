using DotCharter.Rendering.Diagnostics;
using DotCharter.UI.Controls;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     Pure rules of the composed-path render-clock lead (0.16): a constant
///     presentation delay is compensated by leading the clock; the lead must
///     never go negative (rendering behind the playhead) or run away on a
///     compositor stall. Plus the FrameTimeRing jitter (stddev) metric the
///     present pump reports.
/// </summary>
public class VisualLeadTimingTests
{
    [Theory]
    // Auto mode (override < 0): measured + trim, clamped.
    [InlineData(8.0, -1.0, 0.0, 8.0)]
    [InlineData(8.0, -1.0, 4.0, 12.0)]
    [InlineData(8.0, -1.0, -20.0, 0.0)] // trim can't drag the lead negative
    [InlineData(500.0, -1.0, 0.0, VisualLeadTiming.MaxLeadMs)] // stall clamp
    // Fixed override replaces the measurement entirely.
    [InlineData(8.0, 0.0, 0.0, 0.0)] // 0 = lead disabled
    [InlineData(8.0, 15.0, 0.0, 15.0)]
    [InlineData(8.0, 15.0, 2.5, 17.5)]
    [InlineData(8.0, 200.0, 0.0, VisualLeadTiming.MaxLeadMs)]
    // A negative measurement (never published, but defensive) reads as 0.
    [InlineData(-5.0, -1.0, 0.0, 0.0)]
    public void ComputeLeadMs_OverrideTrimAndClamp(double measured, double @override, double trim, double expected) => Assert.Equal(expected, VisualLeadTiming.ComputeLeadMs(measured, @override, trim), 6);

    [Fact]
    public void FrameTimeRing_StdDev_EmptyAndSingleSampleAreZero()
    {
        var ring = new FrameTimeRing(8);
        Assert.Equal(0, ring.StdDevMs());
        ring.Add(16.7);
        Assert.Equal(0, ring.StdDevMs());
    }

    [Fact]
    public void FrameTimeRing_StdDev_ConstantIntervalsHaveZeroJitter()
    {
        var ring = new FrameTimeRing(8);
        for (var i = 0; i < 8; i++) ring.Add(6.94);
        Assert.Equal(0, ring.StdDevMs(), 9);
    }

    [Fact]
    public void FrameTimeRing_StdDev_KnownPopulation()
    {
        var ring = new FrameTimeRing(4);
        // Population {2, 4, 4, 6}: mean 4, variance (4+0+0+4)/4 = 2.
        ring.Add(2);
        ring.Add(4);
        ring.Add(4);
        ring.Add(6);
        Assert.Equal(Math.Sqrt(2), ring.StdDevMs(), 9);
    }
}
