using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Audio.Dsp.RsToneConversion.Coverage;

/// <summary>
///     The audit's ONE curated input, kept apart from the mechanical rules so a
///     reviewer can read every judgement in one place.
///     <para>
///         <b>A judgement names the model it vouches for.</b> If the converter starts
///         emitting a different roster model for that key, the row drops back to
///         🟡 on its own — a judgement can never bless a mapping it did not see.
///     </para>
///     <para>
///         Also home to the value-label mirror the audit renders model names with
///         (<see cref="ValueLabel" />); <c>RsGearCoverageAuditTests</c> checks it
///         against the native catalogue when the DLL loads.
///     </para>
/// </summary>
public static class RsGearCoverageJudgements
{
    // No amp judgement since #88: every amp the game ships runs ITS OWN soundbank
    // chain (`rsamp`), so there is no stand-in to vouch for; what the clone cannot
    // do exactly is reported knob by knob (RsGearCoverageAudit.KnobVerdict).

    /// <summary>
    ///     Cabinet families whose roster IR has the SAME speaker layout (count, size
    ///     and open/closed back), keyed by family stem → (the <c>cab.model</c>
    ///     vouched for, why). The speaker MODEL inside is not claimed: the roster's
    ///     IRs are synthetic, and so are Rocksmith's names.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (int Model, string Why)> SameSpeakerCabStems =
        new Dictionary<string, (int, string)>(StringComparer.Ordinal)
        {
            // 1x12 combos -> 1x12 Combo
            ["Cab_AT0112C"] = (0, "1x12"),
            ["Cab_AT1121C"] = (0, "1x12"),
            ["Cab_BT1120C"] = (0, "1x12"),
            ["Cab_BT1121C"] = (0, "1x12"),
            ["Cab_CA112C"] = (0, "1x12"),
            ["Cab_CS1120C"] = (0, "1x12"),
            ["Cab_TW112C"] = (0, "1x12"),
            // open-back 2x12 combos
            ["Cab_CS212C"] = (1, "open-back 2x12 combo"),
            ["Cab_OrangePPC212OB"] = (5, "open-back 2x12"),
            // closed-back 2x12
            ["Cab_EN212C"] = (6, "closed-back 2x12"),
            ["Cab_HG212C"] = (6, "closed-back 2x12"),
            ["Cab_HG2120C"] = (6, "closed-back 2x12"),
            ["Cab_Marshall1936"] = (6, "closed-back 2x12"),
            // 4x12
            ["Cab_GB412CMKI"] = (7, "closed 4x12, greenback-era"),
            ["Cab_GB412CMKII"] = (7, "closed 4x12, greenback-era"),
            ["Cab_GB412CMKIII"] = (7, "closed 4x12, greenback-era"),
            ["Cab_Marshall1960AX"] = (7, "closed 4x12, Greenback-loaded"),
            ["Cab_Marshall1960TV"] = (7, "closed 4x12 (the straight tall 1960TV)"),
            ["Cab_Marshall1960a"] = (2, "closed 4x12 (the angled 1960A)"),
            ["Cab_CA412C"] = (8, "closed 4x12, V30-class modern"),
            ["Cab_EN4120C"] = (8, "closed 4x12, V30-class modern"),
            ["Cab_OrangePPC412"] = (8, "closed 4x12, V30-class modern"),
            // the game's own prop for it is `orange_jimmybean412` (game gear package)
            ["Cab_OrangeJimmyBean"] = (8, "closed 4x12 (Orange)"),
            // 10-inch and 15-inch guitar cabinets (2026-10, #74)
            ["Cab_TW410C"] = (12, "4x10 guitar"),
            ["Cab_BT410C"] = (12, "4x10 guitar"),
            ["Cab_TW110C"] = (13, "1x10 combo"),
            ["Cab_HG215C"] = (14, "2x15 guitar"),
            ["Cab_CS1515C"] = (14, "2x15 guitar"),
            ["Cab_CA215C"] = (14, "2x15 guitar"),
            // PA and hi-fi boxes: the game's own art for each (game gear package
            // tone_designer thumbnails) is a pole-mounted 15-inch + horn
            // (PS-115.2C), a horn-loaded bass bin with a horn on top (PS-600),
            // a tall column of small drivers (PA-999C) and a pair of towers
            // (Audiophile)
            ["Cab_PA1152C"] = (15, "2-way PA: 15-inch woofer + horn"),
            ["Cab_PA600C"] = (15, "2-way PA: bass bin + horn"),
            ["Cab_PA999C"] = (16, "PA column of small full-range drivers"),
            ["Cab_Audiophile"] = (17, "hi-fi tower speakers"),
            // bass
            ["Bass_Cab_AT810BC"] = (10, "8x10"),
            ["Bass_Cab_OrangeOBC810"] = (10, "8x10"),
            ["Bass_Cab_BT410BC"] = (9, "4x10"),
            ["Bass_Cab_CH410BC"] = (9, "4x10"),
            ["Bass_Cab_CS410BC"] = (9, "4x10"),
            ["Bass_Cab_EdenD410XST"] = (9, "4x10"),
            ["Bass_Cab_AT1150BC"] = (11, "1x15"),
            ["Bass_Cab_BT115BC"] = (11, "1x15"),
            ["Bass_Cab_CS15BC"] = (11, "1x15"),
            ["Bass_Cab_EdenD115XLT"] = (11, "1x15"),
            ["Bass_Cab_OrangeOBC115"] = (11, "1x15"),
            ["Bass_Cab_CH210BC"] = (18, "2x10"),
            ["Bass_Cab_CH310BC"] = (19, "3x10"),
            ["Bass_Cab_EdenD610XST"] = (20, "6x10"),
            ["Bass_Cab_CS112BC"] = (21, "1x12"),
            ["Bass_Cab_BT212BC"] = (22, "2x12"),
            ["Bass_Cab_EdenD212XLT"] = (22, "2x12"),
            ["Bass_Cab_TW215BC"] = (23, "2x15"),
            ["Bass_Cab_GB415BC"] = (24, "4x15"),
            ["Bass_Cab_CA1510BC"] = (25, "1x15 + 1x10")
        };

