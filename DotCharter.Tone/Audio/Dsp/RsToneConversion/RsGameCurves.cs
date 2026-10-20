// RsGameCurves: the knob -> parameter curves Rocksmith 2014 itself applies, for the gear the converter maps through them — read from the PLAYER'S OWN install at run time (RsGameTables), never stored in the repo.
namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

/// <summary>
///     A Wwise RTPC curve segment's shape (<c>AkCurveInterpolation</c>, the 32-bit
///     value stored with each curve point). Only the shapes whose formula is
///     known are here: Linear by definition; Exp1/Exp3/Log1/Log3 are the
///     documented "Exponential/Logarithmic (Base 1.41 / Base 3)" power curves; Sine
///     and SineRecip are the documented constant-power fade-in / fade-out. The
///     S-curves (3 and 5) are left out on purpose: their exact formula is not
///     published, and a guessed one would be an invented taper.
/// </summary>
internal enum WwiseCurveShape
{
    Log3 = 0,
    Sine = 1,
    Log1 = 2,
    Linear = 4,
    Exp1 = 6,
    SineRecip = 7,
    Exp3 = 8,
    Constant = 9
}

/// <summary>How a curve's stored Y relates to the parameter's unit
/// (<c>AkCurveScaling</c>; wwiser names 2 <c>dB_96_3</c> and 3 <c>Frequency_20_20000</c>).
/// The curve is interpolated in the STORED domain and the
/// result converted back, which is why a scaled curve is stored transformed.</summary>
internal enum WwiseCurveScaling
{
    /// <summary>Stored in the parameter's own unit.</summary>
    None = 0,

    /// <summary>A dB parameter stored as <c>sign(d) * (1 - 10^(-|d|/20))</c>: INFERRED,
    /// fitted to (and exact on) every stored dB endpoint of the banks the converter reads.</summary>
    Db = 2,

    /// <summary>A frequency stored as <c>log10(Hz)</c> (1.30103 = 20 Hz, 4.30103 = 20 kHz).</summary>
    Log = 3
}

/// <summary>
///     One two-point Wwise RTPC curve: knob value in, parameter value (in the
///     parameter's own unit) out. Values outside the knob range clamp to the
///     curve's ends, as Wwise does.
/// </summary>
internal readonly record struct WwiseCurve(
    float X0, float StoredY0, float X1, float StoredY1, WwiseCurveShape Shape, WwiseCurveScaling Scaling)
{
    /// <summary>A curve stored in the parameter's own unit.</summary>
    public static WwiseCurve Plain(float x0, float y0, float x1, float y1, WwiseCurveShape shape)
        => new(x0, y0, x1, y1, shape, WwiseCurveScaling.None);

    /// <summary>A frequency curve (log10-scaled), given in Hz.</summary>
    public static WwiseCurve LogHz(float x0, float hz0, float x1, float hz1, WwiseCurveShape shape)
        => new(x0, MathF.Log10(hz0), x1, MathF.Log10(hz1), shape, WwiseCurveScaling.Log);

    /// <summary>A gain curve (dB-scaled), given in dB.</summary>
    public static WwiseCurve Db(float x0, float db0, float x1, float db1, WwiseCurveShape shape)
        => new(x0, ToDbStored(db0), x1, ToDbStored(db1), shape, WwiseCurveScaling.Db);

    /// <summary>The parameter value for knob value <paramref name="x" />.</summary>
    public float Evaluate(float x)
    {
        var t = X1 > X0 ? Math.Clamp((x - X0) / (X1 - X0), 0f, 1f) : 0f;
        var stored = StoredY0 + (StoredY1 - StoredY0) * Fraction(Shape, t);
        return Scaling switch
        {
            WwiseCurveScaling.Log => MathF.Pow(10f, stored),
            WwiseCurveScaling.Db => FromDbStored(stored),
            _ => stored
        };
    }

    /// <summary>The shape's normalised fraction at <paramref name="t" /> in [0, 1].
    /// A descending curve uses the same fraction (y0 + (y1 - y0) f(t)).</summary>
    internal static float Fraction(WwiseCurveShape shape, float t) => shape switch
    {
        WwiseCurveShape.Linear => t,
        WwiseCurveShape.Exp1 => MathF.Pow(t, 1.41f),
        WwiseCurveShape.Exp3 => t * t * t,
        WwiseCurveShape.Log1 => 1f - MathF.Pow(1f - t, 1.41f),
        WwiseCurveShape.Log3 => 1f - (1f - t) * (1f - t) * (1f - t),
        WwiseCurveShape.Sine => MathF.Sin(t * MathF.PI / 2f),
        WwiseCurveShape.SineRecip => 1f - MathF.Cos(t * MathF.PI / 2f),
        WwiseCurveShape.Constant => t >= 1f ? 1f : 0f,
        _ => t
    };

    internal static float ToDbStored(float db) => MathF.Sign(db) * (1f - MathF.Pow(10f, -MathF.Abs(db) / 20f));

    internal static float FromDbStored(float stored)
        => MathF.Sign(stored) * -20f * MathF.Log10(1f - MathF.Min(MathF.Abs(stored), 0.999999f));
}


