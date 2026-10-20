// RsToneConverter: entry points, slot routing (amp / cab + mic / effects by category) and the knob math; the guitar amp builder and its level trims are in RsToneConverter.Amp.cs, the dedicated circuit-model builders in RsToneConverter.GearModels.cs.
using System.Globalization;
using DotCharter.Tone.Audio.Dsp.Tones;
using static DotCharter.Tone.Audio.Dsp.RsToneConversion.DcRigTargetCatalog;

namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

/// <summary>
/// Converts a parsed Rocksmith in-game tone (<see cref="RsTone"/>) into an
/// engine-independent <see cref="DcRigPreset"/> for the in-house DSP rig.
///
/// Strategy: generic, catalogue-driven
/// normalization first (each RS knob's unit + range from
/// <see cref="RsGearCatalog"/> → the target param's range from
/// <see cref="DcRigTargetCatalog"/>), with a small <see cref="RsGearOverrides"/>
/// table for "character" the generic rule can't derive (amp voicing, cab model,
/// overdrive-vs-distortion-vs-fuzz routing, static pins). Pure, deterministic, zero I/O, zero
/// DSP-service dependency. Never throws — gear with no equivalent is skipped and
/// every lossy decision is recorded in the returned <see cref="ConversionReport"/>.
/// </summary>
public sealed partial class RsToneConverter
{
    /// <summary>Convenience overload using the process-wide catalogue + overrides.</summary>
    public DcRigPreset Convert(RsTone tone, out ConversionReport report)
        => Convert(tone, RsGearCatalog.Shared, RsGearOverrides.Shared, out report);

    /// <summary>
    /// <see cref="Convert(RsTone, out ConversionReport)"/> with every omitted
    /// param pinned to its catalogue default. The converter deliberately emits
    /// sparse params (only the pots the RS tone carries), but the live-switch
    /// path reuses same-type nodes in place — without the fill, a mid-song tone
    /// switch inherits the PREVIOUS tone's values on the omitted params. Same
    /// guard as the factory-preset apply seam; the applier's epsilon-skip keeps
    /// the extra sets free.
    /// </summary>
    public DcRigPreset ConvertFilled(RsTone tone, out ConversionReport report)
        => FillDefaults(Convert(tone, out report));

    public DcRigPreset Convert(RsTone tone, RsGearCatalog catalog, RsGearOverrides overrides, out ConversionReport report)
    {
        report = new ConversionReport();
        var modules = new List<DcRigModule>();

        foreach (var gear in tone.InSignalOrder())
            AppendGear(gear, catalog, overrides, report, modules);

        // Trailing master gain: the ESTIMATE (DcRigLoudnessNormalizer + the
        // per-voicing trim); playback re-levels by RENDERED loudness
        // (RsToneLevelling). The RS Volume targets Rocksmith's own mix bus and is
        // never a level here; only its difference from the song's other tones is
        // kept (RsToneLoudnessPolicy).
        var vol = new ParamMap(Gain, overrides);
        vol.Set("gainDb", TrailingGainDb(modules));
        modules.Add(new DcRigModule(Gain, vol.Build()));

        if (MathF.Abs(tone.VolumeDb) > 0.05f)
        {
            report.Add(ConversionNoteKind.Approximated, "Volume",
                string.Create(CultureInfo.InvariantCulture,
                    $"RS mixer volume ({tone.VolumeDb:0.#} dB, Rocksmith's own mix bus) is not a level here — only its difference from the song's other tones is kept (±3 dB)"));
        }

        return new DcRigPreset(modules);
    }

    /// <summary>
    ///     The modules ONE piece of gear converts to (zero when it is dropped, two
    ///     for a cabinet: the speaker IR and the mic layer in front of it), in
    ///     signal order. <see cref="Convert(RsTone, RsGearCatalog, RsGearOverrides, out ConversionReport)" />
    ///     is this over the tone's slots plus the trailing gain; the coverage audit
    ///     (<c>RsGearCoverageAudit</c>) probes it gear by gear.
    /// </summary>
    internal static IReadOnlyList<DcRigModule> ConvertGear(
        RsGear gear, RsGearCatalog catalog, RsGearOverrides overrides, ConversionReport report)
    {
        var modules = new List<DcRigModule>(2);
        AppendGear(gear, catalog, overrides, report, modules);
        return modules;
    }

    private static void AppendGear(
        RsGear gear, RsGearCatalog catalog, RsGearOverrides overrides, ConversionReport report, List<DcRigModule> modules)
    {
        switch (gear)
        {
            case RsAmp amp when RsAmpChains.TryGetModel(amp.GearKey, out var model, out var row):
                // Every amp the game ships runs as the game defines it.
                modules.Add(BuildRsAmp(amp, model, row!, catalog, report));
                break;
            case RsAmp amp:
                modules.Add(BuildAmp(amp, catalog, overrides, report));
                break;
            case RsCab cab:
                modules.Add(BuildCab(cab, overrides));
                if (BuildCabMic(cab, report) is { } mic) modules.Add(mic);
                break;
            default:
                if (BuildEffect(gear, catalog, overrides, report) is { } module)
                {
                    modules.Add(module);
                    if (BuildCompanion(gear, module, catalog, report) is { } companion) modules.Add(companion);
                }

                break;
        }
    }


    // ---------------------------------------------------------------- cab

    private static DcRigModule BuildCab(RsCab cab, RsGearOverrides ov)
    {
        var p = new ParamMap(Cab, ov, cab.GearKey);
        var model = ov.TryGetCabModel(cab.GearKey, out var m) ? m : GuessCabModel(cab.GearKey);
        p.Set("model", model);
        p.ApplyStatic();
        // mix / low-high cut keep module defaults; the mic is BuildCabMic's layer.
        return new DcRigModule(Cab, p.Build());
    }

