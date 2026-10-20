namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

/// <summary>
/// Absolute loudness normalization for tone rigs (both RS-converted tones and
/// the factory preset catalogue): estimates the chain's static output level for
/// a typical guitar input and computes the trailing master gain that brings it
/// to one shared reference, so every tone — clean or saturated, converted or
/// factory — sits at the same place in the monitor mix.
///
/// <para><b>The reference (documented rationale):</b></para>
/// <list type="bullet">
/// <item><see cref="ReferenceInputPeakDb"/> = −18 dBFS: a typical hot guitar
/// peak at healthy interface gain (the level the pre-play signal meter calls
/// good).</item>
/// <item><see cref="TargetOutputPeakDb"/> = −12 dBFS: clearly audible over a
/// full-scale backing track at monitor gain 1.0 while leaving ~12 dB of
/// summing headroom on the output bus; the monitor fader (0..4×) stays the
/// player's balance control. This replaces the old scheme that honoured the
/// RS tone JSON's "Volume" — that value is calibrated for Rocksmith's OWN
/// mix bus (typically −12…−20 dB) and put every converted tone far under the
/// backing track here, so it is now DISCARDED (recorded in the
/// ConversionReport).</item>
/// </list>
///
/// <para><b>For RS conversions this is only the STARTING level</b>: playback
/// re-levels each converted tone by its RENDERED loudness once the chain has been
/// measured (<c>RsToneLevelling</c> + <c>Loudness/RsToneLoudnessPolicy</c>), because
/// this walker cannot see a cab IR raising a saturated amp's peak, and equal peaks
/// are not equal loudness. Factory presets still use this walker alone.</para>
///
/// <para><b>The estimator</b> (<see cref="EstimateChainOutputDb"/>) is a pure,
/// coarse peak-level walker, NOT loudness measurement: it starts at the
/// reference input peak and folds each non-bypassed module's documented static
/// gain staging (mirrored by hand from the native dcdsp cores):</para>
/// <list type="bullet">
/// <item>linear output-level params multiply absolutely
/// (<c>20·log10(max(v·k, 0.05))</c> with each core's output constant k);
/// dB-unit level params add directly;</item>
/// <item>saturating stages (overdrive/distortion/fuzz/bigmuff/klon and the
/// amp preamp) add their drive gain but are CAPPED at
/// <see cref="SaturationCeilingDb"/> — past the knee more drive adds harmonics,
/// not level. This is what makes a scooped high-gain rig and a whisper-clean
/// rig comparable at all;</item>
/// <item>EQ band shelves, cab voicing, modulation/delay/reverb mixes are
/// treated as character (0 dB) — chasing them would be false precision.</item>
/// </list>
///
/// <para><b>Safety clamps</b> (the estimator is a heuristic — corrections stay
/// conservative): the computed trailing gain never boosts more than
/// <see cref="MaxBoostDb"/> (+18 dB), and never cuts below the gain module's
/// −24 dB floor. Hot chains therefore land AT the target (full cut available);
/// an absurdly quiet estimate is only trusted up to +18 dB.</para>
/// </summary>
public static class DcRigLoudnessNormalizer
{
    /// <summary>Assumed guitar input peak (dBFS) the estimator references.</summary>
    public const float ReferenceInputPeakDb = -18f;

    /// <summary>The estimated chain output peak (dBFS) every normalized rig is
    /// pulled toward — the one absolute loudness reference.</summary>
    public const float TargetOutputPeakDb = -12f;

    /// <summary>Peak ceiling (dBFS) a driven tanh/clipper stage is assumed to
    /// pin its output near, regardless of how much further it is driven.</summary>
    public const float SaturationCeilingDb = -3f;

    /// <summary>Maximum boost the normalizer will apply (conservative — a very
    /// low estimate is a heuristic guess, not a measurement).</summary>
    public const float MaxBoostDb = 18f;

    /// <summary>Maximum cut, bounded by the native gain module's −24 dB floor.
    /// The full cut stays available so the hot end always reaches the target.</summary>
    public const float MaxCutDb = 24f;