    // ------------------------------------------------------------ value labels

    /// <summary>The param that names a module's model, for the modules the
    /// converter picks a model on.</summary>
    public static string? ModelParamOf(string moduleTypeId) => moduleTypeId switch
    {
        DcRigTargetCatalog.Amp => "voicing",
        DcRigTargetCatalog.Cab => "model",
        DcRigTargetCatalog.MicPos => "mic",
        DcRigTargetCatalog.Overdrive => "model",
        DcRigTargetCatalog.Distortion => "model",
        DcRigTargetCatalog.BassAmp => "model",
        DcRigTargetCatalog.RsAmpChain => "model",
        _ => null
    };

    /// <summary>Managed mirror of the native value labels the audit prints.
    /// Pinned to the native catalogue by <c>RsGearCoverageAuditTests</c>.</summary>
    public static readonly IReadOnlyDictionary<(string Module, string Param), string[]> Labels =
        new Dictionary<(string, string), string[]>
        {
            [(DcRigTargetCatalog.Amp, "voicing")] =
            [
                "Blackface Twin 85", "Plexi 50", "Five-Oh-One Lead", "AC-Thirty", "Recto Dual",
                "Jazz Solid 120", "Brown Variac", "Tweed Champ 5W", "Tweed Twin 40",
                "Blackface Deluxe 22", "Plexi 100", "JMP-Eight-Hundred", "Two-Oh-Three Crunch",
                "AC-Fifteen", "Recto Triple", "Modern Djent 8-String", "PowerBall Lead",
                "Overdrive Special", "Class-A Boutique 30", "Bass Three-Sixty", "Bass Flip-Top",
                "SVT Bass 300W", "Acoustic Pre", "Studio Direct",
                "Tweed Bass 45", "Plexi 50 Circuit", "Plexi 100 Circuit"
            ],
            [(DcRigTargetCatalog.Cab, "model")] =
            [
                "1x12 Combo", "2x12 Combo", "4x12 Scooped", "Vintage 4x12", "1x8 Tweed",
                "2x12 AC Open", "2x12 Boutique Closed", "4x12 Greenback", "4x12 V30 Modern",
                "4x10 Bass", "8x10 Bass Fridge", "1x15 Vintage Bass",
                "4x10 Tweed Open", "1x10 Combo", "2x15 Guitar", "PA 15 + Horn", "PA Column", "Hi-Fi Tower",
                "2x10 Bass", "3x10 Bass", "6x10 Bass", "1x12 Bass", "2x12 Bass", "2x15 Bass", "4x15 Bass",
                "1x15 + 1x10 Bass"
            ],
            [(DcRigTargetCatalog.MicPos, "mic")] = ["57", "121", "421", "Condenser", "Tube"],
            [(DcRigTargetCatalog.Overdrive, "model")] = ["Generic", "Screamer", "Blues Breaker", "Transparent"],
            [(DcRigTargetCatalog.Distortion, "model")] = ["Generic", "Gnash", "Metal", "Plus"],
            [(DcRigTargetCatalog.BassAmp, "model")] = ["Flip-Top", "Tube 300", "Solid 800", "Modern"]
        };

    public static string? ValueLabel(string module, string param, float value)
    {
        // rsamp's models are the game's own amps, read from the player's install (#755).
        var labels = module == DcRigTargetCatalog.RsAmpChain && param == "model"
            ? RsAmpChains.Models.Select(m => m.Name).ToArray()
            : Labels.GetValueOrDefault((module, param));
        if (labels is null) return null;
        var i = (int)MathF.Round(value);
        return i >= 0 && i < labels.Length ? labels[i] : null;
    }

    // ------------------------------------------------------------ feature gaps

    /// <summary>One gap that is a property of the whole conversion, not one key.</summary>
    public sealed record FeatureGap(string Feature, CoverageStatus Status, string Detail);

