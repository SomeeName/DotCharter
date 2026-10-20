using DotCharter.Tone.Audio.Equalizer;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The EQ's maths, against filter responses that are known in advance rather
///     than against whatever the code happens to produce.
///     <para>
///         This is the reason <see cref="EqCurve" /> is a pure static type and
///         not a method on the plot control: every assertion below would
///         otherwise need a UI thread, and none of them would have been written.
///     </para>
/// </summary>
public sealed class EqCurveTests
{
    private const double Sr = EqCurve.DisplaySampleRate;

    // ── the axis mappings ────────────────────────────────────────────────

    [Fact]
    public void TheFrequencyAxis_PutsTwentyHzAtZeroAndTwentyKilohertzAtOne()
    {
        Assert.Equal(0.0, EqCurve.FrequencyToNormalized(20), 6);
        Assert.Equal(1.0, EqCurve.FrequencyToNormalized(20000), 6);
    }

    [Fact]
    public void TheFrequencyAxis_IsLogarithmic_SoEachDecadeGetsAThird()
    {
        // 20 -> 200 -> 2000 -> 20000 is three decades across the plot.
        Assert.Equal(1.0 / 3.0, EqCurve.FrequencyToNormalized(200), 6);
        Assert.Equal(2.0 / 3.0, EqCurve.FrequencyToNormalized(2000), 6);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(82.41)]   // low E
    [InlineData(440)]
    [InlineData(5000)]
    [InlineData(20000)]
    public void TheFrequencyAxis_RoundTrips(double hz) =>
        Assert.Equal(hz, EqCurve.NormalizedToFrequency(EqCurve.FrequencyToNormalized(hz)), 4);

    [Fact]
    public void TheFrequencyAxis_ClampsRatherThanWrapping()
    {
        // A pointer dragged off either end of the plot must pin the band, not
        // teleport it to the opposite edge.
        Assert.Equal(20.0, EqCurve.NormalizedToFrequency(-3.0), 6);
        Assert.Equal(20000.0, EqCurve.NormalizedToFrequency(4.0), 6);
    }

    [Fact]
    public void TheGainAxis_PutsZeroDbInTheMiddleAndGrowsDownward()
    {
        Assert.Equal(0.5, EqCurve.GainDbToNormalized(0), 6);
        Assert.Equal(0.0, EqCurve.GainDbToNormalized(EqLimits.MaxGainDb), 6);
        Assert.Equal(1.0, EqCurve.GainDbToNormalized(-EqLimits.MaxGainDb), 6);
    }

    [Theory]
    [InlineData(-18)]
    [InlineData(-6)]
    [InlineData(0)]
    [InlineData(3.5)]
    [InlineData(18)]
    public void TheGainAxis_RoundTrips(double db) =>
        Assert.Equal(db, EqCurve.NormalizedToGainDb(EqCurve.GainDbToNormalized(db)), 6);

    [Theory]
    [InlineData(0.3)]
    [InlineData(0.707)]
    [InlineData(1.6)]
    [InlineData(18)]
    public void TheQAxis_RoundTrips(double q) =>
        Assert.Equal(q, EqCurve.NormalizedToQ(EqCurve.QToNormalized(q)), 6);

    [Fact]
    public void TheQAxis_IsLogarithmic_SoTheUsefulLowEndIsNotCrushedIntoTheFirstTenth()
    {
        // Linear over 0.3..18, Q=0.707 would sit at 2.3% of the track — which is
        // the whole reason this mapping is not linear. Log puts it near a third.
        var atDefault = EqCurve.QToNormalized(0.707);
        Assert.InRange(atDefault, 0.15, 0.30);
    }

    // ── known filter responses ───────────────────────────────────────────

    [Theory]
    [InlineData(-12.0)]
    [InlineData(-6.0)]
    [InlineData(0.0)]
    [InlineData(6.0)]
    [InlineData(12.0)]
    public void ABell_AtItsCentreFrequency_YieldsExactlyItsGain(double gainDb)
    {
        var band = new EqBand(EqBandType.Bell, 1000, 1.4, gainDb, true);
        Assert.Equal(gainDb, EqCurve.BandResponseDbAt(band, 1000, Sr), 3);
    }

