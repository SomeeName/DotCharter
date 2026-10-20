using DotCharter.Tone.Audio.Tuner;

namespace DotCharter.Tests.Services.Audio.Tuner;

/// <summary>
///     The two low-string questions the existing tuner tests do not ask, and the
///     "in tune" lamp's hysteresis.
///     <para>
///     <b>Missing fundamental.</b> Every other detector test feeds a tone whose
///     fundamental is the strongest partial. A real low string does not arrive
///     that way: a magnetic pickup, a small speaker and any amount of gain all
///     attenuate the fundamental, and on a 7-string low B (61.7 Hz), a drop-A
///     F#1 (46.2 Hz) or a bass D1 (36.7 Hz) it can be the QUIETEST partial in
///     the spectrum — or absent. A spectral-peak detector reports the octave
///     there; an autocorrelation one must not, because the WAVEFORM is still
///     periodic at f0. This repo has been burned by DSP that passes synthetic
///     tests and fails on real audio, so the synthetic here is shaped like the
///     real failure mode rather than like a textbook tone.
///     </para>
/// </summary>
public sealed class LowStringPitchDetectionTests
{
    private const int Sr = 44100;

    /// <summary>Twelve partials at 1/k, with the FUNDAMENTAL's gain overridden —
    /// 1.0 is a textbook tone, 0.15 a pickup-and-amp low string, 0.0 a genuinely
    /// missing fundamental.</summary>
    private static float[] PickupTone(double f0, double fundamentalGain, int length, double amplitude = 0.25)
    {
        var samples = new double[length];
        var step = 2.0 * Math.PI * f0 / Sr;
        for (var k = 1; k <= 12; k++)
        {
            var gain = k == 1 ? fundamentalGain : 1.0 / k;
            for (var n = 0; n < length; n++) samples[n] += gain * Math.Sin(step * k * n);
        }

        double peak = 0;
        for (var n = 0; n < length; n++) peak = Math.Max(peak, Math.Abs(samples[n]));
        var result = new float[length];
        var scale = peak > 0 ? amplitude / peak : 0;
        for (var n = 0; n < length; n++) result[n] = (float)(samples[n] * scale);
        return result;
    }

    [Theory]
    [InlineData(61.74, 0.15, "7-string low B through a pickup")]
    [InlineData(61.74, 0.00, "7-string low B, fundamental gone entirely")]
    [InlineData(46.25, 0.15, "drop-A F#1 through a pickup")]
    [InlineData(46.25, 0.00, "drop-A F#1, fundamental gone entirely")]
    [InlineData(36.71, 0.15, "5-string bass low D through a pickup")]
    public void A_low_string_whose_fundamental_is_gone_is_still_detected_at_its_fundamental(
        double f0, double fundamentalGain, string why)
    {
        var engine = new TunerPitchEngine(Sr);

        var reading = engine.Detect(PickupTone(f0, fundamentalGain, engine.WindowSize));

        Assert.True(reading.HasPitch, $"{why}: no pitch at all");
        var cents = Math.Abs(PitchMathProbe.Cents(reading.FrequencyHz, f0));
        Assert.True(cents < 5.0,
            $"{why}: reported {reading.FrequencyHz:F2} Hz for a {f0:F2} Hz string " +
            $"({cents:F0} cents off — {(cents > 1150 ? "that is the OCTAVE" : "off")})");
    }

    /// <summary>Both engines must survive it on their own — the composite falls
    /// back MPM→YIN, so a green composite can hide a broken half.</summary>
    [Theory]
    [InlineData(46.25)]
    [InlineData(61.74)]
    public void Both_detectors_survive_the_missing_fundamental_independently(double f0)
    {
        var tone = PickupTone(f0, 0.0, 4096);

        var mpm = new MpmPitchDetector(Sr).Detect(tone);
        var yin = new YinPitchDetector(Sr).Detect(tone);

        Assert.True(Math.Abs(PitchMathProbe.Cents(mpm.FrequencyHz, f0)) < 5.0, $"MPM: {mpm.FrequencyHz:F2} Hz");
        Assert.True(Math.Abs(PitchMathProbe.Cents(yin.FrequencyHz, f0)) < 5.0, $"YIN: {yin.FrequencyHz:F2} Hz");
    }

    private static class PitchMathProbe
    {
        public static double Cents(double hz, double referenceHz)
            => hz <= 0 ? double.PositiveInfinity : 1200.0 * Math.Log2(hz / referenceHz);
    }
}

/// <summary>
///     The "in tune" lamp's hysteresis. The band used to have ONE threshold, so a
///     string parked ON it could never latch: each reading that stepped a fraction
///     of a cent outside cleared the band-entry stamp and restarted the stability
///     hold from zero, and <c>InTune</c> never came true — the lamp strobed at
///     exactly the tuning the player was trying to hold.
/// </summary>
public sealed class TunerDisplayHysteresisTests
{
    private static TunerDisplaySmoother Smoother() =>
        // Median 1 + alpha 1 = no damping, so the raw sequence IS the smoothed
        // one and the test is about the BAND, not about the filter.
        new(medianWindow: 1) { SmoothingAlpha = 1.0, InTuneCents = 5.0, StabilityHoldMs = 750.0 };

    [Fact]
    public void A_needle_dithering_across_the_band_edge_still_latches_in_tune()
    {
        var smoother = Smoother();
        var now = 0.0;
        SmoothedTunerReading last = default;

        // A string tuned dead on the +5 cent edge, with sub-cent detector jitter.
        foreach (var cents in new[] { 4.9, 5.1, 4.8, 5.2, 4.9, 5.1, 4.9, 5.2, 5.0, 4.9 })
        {
            now += 250.0;
            last = smoother.Process(cents, now);
        }

        Assert.True(last.WithinBand, "a needle that entered the band must not fall out over 0.2 cents");
        Assert.True(last.InTune,
            "the stability hold restarted on every dither, so the lamp could never latch");
    }

    [Fact]
    public void The_release_threshold_still_lets_go_of_a_genuinely_detuned_string()
    {
        var smoother = Smoother();
        smoother.Process(0.0, 250);
        Assert.True(smoother.Process(0.0, 1250).InTune);

        var pegTurned = smoother.Process(12.0, 1500);

        Assert.False(pegTurned.WithinBand, "12 cents is past the release threshold");
        Assert.False(pegTurned.InTune);
    }

    [Fact]
    public void Entering_the_band_still_takes_the_tight_threshold()
    {
        var smoother = Smoother();

        // 6 cents is inside the RELEASE band (7.5) but outside the ENTRY band —
        // a Schmitt trigger that latched here would just be a looser tuner.
        Assert.False(smoother.Process(6.0, 250).WithinBand);
        Assert.False(smoother.Process(6.0, 1250).InTune);
    }
}