    /// <summary>The feature-level gaps. Every state that CAN be read off the audit
    /// rows is computed from them; the rest are stated as facts about the
    /// converter's code, each naming where it lives.</summary>
    public static IReadOnlyList<FeatureGap> FeatureGaps(IReadOnlyList<GearCoverage> rows)
    {
        var cabs = rows.Where(r => r.Gear.Type == "Cabinets" && RsGearCoverageAudit.CabMic(r.Gear.Key) is not null).ToList();
        var withMic = cabs.Count(r => r.Modules.Any(m => m.ModuleTypeId == DcRigTargetCatalog.MicPos));
        var micState = withMic == 0 ? CoverageStatus.Missing
            : withMic == cabs.Count ? CoverageStatus.Modelled : CoverageStatus.Approximated;

        var amps = rows.Where(r => r.Gear.Type == "Amps").ToList();
        var chains = amps.Count(r => r.Modules.Count > 0 && r.Modules[0].ModuleTypeId == DcRigTargetCatalog.RsAmpChain);
        var chainKnobs = amps.Where(r => r.Modules.Count > 0 && r.Modules[0].ModuleTypeId == DcRigTargetCatalog.RsAmpChain)
            .SelectMany(r => r.Knobs.Select(k => RsGearCoverageAudit.KnobIsApproximated(r, k))).ToList();
        var exactKnobs = chainKnobs.Count(a => !a);
        var cabModels = rows.Where(r => r.Gear.Type == "Cabinets" && r.Modules.Count > 0)
            .Select(r => r.Modules[0].Params.GetValueOrDefault("model")).Distinct().Count();

        string Folded(string type) => string.Join(", ", rows
            .Where(r => r.Gear.Type == type)
            .SelectMany(r => r.Knobs.Where(k => !k.Lands).Select(k => $"{r.Gear.Key}.{k.Knob.Name}"))
            .Order(StringComparer.Ordinal));

        var ampFolds = Folded("Amps");

        return
        [
            new("Mic type and position", micState,
                $"{withMic} of {cabs.Count} mic/position cabinet keys get a `micpos` layer after the cab (RsToneConverter.BuildCabMic)."),
            new("Mic distance and room", CoverageStatus.Modelled,
                "Rocksmith exposes neither. The mic layer sits at a fixed 15 cm (the farthest `micpos` goes before its floor reflection) with no room."),
            new("Amps: Rocksmith's own chains", chains == amps.Count ? CoverageStatus.Approximated : CoverageStatus.Missing,
                $"{chains} of {amps.Count} amp keys run the game's own soundbank chain (`rsamp`, #88: Wwise Guitar Distortion / FutzBox / Compressor clones, every knob through its RTPC curves). {exactKnobs} of {chainKnobs.Count} amp knobs move only what the clone runs exactly (✅: Guitar Distortion EQ bands, FutzBox levels); {chainKnobs.Count - exactKnobs} carry an inference (🟡: the Guitar Distortion drive law, the FutzBox distortion, filters or EQ Q, the Wwise Compressor's detector, or an unpublished S-curve; each amp's Notes say which). No Rocksmith render exists to compare against."),
            new("Cab IR roster use", cabModels > 4 ? CoverageStatus.Approximated : CoverageStatus.Missing,
                $"Cabinets land on {cabModels} distinct `cab.model` IRs (26 exist); the IRs are synthetic, not captures of the RS cabinets (12-25 are built from measured driver responses, gen_cab_irs.py)."),
            new("Amp switches and extra tone controls", ampFolds.Length == 0 ? CoverageStatus.Modelled : CoverageStatus.Approximated,
                ampFolds.Length == 0 ? "every amp knob lands" : "folded: " + ampFolds),
            new("Knob tapers", CoverageStatus.Approximated,
                "Amp knobs run through the game's own RTPC curves (rsamp, #88), and four pedals through the curves in RsGameCurves; every other RS knob is mapped LINEARLY from its catalogue range onto the param range (RsToneConverter.Norm01). None is calibrated against a Rocksmith render."),
            new("Gain staging / tone volume", CoverageStatus.Approximated,
                "Playback levels each converted tone by its RENDERED loudness (RsToneLevelling: a fixed phrase through the real chain on the offline rig, -24 LUFS under a -5 dBFS peak ceiling, RsToneLoudnessPolicy); until that measurement lands the tone plays on the walker's estimate plus the per-amp-model trim (RsAmpLevelTrims.ByKey). The RS `Volume` is not a level here; only its difference from the song's other tones is kept, ±3 dB. Rocksmith's own output levels are unknown, so 'as loud as in Rocksmith' is not claimed: see RsToneFidelityHarnessTests."),
            new("Signal order (pre / loop / rack)", CoverageStatus.Approximated,
                "Rendered PrePedal1-4 -> Amp -> Cab -> Rack1-4 -> PostPedal1-4 (RsTone.InSignalOrder). Whether Rocksmith runs the post (\"loop\") pedals before the cabinet is not documented; unverified."),
            new("Stereo rack controls", CoverageStatus.Approximated,
                "Second-tap times, pan and stereo-width knobs fold where the target is mono or single-tap — see the Racks table."),
            new("Mid-song tone switching", CoverageStatus.Modelled,
                "DcRigApplier applies the minimal diff at each tone change; RsTonePlaybackCoordinator schedules it.")
        ];
    }
}