    [Fact]
    public void ABell_IsFlatFarFromItsCentreFrequency()
    {
        var band = new EqBand(EqBandType.Bell, 1000, 4.0, 12, true);
        Assert.Equal(0.0, EqCurve.BandResponseDbAt(band, 40, Sr), 1);
        Assert.Equal(0.0, EqCurve.BandResponseDbAt(band, 16000, Sr), 1);
    }

    [Fact]
    public void ATwelveDbPerOctaveHighPass_IsTwelveDbDownOneOctaveBelowCutoff()
    {
        // A 2nd-order Butterworth high pass rolls off at 12 dB/oct in the stop
        // band, which is the number the band editor PRINTS for this shape. If
        // this ever fails, that printed slope has become a lie.
        var band = new EqBand(EqBandType.HighPass, 800, 0.707, 0, true);

        // Well inside the stop band, so the asymptote holds: two octaves apart.
        var atLow = EqCurve.BandResponseDbAt(band, 50, Sr);
        var atHigh = EqCurve.BandResponseDbAt(band, 100, Sr);
        Assert.Equal(12.0, atHigh - atLow, 0.4);
    }

    [Fact]
    public void ALowPass_IsTwelveDbDownOneOctaveAboveCutoff() =>
        // Same asymptote, mirrored. Measured in the stop band, well above the
        // corner, where the second-order slope has settled.
        Assert.Equal(
            12.0,
            EqCurve.BandResponseDbAt(new EqBand(EqBandType.LowPass, 500, 0.707, 0, true), 2000, Sr)
            - EqCurve.BandResponseDbAt(new EqBand(EqBandType.LowPass, 500, 0.707, 0, true), 4000, Sr),
            0.4);

    [Fact]
    public void AButterworthHighPass_IsThreeDbDownAtItsOwnCutoff() =>
        Assert.Equal(-3.0,
            EqCurve.BandResponseDbAt(new EqBand(EqBandType.HighPass, 1000, 0.707, 0, true), 1000, Sr), 0.1);

    [Fact]
    public void ANotch_KillsItsCentreFrequencyAndLeavesTheNeighboursAlone()
    {
        var band = new EqBand(EqBandType.Notch, 50, 14, 0, true);
        Assert.True(EqCurve.BandResponseDbAt(band, 50, Sr) < -40,
            "a notch must actually notch at its centre");

        // The point of a NARROW hum notch: the low E two octaves up is untouched.
        Assert.Equal(0.0, EqCurve.BandResponseDbAt(band, 82.41, Sr), 1);
    }

    [Fact]
    public void ALowShelf_ApproachesItsFullGainWellBelowTheCorner_AndUnityWellAbove()
    {
        var band = new EqBand(EqBandType.LowShelf, 200, 0.71, -6, true);
        Assert.Equal(-6.0, EqCurve.BandResponseDbAt(band, 25, Sr), 0.5);
        Assert.Equal(0.0, EqCurve.BandResponseDbAt(band, 6000, Sr), 0.5);
    }

    [Fact]
    public void AHighShelf_ApproachesItsFullGainWellAboveTheCorner_AndUnityWellBelow()
    {
        var band = new EqBand(EqBandType.HighShelf, 4000, 0.71, 8, true);
        Assert.Equal(8.0, EqCurve.BandResponseDbAt(band, 19000, Sr), 0.5);
        Assert.Equal(0.0, EqCurve.BandResponseDbAt(band, 100, Sr), 0.5);
    }

    [Fact]
    public void AShelfAndABell_UseTheCookbooksSquareRootGainConvention()
    {
        // A is 10^(gain/40), not 10^(gain/20). Getting that wrong silently
        // DOUBLES every boost, and the curve still looks plausible — which is
        // exactly why this is asserted rather than eyeballed.
        var doubled = EqCurve.BandResponseDbAt(new EqBand(EqBandType.Bell, 1000, 1.0, 6, true), 1000, Sr);
        Assert.Equal(6.0, doubled, 3);
        Assert.NotEqual(12.0, doubled, 1);
    }

