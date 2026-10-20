using DotCharter.Analysis.Tone;

namespace DotCharter.Services.Audio.Dsp.Tones;

/// <summary>
///     Whether a measured span is evidence of a GUITAR at all.
///     <para>
///         <see cref="ToneAudioProfile.IsSilent" /> catches a QUIET span and does
///         nothing for a loud, noisy one — and every term feeding
///         <see cref="ToneAudioProfile.Drive" /> maxes out on noise. Measured:
///         white noise scores crest 4.78 dB, flatness 0.846, harmonic ratio
///         0.050 → <c>Drive</c> <b>0.990</b>. Handed to the mapper unguarded, a
///         fade-out, crowd noise or a badly separated stem is confidently told to
///         build a high-gain tone.
///     </para>
///     <para>
///         That gap was found and reported by the analyzer, which correctly
///         refused to fix it there: the analyzer's job is to MEASURE, and
///         "is this worth building a tone from" is the caller's policy. This is
///         that policy, in one place, testable without audio.
///     </para>
/// </summary>
public static class ToneEvidence
{
    /// <summary>
    ///     Above this the spectrum is closer to noise than to a played note.
    ///     <para>
    ///         The measured gap is enormous, which is what makes the guard safe:
    ///         a HARD-CLIPPED guitar — the most spectrally smeared thing this
    ///         feature must still accept — measured 0.027, while white noise
    ///         measured 0.846. Anything past 0.40 is more than an order of
    ///         magnitude flatter than real distortion and is not a guitar.
    ///     </para>
    /// </summary>
    public const double NoiseFlatness = 0.40;

    /// <summary>
    ///     Below this almost no energy sits on harmonics of any fundamental.
    ///     <para>
    ///         Measured: clipped guitar 0.906, white noise 0.050. A span has to
    ///         fail BOTH this and <see cref="NoiseFlatness" /> to be rejected —
    ///         either alone has a legitimate guitar case (a very smeared chord;
    ///         a frame the pitch detector could not resolve), and rejecting a
    ///         real span is worse than the guard is worth.
    ///     </para>
    /// </summary>
    public const double NoiseHarmonicRatio = 0.25;

    /// <summary>
    ///     True when the span is broadband noise rather than a played instrument.
    ///     Requires BOTH conditions — see the constants for why.
    /// </summary>
    public static bool IsNoiseLike(ToneAudioProfile profile) =>
        !profile.IsSilent &&
        profile.SpectralFlatness > NoiseFlatness &&
        profile.HarmonicRatio < NoiseHarmonicRatio;

    /// <summary>
    ///     Is there anything here worth building a tone from? Silence and noise
    ///     are the two ways the answer is no, and they need different words to
    ///     the user, so callers usually branch on both rather than on this.
    /// </summary>
    public static bool IsUsable(ToneAudioProfile profile) =>
        !profile.IsSilent && !IsNoiseLike(profile);
}
