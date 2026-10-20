// RsAmpLevelTrims: the measured level trim of each Rocksmith amp's own chain (rsamp), by gear key — our measurement, not game data.
namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

/// <summary>The trailing gain's per-amp trim for <c>rsamp</c> (<see cref="RsToneConverter.TrailingGainDb" />).</summary>
internal static class RsAmpLevelTrims
{
    /// <summary>
    ///     Per-<c>rsamp</c>-amp level trim (dB), by gear key — not by model index,
    ///     because the rows come from the player's install. The loudness walker treats every amp
    ///     alike; the game's chains differ by tens of dB at equal knob positions.
    ///     MEASURED, like <see cref="RsToneConverter.AmpVoicingTrimDb" />: -12 dBFS minus the
    ///     model's mean rendered peak through the walker-normalised chain with
    ///     every knob at 20 % and then 80 % of its travel, through a 4x12 (guitar
    ///     amps) or an 8x10 (bass heads, DI preamps) and its mic layer, over the harness's
    ///     <c>ReferenceDi</c>. Pinned (±1.5 dB) by
    ///     <c>RsToneFidelityHarnessTests.RsAmpModelTrims_MatchTheRig</c>; only the
    ///     STARTING level (playback re-levels by rendered loudness).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, float> ByKey = new Dictionary<string, float>(StringComparer.Ordinal)
    {
        ["Amp_AT120"] = 3.8f, ["Amp_AT20"] = 0.7f, ["Amp_BT15"] = 2.5f,
        ["Amp_BT30"] = 0.7f, ["Amp_BT45"] = 1.9f, ["Amp_CA100"] = 8.8f,
        ["Amp_CA38"] = 2.7f, ["Amp_CA85"] = 4.4f, ["Amp_CS100"] = 6.1f,
        ["Amp_CS120"] = 2.9f, ["Amp_CS90"] = 1.8f, ["Amp_EN30"] = 4.6f,
        ["Amp_EN50"] = 1.8f, ["Amp_EpiphoneElectarCentury"] = -4.8f, ["Amp_EpiphoneElectarM"] = 2.5f,
        ["Amp_EpiphoneElectricHawaiian"] = -9.8f, ["Amp_EpiphoneZephyr"] = -4.0f, ["Amp_GB100"] = 11.8f,
        ["Amp_GB38"] = 3.2f, ["Amp_GB50"] = 7.3f, ["Amp_GibsonGA79"] = 1.8f,
        ["Amp_GibsonGA8"] = 1.5f, ["Amp_GibsonGA88"] = 2.3f, ["Amp_HG100"] = 4.6f,
        ["Amp_HG180"] = 6.1f, ["Amp_HG500"] = 5.0f, ["Amp_Marshall1962Bluesbreaker"] = 9.6f,
        ["Amp_MarshallDSL100H"] = 8.6f, ["Amp_MarshallDSL15H"] = 8.5f, ["Amp_MarshallJCM800"] = 9.2f,
        ["Amp_MarshallJTM45"] = 9.0f, ["Amp_MarshallJVM410H"] = 5.7f, ["Amp_MarshallPlexi"] = 9.9f,
        ["Amp_OrangeAD50"] = 4.3f, ["Amp_OrangeJimmyBean"] = 11.4f, ["Amp_OrangeOR100"] = 5.0f,
        ["Amp_OrangeOR50H"] = 11.2f, ["Amp_OrangeRockerverb"] = 10.6f, ["Amp_OrangeTinyTerror"] = -1.0f,
        ["Amp_TW22"] = -3.2f, ["Amp_TW26"] = 6.9f, ["Amp_TW40"] = 12.2f,
        ["Bass_Amp_BT600B"] = 5.7f, ["Bass_Amp_BT880B"] = -1.0f, ["Bass_Amp_BT975B"] = 3.3f,
        ["Bass_Amp_CH300B"] = 5.0f, ["Bass_Amp_CH350B"] = 4.4f, ["Bass_Amp_CH600B"] = -8.3f,
        ["Bass_Amp_CS240B"] = 4.4f, ["Bass_Amp_CS300B"] = 7.6f, ["Bass_Amp_CS75B"] = -5.6f,
        ["Bass_Amp_EdenE300"] = -3.5f, ["Bass_Amp_EdenWT550"] = -3.2f, ["Bass_Amp_EdenWT800"] = -4.9f,
        ["Bass_Amp_HT100B"] = 0.7f, ["Bass_Amp_HT300B"] = 2.2f, ["Bass_Amp_HT400B"] = 11.6f,
        ["Bass_Amp_LT25B"] = 2.2f, ["Bass_Amp_LT85B"] = 17.5f, ["Bass_Amp_OrangeAD200B"] = 2.6f,
        ["DI_Amp_BassDriver"] = -0.5f, ["DI_Amp_MixerPre"] = 6.2f, ["DI_Amp_TubePre"] = -0.7f
    };

    /// <summary>The trim of the <c>rsamp</c> row <paramref name="model" /> of the tables in
    /// use, or false (no tables, no such row, an amp not measured).</summary>
    public static bool TryGet(int model, out float trimDb)
    {
        var rows = RsAmpChains.Models;
        trimDb = 0f;
        return model >= 0 && model < rows.Count && ByKey.TryGetValue(rows[model].Key, out trimDb);
    }
}