    /// <summary>Linear level floor for the log conversions (≈ −26 dB): keeps a
    /// zeroed level knob finite and bounds any single module's influence.</summary>
    public const float LinearFloor = 0.05f;

    /// <summary>Half-range of <c>namamp</c>'s 0.36 <c>gain</c> knob in dB (0.5 =
    /// 0 dB, 0/1 = ∓18 dB). Mirrors <c>dcdsp::AmpFace::kGainRangeDb</c>.</summary>
    public const float NamFaceGainRangeDb = 18f;

    // 20·log10(1.8 · 1.6): the distortion core's fixed stage-2/3 drive on top
    // of its gain-pot pre-gain (dcdsp_distortion.h process()).
    private const float DistortionCascadeDb = 9.2f;

    /// <summary>
    /// Estimated static output peak (dBFS) of the chain for a
    /// <paramref name="inputPeakDb"/> guitar input. Pure; returns the input
    /// unchanged for an empty / level-neutral chain. See the class doc for the
    /// per-module model.
    /// </summary>
    public static float EstimateChainOutputDb(
        IReadOnlyList<DcRigModule> modules, float inputPeakDb = ReferenceInputPeakDb)
    {
        var level = inputPeakDb;

        foreach (var m in modules)
        {
            if (m.Bypassed) continue;

            switch (m.ModuleTypeId)
            {
                case DcRigTargetCatalog.Gain:
                    level += P(m, "gainDb");
                    break;

                case DcRigTargetCatalog.Compressor:
                    level += P(m, "makeupDb"); // compression itself ignored (conservative)
                    break;

                case DcRigTargetCatalog.Eq:
                    level += P(m, "outputTrimDb");
                    break;

                case DcRigTargetCatalog.GraphicEq:
                    level += P(m, "level");
                    break;

                case DcRigTargetCatalog.Overdrive:
                    // drive 0..1 → 0..+30 dB into tanh; out ×(level·1.2).
                    level = Saturate(level + Clamp01(P(m, "drive")) * 30f);
                    level += LinDb(P(m, "level") * 1.2f);
                    break;

                case DcRigTargetCatalog.Distortion:
                    // gain 0..1 → 0..+40 dB + the fixed cascade; out ×(level·0.9).
                    level = Saturate(level + Clamp01(P(m, "gain")) * 40f + DistortionCascadeDb);
                    level += LinDb(P(m, "level") * 0.9f);
                    break;

                case DcRigTargetCatalog.Fuzz:
                {
                    // drive = 1 + fuzz²·130 (dcdsp_fuzz.h); out ×level.
                    var fz = Clamp01(P(m, "fuzz"));
                    level = Saturate(level + LinDb(1f + fz * fz * 130f));
                    level += LinDb(P(m, "level"));
                    break;
                }

                case DcRigTargetCatalog.BigMuff:
                {
                    // drive = 2 + 220·sustain² (dcdsp_bigmuff.h); out ×volume.
                    var sus = Clamp01(P(m, "sustain"));
                    level = Saturate(level + LinDb(2f + 220f * sus * sus));
                    level += LinDb(P(m, "volume"));
                    break;
                }

                case DcRigTargetCatalog.Klon:
                    // drive = 1 + gain·40 (dcdsp_klon.h); out ×(output·1.2).
                    // The gain-ganged clean blend is ignored (character).
                    level = Saturate(level + LinDb(1f + Clamp01(P(m, "gain")) * 40f));
                    level += LinDb(P(m, "output") * 1.2f);
                    break;

                case DcRigTargetCatalog.Sustainer:
                {
                    // Output trim law from dcdsp_sustainer.h: 0 → −24 dB,
                    // 0.5 → 0 dB, 1 → +12 dB. Upward compression ignored.
                    var lvl = Clamp01(P(m, "level"));
                    level += lvl <= 0.5f
                        ? lvl / 0.5f * 24f - 24f
                        : (lvl - 0.5f) / 0.5f * 12f;
                    break;
                }

                case DcRigTargetCatalog.Amp:
                {
                    // Preamp drive by voicing (dcdsp_amp.h): clean g·10,
                    // crunch 8+g·22, lead 14+g·30 — saturating; out ×(master·1.2).
                    var g = Clamp01(P(m, "gain"));
                    var driveDb = (int)MathF.Round(P(m, "voicing")) switch
                    {
                        0 => g * 10f,
                        2 => 14f + g * 30f,
                        _ => 8f + g * 22f
                    };
                    level = Saturate(level + driveDb);
                    level += LinDb(P(m, "master") * 1.2f);
                    break;
                }

                case DcRigTargetCatalog.NamAmp:
                {
                    // A neural amp capture is modeled as a saturating amp: inputDb
                    // and the 0.36 `gain` knob both drive it (capped at the ceiling
                    // like the other drive stages), outputDb and `master` are post.
                    // The capture's own absolute loudness is opaque to a static
                    // estimator, so a capture-backed preset centers itself via
                    // those exactly like a real rig — and the apply-seam trailing
                    // gain trims the rest. The tone stack / presence / resonance
                    // are level-NEUTRAL character here, the same treatment eq and
                    // cab shelves get.
                    // BOTH new terms are 0 dB at their catalogue defaults, so a
                    // preset that predates the amp face estimates identically.
                    var driveDb = (Clamp01(P(m, "gain")) - 0.5f) * 2f * NamFaceGainRangeDb;
                    level = Saturate(level + P(m, "inputDb") + driveDb);
                    level += P(m, "outputDb");
                    var master = Clamp01(P(m, "master")) * 2f;   // (2m)^2, unity at 0.5
                    level += LinDb(master * master);
                    break;
                }

                case DcRigTargetCatalog.Swell:
                    // Volume / auto-swell: a pure LINEAR MULTIPLY by `position`. It can only
                    // attenuate — the swell factor settles at 1 during sustain, so the chain's
                    // STATIC output is exactly `position` (1 = unity). No saturation, no makeup.
                    level += LinDb(Clamp01(P(m, "position")));
                    break;

                case DcRigTargetCatalog.Subharmonic:
                    // A synth voice ADDED under the dry; the sub peaks at ~0.9x the input.
                    level += LinDb(Clamp01(P(m, "dry")) + 0.9f * (Clamp01(P(m, "sub1")) + Clamp01(P(m, "sub2"))));
                    break;

                case DcRigTargetCatalog.Feedbacker:
                    // The howl is AGC'd to the note's own peak and ADDED to the dry (a sustainer
                    // does not take your guitar away), so a bloomed note peaks at (1 + mix)x.
                    level += LinDb(1f + Clamp01(P(m, "mix")));
                    break;

                // ---------------------------------------------------------
                // 0.9 gear wave (native catalogue 57 → 100). Every drive gain
                // below is the RANGE THE SHIPPED GUIDE STATES for that core's
                // knob — JuceSharp/docs/guides/gear-*.md, read the same way the
                // pre-0.9 cases were read out of the dcdsp headers, never
                // guessed. A module whose knobs only shape or attenuate stays
                // OFF this switch and is level-neutral by omission.
                // ---------------------------------------------------------

                case DcRigTargetCatalog.Rat:
                    // gear-drive.md: `distortion` is 0 dB … +45 dB into the
                    // diodes; out ×volume.
                    level = Saturate(level + Clamp01(P(m, "distortion")) * 45f);
                    level += LinDb(P(m, "volume"));
                    break;

                case DcRigTargetCatalog.Ds1:
                    // gear-drive.md: `dist` is 0 dB … +42 dB; out ×level.
                    level = Saturate(level + Clamp01(P(m, "dist")) * 42f);
                    level += LinDb(P(m, "level"));
                    break;

                case DcRigTargetCatalog.Hm2:
                    // gear-drive.md: `dist` is 0 dB … +46 dB; out ×level. The
                    // two colour controls are narrow resonant BELLS — character,
                    // like every other tone stack in this walker.
                    level = Saturate(level + Clamp01(P(m, "dist")) * 46f);
                    level += LinDb(P(m, "level"));
                    break;

                case DcRigTargetCatalog.TapeSat:
                    // gear-drive.md: `drive` is −10 dB … +18 dB of record level.
                    level = Saturate(level - 10f + Clamp01(P(m, "drive")) * 28f);
                    break;

                case DcRigTargetCatalog.BassOd:
                {
                    // gear-drive.md: `drive` is 0 dB … +32 dB into the clipper
                    // and `blend` is how much of the DRIVEN leg is heard (0 is
                    // the clean leg alone), so the drive only reaches the output
                    // in proportion to the blend. Out ×level.
                    var blend = Clamp01(P(m, "blend"));
                    level = Saturate(level + Clamp01(P(m, "drive")) * 32f * blend);
                    level += LinDb(P(m, "level"));
                    break;
                }

                case DcRigTargetCatalog.TubePre:
                    // gear-drive.md: `gain` is 0 dB … +36 dB; out ×level.
                    level = Saturate(level + Clamp01(P(m, "gain")) * 36f);
                    level += LinDb(P(m, "level"));
                    break;

                case DcRigTargetCatalog.OctFuzz:
                    // gear-drive.md: `fuzz` is 0 dB … +40 dB; out ×volume.
                    level = Saturate(level + Clamp01(P(m, "fuzz")) * 40f);
                    level += LinDb(P(m, "volume"));
                    break;

                case DcRigTargetCatalog.BassAmp:
                    // gear-ampcab.md: preamp `gain` is 0 dB to +30…+36 dB by
                    // model (33 is the middle). `drive` is GAIN-COMPENSATED —
                    // "a drive control and not a second volume" — so it adds
                    // nothing here. Out ×(master·1.2), the convention the guitar
                    // amp's master already uses.
                    level = Saturate(level + Clamp01(P(m, "gain")) * 33f);
                    level += LinDb(P(m, "master") * 1.2f);
                    break;

                case DcRigTargetCatalog.RsAmpChain:
                    // Rocksmith's own chain, whose level at a knob setting is
                    // the game's (its own output-compensation curves included), not
                    // a law a static walker can read. RS conversion adds the MEASURED
                    // per-model level instead (RsAmpLevelTrims.ByKey);
                    // here it is a saturating stage at unity.
                    level = Saturate(level);
                    break;

                case DcRigTargetCatalog.PowerAmp:
                    // gear-ampcab.md: `drive` is 0…+20 dB and gain-compensated,
                    // so the stage's only level term is `level`.
                    level += LinDb(P(m, "level") * 1.2f);
                    break;

                case DcRigTargetCatalog.AcPre:
                    // Pure colour with one output level, scaled by 1.3 in the
                    // core (dcdsp_acpre.h `level_.setTarget(clamp01(p.level) *
                    // 1.3f)`). The 0.7 default is therefore -0.8 dB, not unity.
                    level += LinDb(P(m, "level") * 1.3f);
                    break;

                case DcRigTargetCatalog.MicPos:
                    // The same 1.3 level scale (dcdsp_micpos.h; the RS cab mic
                    // layer sets 1/1.3 to be exactly unity), plus
                    // the broadband loss of a capsule turned off axis: the
                    // mic's published polar response, which is a LEVEL change
                    // (-1.2 dB for the 57 at 45 degrees, -3 dB for the ribbon).
                    level += LinDb(P(m, "level") * 1.3f)
                             + MicOffAxisLevelDb((int)MathF.Round(P(m, "mic")), P(m, "angleDeg"));
                    break;

                case DcRigTargetCatalog.Limiter:
                    // gear-dynamics.md: `gainDb` is INPUT gain applied before
                    // the detector, and `ceilingDb` is "the level the output
                    // never crosses" — the one stage in this walker that can be
                    // modelled EXACTLY rather than estimated.
                    level = MathF.Min(level + P(m, "gainDb"), P(m, "ceilingDb"));
                    break;

                case DcRigTargetCatalog.OptoComp:
                    level += P(m, "gainDb"); // makeup; compression itself ignored (conservative)
                    break;

                case DcRigTargetCatalog.Multiband:
                    // A peak walker follows the LOUDEST band's makeup; the
                    // compression itself is ignored, as it is for `compressor`.
                    level += MathF.Max(P(m, "lowGainDb"), MathF.Max(P(m, "midGainDb"), P(m, "highGainDb")));
                    break;

                case DcRigTargetCatalog.Transient:
                    // Only the ATTACK gain can raise a peak; `sustainDb` moves
                    // the decay, which a peak estimate never sees.
                    level += MathF.Max(P(m, "attackDb"), 0f);
                    break;

                case DcRigTargetCatalog.Tilt:
                    level += P(m, "level");
                    break;

                case DcRigTargetCatalog.MidSide:
                    // The mid carries the guitar; the side is width.
                    level += P(m, "midDb");
                    break;

                case DcRigTargetCatalog.Volume:
                {
                    // gear-utility.md: heel-down is `minDb`, toe-down `maxDb`.
                    // The taper changes the JOURNEY, not the two ends, so a
                    // linear read is exact at both and conservative between.
                    var min = P(m, "minDb");
                    level += min + Clamp01(P(m, "position")) * (P(m, "maxDb") - min);
                    break;
                }

                case DcRigTargetCatalog.TwelveString:
                    // gear-pitch.md: the courses are SUMMED with the original,
                    // so a strum peaks above the dry by their blended level.
                    level += LinDb(1f + Clamp01(P(m, "mix")) * 0.5f *
                                   (Clamp01(P(m, "octaveLevel")) + Clamp01(P(m, "unisonLevel"))));
                    break;

                case DcRigTargetCatalog.Boost:
                {
                    // dcdsp_boost.h: gain, then (character > 0) a tanh at
                    // drive 1 + 2c with 1/drive make-up — so the output peak
                    // can never pass 1/drive. Reached by RS conversion since
                    // Pedal_RangeBooster routes here; until then the walker
                    // credited a +24 dB boost with 0 dB.
                    level += Math.Clamp(P(m, "gainDb"), 0f, 24f);
                    var c = Clamp01(P(m, "character"));
                    if (c > 1e-3f) level = MathF.Min(level, LinDb(1f / (1f + 2f * c)));
                    break;
                }

                case DcRigTargetCatalog.BassSynth:
                    // gear-pitch.md: a tracked synth voice summed with the dry,
                    // each with its own level; the voice peaks near the input.
                    level += LinDb(Clamp01(P(m, "dryMix")) + Clamp01(P(m, "synthMix")));
                    break;

                    // cab / modulation / delay / reverb / pitch / filters: treated
                    // as level-neutral character (see the class doc). The rest of
                    // the 0.18 wave (whammy / reversedelay / slapback / widener /
                    // freeze / filterseq / resonator / tapestop / glitch) is
                    // level-neutral too — each is a wet blend, a replay of the input
                    // at unit gain, or (widener) an M/S rotation whose mid path is a
                    // wire; none of them has a level knob that scales the dry path.
                    //
                    // The 0.9 gear wave's remaining 24 modules are level-neutral for
                    // the same reasons and that is a DECISION, not an omission:
                    // expander / ducker / gate-likes only ever ATTENUATE (a peak
                    // walker must not credit a cut it cannot guarantee), the filter,
                    // modulation, delay, reverb and granular modules are wet blends
                    // against an untouched dry, and `looper` adds a take whose level
                    // is whatever was played into it — unknowable statically.
            }
        }

        return level;
    }

