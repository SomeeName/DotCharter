namespace DotCharter.Tone.Audio.Dsp.Loudness;

/// <summary>
///     Where a converted Rocksmith tone's level lands once its chain has been
///     MEASURED: one loudness reference for every tone, a peak ceiling, and the
///     song-relative intent Rocksmith's own tone <c>Volume</c> carries.
///     <para>
///         <b>The reference.</b> <see cref="TargetLufs" /> is integrated loudness of the
///         chain's output for <see cref="CalibrationPhrase" /> (a -18 dBFS-peak guitar).
///         -24 LUFS is where the MEDIAN converted tone already sat under the old peak
///         walker (measured over the owner's 580 tones: -24.5), so levelling moves the
///         outliers to the middle rather than the whole library up or down.
///     </para>
///     <para>
///         <b>The ceiling.</b> A clean tone's crest factor is ~22 dB against ~13 for a
///         driven one, so equal loudness puts a clean tone's peaks higher. The gain never
///         takes the phrase's peak over <see cref="PeakCeilingDbfs" /> (-5 dBFS: the
///         walker-era library's p90 peak was -3.8): 8 % of the library's tones (the
///         cleanest) land a little under the reference instead of nearer full scale.
///     </para>
///     <para>
///         <b>The intent</b> (<see cref="IntentOffsetsDb" />), decided from the data of
///         the owner's library (official tones: 280 in 103 songs with two or more
///         guitar tones). Rocksmith's per-tone <c>Volume</c> moves within a song are
///         small (median 1.2 dB, p90 3.5) and do NOT track how driven a tone is: their
///         correlation with the tones' rendered loudness here is -0.16 (slope -0.04),
///         and a song's LEAD tone is not systematically louder (median -0.3 dB against
///         its other tones, louder in 40 % of pairs, quieter in 56 %). So a Volume
///         difference is read as the song's intended balance, applied 1:1 on top of the
///         equal-loudness level, relative to the median of the tones the arrangement
///         plays, and clamped to <see cref="MaxIntentDb" /> (the p90 of those moves). On
///         customs the Volume partly COMPENSATES for gain (slope -0.49) — taken 1:1
///         there that costs ~1 dB, which flattening would cost everywhere (1.2 dB mean
///         against 0.2 for 1:1).
///     </para>
/// </summary>
public static class RsToneLoudnessPolicy
{
    /// <summary>The loudness every converted tone is levelled to (LUFS, calibration phrase).</summary>
    public const double TargetLufs = -24.0;

    /// <summary>The phrase's peak never goes over this (dBFS).</summary>
    public const double PeakCeilingDbfs = -5.0;

    /// <summary>Largest song-relative offset a tone's Rocksmith Volume may ask for (dB).</summary>
    public const float MaxIntentDb = 3f;

    /// <summary>The native gain module's range.</summary>
    public const float MaxGainDb = 24f;

    /// <summary>
    ///     The trailing master gain (dB) for a chain measured at
    ///     <paramref name="body" /> without it: the reference plus
    ///     <paramref name="intentDb" />, under the peak ceiling, within the gain
    ///     module's ±24 dB.
    /// </summary>
    public static float TrailingGainDb(ToneLoudness body, float intentDb)
        => Math.Clamp(NeededGainDb(body, intentDb), -MaxGainDb, MaxGainDb);

    /// <summary>
    ///     The gain (dB) <paramref name="body" /> needs to reach the reference plus
    ///     <paramref name="intentDb" /> under the peak ceiling, NOT clamped to the
    ///     trailing gain's range. A Rocksmith amp's own chain (`rsamp`) can sit
    ///     more than 24 dB off the reference at a clean setting; what the trailing
    ///     gain cannot give goes into that amp's output trim
    ///     (<c>RsToneLevelling.Level</c>).
    /// </summary>
    public static float NeededGainDb(ToneLoudness body, float intentDb)
    {
        var gain = TargetLufs + intentDb - body.Lufs;
        gain = Math.Min(gain, PeakCeilingDbfs - body.PeakDbfs);
        return (float)gain;
    }

    /// <summary>
    ///     Each tone's intended offset (dB) from the reference: its Rocksmith
    ///     <c>Volume</c> minus the MEDIAN Volume of <paramref name="volumesDb" /> (the
    ///     tones played together), clamped to ±<see cref="MaxIntentDb" />. A missing
    ///     (NaN) Volume, or a lone tone, asks for nothing.
    /// </summary>
    public static IReadOnlyList<float> IntentOffsetsDb(IReadOnlyList<float> volumesDb)
    {
        ArgumentNullException.ThrowIfNull(volumesDb);
        var known = volumesDb.Where(float.IsFinite).Order().ToArray();
        var result = new float[volumesDb.Count];
        if (known.Length < 2) return result;

        var median = known.Length % 2 == 1
            ? known[known.Length / 2]
            : 0.5f * (known[known.Length / 2 - 1] + known[known.Length / 2]);
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = float.IsFinite(volumesDb[i])
                ? Math.Clamp(volumesDb[i] - median, -MaxIntentDb, MaxIntentDb)
                : 0f;
        }

        return result;
    }
}
