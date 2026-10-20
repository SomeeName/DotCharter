using DotCharter.UI.Controls.Layers.Helpers;

namespace DotCharter.Tests.UI.Rendering;

/// <summary>
/// Pins the measure-pill decision seam: which segments a pill prints, and the
/// BPM-change epsilon that distinguishes a real tempo change from float noise.
/// The two segments deliberately follow DIFFERENT rules — a tempo varies bar to
/// bar, a meter is usually decided once — and this file is what keeps them from
/// being "unified" back together.
/// </summary>
public class MeasurePillPolicyTests
{
    [Fact]
    public void BpmDiffers_RealTempoChange_IsTrue() =>
        // The Matasaburo chart's real alternation — must register as a change.
        Assert.True(MeasurePillPolicy.BpmDiffers(131.87, 132.16));

    [Fact]
    public void BpmDiffers_IdenticalBpm_IsFalse() => Assert.False(MeasurePillPolicy.BpmDiffers(200.0, 200.0));

    [Fact]
    public void BpmDiffers_WithinEpsilon_IsFalse() =>
        // Float noise below the 0.005 threshold is not a change.
        Assert.False(MeasurePillPolicy.BpmDiffers(200.0, 200.003));

    [Fact]
    public void BpmDiffers_JustBeyondEpsilon_IsTrue() => Assert.True(MeasurePillPolicy.BpmDiffers(200.0, 200.01));

    [Theory]
    [InlineData(false, false, false)] // unchanged, not first, cramped → hidden
    [InlineData(true, false, true)]   // changed → shown
    [InlineData(false, true, true)]   // first visible → shown for context
    [InlineData(true, true, true)]
    public void ShowBpm_ChangeOrFirstVisible_WhenTheMeasureIsCramped(bool changed, bool first, bool expected) => Assert.Equal(expected, MeasurePillPolicy.ShowBpm(changed, first, measureWidthPx: 40));

    /// <summary>
    ///     0.51: a measure with room prints its tempo whether or not it changed
    ///     — the design's `M73 143.88 BPM` on every bar. The 0.15-era
    ///     change-only rule survives only as the CRAMPED fallback above.
    /// </summary>
    [Fact]
    public void ShowBpm_AMeasureWithRoom_PrintsItsTempoEvenUnchanged()
    {
        Assert.True(MeasurePillPolicy.ShowBpm(
            bpmChangedFromPrevious: false, isFirstVisible: false,
            measureWidthPx: MeasurePillPolicy.MinBpmMeasureWidthPx));
    }

    /// <summary>The gate is what stops the band becoming overlapping
    /// text-on-text when the user zooms out; one pixel under it, an unchanged
    /// tempo stays off.</summary>
    [Fact]
    public void ShowBpm_OnePixelUnderTheGate_StaysOff()
    {
        Assert.False(MeasurePillPolicy.ShowBpm(
            bpmChangedFromPrevious: false, isFirstVisible: false,
            measureWidthPx: MeasurePillPolicy.MinBpmMeasureWidthPx - 1));
    }

    /// <summary>The LAST measure of a chart has no successor, so
    /// <c>BackgroundLayer.MeasureWidthPx</c> reports it as unbounded — nothing
    /// can collide with it and it must still print.</summary>
    [Fact]
    public void ShowBpm_TheLastMeasure_IsNeverCramped() => Assert.True(MeasurePillPolicy.ShowBpm(false, false, double.MaxValue));

    /// <summary>
    ///     A real meter CHANGE always prints, wherever it is and whatever the
    ///     chart opened in — that is the whole reason the segment exists.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ShowTimeSig_AGenuineMeterChange_AlwaysPrints(bool first, bool differsFromOpening)
    {
        Assert.True(MeasurePillPolicy.ShowTimeSig(
            sigChangedFromPrevious: true, isFirstVisible: first, differsFromChartOpening: differsFromOpening));
    }

    /// <summary>
    ///     <b>0.51: a chart in the meter it opened in never spends a pixel
    ///     saying so.</b> The rule used to be <c>changed || isFirstVisible</c>,
    ///     so the pinned left-edge pill — the one the reader looks at most —
    ///     printed "4/4" on every 4/4 chart in the library forever. The
    ///     reference band is <c>M73 143.88 BPM</c> and nothing else.
    /// </summary>
    [Fact]
    public void ShowTimeSig_TheLeftEdgeContextPill_StaysSilentInTheChartsOwnMeter()
    {
        Assert.False(MeasurePillPolicy.ShowTimeSig(
            sigChangedFromPrevious: false, isFirstVisible: true, differsFromChartOpening: false));
    }

    /// <summary>
    ///     …and the case the context clause exists for survives: scrolled into
    ///     a passage whose meter is NOT the one the chart opened in, the pinned
    ///     pill still says which meter you are in, even though the change
    ///     itself is off screen to the left.
    /// </summary>
    [Fact]
    public void ShowTimeSig_ScrolledIntoAModulatedPassage_TheContextPillStillSaysTheMeter()
    {
        Assert.True(MeasurePillPolicy.ShowTimeSig(
            sigChangedFromPrevious: false, isFirstVisible: true, differsFromChartOpening: true));
    }

    /// <summary>An ordinary mid-viewport pill never carries the meter — only
    /// the pinned context pill can, and only under the clause above.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShowTimeSig_AnOrdinaryUnchangedMeasure_NeverPrintsTheMeter(bool differsFromOpening)
    {
        Assert.False(MeasurePillPolicy.ShowTimeSig(
            sigChangedFromPrevious: false, isFirstVisible: false, differsFromChartOpening: differsFromOpening));
    }

    // The sticky (pinned) pill is pushed out by the first in-flow pill now, never drawn on it
    // and never made to vanish: see StickyLabelTests.
}