    /// <summary>
    /// The trailing master gain (dB) that pulls the chain's estimated output to
    /// <see cref="TargetOutputPeakDb"/>, clamped to [−<see cref="MaxCutDb"/>,
    /// +<see cref="MaxBoostDb"/>]. Compute this over the chain WITHOUT the
    /// trailing gain module itself.
    /// </summary>
    public static float ComputeTrailingGainDb(IReadOnlyList<DcRigModule> modules)
        => Math.Clamp(
            TargetOutputPeakDb - EstimateChainOutputDb(modules),
            -MaxCutDb, MaxBoostDb);

    /// <summary>
    /// Returns the preset with its trailing master gain set so the estimated
    /// chain output lands on <see cref="TargetOutputPeakDb"/>: an existing
    /// non-bypassed trailing <c>gain</c> module is REPLACED (making the
    /// operation idempotent — normalizing twice changes nothing), otherwise one
    /// is appended. A mid-chain gain module (a clean boost pedal) is left
    /// alone; only the LAST module counts as the trailing master.
    /// </summary>
    public static DcRigPreset Normalize(DcRigPreset preset)
    {
        var modules = preset.Modules;
        var hasTrailingGain = modules.Count > 0 &&
                              modules[^1].ModuleTypeId == DcRigTargetCatalog.Gain &&
                              !modules[^1].Bypassed;

        var body = new List<DcRigModule>(hasTrailingGain ? modules.Count : modules.Count + 1);
        for (var i = 0; i < modules.Count - (hasTrailingGain ? 1 : 0); i++)
            body.Add(modules[i]);

        var gainDb = ComputeTrailingGainDb(body);
        var values = new Dictionary<string, float>(StringComparer.Ordinal) { ["gainDb"] = gainDb };
        body.Add(hasTrailingGain ? modules[^1] with { Params = values } :
            new DcRigModule(DcRigTargetCatalog.Gain, values));

        return preset with { Modules = body };
    }

