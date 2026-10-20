namespace DotCharter.Tone.UI.Controls.Dsp;

/// <summary>
/// Pure display math for the pedalboard's rig tuner panel: chromatic
/// nearest-note mapping (A4 = 440 Hz by default, MIDI 69 — every mapping member
/// has an overload taking the player's own concert pitch, see
/// <see cref="ClampReference"/>), cents offsets, the needle
/// fraction and the strobe-wheel phase advance. The DSP itself lives in the
/// managed tuner suite (<c>TunerPitchEngine</c>/<c>VirtualStrobe</c>/
/// <c>TunerDisplaySmoother</c> in DotCharter.Services); this class only turns
/// their numbers into visuals. No Avalonia types — unit-tested in
/// <c>RigTunerMathTests</c>.
/// </summary>
public static class RigTunerMath
{
    /// <summary>Cents at the needle's end stops (±).</summary>
    public const double MaxDisplayCents = 50.0;

    /// <summary>Strobe-wheel speed: full turns per second at 1 cent off.
    /// (25 cents off ⇒ one revolution per second — reads like a real strobe:
    /// stationary in tune, crawling near it, spinning far off.)</summary>
    public const double WheelTurnsPerCentSecond = 1.0 / 25.0;

    /// <summary>Standard concert pitch: A4 = 440 Hz (MIDI 69).</summary>
    public const double DefaultReferenceHz = 440.0;

    /// <summary>
    ///     Reference-pitch bounds. 415 is a semitone below A440 (baroque A415 is
    ///     the classic low reference) and 466 a semitone above (the highest
    ///     ensemble pitch anyone tunes to); outside that a "reference" is a
    ///     transposition, and the nearest-note mapping would name the wrong note.
    /// </summary>
    public const double MinReferenceHz = 415.0;

    /// <inheritdoc cref="MinReferenceHz" />
    public const double MaxReferenceHz = 466.0;

    /// <summary>Clamps a reference pitch into the supported range; a NaN or an
    /// infinity reads as "unset" and comes back as A440, because a needle with no
    /// reference is worse than a standard one.</summary>
    public static double ClampReference(double referenceHz)
        => double.IsFinite(referenceHz)
            ? Math.Clamp(referenceHz, MinReferenceHz, MaxReferenceHz)
            : DefaultReferenceHz;

    /// <summary>Nearest MIDI note for a frequency (A4 = 440 = MIDI 69).
    /// Non-positive frequencies yield -1 (no note).</summary>
    public static int NearestMidi(double frequencyHz) => NearestMidi(frequencyHz, DefaultReferenceHz);

    /// <summary>Nearest MIDI note against a chosen concert pitch — A4 =
    /// <paramref name="referenceHz"/> (MIDI 69). Non-positive frequencies yield
    /// -1; the reference is clamped, so no caller can move the note grid outside
    /// the supported range.</summary>
    public static int NearestMidi(double frequencyHz, double referenceHz)
    {
        if (frequencyHz <= 0) return -1;
        return (int)Math.Round(69.0 + 12.0 * Math.Log2(frequencyHz / ClampReference(referenceHz)));
    }

    /// <summary>Equal-tempered frequency of a MIDI note.</summary>
    public static double FrequencyOfMidi(int midi) => FrequencyOfMidi(midi, DefaultReferenceHz);

    /// <summary>Equal-tempered frequency of a MIDI note against a chosen concert
    /// pitch.</summary>
    public static double FrequencyOfMidi(int midi, double referenceHz)
        => ClampReference(referenceHz) * Math.Pow(2.0, (midi - 69) / 12.0);

    /// <summary>Signed cents of <paramref name="frequencyHz"/> against the
    /// MIDI note's target (negative = flat).</summary>
    public static double CentsOff(double frequencyHz, int midi)
        => CentsOff(frequencyHz, midi, DefaultReferenceHz);

    /// <summary>Signed cents against the MIDI note's target at a chosen concert
    /// pitch — the whole point of the reference: at A442, 442 Hz IS in tune.</summary>
    public static double CentsOff(double frequencyHz, int midi, double referenceHz)
        => 1200.0 * Math.Log2(frequencyHz / FrequencyOfMidi(midi, referenceHz));

    /// <summary>Needle position as a 0..1 fraction of the bar (0.5 = in
    /// tune), clamped at the ±<see cref="MaxDisplayCents"/> end stops.</summary>
    public static double NeedleFraction(double cents)
    {
        var clamped = Math.Clamp(cents, -MaxDisplayCents, MaxDisplayCents);
        return 0.5 + clamped / (2.0 * MaxDisplayCents);
    }

    /// <summary>
    /// Advances the strobe-wheel phase (0..1 wrap) by the beat rate the cents
    /// offset implies: stationary exactly in tune, direction = sharp/flat.
    /// </summary>
    public static double AdvanceWheel(double phase, double cents, double deltaMs)
    {
        var next = phase + cents * WheelTurnsPerCentSecond * (deltaMs / 1000.0);
        next %= 1.0;
        if (next < 0) next += 1.0;
        return next;
    }
}