    /// <summary>
    ///     The microphone layer behind a cabinet. 441 of the 450 RS cabinet keys
    ///     end in <c>_&lt;Mic&gt;_&lt;Position&gt;</c> (mic 57 / Condenser / Ribbon /
    ///     Tube, position Cone / Edge / OffAxis) — nine audibly different
    ///     recordings of one cabinet in Rocksmith, which one IR per cabinet would
    ///     render alike. The <c>micpos</c> module models exactly that axis
    ///     AFTER a cab node, so every such key gets one. The nine one-off
    ///     cabinets (PA, radio, gramophone …) carry no mic and get none.
    ///     <para>
    ///         Choices (each is a reading of the module, not a tuned number):
    ///         <list type="bullet">
    ///             <item>mic: 57 → "57", Ribbon → "121", Condenser → Condenser, Tube →
    ///             Tube (the module's large-diaphragm tube
    ///             condenser, fitted to the Neumann U 67's published cardioid
    ///             response);</item>
    ///             <item>position: Cone → 0 (on the cap, the module's on-axis end),
    ///             Edge → 1 (the cone edge), OffAxis → on the cap (0) ANGLED 45°
    ///             (<see cref="MicLayerOffAxisDegrees" />: the module's `angleDeg`
    ///             applies the mic's own published polar response, which is what
    ///             turning a capsule off the axis changes);</item>
    ///             <item>distance 15 cm: <c>dcdsp_micpos.h</c>'s <c>kFloorOnsetCm</c>,
    ///             the farthest the module goes before its floor reflection combs.
    ///             It adds the least proximity bass (+4 dB shelf weight 0.5 instead of
    ///             0.83 at the 5 cm default) on top of an IR that is already a
    ///             close-mic capture;</item>
    ///             <item>level 1/1.3: the core scales <c>level</c> by 1.3, so this is
    ///             exactly unity — the layer changes colour, never loudness.</item>
    ///         </list>
    ///     </para>
    /// </summary>
    private static DcRigModule? BuildCabMic(RsCab cab, ConversionReport report)
    {
        if (!TryParseCabMic(cab.GearKey, out var mic, out var position)) return null;

        var p = new ParamMap(MicPos, null, cab.GearKey);
        switch (mic)
        {
            case "57":
                p.Set("mic", 0f);
                break;
            case "Ribbon":
                p.Set("mic", 1f);
                break;
            case "Condenser":
                p.Set("mic", 3f);
                break;
            default: // "Tube"
                p.Set("mic", 4f);
                break;
        }

        switch (position)
        {
            case "Cone":
                p.Set("position", 0f);
                break;
            case "Edge":
                p.Set("position", 1f);
                break;
            default: // "OffAxis"
                p.Set("position", 0f);
                p.Set("angleDeg", MicLayerOffAxisDegrees);
                break;
        }

        p.Set("distanceCm", MicLayerDistanceCm);
        p.Set("level", MicLayerUnityLevel);
        return new DcRigModule(MicPos, p.Build());
    }

    /// <summary><c>dcdsp_micpos.h</c> <c>kFloorOnsetCm</c>: no floor reflection yet,
    /// half the proximity shelf. See <see cref="BuildCabMic" />.</summary>
    internal const float MicLayerDistanceCm = 15f;

    /// <summary>The micpos core multiplies <c>level</c> by 1.3, so this is unity gain.</summary>
    internal const float MicLayerUnityLevel = 1f / 1.3f;

    /// <summary>
    ///     What Rocksmith's "Off Axis" placement becomes: the capsule on the cone's
    ///     centre, turned 45° off the speaker axis. 45° is the conventional
    ///     off-axis angle for a close-miked guitar speaker — Sound On Sound,
    ///     "Guitar Amp Recording" (Aug 2007) compares three SM57s on the grille at
    ///     the cone's centre, on axis, at ~22° and at ~45° — and Rocksmith states
    ///     no angle of its own. See <see cref="BuildCabMic" />.
    /// </summary>
    internal const float MicLayerOffAxisDegrees = 45f;

    /// <summary>Splits <c>Cab_Family_&lt;Mic&gt;_&lt;Position&gt;</c>; false for a key
    /// without both suffixes (the one-off cabinets, future custom gear).</summary>
    internal static bool TryParseCabMic(string cabKey, out string mic, out string position)
    {
        mic = position = "";
        var last = cabKey.LastIndexOf('_');
        if (last <= 0) return false;
        var prev = cabKey.LastIndexOf('_', last - 1);
        if (prev < 0) return false;

        var m = cabKey[(prev + 1)..last];
        var pos = cabKey[(last + 1)..];
        if (m is not ("57" or "Condenser" or "Ribbon" or "Tube")) return false;
        if (pos is not ("Cone" or "Edge" or "OffAxis")) return false;

        mic = m;
        position = pos;
        return true;
    }

    private static int GuessCabModel(string key)
    {
        // Native cab roster (dcdsp_cab.h, labels in DspModuleFactory.cpp). Only
        // reached for keys the catalogue-keyed override table doesn't cover
        // (future DLC / custom gear): bass cabs onto the bass IRs (8x10, 1x15,
        // else 4x10), guitar cabs by speaker count.
        if (key.StartsWith("Bass_Cab", StringComparison.Ordinal))
        {
            if (ContainsAny(key, "810", "610", "8x10")) return 10;
            if (ContainsAny(key, "115", "15BC", "1x15")) return 11;
            return 9;
        }

        if (ContainsAny(key, "412", "4x12", "1960")) return 2;
        if (ContainsAny(key, "212", "2x12", "215")) return 1;
        if (ContainsAny(key, "112", "1x12", "110")) return 0;
        return 1; // a 2x12 is the safe middle default for an unknown cab
    }

    // ------------------------------------------------------- pedals / racks

    private static DcRigModule? BuildEffect(RsGear gear, RsGearCatalog catalog, RsGearOverrides ov, ConversionReport report)
    {
        // Authoritative routing comes from the catalogue (the tone JSON Category
        // is sometimes a loc-id); fall back to the tone's own Category only when
        // the key is unknown to the catalogue.
        var category = catalog.GetCategory(gear.GearKey) ?? gear.Category ?? "";

        switch (category)
        {
            case "Distortion":
                return BuildDistortion(gear, catalog, ov, report);
            case "Delay":
                return BuildDelay(gear, catalog, ov, report);
            case "Reverb":
                return BuildReverb(gear, catalog, ov);
            case "Modulation":
                return BuildModulation(gear, catalog, ov, report);
            case "Tremolo":
                return BuildTremolo(gear, catalog);
            case "Dynamics":
                return BuildDynamics(gear, catalog, ov, report);
            case "Filter":
                return BuildFilter(gear, catalog, ov, report);
            case "Pitch":
                return BuildPitch(gear, catalog, ov, report);
            default:
                report.Add(ConversionNoteKind.Skipped, gear.GearKey,
                    category.Length == 0
                        ? "unknown gear (not in catalogue) — skipped"
                        : $"no module for category '{category}' — skipped");
                return null;
        }
    }

