// RsToneConverter's fallback guitar AMP (a key the game does not ship) and the per-model level trims the trailing gain adds.
using DotCharter.Tone.Audio.Dsp.Tones;
using static DotCharter.Tone.Audio.Dsp.RsToneConversion.DcRigTargetCatalog;

namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

public sealed partial class RsToneConverter
{
    /// <summary>
    ///     The trailing master gain: the loudness walker's
    ///     (<see cref="DcRigLoudnessNormalizer.ComputeTrailingGainDb" />) plus the
    ///     measured trim of the tone's <c>rsamp</c> model
    ///     (<see cref="RsAmpLevelTrims" />) or <c>amp</c> roster model
    ///     (<see cref="AmpVoicingTrimDb" />), within the gain module's ±24 dB.
    /// </summary>
    internal static float TrailingGainDb(IReadOnlyList<DcRigModule> modules)
    {
        var gain = DcRigLoudnessNormalizer.ComputeTrailingGainDb(modules);
        foreach (var m in modules)
        {
            if (m.Bypassed) continue;
            if (m.ModuleTypeId == RsAmpChain && m.Params.TryGetValue("model", out var model))
            {
                if (RsAmpLevelTrims.TryGet((int)MathF.Round(model), out var trim)) gain += trim;
                break;
            }

            if (m.ModuleTypeId != Amp || !m.Params.TryGetValue("voicing", out var v)) continue;
            var i = (int)MathF.Round(v);
            if (i >= 0 && i < AmpVoicingTrimDb.Length) gain += AmpVoicingTrimDb[i];
            break;
        }

        return Math.Clamp(gain, -24f, 24f);
    }

    /// <summary>
    ///     Per-roster-model level trim (dB) for the guitar <c>amp</c>, voicings
    ///     0..18. The loudness walker models the amp WITHOUT its voicing, but at
    ///     equal knobs the roster models' rendered peaks span 37 dB (Modern Djent ~13 dB
    ///     over the Blackface Twin). Voicings 0..2 alone sit within 9 dB, but RS
    ///     conversion uses the full roster, which untrimmed made converted tones LESS
    ///     level-consistent (library RMS p10-p90 spread 16.7 → 20.9 dB).
    ///     <para>
    ///         MEASURED, not tuned: each value is -12 dBFS minus the mean rendered
    ///         peak of that voicing through the walker-normalised chain, over gain
    ///         20 and 80 (the JCM800 knob set, others at 50) and two cabinets (1x12,
    ///         4x12), over the playing part of <c>ReferenceDi</c> — so it also absorbs
    ///         the cab-IR overshoot the walker misses for that voicing. Pinned (±1.5 dB) by
    ///         <c>RsToneFidelityHarnessTests.AmpVoicingTrims_MatchTheRig</c>; RS
    ///         conversion only — factory presets keep the plain walker. Only the
    ///         STARTING level: playback re-levels by rendered loudness.
    ///     </para>
    /// </summary>
    internal static readonly float[] AmpVoicingTrimDb =
    [
        -1.5f, -4.7f, -10.1f, -3.4f, -8.7f, 7.5f, -7.9f, 0.6f, 5.2f, 4.1f,
        -2.2f, -8.2f, -6.9f, -5.9f, -10.5f, -15.4f, -13.1f, -4.4f, -3.0f,
        17.3f, 3.2f, 2.8f, 26.6f, 21.4f, 5.8f, -4.8f, -2.2f
    ];

    // ---------------------------------------------------------------- amp

    /// <summary>
    ///     An amp the game does not ship (a tone with no Amp slot, or a custom
    ///     key): the guitar <c>amp</c> on the roster model its key suggests, its
    ///     generic knobs by name. Every amp Rocksmith ships converts through its own
    ///     chain instead (<see cref="BuildRsAmp" />).
    /// </summary>
    private static DcRigModule BuildAmp(RsAmp amp, RsGearCatalog catalog, RsGearOverrides ov, ConversionReport report)
    {
        var p = new ParamMap(Amp, ov, amp.GearKey);
        foreach (var (pot, val) in amp.Pots())
        {
            catalog.TryGetKnob(amp.GearKey, pot, out var knob);
            switch (pot)
            {
                case "Gain" or "Drive" or "Loudness1" or "Loudness" or "PreGain":
                    p.Set("gain", Norm01(val, knob));
                    break;
                case "Volume" when !p.Has("gain"):
                    p.Set("gain", Norm01(val, knob));
                    break;
                case "Bass" or "Low" or "Lo":
                    p.Set("bass", Norm01(val, knob));
                    break;
                case "Mid" or "Middle":
                    p.Set("mid", Norm01(val, knob));
                    break;
                case "Treble" or "High" or "Hi":
                    p.Set("treble", Norm01(val, knob));
                    break;
                case "Pres" or "Presence":
                    p.Set("presence", Norm01(val, knob));
                    break;
                case "Loudness2" or "Master" or "MasterVol" or "PostGain" or "Level":
                    p.Set("master", Norm01(val, knob));
                    break;
            }
        }

        p.Set("voicing", GuessVoicing(amp.GearKey));
        p.ApplyStatic();

        report.Add(ConversionNoteKind.Fallback, amp.GearKey,
            amp.GearKey == RsToneJsonParser.SyntheticAmpKey
                ? "tone had no Amp slot; used a neutral synthetic amp"
                : "not an amp Rocksmith ships: the nearest guitar-amp roster model by its key");

        return new DcRigModule(Amp, p.Build());
    }

    private static int GuessVoicing(string key)
    {
        if (ContainsAny(key, "Twin", "Deluxe", "AC30", "Bassman", "Clean", "Jazz", "Champ"))
            return 0;
        if (ContainsAny(key, "Rect", "Recto", "CA100", "5150", "Uber", "Diezel", "Mesa", "Bogner", "Metal", "Engl", "Savage"))
            return 2;
        return 1; // Marshall/Orange/Vox-style crunch is the safe middle default
    }
}
