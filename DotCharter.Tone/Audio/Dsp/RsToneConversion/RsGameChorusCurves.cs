namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

/// <summary>
///     One Rocksmith 2014 chorus gear as its soundbank builds it: one or two Audiokinetic
///     <b>Wwise Flanger</b> objects (plug-in 0x007D0003) with a FIXED delay, and an RTPC
///     curve per knob. Read from the player's own game gear file at run time (<see cref="RsGameTables" />;
///     bank version 91, <c>audio/windows/&lt;gear&gt;.bnk</c>); of the Flangers the manifest
///     references, the first in the bank's own object order supplies the curves and the delay. <see cref="RsGameCurves.ChorusKeys" /> names the gear.
///     <para>
///         The Flanger's parameter ids are the same in every chorus bank and are named
///         by the knobs that drive them: 0 delay time (Studio Chorus "Delay"), 1 wet/dry
///         mix in % ("Mix"), 9 LFO rate in Hz ("Rate"), 10 modulation depth in %
///         ("Depth"), 16 output level in dB (a +1 to +2 dB make-up on Mix, left to the
///         rendered-loudness levelling) and 22 phase spread in degrees ("Stereo").
///     </para>
/// </summary>
/// <param name="GearKey">The gear key.</param>
/// <param name="DelayMs">The Flanger's base delay time (block offset 0, ms).</param>
/// <param name="Voices">How many Flanger objects the bank chains (our chorus voices).</param>
/// <param name="DryLevel">Block offset 4: the dry share inside the Flanger's own output.</param>
/// <param name="FeedForward">Block offset 8: the delayed tap's gain (−1 is inverted).</param>
/// <param name="MixPercent">Mix → wet/dry mix, %.</param>
/// <param name="DepthPercent">Depth → modulation depth, % of the delay time.</param>
/// <param name="RateHz">Rate → LFO rate, Hz. The knob is LABELLED in Hz, but most banks
/// bend it (Exp3, SineRecip), so the label is not the rate.</param>
/// <param name="DelayKnobMs">Studio Chorus's Delay knob → delay time, ms, when it has one.</param>
/// <param name="StereoDegrees">Stereo → LFO phase spread between channels, degrees.</param>
internal sealed record RsFlangerChorus(
    string GearKey,
    float DelayMs,
    int Voices,
    float DryLevel,
    float FeedForward,
    WwiseCurve MixPercent,
    WwiseCurve DepthPercent,
    WwiseCurve RateHz,
    WwiseCurve? DelayKnobMs = null,
    WwiseCurve? StereoDegrees = null)
{
    /// <summary>
    ///     The share of wet signal in a dry/wet CROSSFADE (the <c>chorus</c> module's
    ///     <c>mix</c>) that gives the bank's wet-to-dry ratio. The Flanger outputs
    ///     <c>Dry·x + FeedForward·delayed</c> and its Mix blends that with the input, so
    ///     the delayed tap's gain is <c>m·|FeedForward|</c> against a dry gain of
    ///     <c>(1 − m) + m·Dry</c>. With <c>Dry = 0</c> that is just <c>m</c>.
    /// </summary>
    public float CrossfadeWet(float mixPercent)
    {
        var m = Math.Clamp(mixPercent / 100f, 0f, 1f);
        var wet = m * MathF.Abs(FeedForward);
        var dry = 1f - m + m * DryLevel;
        return wet + dry <= 0f ? 0f : wet / (wet + dry);
    }

    /// <summary>
    ///     The <c>chorus</c> module's PEAK delay deviation (ms) for a depth of
    ///     <paramref name="depthPercent" /> % on a delay of <paramref name="delayMs" />.
    ///     <b>Inferred:</b> Wwise documents the Flanger's depth as a percentage of the
    ///     delay time, and the sweep cannot pass zero delay, so it spans
    ///     <c>depth × delay</c> peak to peak: half of that either side.
    /// </summary>
    public static float PeakDeviationMs(float depthPercent, float delayMs)
        => Math.Clamp(depthPercent / 100f, 0f, 1f) * delayMs / 2f;
}
