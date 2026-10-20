namespace DotCharter.Analysis.Tone;

/// <summary>
///     What a span of guitar audio SOUNDS like, reduced to the handful of
///     measurements a tone can be built from.
///     <para>
///         The owner's rule for the feature this serves: <b>the tone PLACEMENT is
///         not generated</b> — the charter places a tone change, and the tone is
///         built from the audio between that change and the next one. So this
///         type describes one span, and nothing in it knows about time, charts or
///         gear.
///     </para>
///     <para>
///         Deliberately small. Every field is something a guitarist would
///         recognise as a description of a sound, because the next stage has to
///         turn it into amp and pedal settings a human will read and adjust.
///         Adding a feature nobody can map to a knob just makes the mapping
///         harder to argue about.
///     </para>
///     <para>
///         <b>This is an approximation and must be presented as one.</b> A
///         recorded guitar is already through an amp, a room and a mix bus, and
///         separated stems carry artefacts; no measurement here can recover the
///         rig that made it. The honest claim is "a starting point that lands in
///         the right neighbourhood", never "the tone from the record".
///     </para>
/// </summary>
/// <param name="Rms">Linear RMS of the span, 0..1.</param>
/// <param name="PeakDb">Peak level in dBFS; <c>-120</c> for silence.</param>
/// <param name="CrestFactorDb">
///     Peak minus RMS in dB. A clean, dynamic performance keeps a wide crest
///     (roughly 12–20 dB); heavy distortion squashes it toward 6 dB or less,
///     because clipping is what removes the peaks.
/// </param>
/// <param name="SpectralCentroidHz">
///     The spectrum's centre of mass — the single best proxy for "bright" vs
///     "dark", and what the tone stack's treble/presence should follow.
/// </param>
/// <param name="SpectralFlatness">
///     0 = pure tone, 1 = white noise. Distortion fills the gaps between
///     harmonics, so flatness rises with gain; it also rises with cymbal bleed,
///     which is why the guitar stem is the input and not the mix.
/// </param>
/// <param name="HarmonicRatio">
///     Share of energy sitting on harmonics of the detected fundamental, 0..1.
///     High with a clean note, falling as distortion and noise spread energy
///     between the partials.
/// </param>
/// <param name="LowEnergy">Energy share below 250 Hz, 0..1.</param>
/// <param name="MidEnergy">Energy share 250 Hz–2 kHz, 0..1 — the guitar's body.</param>
/// <param name="HighEnergy">Energy share above 2 kHz, 0..1 — bite and presence.</param>
/// <param name="Sustain">
///     How much level survives between attacks, 0..1: the mean of the span's
///     frame envelope divided by its peak envelope. High gain and compression
///     both raise it, which is exactly the pair a drive pedal models.
/// </param>
public readonly record struct ToneAudioProfile(
    double Rms,
    double PeakDb,
    double CrestFactorDb,
    double SpectralCentroidHz,
    double SpectralFlatness,
    double HarmonicRatio,
    double LowEnergy,
    double MidEnergy,
    double HighEnergy,
    double Sustain)
{
    /// <summary>
    ///     Below this RMS the span carries no usable signal and no tone may be
    ///     inferred from it. A muted stem is the common case — the charter soloed
    ///     the guitar out, or the span sits in a gap — and guessing a tone from
    ///     noise would produce a confident, wrong answer.
    /// </summary>
    public const double SilenceRms = 1e-4;

    /// <summary>Nothing to analyse. See <see cref="SilenceRms" />.</summary>
    public bool IsSilent => Rms < SilenceRms;

    /// <summary>
    ///     A single 0..1 "how driven does this sound", folded from the three
    ///     measurements that actually move with gain: a squashed crest, a flat
    ///     spectrum and energy filling in between the harmonics.
    ///     <para>
    ///         It lives on the PROFILE rather than in the mapper so that the
    ///         number the tone is chosen by is the number the tests pin, and a
    ///         mapper change cannot silently redefine what "driven" means.
    ///     </para>
    /// </summary>
    public double Drive
    {
        get
        {
            if (IsSilent) return 0;

            // 18 dB crest → 0 (clean and dynamic), 6 dB → 1 (clipped flat).
            var squash = Math.Clamp((18.0 - CrestFactorDb) / 12.0, 0, 1);
            var flat = Math.Clamp(SpectralFlatness / 0.35, 0, 1);
            var smeared = Math.Clamp(1.0 - HarmonicRatio, 0, 1);

            return Math.Clamp(0.5 * squash + 0.3 * flat + 0.2 * smeared, 0, 1);
        }
    }
}
