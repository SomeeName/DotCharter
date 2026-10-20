using DotCharter.Analysis.Tone;
using DotCharter.Services.Audio.Dsp.Tones;

namespace DotCharter.Tests.Audio.Dsp.Tones;

/// <summary>
///     Whether a measured span is evidence of a guitar at all.
///     <para>
///         <c>ToneAudioProfile.IsSilent</c> catches a QUIET span and does nothing
///         for a loud, noisy one — and every term feeding <c>Drive</c> maxes out
///         on noise. The analyzer measured white noise at crest 4.78 dB, flatness
///         0.846, harmonic ratio 0.050, giving <c>Drive</c> <b>0.990</b>: handed
///         to the mapper unguarded, a fade-out, crowd noise or a badly separated
///         stem is confidently told to build a high-gain tone.
///     </para>
///     <para>
///         The analyzer reported that gap and correctly refused to close it
///         there — measuring is its job, and "is this worth building a tone from"
///         is policy. These pin the policy.
///     </para>
/// </summary>
public sealed class ToneEvidenceTests
{
    /// <summary>Measured by the analyzer's own calibration run on white noise.</summary>
    private static ToneAudioProfile WhiteNoise() => new(
        Rms: 0.30, PeakDb: -3.0, CrestFactorDb: 4.78, SpectralCentroidHz: 5200,
        SpectralFlatness: 0.846, HarmonicRatio: 0.050,
        LowEnergy: 0.01, MidEnergy: 0.08, HighEnergy: 0.91, Sustain: 0.989);

    /// <summary>Measured on a HARD-CLIPPED pluck — the most spectrally smeared
    /// thing this feature must still accept.</summary>
    private static ToneAudioProfile ClippedGuitar() => new(
        Rms: 0.25, PeakDb: -2.0, CrestFactorDb: 6.03, SpectralCentroidHz: 819,
        SpectralFlatness: 0.0269, HarmonicRatio: 0.906,
        LowEnergy: 0.91, MidEnergy: 0.09, HighEnergy: 0.00, Sustain: 0.347);

    private static ToneAudioProfile CleanGuitar() => new(
        Rms: 0.12, PeakDb: -6.0, CrestFactorDb: 16.50, SpectralCentroidHz: 195,
        SpectralFlatness: 0.0000, HarmonicRatio: 0.978,
        LowEnergy: 0.92, MidEnergy: 0.08, HighEnergy: 0.00, Sustain: 0.137);

    [Fact]
    public void WhiteNoiseIsRejected() => Assert.True(ToneEvidence.IsNoiseLike(WhiteNoise()));

    /// <summary>
    ///     The one that matters: a heavily distorted guitar is NOT noise. The
    ///     guard exists to stop a false high-gain tone, and it would be worthless
    ///     if it rejected the real high-gain case.
    /// </summary>
    [Fact]
    public void AHardClippedGuitarIsStillAGuitar()
    {
        Assert.False(ToneEvidence.IsNoiseLike(ClippedGuitar()));
        Assert.True(ToneEvidence.IsUsable(ClippedGuitar()));
    }

    [Fact]
    public void ACleanGuitarIsUsable() => Assert.True(ToneEvidence.IsUsable(CleanGuitar()));

    /// <summary>
    ///     The measured margin, asserted so a future tweak cannot quietly close
    ///     it: real distortion is more than an order of magnitude less flat than
    ///     noise, which is what makes a single threshold safe here.
    /// </summary>
    [Fact]
    public void TheThresholdSitsInAVeryWideMeasuredGap()
    {
        Assert.True(ClippedGuitar().SpectralFlatness < ToneEvidence.NoiseFlatness / 10,
            "distorted guitar is no longer an order of magnitude below the noise threshold");
        Assert.True(WhiteNoise().SpectralFlatness > ToneEvidence.NoiseFlatness * 2,
            "white noise is no longer comfortably above the noise threshold");
    }

    /// <summary>
    ///     BOTH conditions are required. Either alone has a legitimate guitar
    ///     case — a very smeared chord, or a frame whose pitch could not be
    ///     resolved — and rejecting a real span is worse than the guard is worth.
    /// </summary>
    [Fact]
    public void FlatButHarmonicIsNotRejected()
    {
        var smeared = ClippedGuitar() with { SpectralFlatness = 0.55 };

        Assert.True(smeared.SpectralFlatness > ToneEvidence.NoiseFlatness);
        Assert.False(ToneEvidence.IsNoiseLike(smeared));
    }

    [Fact]
    public void InharmonicButNotFlatIsNotRejected()
    {
        var unresolved = ClippedGuitar() with { HarmonicRatio = 0.05 };

        Assert.True(unresolved.HarmonicRatio < ToneEvidence.NoiseHarmonicRatio);
        Assert.False(ToneEvidence.IsNoiseLike(unresolved));
    }

    /// <summary>Silence is its own answer and needs its own words to the user,
    /// so it must not also be reported as noise.</summary>
    [Fact]
    public void SilenceIsNotReportedAsNoise()
    {
        var silence = new ToneAudioProfile(
            Rms: 0, PeakDb: -120, CrestFactorDb: 0, SpectralCentroidHz: 0,
            SpectralFlatness: 1.0, HarmonicRatio: 0.0,
            LowEnergy: 0, MidEnergy: 0, HighEnergy: 0, Sustain: 0);

        Assert.True(silence.IsSilent);
        Assert.False(ToneEvidence.IsNoiseLike(silence));
        Assert.False(ToneEvidence.IsUsable(silence));
    }

    /// <summary>
    ///     The reason this guard exists, stated as a test: noise scores as
    ///     maximally driven, so without it the mapper receives the strongest
    ///     possible "build a high-gain amp" signal from a span with no guitar.
    /// </summary>
    [Fact]
    public void NoiseWouldOtherwiseReadAsMaximumDrive()
    {
        Assert.True(WhiteNoise().Drive > 0.9,
            "if noise no longer scores as driven, re-derive whether this guard is still needed");
        Assert.True(ToneEvidence.IsNoiseLike(WhiteNoise()));
    }
}
