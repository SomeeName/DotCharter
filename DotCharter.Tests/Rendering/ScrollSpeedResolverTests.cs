using DotCharter.Core.Graphics;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     Which scroll speed a run actually plays at. Three sources can supply one and
///     they have to be tried in a specific order, or the feature reads as broken in
///     ways that are hard to attribute: a global override that quietly beats what
///     the player just typed on the pre-song card, or a chart's authored value that
///     beats an override the player deliberately turned on.
/// </summary>
public sealed class ScrollSpeedResolverTests
{
    private const float RsDefault = 1.3f;

    [Fact]
    public void NothingSet_FallsBackToRocksmithsDefault()
        => Assert.Equal(RsDefault, HighwaySpeedPolicy.ResolveScrollSpeed(null, null, null), 3);

    [Fact]
    public void TheChartsOwnValue_IsUsedWhenNobodyOverrides()
        => Assert.Equal(2.0f, HighwaySpeedPolicy.ResolveScrollSpeed(null, null, chartValue: 2.0f), 3);

    [Fact]
    public void TheGlobalOverride_BeatsTheChart()
        => Assert.Equal(0.8f, HighwaySpeedPolicy.ResolveScrollSpeed(null, globalOverride: 0.8f, chartValue: 2.0f), 3);

    /// <summary>The pre-song card is the most specific thing the player can say, so
    /// it beats everything — including a global override they set weeks ago.</summary>
    [Fact]
    public void ThePerRunValue_BeatsEverything()
        => Assert.Equal(1.7f,
            HighwaySpeedPolicy.ResolveScrollSpeed(perRun: 1.7f, globalOverride: 0.8f, chartValue: 2.0f), 3);

    /// <summary>An unset override must be null, not 1.3 — otherwise "I didn't turn
    /// the override on" is indistinguishable from "I forced 1.3", and the chart's
    /// own value could never win.</summary>
    [Fact]
    public void ADisabledOverride_DoesNotMaskTheChart()
        => Assert.Equal(2.5f, HighwaySpeedPolicy.ResolveScrollSpeed(null, globalOverride: null, chartValue: 2.5f), 3);

    /// <summary>Real CDLC can carry up to 5.0; we support 3.0. Clamping is correct —
    /// but it must clamp, not divide by a wild value downstream.</summary>
    [Theory]
    [InlineData(5.0f, 3.0f)]   // a legal RS value above our range
    [InlineData(0.1f, 0.5f)]   // below our range
    [InlineData(0f, 0.5f)]     // a corrupt chart: 0 would be an infinite time→Z scale
    public void AnOutOfRangeChartValue_IsClamped(float chartValue, float expected)
        => Assert.Equal(expected, HighwaySpeedPolicy.ResolveScrollSpeed(null, null, chartValue), 3);
}