/// <summary><c>Rack_StudioEQ</c>: a Guitar Distortion with its distortion off, used as a
/// six-band EQ. Bass Freq. = pre band 1's frequency (param 2), Low-Mid = pre band 2
/// (12, Q 13), High-Mid = pre band 3 (22, Q 23), Treble = post band 3 (52). The Q curve
/// is Low-Mid's (both Q knobs are the same Wide/Narrow switch).</summary>
internal sealed record RsStudioEqCurves(
    WwiseCurve BassFreqHz, WwiseCurve LoMidFreqHz, WwiseCurve HiMidFreqHz, WwiseCurve TrebleFreqHz, WwiseCurve Q);

/// <summary><c>Pedal_Compression</c>: the Wwise Compressor (params 0 threshold dB,
/// 1 ratio, 2 attack s, 3 release s, 4 output dB). Compression drives 0, 1 and 4;
/// attack and release are given here in ms.</summary>
internal sealed record RsCompressionCurves(
    WwiseCurve ThresholdDb, WwiseCurve Ratio, WwiseCurve OutputGainDb, WwiseCurve AttackMs, WwiseCurve ReleaseMs);

/// <summary><c>Pedal_LoFiFilter</c>: "Lo Cut" (key <c>Mix</c>) is the low-cut corner,
/// "Hi Cut" (key <c>FilterType</c>) the high-cut corner, both Hz.</summary>
internal sealed record RsLoFiCurves(WwiseCurve LoCutHz, WwiseCurve HiCutHz);

/// <summary><c>Pedal_BassEmulator</c>: the Wwise Harmonizer's voice filter, Body its
/// gain (param 10, dB) and Tone its frequency (param 11, Hz).</summary>
internal sealed record RsBassEmulatorCurves(WwiseCurve BodyDb, WwiseCurve ToneHz);

/// <summary><c>Pedal_BitCruncher</c>: a McDSP FutzBox. "Filter" (key <c>Attack</c>,
/// param 33) the Lo-Fi filter %, "Rectify" (key <c>Sens</c>, param 4) the distortion's
/// rectify %, Mix (param 42) the balance %; and the block's input / output gain (dB)
/// and distortion Amount / Intensity (%), which no knob moves.</summary>
internal sealed record RsBitCruncherCurves(
    WwiseCurve FilterPct, WwiseCurve RectifyPct, WwiseCurve MixPct,
    float InputDb, float OutputDb, float DrivePct, float IntensityPct);

/// <summary><c>Bass_Pedal_BassOverdrive</c>: Gain the Guitar Distortion's drive %
/// (param 61), Blend its wet % (65) and output dB (64), Tone its post-EQ band 5's
/// gain (51), dB.</summary>
internal sealed record RsBassOverdriveCurves(WwiseCurve DrivePct, WwiseCurve WetPct, WwiseCurve OutputDb, WwiseCurve TrebleDb);

/// <summary><c>Pedal_AcousticEmulator</c>: Mid-range the six-band EQ's band-0 gain
/// (param 1, dB), Mid Frequency its frequency (param 2, Hz).</summary>
internal sealed record RsAcousticCurves(WwiseCurve MidGainDb, WwiseCurve MidFreqHz);

/// <summary>
///     Every curve set the converter reads, each null when the player's install did not
///     yield it (no install, no key file, a bank this reader cannot decode). A null set
///     is not an error: that gear keeps the converter's generic, label-reading mapping.
/// </summary>
internal sealed record RsGameCurveSet(
    RsStudioEqCurves? StudioEq,
    RsCompressionCurves? Compression,
    RsLoFiCurves? LoFi,
    RsBassEmulatorCurves? BassEmulator,
    RsBitCruncherCurves? BitCruncher,
    RsBassOverdriveCurves? BassOverdrive,
    RsAcousticCurves? Acoustic,
    IReadOnlyDictionary<string, RsFlangerChorus> Choruses,
    RsMbCompModel? MbComp = null)
{
    /// <summary>No game curves at all.</summary>
    public static RsGameCurveSet Empty { get; } =
        new(null, null, null, null, null, null, null, new Dictionary<string, RsFlangerChorus>(StringComparer.Ordinal));

    /// <summary>Whether no set at all was read.</summary>
    public bool IsEmpty => StudioEq is null && Compression is null && LoFi is null && BassEmulator is null &&
                           BitCruncher is null && BassOverdrive is null && Acoustic is null && Choruses.Count == 0 &&
                           MbComp is null;
}