    // ------------------------------------------------------------- helpers

    /// <summary>Param value, falling back to the catalogue default when the
    /// preset leaves it unset (identical to what the native module would use).</summary>
    private static float P(DcRigModule module, string paramId)
    {
        if (module.Params.TryGetValue(paramId, out var value)) return value;
        return DcRigTargetCatalog.TryGetParam(module.ModuleTypeId, paramId, out var spec)
            ? spec.Default
            : 0f;
    }

    private static float Saturate(float levelDb) => MathF.Min(levelDb, SaturationCeilingDb);

    private static float LinDb(float linear) => 20f * MathF.Log10(MathF.Max(linear, LinearFloor));

    /// <summary>
    ///     The broadband level <c>micpos</c> loses with its capsule turned
    ///     <paramref name="angleDeg" /> off axis: <c>kOffAxis[mic].gainDb</c> in
    ///     <c>dcdsp_micpos.h</c> (rows at 30 / 45 / 60 degrees, linear in angle,
    ///     0 on axis), mirrored here because the walker is pure. Pinned against
    ///     the native core by <c>DcRigLoudnessNormalizerTests</c>.
    /// </summary>
    internal static float MicOffAxisLevelDb(int mic, float angleDeg)
    {
        if (!(angleDeg > 0f)) return 0f;
        var row = MicOffAxisGainDb[Math.Clamp(mic, 0, MicOffAxisGainDb.Length - 1)];
        var a = MathF.Min(angleDeg, 60f);
        return a <= 30f ? row[0] * (a / 30f)
            : a <= 45f ? row[0] + (row[1] - row[0]) * ((a - 30f) / 15f)
            : row[1] + (row[2] - row[1]) * ((a - 45f) / 15f);
    }

    /// <summary><c>dcdsp_micpos.h</c> <c>kOffAxis[].gainDb</c>: 57, 121, 421,
    /// Condenser, Tube at 30 / 45 / 60 degrees.</summary>
    private static readonly float[][] MicOffAxisGainDb =
    [
        [-0.27f, -1.23f, -2.71f],
        [-1.249f, -3.010f, -6.021f],
        [-0.624f, -1.371f, -2.499f],
        [-0.624f, -1.371f, -2.499f],
        [-0.80f, -1.78f, -2.86f]
    ];

    private static float Clamp01(float v) => Math.Clamp(v, 0f, 1f);
}
