using DotCharter.Tone.UI.Controls.Fire;

namespace DotCharter.Tests.UI.Controls;

/// <summary>
///     The level meter's colours are SEMANTIC — "green below −6 dB, amber
///     −6…−2, red above" is what the meter means, not how it is decorated. So
///     both boundaries are pinned on BOTH sides: a meter that colours by bar
///     index looks identical today and starts lying the moment its scale
///     changes.
/// </summary>
public sealed class FireLevelScaleTests
{
    [Theory]
    [InlineData(-60.0, FireLevelBand.Good)]
    [InlineData(-6.01, FireLevelBand.Good)]  // just BELOW −6 is still green
    [InlineData(-6.0, FireLevelBand.Hot)]    // −6 itself is amber ("green BELOW −6")
    [InlineData(-4.0, FireLevelBand.Hot)]
    [InlineData(-2.0, FireLevelBand.Hot)]    // −2 itself is still amber ("red ABOVE −2")
    [InlineData(-1.99, FireLevelBand.Over)]  // just above −2 is red
    [InlineData(0.0, FireLevelBand.Over)]
    [InlineData(3.0, FireLevelBand.Over)]
    public void TheBandThresholdsAreMinusSixAndMinusTwo(double db, FireLevelBand expected)
        => Assert.Equal(expected, FireLevelScale.BandFor(db));

    /// <summary>
    ///     An UNLIT bar keeps the track token's translucency. The token is
    ///     10 %-white; the old path ran it through <c>FireUnitState.At</c>,
    ///     which REPLACES the alpha channel — every unlit bar rendered solid
    ///     white and an idle meter (level −∞) read as a fully-hot input while
    ///     showing no audio (live-audit finding, 2026-08-11).
    /// </summary>
    [Fact]
    public void AnUnlitBar_KeepsTheTracksOwnTranslucency()
    {
        var track = Avalonia.Media.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF); // FireSurfaceTrack
        var unlit = FireLevelScale.UnlitColor(track);

        Assert.Equal(0x1A, unlit.A); // scaled by UnlitAlpha (1.0), never promoted
        Assert.Equal(track.R, unlit.R);
        Assert.True(unlit.A < 0x40,
            $"an unlit bar at alpha {unlit.A} is bright enough to read as LIT");
    }

    [Fact]
    public void SilenceLightsNothing_AndTheTopOfTheScaleLightsEverything()
    {
        const int count = FireLevelScale.DefaultSegmentCount;
        var min = FireLevelScale.DefaultMinimumDb;
        var max = FireLevelScale.DefaultMaximumDb;

        Assert.Equal(0, FireLevelScale.LitSegments(double.NegativeInfinity, count, min, max));
        Assert.Equal(0, FireLevelScale.LitSegments(min, count, min, max));
        Assert.Equal(0, FireLevelScale.LitSegments(min - 20, count, min, max));
        Assert.Equal(count, FireLevelScale.LitSegments(max, count, min, max));
        Assert.Equal(count, FireLevelScale.LitSegments(max + 20, count, min, max));
    }

    [Fact]
    public void ALevelInsideOneSegment_NeverChangesTheLitCount()
    {
        // This IS the meter's repaint gate: it is fed from audio, and a level
        // that moves without lighting another bar changes nothing on screen.
        const int count = FireLevelScale.DefaultSegmentCount;
        var min = FireLevelScale.DefaultMinimumDb;
        var max = FireLevelScale.DefaultMaximumDb;

        var a = FireLevelScale.LitSegments(-20.0, count, min, max);
        var b = FireLevelScale.LitSegments(-19.9, count, min, max);
        Assert.Equal(a, b);

        // …and crossing a boundary does.
        var top = FireLevelScale.SegmentTopDb(a - 1, count, min, max);
        Assert.NotEqual(a, FireLevelScale.LitSegments(top + 1.0, count, min, max));
    }

    [Fact]
    public void TheDefaultScale_ReproducesTheMockupsTenGreenTwoAmberTwoRed()
    {
        // `Amp and FX Rig.dc.html`'s footer meter is 14 bars coloured
        // `i > 11 → red, i > 9 → amber, else green`. That picture is not
        // hard-coded here — it FALLS OUT of the −6/−2 thresholds on the default
        // −30…+1 dB scale, which is the whole reason those defaults are those
        // numbers. If someone "tidies" the scale, this fails.
        const int count = FireLevelScale.DefaultSegmentCount;
        var min = FireLevelScale.DefaultMinimumDb;
        var max = FireLevelScale.DefaultMaximumDb;

        var bands = Enumerable.Range(0, count)
            .Select(i => FireLevelScale.BandForSegment(i, count, min, max))
            .ToArray();

        Assert.Equal(10, bands.Count(b => b == FireLevelBand.Good));
        Assert.Equal(2, bands.Count(b => b == FireLevelBand.Hot));
        Assert.Equal(2, bands.Count(b => b == FireLevelBand.Over));

        for (var i = 0; i < count; i++)
        {
            var expected = i > 11 ? FireLevelBand.Over
                : i > 9 ? FireLevelBand.Hot
                : FireLevelBand.Good;
            Assert.Equal(expected, bands[i]);
        }
    }

    [Fact]
    public void TheBandsRunQuietToLoud_AndNeverGoBackwards()
    {
        const int count = 40;
        var previous = FireLevelBand.Good;
        for (var i = 0; i < count; i++)
        {
            var band = FireLevelScale.BandForSegment(i, count, -48, 3);
            Assert.True(band >= previous, $"bar {i} went back from {previous} to {band}");
            previous = band;
        }
    }

    [Fact]
    public void TheBarsRampFromFiveToFourteenPixels()
    {
        const int count = FireLevelScale.DefaultSegmentCount;
        var first = FireLevelScale.SegmentHeight(0, count,
            FireLevelScale.DefaultMinBarHeight, FireLevelScale.DefaultMaxBarHeight);
        var last = FireLevelScale.SegmentHeight(count - 1, count,
            FireLevelScale.DefaultMinBarHeight, FireLevelScale.DefaultMaxBarHeight);

        Assert.Equal(5.0, first, 9);
        Assert.Equal(14.0, last, 9);
        Assert.InRange(FireLevelScale.SegmentHeight(count / 2, count, 5, 14), 5, 14);
    }

    [Fact]
    public void AFlatRowIsTheSameHelperWithBothHeightsEqual()
    {
        // The footer meter in the mockup is a flat 12 px row, not a ramp — one
        // control, two forms, no second code path.
        for (var i = 0; i < 14; i++)
            Assert.Equal(12.0, FireLevelScale.SegmentHeight(i, 14, 12, 12), 9);
    }

    [Fact]
    public void TheRowIsFourPixelBarsTwoApart()
    {
        Assert.Equal(4.0, FireLevelScale.DefaultBarWidth);
        Assert.Equal(2.0, FireLevelScale.DefaultBarGap);
        Assert.Equal(14 * 4.0 + 13 * 2.0, FireLevelScale.RowWidth(14, 4, 2), 9);
    }

    [Fact]
    public void ADegenerateScale_LightsNothingRatherThanThrowing()
    {
        Assert.Equal(0, FireLevelScale.LitSegments(0, 0, -30, 1));
        Assert.Equal(0, FireLevelScale.LitSegments(0, 14, 0, 0));
        Assert.Equal(0, FireLevelScale.LitSegments(double.NaN, 14, -30, 1));
    }
}