/// <summary>
///     The gear this converter maps through the GAME'S OWN knob curves rather than a
///     reading of the knob label, and the converter's own constants around them.
///     <para>
///         <b>Source.</b> Every gear piece names a Wwise soundbank in its manifest
///         (the gear archive: <c>manifests/gears/gear_*.json</c>, "SoundBank"), and
///         that bank (<c>audio/windows/*.bnk</c>, Wwise bank version 91) holds the
///         effect plug-in, its base parameter block and one RTPC curve per knob — the
///         knob's RTPC is the FNV-1 hash of its lower-cased <c>Key</c>. The curves are
///         read from the PLAYER'S OWN install at run time (<see cref="RsGameTables" />,
///         built by the plugin's game-table builder); no number from a game file is
///         in the repository. <see cref="Current" /> is empty until they are loaded,
///         and stays empty without an install.
///     </para>
///     <para>
///         <b>Curve shapes</b> (<see cref="WwiseCurveShape" />): the enum is the public
///         SDK's <c>AkCurveInterpolation</c> (audiokinetic/WwiseIncludes, <c>AkTypes.h</c>);
///         the power and sine formulas match the published reproduction (dvatioff,
///         "Wwise RTPC curve reproduction", April 2024). The plug-in parameter layouts are
///         bnnm/wwiser's (<c>wparser_plg.py</c>, <c>wdefs.py</c>). <b>Inferred, not
///         published:</b> the <see cref="WwiseCurveScaling.Db" /> transform and that a
///         scaled curve is interpolated in its stored domain.
///     </para>
/// </summary>
internal static class RsGameCurves
{
    public const string StudioEqKey = "Rack_StudioEQ";
    public const string CompressionKey = "Pedal_Compression";
    public const string LoFiFilterKey = "Pedal_LoFiFilter";
    public const string BassEmulatorKey = "Pedal_BassEmulator";
    public const string BitCruncherKey = "Pedal_BitCruncher";
    public const string BassOverdriveKey = "Bass_Pedal_BassOverdrive";
    public const string AcousticEmulatorKey = "Pedal_AcousticEmulator";

    /// <summary>The MB Comp: a meter-driven chain, fitted onto a broadband compressor
    /// (<see cref="RsMbCompModel" />).</summary>
    public const string MbCompKey = "Bass_Pedal_MBComp";

    /// <summary>The Bass Emulator's harmonizer voice: an octave down, the pedal's whole
    /// identity (the converter's choice, not a bank read).</summary>
    public const float BassEmulatorSemitones = -12f;

    /// <summary>The Bass Emulator's voice-filter Q: 1, a neutral bell (the converter's
    /// choice; the bank's Q is not read).</summary>
    public const float BassEmulatorFilterQ = 1f;

    /// <summary>
    ///     How much hotter the game's chain sees the rig's guitar, dB:
    ///     <c>dcdsp_rsamp.h</c>'s <c>kInputDb</c> (INFERRED there from the designers'
    ///     own level-compensation curves). A pedal whose game stage clips or
    ///     quantizes against an ABSOLUTE level — a FutzBox's bit depth, a Guitar
    ///     Distortion's ceiling — runs in that frame: in by this much, out by as much.
    /// </summary>
    public const float GameLevelDb = 18f;

    /// <summary>The Overdrive transfer function's ceiling (dcdsp_rsamp.h's fit of
    /// Audiokinetic's published figure).</summary>
    public const float BassOverdriveShaperCeiling = 0.5f;

    /// <summary>The Wwise Flanger choruses the converter maps through their banks. The
    /// Enbiggenator (a Wwise Stereo Delay), the Omni-Mod (a Flanger AND a Tremolo) and
    /// Send In The Clones (its knobs drive the delay and the rate) are not plain
    /// Flanger choruses and keep the generic mapping.</summary>
    public static readonly string[] ChorusKeys =
        ["Pedal_Chorus", "Pedal_Chorus20", "Pedal_DigitalChorus", "Pedal_VintageChorus", "Rack_StudioChorus", "Bass_Pedal_BassChorus"];

    /// <summary>The curves of the tables in use (<see cref="RsGameTables.Current" />).</summary>
    public static RsGameCurveSet Current => RsGameTables.Current.Curves;

    /// <summary>
    ///     "Downsample" (key <c>Pedal_BitCruncher_Release</c>) 0..32, one step per knob
    ///     value. INFERRED: the decimation factor is that plus one, the only reading
    ///     under which the top (a factor of 33 at the 44.1 kHz FutzBox's own reference)
    ///     is the "as low as 1,336 Hz" Audiokinetic publishes for the Lo-Fi section —
    ///     and 0 is no decimation at all.
    /// </summary>
    public static int BitCruncherDownsampleFactor(float knob) => (int)MathF.Round(Math.Clamp(knob, 0f, 32f)) + 1;

    /// <summary>
    ///     "Bit Depth" (key <c>Pedal_BitCruncher_FilterType</c>) 2..8: the bits that
    ///     play are taken as the KNOB's label (the manual says only "selects bit
    ///     depth"). INFERRED, not published.
    /// </summary>
    public static int BitCruncherBits(float knob) => (int)MathF.Round(Math.Clamp(knob, 2f, 8f));
}