    // ── summing ──────────────────────────────────────────────────────────

    [Fact]
    public void CascadedBands_SumInDecibels()
    {
        // Two identical bells at one frequency are one 12 dB boost, because
        // cascaded sections multiply in magnitude — which is what the audio path
        // actually does, one biquad after another over the same signal.
        var one = new EqBand(EqBandType.Bell, 1000, 1.0, 6, true);
        Assert.Equal(12.0, EqCurve.ResponseDbAt([one, one], 1000, Sr), 3);
    }

    [Fact]
    public void NoBands_IsAFlatCurve()
    {
        var response = EqCurve.Response([], 64, Sr);
        Assert.All(response, db => Assert.Equal(0.0, db, 9));
    }

    [Fact]
    public void Response_SamplesTheLogAxisEndToEnd()
    {
        // A bell parked at the geometric middle must peak in the middle column.
        var bands = new[] { new EqBand(EqBandType.Bell, 632.45, 6.0, 12, true) };
        var response = EqCurve.Response(bands, 201, Sr);

        var peak = Array.IndexOf(response, response.Max());
        Assert.InRange(peak, 97, 103);
    }

    [Fact]
    public void Response_RefusesFewerThanTwoPoints() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => EqCurve.Response([], 1, Sr));

    // ── the drag verb ────────────────────────────────────────────────────

    [Fact]
    public void Drag_MapsSidewaysToFrequencyAndUpToGain()
    {
        var band = new EqBand(EqBandType.Bell, 1000, 1.0, 0, true);

        // Two thirds along the axis is 2 kHz; a quarter DOWN the plot is +9 dB.
        var dragged = EqCurve.Drag(band, 2.0 / 3.0, 0.25, grabNormalizedY: 0.5);

        Assert.Equal(2000, dragged.FrequencyHz, 1);
        Assert.Equal(9.0, dragged.GainDb, 6);
    }

    [Fact]
    public void Drag_ClampsToThePlotRatherThanWrapping()
    {
        var band = new EqBand(EqBandType.Bell, 1000, 1.0, 0, true);

        var offLeft = EqCurve.Drag(band, -0.4, -0.8, grabNormalizedY: 0.5);
        Assert.Equal(EqLimits.MinFrequencyHz, offLeft.FrequencyHz, 6);
        Assert.Equal(EqLimits.MaxGainDb, offLeft.GainDb, 6);

        var offRight = EqCurve.Drag(band, 1.9, 2.2, grabNormalizedY: 0.5);
        Assert.Equal(EqLimits.MaxFrequencyHz, offRight.FrequencyHz, 6);
        Assert.Equal(-EqLimits.MaxGainDb, offRight.GainDb, 6);
    }

    [Theory]
    [InlineData(EqBandType.HighPass)]
    [InlineData(EqBandType.LowPass)]
    [InlineData(EqBandType.Notch)]
    public void Drag_LeavesGainAtZeroForAShapeThatHasNone(EqBandType type)
    {
        var band = new EqBand(type, 1000, 1.0, 0, true);
        var dragged = EqCurve.Drag(band, 0.5, 0.05, grabNormalizedY: 0.5);

        Assert.Equal(0.0, dragged.GainDb, 9);
        Assert.NotEqual(1000, dragged.FrequencyHz, 0); // it still MOVED sideways
    }

    // ── the gainless band's vertical axis is Q ───────────────────────────
    //
    // Bands 1 and 6 of every preset are a high pass and a low pass. Half of
    // their handle's travel used to do nothing at all, which the owner reported
    // as "6 and 1 can't be moved in height".

    [Theory]
    [InlineData(EqBandType.HighPass)]
    [InlineData(EqBandType.LowPass)]
    [InlineData(EqBandType.Notch)]
    public void Drag_OnAGainlessShape_RaisesQWhenDraggedUpAndLowersItWhenDraggedDown(EqBandType type)
    {
        // Q 2.0 rather than the default 0.707, so a quarter-plot drag in EITHER
        // direction stays inside [MinQ, MaxQ] and the symmetry below is a
        // statement about the mapping instead of about the clamp.
        var band = new EqBand(type, 1000, 2.0, 0, true);

        var up = EqCurve.Drag(band, 0.5, 0.25, grabNormalizedY: 0.5);
        var down = EqCurve.Drag(band, 0.5, 0.75, grabNormalizedY: 0.5);

        Assert.True(up.Q > band.Q, $"dragging up left Q at {up.Q:0.###}");
        Assert.True(down.Q < band.Q, $"dragging down left Q at {down.Q:0.###}");
        // Symmetric on the log axis, which is what makes the gesture feel the
        // same at 0.3 as at 18.
        Assert.Equal(band.Q * band.Q, up.Q * down.Q, 6);
    }

    [Fact]
    public void Drag_OnAGainlessShape_DoesNotMoveQUntilThePointerDoes()
    {
        // The handle is drawn on the 0 dB line whatever its Q, so an ABSOLUTE
        // mapping would snap Q to the axis midpoint the instant it was touched.
        var band = new EqBand(EqBandType.HighPass, 120, 0.9, 0, true);
        var grabbed = EqCurve.Drag(band, EqCurve.FrequencyToNormalized(120), 0.5, grabNormalizedY: 0.5);

        Assert.Equal(0.9, grabbed.Q, 9);
    }

    [Fact]
    public void Drag_OnAGainlessShape_MeasuresTheTravelFromTheGrabAndNotFromTheLiveBand()
    {
        // The control re-runs the verb on every pointer move against the band as
        // it was when the drag STARTED. Feeding the live band back in compounds
        // the multiplier once per move and pins Q at a limit in a few pixels —
        // so a pointer that ends where it started must end at the original Q.
        var origin = new EqBand(EqBandType.LowPass, 8000, 2.0, 0, true);

        var againstTheOrigin = origin;
        var againstTheLiveBand = origin;
        foreach (var y in new[] { 0.45, 0.3, 0.2, 0.35, 0.5 })
        {
            againstTheOrigin = EqCurve.Drag(origin, 0.7, y, grabNormalizedY: 0.5);
            againstTheLiveBand = EqCurve.Drag(againstTheLiveBand, 0.7, y, grabNormalizedY: 0.5);
        }

        Assert.Equal(origin.Q, againstTheOrigin.Q, 9);
        Assert.True(againstTheLiveBand.Q > origin.Q * 4,
            "feeding the LIVE band back in has to compound — if it does not, this test is not " +
            $"measuring anything (it ended at {againstTheLiveBand.Q:0.###} from {origin.Q:0.###})");
    }

    [Fact]
    public void DragQ_CoversTheWholeRangeInOneFullHeightDrag()
    {
        Assert.Equal(EqLimits.MaxQ, EqCurve.DragQ(EqLimits.MinQ, normalizedY: 0.0, grabNormalizedY: 1.0), 6);
        Assert.Equal(EqLimits.MinQ, EqCurve.DragQ(EqLimits.MaxQ, normalizedY: 1.0, grabNormalizedY: 0.0), 6);
        // …and never past it.
        Assert.Equal(EqLimits.MaxQ, EqCurve.DragQ(EqLimits.MaxQ, normalizedY: -3.0, grabNormalizedY: 1.0), 6);
    }

    [Fact]
    public void Drag_SwitchesTheBandOn()
    {
        // The mockup's `onMove` writes `on: true`, and the state it avoids is
        // the one the owner screenshotted: a band reading −10.9 dB over a curve
        // with no bend in it and a badge honestly saying 0/6. Turning it back
        // off is the chip's dot, which keeps whatever the drag left.
        var off = new EqBand(EqBandType.Bell, 1000, 1.0, 0, false);
        Assert.True(EqCurve.Drag(off, 0.4, 0.4, grabNormalizedY: 0.5).Enabled);

        var filter = new EqBand(EqBandType.HighPass, 100, 0.71, 0, false);
        Assert.True(EqCurve.Drag(filter, 0.4, 0.4, grabNormalizedY: 0.5).Enabled);
    }

    [Fact]
    public void NearestHandle_PicksTheBandUnderThePointer()
    {
        List<EqBand> bands =
        [
            new(EqBandType.HighPass, 100, 0.71, 0, true),
            new(EqBandType.Bell, 1000, 1.0, 0, true),
            new(EqBandType.HighShelf, 10000, 0.71, 0, true)
        ];

        Assert.Equal(0, EqCurve.NearestHandle(bands, EqCurve.FrequencyToNormalized(110), 0.5));
        Assert.Equal(1, EqCurve.NearestHandle(bands, EqCurve.FrequencyToNormalized(900), 0.5));
        Assert.Equal(2, EqCurve.NearestHandle(bands, EqCurve.FrequencyToNormalized(14000), 0.5));
    }

    [Fact]
    public void NearestHandle_MeasuresInPlotSpace_SoGainCountsAsMuchAsFrequency()
    {
        // Two bands at the SAME frequency, one boosted to the top of the plot.
        // A pointer near the bottom must grab the low one.
        List<EqBand> bands =
        [
            new(EqBandType.Bell, 1000, 1.0, 16, true),
            new(EqBandType.Bell, 1000, 1.0, -16, true)
        ];

        var x = EqCurve.FrequencyToNormalized(1000);
        Assert.Equal(0, EqCurve.NearestHandle(bands, x, 0.05));
        Assert.Equal(1, EqCurve.NearestHandle(bands, x, 0.95));
    }

    [Fact]
    public void NearestHandle_UsesZeroGainForAShapeWithNone() =>
        // A high pass draws its handle on the 0 dB line whatever its GainDb
        // field happens to hold, so the hit test must agree with the drawing.
        Assert.Equal(0, EqCurve.NearestHandle(
            [new EqBand(EqBandType.HighPass, 1000, 0.71, 18, true)],
            EqCurve.FrequencyToNormalized(1000), 0.5));

    [Fact]
    public void NearestHandle_AnswersMinusOneForNoBands() =>
        Assert.Equal(-1, EqCurve.NearestHandle([], 0.5, 0.5));

    // ── clamping at the model boundary ───────────────────────────────────

    [Fact]
    public void Clamped_HoldsEveryValueToTheEnginesRange()
    {
        var wild = new EqBand(EqBandType.Bell, 99999, 900, 400, true).Clamped();

        Assert.Equal(EqLimits.MaxFrequencyHz, wild.FrequencyHz, 6);
        Assert.Equal(EqLimits.MaxQ, wild.Q, 6);
        Assert.Equal(EqLimits.MaxGainDb, wild.GainDb, 6);
    }

    [Fact]
    public void Clamped_ZeroesTheGainOfAShapeThatHasNone() =>
        Assert.Equal(0.0, new EqBand(EqBandType.LowPass, 5000, 0.71, -9, true).Clamped().GainDb, 9);

    [Theory]
    [InlineData(EqBandType.LowShelf)]
    [InlineData(EqBandType.Bell)]
    [InlineData(EqBandType.HighShelf)]
    public void TheGainBearingShapes_AreExactlyTheThreeThatRespondToGain(EqBandType type)
    {
        Assert.True(EqBand.HasGainFor(type));

        // …and the claim is not decorative: the response really moves.
        var band = new EqBand(type, 1000, 1.0, 9, true);
        Assert.True(Math.Abs(EqCurve.BandResponseDbAt(band, 1000, Sr)) > 1.0);
    }

    [Theory]
    [InlineData(EqBandType.HighPass)]
    [InlineData(EqBandType.LowPass)]
    [InlineData(EqBandType.Notch)]
    public void TheGainlessShapes_IgnoreGainCompletely(EqBandType type)
    {
        Assert.False(EqBand.HasGainFor(type));

        var quiet = new EqBand(type, 1000, 0.9, 0, true);
        var loud = quiet with { GainDb = 18 };
        Assert.Equal(EqCurve.BandResponseDbAt(quiet, 700, Sr), EqCurve.BandResponseDbAt(loud, 700, Sr), 9);
    }
}