    private static DcRigModule BuildDistortion(RsGear gear, RsGearCatalog catalog, RsGearOverrides ov, ConversionReport report)
    {
        // Explicit drive-module routing first: fuzz-family gear gets the real
        // fuzz/bigmuff/klon module instead of the generic drive approximation,
        // and the 0.9 gear wave added rat / ds1 / hm2 / bassod for the pedals
        // the two generic drive modules only ever approximated.
        if (ov.TryGetTargetModule(gear.GearKey, out var target))
        {
            switch (target)
            {
                case Fuzz or BigMuff or Klon: return BuildFuzzFamily(gear, catalog, ov, target);
                case Rat: return BuildRat(gear, catalog, ov);
                case Ds1: return BuildDs1(gear, catalog, ov);
                case Hm2: return BuildHm2(gear, catalog, ov);
                case BassOd: return BuildBassOd(gear, catalog, ov, report);
            }
        }

        var heavy = ov.IsForcedDistortion(gear.GearKey) ||
                    (!ov.IsForcedOverdrive(gear.GearKey) &&
                     ContainsAny(gear.GearKey, "Fuzz", "Metal", "Rect", "Death", "Grind", "Muff", "Distortion", "Doom"));

        var module = heavy ? Distortion : Overdrive;
        var driveParam = heavy ? "gain" : "drive";
        var p = new ParamMap(module, ov, gear.GearKey);

        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            switch (pot)
            {
                case "Gain" or "Drive" or "Distortion" or "Dist" or "Fuzz":
                    p.Set(driveParam, Norm01(val, knob));
                    break;
                case "Tone" or "Filter" or "Contour":
                    p.Set("tone", Norm01(val, knob));
                    break;
                case "Level" or "Output" or "Volume" or "Vol":
                    p.Set("level", Norm01(val, knob));
                    break;
            }
        }

