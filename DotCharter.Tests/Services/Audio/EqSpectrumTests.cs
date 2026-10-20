using DotCharter.Tone.Audio.Equalizer;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The analyser's two shapes: which one is chosen, and what the preview
///     actually looks like.
/// </summary>
/// <remarks>
///     The preview exists because the plot used to draw NOTHING without a live
///     frame, so a stopped transport gave a bare grid — the owner asked for the
///     mockup's behaviour ("when silence a preview spectrogram"). The rule it
///     ships under is that it must be distinguishable from measured audio, which
///     is why <see cref="EqSpectrumKind" /> is a value and not an inference.
/// </remarks>
public sealed class EqSpectrumTests
{
    // ── which spectrum ───────────────────────────────────────────────────

    [Fact]
    public void IsSilent_TreatsAnAbsentOrUnusableFrameAsSilence()
    {
        Assert.True(EqSpectrum.IsSilent(null));
        Assert.True(EqSpectrum.IsSilent([]));
        Assert.True(EqSpectrum.IsSilent([-3f]));
    }

    [Fact]
    public void IsSilent_TreatsAStoppedTransportsFloorAsSilence()
    {
        // A real engine keeps answering while nothing plays; every bin sits on
        // the interface's own noise floor. That state is the whole reason the
        // threshold is above the well's floor rather than at it.
        var floor = new float[64];
        Array.Fill(floor, -120f);
        Assert.True(EqSpectrum.IsSilent(floor));

        var justUnder = new float[64];
        Array.Fill(justUnder, (float)EqSpectrum.SilenceThresholdDb - 0.5f);
        Assert.True(EqSpectrum.IsSilent(justUnder));
    }

    [Fact]
    public void IsSilent_IsFalseAsSoonAsOneBinCarriesSignal()
    {
        var frame = new float[64];
        Array.Fill(frame, -120f);
        frame[20] = -12f;

        Assert.False(EqSpectrum.IsSilent(frame));
    }

    [Fact]
    public void IsSilent_DoesNotReadANaNBinAsALoudOne()
    {
        // Every comparison against NaN is false, so an untested NaN reads as
        // "not silent" and pins the plot on a live frame that draws nothing.
        var frame = new float[64];
        Array.Fill(frame, -120f);
        frame[7] = float.NaN;

        Assert.True(EqSpectrum.IsSilent(frame));
    }

    // ── the level mapping ────────────────────────────────────────────────

    [Fact]
    public void LevelFor_PutsTheFloorAtZeroAndFullScaleAtOne()
    {
        Assert.Equal(0.0, EqSpectrum.LevelFor(EqSpectrum.FloorDb), 9);
        Assert.Equal(1.0, EqSpectrum.LevelFor(0.0), 9);
        Assert.Equal(0.5, EqSpectrum.LevelFor(EqSpectrum.FloorDb / 2.0), 9);

        // Outside the well, clamped rather than allowed off the top or bottom.
        Assert.Equal(0.0, EqSpectrum.LevelFor(-200), 9);
        Assert.Equal(1.0, EqSpectrum.LevelFor(+12), 9);
    }

    [Fact]
    public void Levels_MapsAWholeFrameAndTreatsNaNAsTheFloor()
    {
        var levels = EqSpectrum.Levels([0f, (float)EqSpectrum.FloorDb, float.NaN]);

        Assert.Equal([1.0, 0.0, 0.0], levels);
    }

    // ── the preview shape ────────────────────────────────────────────────

    [Fact]
    public void Preview_IsTheMockupsThreeHumps()
    {
        var preview = EqSpectrum.Preview(EqSpectrum.PreviewPoints);

        // The body hump near 190 Hz is the tallest thing in it, and the top of
        // the axis is the quietest — that ordering IS the picture. A preview
        // whose shape inverted would still be "a spectrum" and would tell the
        // user the opposite thing about their mix.
        var peak = Array.IndexOf(preview, preview.Max());
        var peakHz = EqCurve.NormalizedToFrequency(peak / (double)(preview.Length - 1));
        Assert.InRange(peakHz, 120, 300);

        Assert.True(preview[^1] < preview[peak] * 0.5,
            $"20 kHz sits at {preview[^1]:0.###} against a peak of {preview[peak]:0.###}");
        Assert.All(preview, v => Assert.InRange(v, 0.0, 1.0));
    }

    [Fact]
    public void Preview_IsDeterministicAndKeepsItsShapeAtAnyResolution()
    {
        // Two calls agree — the jitter is a hash of the position, not a random
        // number, so the plot does not shimmer between repaints.
        Assert.Equal(EqSpectrum.Preview(64), EqSpectrum.Preview(64));

        // …and the SHAPE survives a different bin count, which matters because
        // that count is an analyser detail the user has no idea exists. The
        // jitter is seeded on the mockup's own 0..149 scale for this reason; the
        // per-bin values do not resample exactly (the hash is not band-limited)
        // but the picture does.
        foreach (var bins in new[] { 40, 64, 128, EqSpectrum.PreviewPoints, 400 })
        {
            var preview = EqSpectrum.Preview(bins);
            var peak = Array.IndexOf(preview, preview.Max());
            var peakHz = EqCurve.NormalizedToFrequency(peak / (double)(bins - 1));

            Assert.InRange(peakHz, 120, 300);
            Assert.True(preview[^1] < preview[peak] * 0.5, $"{bins} bins: the top of the axis did not fall away");
        }
    }

    [Fact]
    public void Preview_RefusesFewerThanTwoPoints() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => EqSpectrum.Preview(1));
}
