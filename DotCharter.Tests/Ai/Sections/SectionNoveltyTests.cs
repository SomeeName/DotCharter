using DotCharter.Services.Ai.Match.Sections;

namespace DotCharter.Tests.Ai.Sections;

/// <summary>
///     The peak picker, against hand-written curves. Every interesting failure of a
///     segmenter is a failure HERE, and a curve is three lines while a stem separation
///     is three minutes.
/// </summary>
public sealed class SectionNoveltyTests
{
    private static IReadOnlyList<NoveltySample> Curve(params double[] values) =>
        [.. values.Select((v, i) => new NoveltySample(TimeSpan.FromSeconds(i), v))];

    private static readonly TimeSpan Gap = TimeSpan.FromSeconds(4);

    [Fact]
    public void OneSpike_IsOnePeak_AtItsOwnTime()
    {
        var peaks = SectionNovelty.PickPeaks(
            Curve(0.05, 0.05, 0.06, 0.05, 0.9, 0.05, 0.06, 0.05, 0.05), Gap);

        var peak = Assert.Single(peaks);
        Assert.Equal(4.0, peak.Time.TotalSeconds, 3);
        Assert.Equal(0.9, peak.Height, 3);
        Assert.Equal(0.9, peak.Confidence, 3);
    }

    /// <summary>
    ///     The case the absolute floor exists for. A curve that is pure low-level
    ///     wobble has a median and a maximum like any other, so the adaptive half
    ///     alone finds "the biggest wiggle" and a song that never changes tone comes
    ///     back in four parts.
    /// </summary>
    [Fact]
    public void NoiseBelowTheFloor_IsNoPeaks()
    {
        var peaks = SectionNovelty.PickPeaks(
            Curve(0.05, 0.11, 0.06, 0.14, 0.07, 0.12, 0.05, 0.13, 0.06), Gap);

        Assert.Empty(peaks);
    }

    /// <summary>
    ///     …and the adaptive half is not decoration either: a curve sitting entirely
    ///     ABOVE the floor (a noisy stem, a lot of playing) must still yield only its
    ///     genuine maximum, not every bump that cleared 0.35.
    /// </summary>
    [Fact]
    public void ARaisedFloorDoesNotMakeEveryBumpAPeak()
    {
        var peaks = SectionNovelty.PickPeaks(
            Curve(0.40, 0.52, 0.41, 0.55, 0.42, 1.40, 0.43, 0.51, 0.40), Gap);

        var peak = Assert.Single(peaks);
        Assert.Equal(5.0, peak.Time.TotalSeconds, 3);
    }

    /// <summary>
    ///     Strongest-first, not left-to-right. Walking the curve in time order keeps
    ///     the FIRST of two close peaks, which for a boundary smeared across a couple
    ///     of samples is the weaker one — so the real change is the one that gets
    ///     dropped, two seconds from where it happened.
    /// </summary>
    [Fact]
    public void TwoPeaksInsideTheMinimumGap_KeepsTheStrongerOne()
    {
        var peaks = SectionNovelty.PickPeaks(
            Curve(0.05, 0.6, 0.05, 0.95, 0.05, 0.05, 0.05, 0.05, 0.05), Gap);

        var peak = Assert.Single(peaks);
        Assert.Equal(3.0, peak.Time.TotalSeconds, 3);
        Assert.Equal(0.95, peak.Height, 3);
    }

    [Fact]
    public void PeaksFurtherApartThanTheMinimumGap_AreBothKept()
    {
        var peaks = SectionNovelty.PickPeaks(
            Curve(0.05, 0.9, 0.05, 0.05, 0.05, 0.8, 0.05, 0.05, 0.05), Gap);

        Assert.Equal(2, peaks.Count);
        Assert.Equal(1.0, peaks[0].Time.TotalSeconds, 3);
        Assert.Equal(5.0, peaks[1].Time.TotalSeconds, 3);
    }

    /// <summary>Confidence is normalised against a FIXED perceptual scale, not against
    /// the song's own maximum — otherwise one violent change would push every other
    /// real change in the song towards zero.</summary>
    [Fact]
    public void ConfidenceIsTheHeightOverTheFixedScale_NotOverTheSongsMaximum()
    {
        // The only peak in this curve IS the song's maximum. Normalised against the
        // maximum it would be 1.0; against the fixed scale it is what it measured.
        var modest = Assert.Single(
            SectionNovelty.PickPeaks(Curve(0.05, 0.05, 0.6, 0.05, 0.05, 0.05, 0.05), Gap));
        Assert.Equal(0.6 / SectionNovelty.ConfidenceScale, modest.Confidence, 3);

        var violent = Assert.Single(
            SectionNovelty.PickPeaks(Curve(0.05, 0.05, 3.0, 0.05, 0.05, 0.05, 0.05), Gap));
        Assert.Equal(1.0, violent.Confidence, 3);
    }

    /// <summary>A flat top is one boundary, reported at its left edge, never two
    /// adjacent ones.</summary>
    [Fact]
    public void AFlatTop_IsOnePeakAtItsLeftEdge()
    {
        var peaks = SectionNovelty.PickPeaks(
            Curve(0.05, 0.05, 0.9, 0.9, 0.05, 0.05, 0.05, 0.05, 0.05), Gap);

        var peak = Assert.Single(peaks);
        Assert.Equal(2.0, peak.Time.TotalSeconds, 3);
    }

    [Fact]
    public void ACurveTooShortToHaveAnInteriorIsNoPeaks()
    {
        Assert.Empty(SectionNovelty.PickPeaks(Curve(9.0, 9.0), Gap));
        Assert.Empty(SectionNovelty.PickPeaks(Curve(), Gap));
    }

    [Fact]
    public void TheThresholdIsTheLargerOfTheFloorAndTheAdaptivePart()
    {
        // Median 0.05, max 0.9 → 0.05 + 0.45*0.85 = 0.4325, above the 0.35 floor.
        var adaptive = SectionNovelty.Threshold(Curve(0.05, 0.05, 0.05, 0.9, 0.05));
        Assert.Equal(0.05 + SectionNovelty.DefaultPeakFactor * 0.85, adaptive, 6);

        // Median 0.05, max 0.2 → 0.1175, below the floor, so the floor wins.
        var floored = SectionNovelty.Threshold(Curve(0.05, 0.05, 0.05, 0.2, 0.05));
        Assert.Equal(SectionNovelty.DefaultMinNovelty, floored, 6);
    }
}