        p.ApplyStatic();
        return new DcRigModule(module, p.Build());
    }

    /// <summary>Maps a fuzz-family drive pedal onto its dedicated module. The
    /// three modules share a drive/tone/level shape under different param ids
    /// (fuzz: fuzz/tone/level, bigmuff: sustain/tone/volume, klon: gain/treble/output).</summary>
    private static DcRigModule BuildFuzzFamily(RsGear gear, RsGearCatalog catalog, RsGearOverrides ov, string module)
    {
        var (driveParam, toneParam, levelParam) = module switch
        {
            BigMuff => ("sustain", "tone", "volume"),
            Klon => ("gain", "treble", "output"),
            _ => ("fuzz", "tone", "level")
        };

        var p = new ParamMap(module, ov, gear.GearKey);
        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            switch (pot)
            {
                case "Gain" or "Drive" or "Distortion" or "Dist" or "Fuzz" or "Sustain":
                    p.Set(driveParam, Norm01(val, knob));
                    break;
                case "Tone" or "Filter" or "Contour" or "Treble":
                    p.Set(toneParam, Norm01(val, knob));
                    break;
                case "Level" or "Output" or "Volume" or "Vol":
                    p.Set(levelParam, Norm01(val, knob));
                    break;
            }
        }

        p.ApplyStatic();
        return new DcRigModule(module, p.Build());
    }

    private static DcRigModule BuildDelay(RsGear gear, RsGearCatalog catalog, RsGearOverrides ov, ConversionReport report)
    {
        // 0.9 gear wave: the bucket-brigade pedals get the real BBD model and
        // the two-tap studio rack gets the multitap, instead of both being
        // approximated by the tape echo / clean digital delay.
        if (ov.TryGetTargetModule(gear.GearKey, out var target))
        {
            switch (target)
            {
                case BbdDelay: return BuildBbdDelay(gear, catalog, ov, report);
                case Multitap: return BuildMultitap(gear, catalog, ov, report);
            }
        }

        // Tape/analog echoes get the dedicated tapedelay module (wow/flutter,
        // darker repeats); everything else stays on the clean digital delay.
        var isTape = ContainsAny(gear.GearKey, "Tape", "Analog", "Analogue", "OilCan", "Valve");
        var module = isTape ? TapeDelay : Delay;
        var p = new ParamMap(module, null, gear.GearKey);
        var droppedMod = false;

        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            switch (pot)
            {
                case "Time" or "Delay":
                    p.Set("timeMs", Direct(val, knob));
                    break;
                case "Feedback" or "Regen" or "Repeats":
                    p.Set("feedback", Norm01(val, knob)); // clamped to 0..0.95
                    break;
                case "Mix" or "Level" or "Blend":
                    p.Set("mix", Norm01(val, knob));
                    break;
                case "Tone" or "Color" or "Cutoff":
                    p.Set("toneHz", knob is { UnitType: RsKnobUnit.Hz or RsKnobUnit.Khz }
                        ? Direct(val, knob)
                        : NaturalScaled(val, knob, 500f, 12000f));
                    break;
                case "Depth" or "Mod" or "Chorus" when isTape:
                    // Modulated repeats survive as tape wow/flutter.
                    p.Set("wowFlutter", Norm01(val, knob));
                    break;
                case "Rate" when isTape:
                    break; // wow rate is fixed on the tape model — folded
                case "Rate" or "Depth" or "Mod" or "Chorus":
                    droppedMod = true;
                    break;
            }
        }

        if (!isTape && ContainsAny(gear.GearKey, "PingPong", "Stereo", "Ping"))
            p.Set("pingPong", 1f);
        if (droppedMod)
            report.Add(ConversionNoteKind.Approximated, gear.GearKey, "modulated-delay depth/rate dropped (static delay)");

        return new DcRigModule(module, p.Build());
    }

    /// <summary>Calibration: Rocksmith's reverb engine is far drier per unit of
    /// its Mix knob than our (Wave-2A modulated) reverbs — a 1:1 mapping makes
    /// every RS-converted tone swim (A/B against the game).
    /// Converted reverb wet levels scale by this factor across all three
    /// reverb-family targets; trim it with the 7.6 render harness when that
    /// lands rather than per-tone.</summary>
    internal const float RsReverbMixScale = 0.5f;

    private static float ReverbMix(float val, RsGearKnob? knob) => RsReverbMixScale * Norm01(val, knob);

    private static DcRigModule BuildReverb(RsGear gear, RsGearCatalog catalog, RsGearOverrides ov)
    {
        // 0.9 gear wave: the room/chamber/hall reverbs get the real FDN hall,
        // which is also the only one of our reverb modules with a `diffusion`
        // param — the RS racks carry a Diffusion knob this converter DROPPED.
        if (ov.TryGetTargetModule(gear.GearKey, out var target) && target == HallReverb)
            return BuildHallReverb(gear, catalog, ov);

        if (ContainsAny(gear.GearKey, "Spring")) return BuildSpringReverb(gear, catalog);
        if (ContainsAny(gear.GearKey, "Plate")) return BuildPlateReverb(gear, catalog);

        var p = new ParamMap(Reverb, null, gear.GearKey);

        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            switch (pot)
            {
                case "Time" or "Decay" or "Size" or "Length" or "Room":
                    p.Set("roomSize", Norm01(val, knob));
                    break;
                case "Damp" or "Damping":
                    p.Set("damping", Norm01(val, knob));
                    break;
                case "Tone" or "Color":
                    // brighter tone ⇒ less damping
                    p.Set("damping", 1f - Norm01(val, knob));
                    break;
                case "Mix" or "Level" or "Blend" or "Wet":
                    p.Set("mix", ReverbMix(val, knob));
                    break;
                    // Depth / Pre-delay have no reverb param — folded.
            }
        }

        // No mix pot on the RS gear ⇒ our catalogue default (tuned for OUR
        // presets) is still too wet for RS parity — pin the scaled default.
        p.SetIfAbsent("mix", RsReverbMixScale * 0.3f);

        return new DcRigModule(Reverb, p.Build());
    }

    private static DcRigModule BuildSpringReverb(RsGear gear, RsGearCatalog catalog)
    {
        var p = new ParamMap(SpringReverb, null, gear.GearKey);

        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            switch (pot)
            {
                case "Time" or "Decay" or "Size" or "Length":
                    p.Set("decay", Norm01(val, knob));
                    break;
                case "Depth":
                    // spring "depth" is how pronounced the springs are → drip
                    p.Set("drip", Norm01(val, knob));
                    break;
                case "Tone" or "Color":
                    p.Set("tone", Norm01(val, knob));
                    break;
                case "Mix" or "Level" or "Blend" or "Wet":
                    p.Set("mix", ReverbMix(val, knob));
                    break;
            }
        }

        p.SetIfAbsent("mix", RsReverbMixScale * 0.3f);

        return new DcRigModule(SpringReverb, p.Build());
    }

    private static DcRigModule BuildPlateReverb(RsGear gear, RsGearCatalog catalog)
    {
        var p = new ParamMap(PlateReverb, null, gear.GearKey);

        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            switch (pot)
            {
                case "Time" or "Decay" or "Size" or "Length" or "Room":
                    p.Set("decay", Norm01(val, knob));
                    break;
                case "Damp" or "Damping":
                    p.Set("damping", Norm01(val, knob));
                    break;
                case "Tone" or "Color":
                    p.Set("damping", 1f - Norm01(val, knob)); // brighter ⇒ less damping
                    break;
                case "Depth":
                    p.Set("width", Norm01(val, knob));
                    break;
                case "Mix" or "Level" or "Blend" or "Wet":
                    p.Set("mix", ReverbMix(val, knob));
                    break;
            }
        }

        p.SetIfAbsent("mix", RsReverbMixScale * 0.3f);

        return new DcRigModule(PlateReverb, p.Build());
    }

    private static DcRigModule BuildModulation(RsGear gear, RsGearCatalog catalog, RsGearOverrides ov, ConversionReport report)
    {
        // Pre-existing routing gap closed in the same pass: Pedal_RingMod is a
        // RING MODULATOR and `ringmod` has existed since the 0.15.1 wave, but
        // the key matched none of the substring tests below and fell through to
        // the chorus fallback — an audibly wrong effect, not an approximation.
        if (ov.TryGetTargetModule(gear.GearKey, out var target) && target == RingMod)
            return BuildRingMod(gear, catalog, ov, report);

        // Dedicated modules first (0.15.2 catalogue): phasers → phaser, rotary
        // speakers → rotary, photocell vibes → univibe. Order matters: check
        // rotary before vibe so "BakedRotatoe" lands on rotary while
        // "RotaVibe"/"Supervibe" land on univibe.
        // The Wwise Flanger choruses go through their banks' own curves.
        // Read from the player's install; without one, the generic chorus below.
        if (RsGameCurves.Current.Choruses.TryGetValue(gear.GearKey, out var chorusBank))
            return BuildFlangerChorus(gear, chorusBank, catalog, report);
        if (ContainsAny(gear.GearKey, "Phaser", "Phase"))
            return BuildSweepMod(gear, catalog, Phaser, 0.05f, 8f);
        if (ContainsAny(gear.GearKey, "Rotary", "Rotatoe", "Leslie"))
            return BuildRotary(gear, catalog);
        if (ContainsAny(gear.GearKey, "Vibe", "Univibe", "Uni-Vibe"))
            return BuildSweepMod(gear, catalog, UniVibe, 0.1f, 8f);

        var module = ContainsAny(gear.GearKey, "Flanger", "Flange") ? Flanger : Chorus;
        var depthMin = module == Flanger ? 0.5f : 1f;
        var depthMax = module == Flanger ? 5f : 15f;
        // RS mod "Rate"/"Speed" is typically a number 1..100, NOT Hz — scale it
        // into the module's real rate range (chorus 0.1..5, flanger 0.05..3)
        // rather than passing it through and clamping to the ceiling.
        var rateMin = module == Flanger ? 0.05f : 0.1f;
        var rateMax = module == Flanger ? 3f : 5f;
        var p = new ParamMap(module, null, gear.GearKey);

        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            switch (pot)
            {
                case "Rate" or "Speed":
                    p.Set("rateHz", NaturalScaled(val, knob, rateMin, rateMax));
                    break;
                case "Depth" or "Intensity" or "Width":
                    p.Set("depthMs", NaturalScaled(val, knob, depthMin, depthMax));
                    break;
                case "Mix" or "Level" or "Blend":
                    p.Set("mix", Norm01(val, knob));
                    break;
                case "Feedback" or "Regen" or "Resonance" or "Res" when module == Flanger:
                    p.Set("feedback", Norm01(val, knob));
                    break;
                // The chorus carries a stereo spread and a wet-path high-pass (the
                // BBD bass cut); the RS chorus pedals' Stereo and Low Filter knobs
                // are exactly those.
                case "Stereo" when module == Chorus:
                    p.Set("spread", Norm01(val, knob));
                    break;
                case "LoFilter" when module == Chorus:
                    p.Set("wetHpfHz", NaturalScaled(val, knob, 20f, 400f));
                    break;
            }
        }

        return new DcRigModule(module, p.Build());
    }

    /// <summary>Shared phaser/univibe mapping: both have a 0..1 depth + wet mix
    /// and an Hz sweep rate (Hz knobs pass through; RS 1..100 numbers scale into
    /// the module's rate range). Phaser additionally takes feedback/resonance.</summary>
    private static DcRigModule BuildSweepMod(RsGear gear, RsGearCatalog catalog, string module, float rateMin, float rateMax)
    {
        var p = new ParamMap(module, null, gear.GearKey);

        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            switch (pot)
            {
                case "Rate" or "Speed":
                    p.Set("rateHz", knob is { UnitType: RsKnobUnit.Hz }
                        ? Direct(val, knob)
                        : NaturalScaled(val, knob, rateMin, rateMax));
                    break;
                case "Depth" or "Intensity" or "Width":
                    p.Set("depth", Norm01(val, knob));
                    break;
                case "Mix" or "Level" or "Blend":
                    p.Set("mix", Norm01(val, knob));
                    break;
                case "Feedback" or "Regen" or "Resonance" or "Res" when module == Phaser:
                    p.Set("feedback", Norm01(val, knob));
                    break;
            }
        }

        return new DcRigModule(module, p.Build());
    }

    private static DcRigModule BuildRotary(RsGear gear, RsGearCatalog catalog)
    {
        var p = new ParamMap(Rotary, null, gear.GearKey);

        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            switch (pot)
            {
                case "Rate" or "Speed":
                    p.Set("speed", Norm01(val, knob)); // 0 slow/chorale → 1 fast/tremolo
                    break;
                case "Depth" or "Intensity":
                    p.Set("depth", Norm01(val, knob));
                    break;
                case "Mix" or "Level" or "Blend":
                    p.Set("mix", Norm01(val, knob));
                    break;
                    // Balance (horn/drum) has no rotary param — folded.
            }
        }

        return new DcRigModule(Rotary, p.Build());
    }

    private static DcRigModule BuildTremolo(RsGear gear, RsGearCatalog catalog)
    {
        // RS files its amp-style vibrato units (Amp Vibe, Multi-Vibe, Stereo
        // Analog Vibrato) under Tremolo; those are true pitch vibratos → the
        // dedicated vibrato module. Real tremolos stay tremolo.
        if (ContainsAny(gear.GearKey, "Vibe", "Vibrato"))
            return BuildVibrato(gear, catalog);

        var p = new ParamMap(Tremolo, null, gear.GearKey);
        // Pedal_Tremolo / Pedal_MultiTrem / Pedal_TremOle have no Depth knob: their
        // only intensity control is "Mix", and a tremolo's wet mix IS its depth
        // (the dry share is the part the LFO never touches).
        var mixIsDepth = !HasAny(gear, "Depth", "Intensity", "Amount");

        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            switch (pot)
            {
                case "Rate" or "Speed":
                    // RS tremolo Speed is a number 1..100, not Hz — scale to the
                    // tremolo rate range (0.1..12) instead of clamping to max.
                    p.Set("rateHz", NaturalScaled(val, knob, 0.1f, 12f));
                    break;
                case "Depth" or "Intensity" or "Amount":
                    p.Set("depth", Norm01(val, knob));
                    break;
                case "Mix" when mixIsDepth:
                    p.Set("depth", Norm01(val, knob));
                    break;
                case "Waveform" or "Shape" or "Wave":
                    // RS enum is already a 0-based index (sine/tri/square → 0/1/2).
                    p.Set("shape", MathF.Round(val));
                    break;
            }
        }

        return new DcRigModule(Tremolo, p.Build());
    }

    private static DcRigModule BuildVibrato(RsGear gear, RsGearCatalog catalog)
    {
        var p = new ParamMap(Vibrato, null, gear.GearKey);

        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            switch (pot)
            {
                case "Rate" or "Speed":
                    p.Set("rateHz", NaturalScaled(val, knob, 0.1f, 10f));
                    break;
                case "Depth" or "Intensity":
                    p.Set("depthMs", NaturalScaled(val, knob, 0.5f, 12f));
                    break;
                case "Mix" when !p.Has("depthMs"):
                    // A true vibrato has no wet/dry — the RS Mix folds into depth
                    // (an explicit Depth knob, when present, wins).
                    p.Set("depthMs", NaturalScaled(val, knob, 0.5f, 12f));
                    break;
                case "Waveform" or "Shape" or "Wave":
                    p.Set("waveform", MathF.Round(val)); // clamped 0..1 (sine/triangle)
                    break;
            }
        }

        return new DcRigModule(Vibrato, p.Build());
    }

    private static DcRigModule BuildDynamics(RsGear gear, RsGearCatalog catalog, RsGearOverrides ov, ConversionReport report)
    {
        // 0.9 gear wave: a limiter, a multiband compressor and an optical
        // compressor exist now, so the three RS pedals whose whole identity is
        // one of those stop being generic studio compressors.
        if (ov.TryGetTargetModule(gear.GearKey, out var target))
        {
            switch (target)
            {
                case Limiter: return BuildLimiter(gear, catalog, ov);
                case Multiband when gear.GearKey == RsGameCurves.MbCompKey && RsGameCurves.Current.MbComp is { } mbComp:
                    return BuildMbComp(gear, catalog, report, mbComp);
                case Multiband: return BuildMultiband(gear, catalog, ov, report);
                case OptoComp: return BuildOptoComp(gear, catalog, ov, report);
                case Compressor when gear.GearKey == RsGameCurves.CompressionKey && RsGameCurves.Current.Compression is { } curves:
                    return BuildWwiseCompressor(gear, catalog, ov, curves);
            }
        }

        // Squishy sustain-style pedals (steered per key via the overrides table)
        // are a sustainer, not a studio compressor.
        if (ov.IsForcedSustainer(gear.GearKey) || ContainsAny(gear.GearKey, "Sustain"))
            return BuildSustainer(gear, catalog);

        var gate = ContainsAny(gear.GearKey, "Gate", "NoiseGate");
        var module = gate ? Gate : Compressor;
        var p = new ParamMap(module, null, gear.GearKey);

        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            if (gate)
            {
                switch (pot)
                {
                    case "Threshold" or "Thresh" or "Sens" or "Sensitivity":
                        p.Set("thresholdDb", knob is { UnitType: RsKnobUnit.Db }
                            ? Direct(val, knob)
                            : NaturalScaled(val, knob, -80f, 0f));
                        break;
                    case "Decay" or "Release":
                        p.Set("releaseMs", Direct(val, knob));
                        break;
                    case "Rate":
                        // Pedal_NoiseGate's "Rate" (1..100) is how fast it closes:
                        // faster = shorter release, the same DESCENDING reading the
                        // limiter's Rate gets (500 .. 5 ms).
                        p.Set("releaseMs", NaturalScaled(val, knob, 500f, 5f));
                        break;
                    case "Attack":
                        p.Set("attackMs", Direct(val, knob));
                        break;
                }
            }
            else
            {
                switch (pot)
                {
                    case "Threshold" or "Thresh":
                        p.Set("thresholdDb", knob is { UnitType: RsKnobUnit.Db }
                            ? Direct(val, knob)
                            : NaturalScaled(val, knob, -60f, 0f));
                        break;
                    case "Ratio":
                        p.Set("ratio", knob is { UnitType: RsKnobUnit.Enum } or null
                            ? NaturalScaled(val, knob, 1f, 20f)
                            : Direct(val, knob));
                        break;
                    case "Comp" or "Compress" or "Sustain" or "Sens" or "Amount":
                        p.Set("ratio", NaturalScaled(val, knob, 1f, 20f));
                        break;
                    case "Attack":
                        p.Set("attackMs", Direct(val, knob));
                        break;
                    case "Release":
                        p.Set("releaseMs", Direct(val, knob));
                        break;
                    case "Level" or "Gain" or "Makeup" or "Output":
                        p.Set("makeupDb", NaturalScaled(val, knob, 0f, 24f));
                        break;
                }
            }
        }

        return new DcRigModule(module, p.Build());
    }

    private static DcRigModule BuildSustainer(RsGear gear, RsGearCatalog catalog)
    {
        var p = new ParamMap(Sustainer, null, gear.GearKey);

        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            switch (pot)
            {
                case "Sustain" or "Comp" or "Compress" or "Smash" or "Sens" or "Amount":
                    p.Set("sustain", Norm01(val, knob));
                    break;
                case "Attack" or "Rate":
                    p.Set("attack", Norm01(val, knob));
                    break;
                case "Tone":
                    p.Set("tone", Norm01(val, knob));
                    break;
                case "Level" or "Output" or "Volume" or "Gain" or "Makeup":
                    p.Set("level", Norm01(val, knob));
                    break;
            }
        }

        return new DcRigModule(Sustainer, p.Build());
    }

    // ---------------------------------------------------------------- pitch

    private static DcRigModule BuildPitch(RsGear gear, RsGearCatalog catalog, RsGearOverrides ov, ConversionReport report)
    {
        // 0.9 gear wave: an octave-UP pedal is an octave FUZZ circuit, and
        // `octfuzz` is it. Until this the converter rendered one as a SUB-octave
        // octaver and apologised for it in the report — the wrong direction, not
        // a lossy approximation.
        if (ov.TryGetTargetModule(gear.GearKey, out var target) && target == OctFuzz)
            return BuildOctFuzz(gear, catalog, ov);

        // Whammy / pitch-shift gear exposes a semitone knob → harmonizer;
        // octave pedals (Mix/Tone only) → octaver.
        if (HasAny(gear, "Pitch", "Pitch1"))
        {
            var h = new ParamMap(Harmonizer, null, gear.GearKey);
            foreach (var (pot, val) in gear.Pots())
            {
                catalog.TryGetKnob(gear.GearKey, pot, out var knob);
                switch (pot)
                {
                    case "Pitch" or "Pitch1":
                        var semis = MathF.Round(val);
                        h.Set("semitones", semis); // clamped to the module's ±12
                        if (MathF.Abs(semis) > 12f)
                        {
                            report.Add(ConversionNoteKind.Approximated, gear.GearKey,
                                $"pitch shift of {semis:0} semitones clamped to ±12");
                        }

                        break;
                    case "Mix" or "Level" or "Blend":
                        h.Set("mix", Norm01(val, knob));
                        break;
                        // Tone / Pan have no harmonizer param — folded.
                }
            }

            return new DcRigModule(Harmonizer, h.Build());
        }

        var p = new ParamMap(Octaver, null, gear.GearKey);
        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            switch (pot)
            {
                case "Mix" or "Level" or "Blend":
                    p.Set("sub1", Norm01(val, knob));
                    break;
                case "Tone":
                    p.Set("toneHz", NaturalScaled(val, knob, 500f, 5000f));
                    break;
            }
        }

        if (ContainsAny(gear.GearKey, "OctaveUp", "Octavius"))
        {
            report.Add(ConversionNoteKind.Approximated, gear.GearKey,
                "octave-up pedal rendered as a sub-octave octaver");
        }

        return new DcRigModule(Octaver, p.Build());
    }

    private static DcRigModule? BuildFilter(RsGear gear, RsGearCatalog catalog, RsGearOverrides ov, ConversionReport report)
    {
        // 0.9 gear wave / gap close: the envelope-swept synth filters and the
        // acoustic emulator were SKIPPED outright (no EQ gain band ⇒ the
        // "non-EQ filter with no equivalent" drop below). They have modules now.
        if (ov.TryGetTargetModule(gear.GearKey, out var target))
        {
            switch (target)
            {
                case Svf when gear.GearKey == RsGameCurves.LoFiFilterKey && RsGameCurves.Current.LoFi is { } lofi:
                    return BuildLoFiFilter(gear, catalog, report, lofi);
                case Svf: return BuildSvf(gear, catalog, ov);
                case AcousticSim: return BuildAcousticSim(gear, catalog, ov, report);
                case Boost: return BuildTrebleBooster(gear, catalog, ov);
                case BitCrusher: return BuildBitCrusher(gear, catalog, ov, report);
                case Harmonizer when gear.GearKey == RsGameCurves.BassEmulatorKey && RsGameCurves.Current.BassEmulator is not null:
                    return BuildBassEmulator(gear, ov);
            }
        }

        // The most-used gear in the library, mapped through the game's own curves.
        if (gear.GearKey == RsGameCurves.StudioEqKey && RsGameCurves.Current.StudioEq is { } studioEq)
            return BuildStudioEq(gear, catalog, ov, studioEq);

        // Envelope-followed filters first (AutoWah contains "Wah" too), then
        // pedal wahs, then graphic EQs, then the parametric 3-band fallback.
        if (ContainsAny(gear.GearKey, "AutoFilter", "AutoWah", "Envelope"))
            return BuildEnvFilter(gear, catalog);
        if (ContainsAny(gear.GearKey, "Wah", "Talk"))
            return BuildWah(gear, catalog);
        if (TryBuildGraphicEq(gear, catalog, out var graphicEq))
            return graphicEq;

        var p = new ParamMap(Eq, ov, gear.GearKey);

        // Track whether any GAIN band matched — a real EQ is defined by its
        // gain bands; freq/Q without a gain band is not an EQ.
        // Low band.
        var anyBand = SetBandGain(gear, catalog, p, "lowGainDb", "Bass", "Low");
        SetBandFreq(gear, catalog, p, report, "lowFreq", "BassFreq", "LowFreq");

        // Mid band: prefer LoMid/Mid; fold HiMid as an approximation if the mid
        // gain is already taken.
        // Mid band: LoMid/Mid. A HiMid next to it is NOT folded any more: it
        // gets an `eq` of its own (BuildHiMidBand), because the `eq` module has
        // one bell and Rack_StudioEQ — the most-used gear in the owner's library,
        // 418 of 580 tones, HiMid moved in 375 of them — has two.
        var midSet = SetBandGain(gear, catalog, p, "midGainDb", "LoMid", "Mid", "Middle");
        anyBand |= midSet;
        if (!midSet)
        {
            anyBand |= SetBandGain(gear, catalog, p, "midGainDb", "HiMid");
            SetBandFreq(gear, catalog, p, report, "midFreq", "HiMidFreq");
            SetBandQ(gear, catalog, p, "midQ", "HiMidQ");
        }
        else
        {
            SetBandFreq(gear, catalog, p, report, "midFreq", "LoMidFreq", "MidFreq");
            SetBandQ(gear, catalog, p, "midQ", "LoMidQ", "MidQ");
        }

        // High band.
        anyBand |= SetBandGain(gear, catalog, p, "highGainDb", "Treble", "High");
        SetBandFreq(gear, catalog, p, report, "highFreq", "TrebleFreq", "HighFreq");

        // A Filter-category pedal that matched NO EQ band (e.g. BobFilter,
        // BitCrusher, SynthFilter, RingMod, RangeBooster) is not an EQ — don't
        // emit a no-op eq that silently does nothing; skip it and say so, so the
        // ConversionReport is honest about what wasn't reproduced.
        if (!anyBand)
        {
            report.Add(ConversionNoteKind.Skipped, gear.GearKey, "non-EQ filter/effect with no equivalent module — skipped");
            return null;
        }

        return new DcRigModule(Eq, p.Build());
    }

    private static DcRigModule BuildWah(RsGear gear, RsGearCatalog catalog)
    {
        var p = new ParamMap(Wah, null, gear.GearKey);

        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            switch (pot)
            {
                case "Pedal" or "Position":
                    p.Set("position", Norm01(val, knob));
                    break;
                case "Sens" or "Sensitivity":
                    p.Set("sensitivity", Norm01(val, knob));
                    break;
                case "Auto":
                    // RS enum OFF/ON → the module's manual/auto sweep mode.
                    p.Set("mode", MathF.Round(val));
                    break;
                case "Res" or "Resonance" or "Q":
                    p.Set("resonance", Norm01(val, knob));
                    break;
                case "Mix" or "Level" or "Blend":
                    p.Set("mix", Norm01(val, knob));
                    break;
                    // Speed (auto-sweep rate) / Attack / Release have no wah param — folded.
            }
        }

        return new DcRigModule(Wah, p.Build());
    }

    private static DcRigModule BuildEnvFilter(RsGear gear, RsGearCatalog catalog)
    {
        var p = new ParamMap(EnvFilter, null, gear.GearKey);

        foreach (var (pot, val) in gear.Pots())
        {
            catalog.TryGetKnob(gear.GearKey, pot, out var knob);
            switch (pot)
            {
                case "Sens" or "Sensitivity":
                    p.Set("sensitivity", Norm01(val, knob));
                    break;
                case "Res" or "Resonance" or "Q":
                    p.Set("resonance", Norm01(val, knob));
                    break;
                case "Mix" or "Level" or "Blend":
                    p.Set("mix", Norm01(val, knob));
                    break;
                    // Attack / Release / FilterType have no envfilter param — folded.
            }
        }

        return new DcRigModule(EnvFilter, p.Build());
    }

    // ------------------------------------------- 0.9 gear wave: shared helpers

    /// <summary>A ±12 dB shelf/bell value from an RS knob: a dB-unit knob passes
    /// through (khz-safe), a 1..100 number knob scales across the full range.</summary>
    private static float Shelf(float val, RsGearKnob? knob)
        => knob is { UnitType: RsKnobUnit.Db } ? Direct(val, knob) : NaturalScaled(val, knob, -12f, 12f);

    // ---------------------------------------------------------- graphic EQ

    private static readonly (float Hz, string Param)[] GraphicEqBands =
    [
        (31f, "band31"), (63f, "band63"), (125f, "band125"), (250f, "band250"),
        (500f, "band500"), (1000f, "band1k"), (2000f, "band2k"), (4000f, "band4k"),
        (8000f, "band8k"), (16000f, "band16k")
    ];

    /// <summary>Detects the two RS graphic-EQ shapes and maps each gear band's
    /// gain onto the NEAREST (log-distance) band of the 10-band graphiceq module:
    /// numeric pot suffixes ARE the centre frequency (Pedal_EQ5/EQ8,
    /// Bass_Pedal_BassEQ8: <c>"63": -6</c>), and named dB bands with a
    /// <c>&lt;Band&gt;Freq</c> partner knob (Rack_StudioGraphicEQ) place their
    /// gain at the partner's frequency value. Not a graphic EQ ⇒ false.</summary>
    private static bool TryBuildGraphicEq(RsGear gear, RsGearCatalog catalog, out DcRigModule? module)
    {
        module = null;
        var bands = new List<(float Hz, float GainDb)>();

        foreach (var (pot, val) in gear.Pots())
        {
            if (float.TryParse(pot, NumberStyles.Float, CultureInfo.InvariantCulture, out var hz) && hz >= 20f)
                bands.Add((hz, val));
        }

        if (bands.Count == 0 && ContainsAny(gear.GearKey, "GraphicEQ"))
        {
            foreach (var (pot, val) in gear.Pots())
            {
                if (pot.EndsWith("Freq", StringComparison.Ordinal)) continue;
                if (!gear.TryGetKnob(pot + "Freq", out var freq)) continue;
                catalog.TryGetKnob(gear.GearKey, pot + "Freq", out var freqKnob);
                var hz = Direct(freq, freqKnob); // khz → Hz
                if (hz >= 20f) bands.Add((hz, val));
            }
        }

        if (bands.Count < 2) return false;

        // Sorted by frequency so a two-bands-fold-to-one collision resolves
        // deterministically (the higher band wins) regardless of JSON order.
        bands.Sort((a, b) => a.Hz.CompareTo(b.Hz));
        var p = new ParamMap(GraphicEq, null, gear.GearKey);
        foreach (var (hz, gainDb) in bands)
            p.Set(NearestGraphicEqBand(hz), gainDb); // dB passthrough, clamped ±12

        if (gear.TryGetKnob("Level", out var level) || gear.TryGetKnob("Volume", out level))
            p.Set("level", level);

        module = new DcRigModule(GraphicEq, p.Build());
        return true;
    }

    private static string NearestGraphicEqBand(float hz)
    {
        var best = GraphicEqBands[0].Param;
        var bestDist = float.MaxValue;
        foreach (var (bandHz, param) in GraphicEqBands)
        {
            var d = MathF.Abs(MathF.Log(hz / bandHz));
            if (d < bestDist)
            {
                bestDist = d;
                best = param;
            }
        }

        return best;
    }

    // ---------------------------------------------------------- EQ helpers

    private static bool SetBandGain(RsGear gear, RsGearCatalog catalog, ParamMap p, string param, params string[] pots)
    {
        foreach (var pot in pots)
        {
            if (gear.TryGetKnob(pot, out var val))
            {
                catalog.TryGetKnob(gear.GearKey, pot, out var knob);
                p.Set(param, Direct(val, knob)); // dB passthrough (khz-safe, though gains are dB)
                return true;
            }
        }

        return false;
    }

    /// <summary>Sets a band frequency; when <paramref name="report" /> is given, a
    /// frequency outside the target band's range is REPORTED rather than clamped
    /// silently (Rack_StudioEQ has its own builder and fits the widened `eq`).</summary>
    private static void SetBandFreq(RsGear gear, RsGearCatalog catalog, ParamMap p, ConversionReport? report, string param, params string[] pots)
    {
        foreach (var pot in pots)
        {
            if (gear.TryGetKnob(pot, out var val))
            {
                catalog.TryGetKnob(gear.GearKey, pot, out var knob);
                var hz = Direct(val, knob); // khz → Hz handled in Direct
                p.Set(param, hz);
                if (report is not null && TryGetParam(p.Module, param, out var spec) && (hz < spec.Min || hz > spec.Max))
                {
                    report.Add(ConversionNoteKind.Approximated, gear.GearKey,
                        string.Create(CultureInfo.InvariantCulture,
                            $"{pot} {hz:0} Hz clamped to the {p.Module} {param} range {spec.Min:0}-{spec.Max:0} Hz"));
                }

                return;
            }
        }
    }

    private static void SetBandQ(RsGear gear, RsGearCatalog catalog, ParamMap p, string param, params string[] pots)
    {
        foreach (var pot in pots)
        {
            if (gear.TryGetKnob(pot, out var val))
            {
                catalog.TryGetKnob(gear.GearKey, pot, out var knob);
                p.Set(param, NaturalScaled(val, knob, 0.3f, 8f)); // Wide→low Q, Narrow→high Q
                return;
            }
        }
    }

    private static bool HasAny(RsGear gear, params string[] pots)
    {
        foreach (var pot in pots)
        {
            if (gear.TryGetKnob(pot, out _))
                return true;
        }

        return false;
    }

    // ------------------------------------------------------- math helpers

    private static float Norm01(float v, RsGearKnob? knob)
    {
        float min, max;
        if (knob is not null && knob.MaxValue > knob.MinValue)
        {
            min = knob.MinValue;
            max = knob.MaxValue;
        }
        else
        {
            // No catalogue range: RS percent/number knobs run 0..100.
            min = 0f;
            max = 100f;
        }

        return Math.Clamp((v - min) / (max - min), 0f, 1f);
    }

    private static float NaturalScaled(float v, RsGearKnob? knob, float targetMin, float targetMax)
        => targetMin + Norm01(v, knob) * (targetMax - targetMin);

    private static float Direct(float v, RsGearKnob? knob)
        => knob is { UnitType: RsKnobUnit.Khz } ? v * 1000f : v;

    private static bool ContainsAny(string haystack, params string[] needles)
    {
        foreach (var n in needles)
        {
            if (haystack.Contains(n, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // ------------------------------------------------------------- ParamMap

    /// <summary>Accumulates params for one module, clamping every set to the
    /// target catalogue range and applying static override pins at the end.</summary>
    private sealed class ParamMap
    {
        private readonly string _module;
        private readonly RsGearOverrides? _overrides;
        private readonly string? _gearKey;
        private readonly Dictionary<string, float> _params = new(StringComparer.Ordinal);

        public ParamMap(string module, RsGearOverrides? overrides = null, string? gearKey = null)
        {
            _module = module;
            _overrides = overrides;
            _gearKey = gearKey;
        }

        public string Module => _module;

        public bool Has(string id) => _params.ContainsKey(id);

        public bool TryGet(string id, out float value) => _params.TryGetValue(id, out value);

        public void Set(string id, float value) => _params[id] = Clamp(_module, id, value);

        /// <summary>Sets only when no pot mapped the param yet — for pinning a
        /// calibrated value where the catalogue default would be wrong for RS
        /// parity (e.g. reverb mix).</summary>
        public void SetIfAbsent(string id, float value)
        {
            if (!_params.ContainsKey(id)) Set(id, value);
        }

        /// <summary>Applies static override pins for this gear (clamped to range).</summary>
        public void ApplyStatic()
        {
            if (_overrides is null || _gearKey is null) return;
            var pins = _overrides.GetStaticParams(_gearKey);
            if (pins is null) return;
            foreach (var pin in pins)
                Set(pin.Key, pin.Value);
        }

        public IReadOnlyDictionary<string, float> Build() => _params;
    }
}
