namespace DotCharter.Tone.Audio.Equalizer;

/// <summary>
///     The shape of one EQ band.
///     <para>
///         The values are the native ABI's <c>dca_eq_band_type</c> (v29) —
///         the audio service casts straight across, so these
///         may be appended to but never renumbered.
///     </para>
/// </summary>
public enum EqBandType
{
    /// <summary>2nd-order RBJ high pass: 12 dB/oct, no gain.</summary>
    HighPass = 0,

    /// <summary>Low shelf. Has gain.</summary>
    LowShelf = 1,

    /// <summary>Peaking / bell. Has gain.</summary>
    Bell = 2,

    /// <summary>High shelf. Has gain.</summary>
    HighShelf = 3,

    /// <summary>2nd-order RBJ low pass: 12 dB/oct, no gain.</summary>
    LowPass = 4,

    /// <summary>Notch. Full cut at the centre frequency, no gain.</summary>
    Notch = 5
}

/// <summary>Which bus the six bands are applied to. Mirrors <c>dca_eq_route</c>.</summary>
public enum EqRoute
{
    /// <summary>The mixed playback output only.</summary>
    Song = 0,

    /// <summary>Every seat's post-rig guitar signal only.</summary>
    GuitarIn = 1,

    /// <summary>Both.</summary>
    Both = 2
}

/// <summary>
///     One band of the inspector EQ, as the ENGINE holds it.
///     <para>
///         Immutable on purpose: <see cref="EqSection" /> is the mutable thing,
///         and every write goes through it so the engine, the "n/6" badge and
///         the drawn curve can never be reading three different band lists.
///         That is the fault this whole type exists to prevent — a display
///         model that drifts from the DSP.
///     </para>
/// </summary>
/// <param name="Type">The band shape.</param>
/// <param name="FrequencyHz">Centre / corner frequency, clamped to
///     [<see cref="EqLimits.MinFrequencyHz" />, <see cref="EqLimits.MaxFrequencyHz" />].</param>
/// <param name="Q">Resonance, clamped to [<see cref="EqLimits.MinQ" />, <see cref="EqLimits.MaxQ" />].</param>
/// <param name="GainDb">Gain in dB. Ignored by the shapes <see cref="HasGain" /> is false for.</param>
/// <param name="Enabled">Whether the user has this band switched on.</param>
public readonly record struct EqBand(
    EqBandType Type,
    double FrequencyHz,
    double Q,
    double GainDb,
    bool Enabled)
{
    /// <summary>Whether this band's SHAPE responds to gain at all. A high pass,
    /// low pass or notch does not — which is why the mockup's gain row reads
    /// "no gain on this type" and shows the slope instead.</summary>
    public bool HasGain => HasGainFor(Type);

    /// <summary>The gain-bearing shapes. Static so a row can ask about a type it
    /// is offering, not only about the band it holds.</summary>
    public static bool HasGainFor(EqBandType type) =>
        type is EqBandType.LowShelf or EqBandType.Bell or EqBandType.HighShelf;

    /// <summary>This band with every value forced inside the engine's range. The
    /// only constructor path anything should use — a band built by hand can carry
    /// a frequency the native side will clamp anyway, and then the drawn curve
    /// and the audible one disagree.</summary>
    public EqBand Clamped() => new(
        Type,
        Math.Clamp(FrequencyHz, EqLimits.MinFrequencyHz, EqLimits.MaxFrequencyHz),
        Math.Clamp(Q, EqLimits.MinQ, EqLimits.MaxQ),
        HasGainFor(Type) ? Math.Clamp(GainDb, -EqLimits.MaxGainDb, EqLimits.MaxGainDb) : 0.0,
        Enabled);
}

/// <summary>
///     The ranges every EQ value is held to, in ONE place.
///     <para>
///         These are duplicated in the native engine (<c>AudioEngine::setEqBand</c>
///         clamps identically) because the ABI is a boundary and a boundary that
///         trusts its caller is not a boundary. No test can assert an agreement
///         across two languages, so the C++ side carries a comment naming this
///         file — change either and change both.
///     </para>
/// </summary>
public static class EqLimits
{
    /// <summary>The bottom of the plot, and the lowest a band may be tuned.</summary>
    public const double MinFrequencyHz = 20.0;

    /// <summary>The top of the plot, and the highest a band may be tuned.</summary>
    public const double MaxFrequencyHz = 20000.0;

    /// <summary>Widest bell / gentlest shelf slope.</summary>
    public const double MinQ = 0.3;

    /// <summary>Narrowest bell — enough for a 50 Hz hum notch.</summary>
    public const double MaxQ = 18.0;

    /// <summary>
    ///     The neutral Q — Butterworth, the value a band goes back to.
    ///     <para>
    ///         Named because three separate things have to agree on it:
    ///         <c>EqSection.ResetBand</c>, the band editor's RESET button and the
    ///         double-click-to-default on the Q slider. Three literals is how the
    ///         reset button and the slider come to disagree about what "default"
    ///         means.
    ///     </para>
    /// </summary>
    public const double DefaultQ = 0.707;

    /// <summary>Gain travel in either direction, and the half-height of the plot.</summary>
    public const double MaxGainDb = 18.0;

    /// <summary>Output trim travel in either direction.</summary>
    public const double MaxTrimDb = 12.0;

    /// <summary>How many bands the section has. Fixed by the ABI
    /// (<c>DCA_EQ_BAND_COUNT</c>) — the badge reads "n/6" because of this.</summary>
    public const int BandCount = 6;

    /// <summary>The slope every one of these 2nd-order RBJ sections rolls off at.
    /// Printed by the band editor for the shapes that have no gain, and it is a
    /// FACT about the filter rather than a label: a biquad high pass is 12 dB/oct.</summary>
    public const int SlopeDbPerOctave = 12;
}
