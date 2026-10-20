using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.Tone.Parsing.Psarc;
using DotCharter.Tests.Audio.Dsp.Tones;

namespace DotCharter.Tests.Audio.Dsp.RsToneConversion;

public sealed class RsToneConverterTests
{
    private static readonly RsToneConverter Converter = new();

    /// <summary>The synthetic install's curves (#755): the stock tones run on it.</summary>
    private static RsGameCurveSet Game => SyntheticRsGame.Tables.Curves;

    private static DcRigPreset Convert(RsTone tone, out ConversionReport report)
        => Converter.Convert(tone, RsGearCatalog.Shared, RsGearOverrides.Shared, out report);

    private static float Param(DcRigModule m, string id)
    {
        Assert.True(m.Params.TryGetValue(id, out var v), $"module '{m.ModuleTypeId}' is missing param '{id}'");
        return v;
    }

    private static RsTone ParseInline(string json) =>
        RsToneJsonParser.Parse(new PsarcToneDefinition("K", "N", json));

    // -------------------------------------------------- golden: default_lead

    [Fact]
    public void DefaultLead_ProducesExpectedModuleOrder()
    {
        using var game = SyntheticRsGame.Use();
        var preset = Convert(ToneFixtures.ParseFixture("default_lead.json"), out _);

        Assert.Equal(
            // Rack_StudioChamber moved from the generic `reverb` onto `hallreverb`
            // in the 0.9 gear wave: it is the only reverb module with a
            // `diffusion` param, and the rack carries a Diffusion knob this
            // converter used to drop on the floor. The cab's mic (Ribbon / Cone)
            // is its own `micpos` layer, and the four-band Rack_StudioEQ is two
            // `eq` nodes (Low/LoMid/Treble, then the HiMid bell) since 2026-10.
            // The amp is the game's own chain (`rsamp`, #88).
            new[] { "overdrive", "rsamp", "cab", "micpos", "eq", "eq", "hallreverb", "delay", "gain" },
            preset.Modules.Select(m => m.ModuleTypeId).ToArray());
    }

    [Fact]
    public void DefaultLead_MapsKeyParams()
    {
        using var game = SyntheticRsGame.Use();
        var preset = Convert(ToneFixtures.ParseFixture("default_lead.json"), out var report);
        var m = preset.Modules;

        // overdrive (Pedal_GermaniumDrive → forced overdrive)
        Assert.Equal(0.7980f, Param(m[0], "drive"), 0.001);   // Gain 80, 1..100
        Assert.Equal(0.3535f, Param(m[0], "tone"), 0.001);    // Tone 36, 1..100

        // rsamp (Marshall Plexi's row): each knob at its position between its own
        // 1..100, in the manifest's order — Loudness 1, Loudness 2, Bass, Mid,
        // Treble, Presence.
        Assert.Equal(SyntheticRsGame.Model("Amp_MarshallPlexi"), Param(m[1], "model"), 0.001);
        Assert.Equal(0.8788f, Param(m[1], "k1"), 0.001);
        Assert.Equal(0.5859f, Param(m[1], "k2"), 0.001);
        Assert.Equal(0.8182f, Param(m[1], "k3"), 0.001);
        Assert.Equal(0.8485f, Param(m[1], "k4"), 0.001);
        Assert.Equal(0.8889f, Param(m[1], "k5"), 0.001);
        Assert.Equal(0.0909f, Param(m[1], "k6"), 0.001);

        // cab (Marshall 1960 TV 4x12 → roster 7 = 4x12 Greenback)
        Assert.Equal(7f, Param(m[2], "model"), 0.001);

        // micpos (the key's Ribbon / Cone): "121" ribbon on the cap, at the
        // mic layer's fixed distance and unity level.
        Assert.Equal(1f, Param(m[3], "mic"), 0.001);
        Assert.Equal(0f, Param(m[3], "position"), 0.001);
        Assert.Equal(RsToneConverter.MicLayerDistanceCm, Param(m[3], "distanceCm"), 0.001);
        Assert.Equal(RsToneConverter.MicLayerUnityLevel, Param(m[3], "level"), 0.001);

        // eq (Rack_StudioEQ): gains are dB passthrough; frequencies and Q go
        // through the GAME'S knob curves (RsGameCurves) since 2026-10 — the labels
        // are not the corners.
        var eq = Game.StudioEq!;
        Assert.Equal(2f, Param(m[4], "lowGainDb"), 0.001);
        Assert.Equal(eq.BassFreqHz.Evaluate(270f), Param(m[4], "lowFreq"), 0.05);   // Bass Freq. 270
        Assert.Equal(-5f, Param(m[4], "midGainDb"), 0.001);
        Assert.Equal(eq.LoMidFreqHz.Evaluate(210f), Param(m[4], "midFreq"), 0.05);  // Low-Mid Freq. 210
        Assert.Equal(eq.Q.Evaluate(1f), Param(m[4], "midQ"), 0.001);                 // Low-Mid "Q" Narrow
        Assert.Equal(5f, Param(m[4], "highGainDb"), 0.001);
        Assert.Equal(eq.TrebleFreqHz.Evaluate(5.6f), Param(m[4], "highFreq"), 0.5);  // Treble Freq. 5.6

        // …and its HiMid band (+6 dB at High-Mid Freq. 2, Wide) on a second eq whose
        // shelves stay flat. It used to be folded away.
        Assert.Equal(6f, Param(m[5], "midGainDb"), 0.001);
        Assert.Equal(eq.HiMidFreqHz.Evaluate(2f), Param(m[5], "midFreq"), 0.5);
        Assert.Equal(eq.Q.Evaluate(0f), Param(m[5], "midQ"), 0.001);
        Assert.Equal(0f, Param(m[5], "lowGainDb"), 0.001);
        Assert.Equal(0f, Param(m[5], "highGainDb"), 0.001);

        // hallreverb (Rack_StudioChamber): Time→decayS, the `Depth`-keyed
        // DIFFUSION knob→diffusion (dropped entirely before 0.9), Tone→damping
        // corner (brighter ⇒ higher), Mix→mix, and `space` = Chamber pinned by
        // the overrides table.
        Assert.Equal(0.3f + 0.5556f * 7.7f, Param(m[6], "decayS"), 0.001);
        Assert.Equal(0.7172f, Param(m[6], "diffusion"), 0.001);
        Assert.Equal(1.5f + 0.2525f * 14.5f, Param(m[6], "dampingKHz"), 0.001);
        Assert.Equal(RsToneConverter.RsReverbMixScale * 0.1515f, Param(m[6], "mix"), 0.001);
        Assert.Equal(1f, Param(m[6], "space"), 0.001);

        // delay (Pedal_ModDelay): Time ms passthrough, Feedback %, Mix %
        Assert.Equal(360f, Param(m[7], "timeMs"), 0.001);
        Assert.Equal(0.24f, Param(m[7], "feedback"), 0.001);
        Assert.Equal(0.4040f, Param(m[7], "mix"), 0.001);

        // Trailing master gain = the absolute loudness normalization over the
        // chain body (the RS Volume of −19.5 dB is DISCARDED — it targets
        // Rocksmith's own mix bus, not ours).
        var body = m.Take(m.Count - 1).ToList();
        Assert.Equal(
            RsToneConverter.TrailingGainDb(body),
            Param(m[8], "gainDb"), 0.001);
        // …and the discard is recorded for the fidelity badge.
        Assert.Contains(report.Notes, n =>
            n.GearKey == "Volume" && n.Kind == ConversionNoteKind.Approximated);

        // the mod-delay Rate/Depth drop is recorded
        Assert.True(report.ApproximatedCount >= 1);
    }

    // ------------------------------------------------- golden: default_rhythm

    [Fact]
    public void DefaultRhythm_ProducesExpectedChain()
    {
        // The stock rhythm tone's Pedal_SpringReverb lands on the dedicated
        // springreverb module since the 2.2 full-catalogue retarget. Its
        // Pedal_Compression rode the OPTICAL compressor from the 0.9 gear wave
        // until 2026-10 (#69): the game's own bank says it is the Wwise
        // Compressor, so it is the `compressor` with all five params landing.
        using var game = SyntheticRsGame.Use();
        var preset = Convert(ToneFixtures.ParseFixture("default_rhythm.json"), out var report);

        Assert.Equal(
            new[] { "compressor", "rsamp", "cab", "micpos", "eq", "eq", "springreverb", "gain" },
            preset.Modules.Select(m => m.ModuleTypeId).ToArray());

        var comp = preset.Modules[0];
        var curves = Game.Compression!;
        Assert.Equal(curves.ThresholdDb.Evaluate(36f), Param(comp, "thresholdDb"), 0.01);   // Comp 36
        Assert.Equal(curves.Ratio.Evaluate(36f), Param(comp, "ratio"), 0.01);
        Assert.Equal(curves.OutputGainDb.Evaluate(36f), Param(comp, "makeupDb"), 0.01);
        Assert.Equal(curves.AttackMs.Evaluate(30f), Param(comp, "attackMs"), 0.001);        // Attack 30
        Assert.Equal(curves.ReleaseMs.Evaluate(85f), Param(comp, "releaseMs"), 0.001);      // Release 85
        Assert.DoesNotContain(report.Notes, n => n.GearKey == "Pedal_Compression");

        var amp = preset.Modules[1];
        Assert.Equal(SyntheticRsGame.Model("Amp_OrangeAD50"), Param(amp, "model"), 0.001);
        Assert.Equal(0.2424f, Param(amp, "k1"), 0.001);      // its Gain 25, 1..100

        // Trailing master gain comes from the loudness normalizer, not the RS Volume.
        Assert.Equal(
            RsToneConverter.TrailingGainDb(
                preset.Modules.Take(preset.Modules.Count - 1).ToList()),
            Param(preset.Modules[^1], "gainDb"), 0.001);
    }

    // -------------------------------------------------- golden: default_bass

    [Fact]
    public void DefaultBass_ProducesExpectedChain()
    {
        // The head is the game's own Eden WT550 chain (`rsamp`, #88) and the
        // "multiband comp" pedal is its bank's chain fitted onto a `compressor` (#167).
        using var game = SyntheticRsGame.Use();
        var preset = Convert(ToneFixtures.ParseFixture("default_bass.json"), out _);

        Assert.Equal(
            new[] { "eq", "rsamp", "cab", "micpos", "compressor", "compressor", "gain" },
            preset.Modules.Select(m => m.ModuleTypeId).ToArray());

        // Bass_Amp_EdenWT550: Gain 14 of 1..100, Bass +2 of -12..12 dB,
        // Low-Mid Freq. 300 of 30..300 Hz, High-Mid Freq. 2.6 of 1.2..12 kHz.
        var head = preset.Modules[1];
        Assert.Equal(SyntheticRsGame.Model("Bass_Amp_EdenWT550"), Param(head, "model"), 0.001);
        Assert.Equal((14f - 1f) / 99f, Param(head, "k1"), 0.001);
        Assert.Equal(14f / 24f, Param(head, "k2"), 0.001);
        Assert.Equal(1f, Param(head, "k4"), 0.001);
        Assert.Equal((2.6f - 1.2f) / 10.8f, Param(head, "k8"), 0.001);

        // Eden D610XST (6x10) → the 6x10 Bass IR (#74; the 8x10 Bass Fridge
        // stood in until then, and before that every bass cab rode the guitar 4x12).
        Assert.Equal(20f, Param(preset.Modules[2], "model"), 0.001);
        // Condenser / Edge
        Assert.Equal(3f, Param(preset.Modules[3], "mic"), 0.001);
        Assert.Equal(1f, Param(preset.Modules[3], "position"), 0.001);
        Assert.Equal(
            RsToneConverter.TrailingGainDb(
                preset.Modules.Take(preset.Modules.Count - 1).ToList()),
            Param(preset.Modules[^1], "gainDb"), 0.001);
    }

    // ------------------------------------------------------- normalization

    [Fact]
    public void Norm01_UsesCatalogueRange()
    {
        // An amp knob (number 1..100) is its position on the game's own range:
        // value 50 → (50-1)/99.
        using var game = SyntheticRsGame.Use();
        var tone = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_MarshallPlexi","KnobValues":{"Amp_MarshallPlexi_Loudness1":50}}}}
            """);
        var preset = Convert(tone, out _);
        var amp = preset.Modules.Single(m => m.ModuleTypeId == "rsamp");
        Assert.Equal((50f - 1f) / 99f, Param(amp, "k1"), 0.001);
    }

    [Fact]
    public void OutputsAlwaysClampToTargetRange()
    {
        // A delay with an absurd feedback → clamped to the 0..0.95 param range.
        var tone = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}},
              "PostPedal1":{"Type":"Pedals","Key":"Pedal_ModDelay","KnobValues":{"Pedal_ModDelay_Feedback":100}}}}
            """);
        var preset = Convert(tone, out _);
        var delay = preset.Modules.Single(m => m.ModuleTypeId == "delay");
        Assert.Equal(0.95f, Param(delay, "feedback"), 0.001);
    }

    // ------------------------------------------------ category → module map

    [Fact]
    public void Distortion_HeavyKeyPicksDistortionModule()
    {
        // Pedal_ShredZone: catalogue category Distortion + forced-distortion
        // override. (Pedal_MetalDistortion used to be the example here; since
        // the 0.9 gear wave it routes to `hm2` instead — see
        // MetalDistortion_MapsToChainsawModule.)
        var tone = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}},
              "PrePedal1":{"Type":"Pedals","Key":"Pedal_ShredZone","KnobValues":{"Pedal_ShredZone_Gain":90}}}}
            """);
        var preset = Convert(tone, out _);
        Assert.Contains(preset.Modules, m => m.ModuleTypeId == "distortion");
        Assert.DoesNotContain(preset.Modules, m => m.ModuleTypeId == "overdrive");
    }

    [Fact]
    public void Reverb_PlainDigitalVerb_MapsToHallReverb()
    {
        var tone = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}},
              "PostPedal1":{"Type":"Pedals","Key":"Pedal_DigitalVerb","KnobValues":{"Pedal_DigitalVerb_Time":74,"Pedal_DigitalVerb_Mix":20}}}}
            """);
        var preset = Convert(tone, out _);
        var reverb = preset.Modules.Single(m => m.ModuleTypeId == "hallreverb");
        Assert.Equal(0.3f + (74f - 1f) / 99f * 7.7f, Param(reverb, "decayS"), 0.001);
        // RS-parity calibration: our reverbs are wetter per unit mix than
        // Rocksmith's engine, so converted mixes scale down (RsReverbMixScale).
        Assert.Equal(RsToneConverter.RsReverbMixScale * (20f - 1f) / 99f, Param(reverb, "mix"), 0.001);
        // The pedal carries no `space` pin, and the converter emits SPARSE
        // params by design — so `space` is left for FillDefaults to supply
        // (Hall), not written here.
        Assert.False(reverb.Params.ContainsKey("space"));
        Assert.DoesNotContain(preset.Modules, m => m.ModuleTypeId == "reverb");
    }

    // -------------------------------------- 2.2 retarget: reverb/delay types

    [Fact]
    public void SpringReverb_MapsToSpringReverbModule()
    {
        var tone = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}},
              "PostPedal1":{"Type":"Pedals","Key":"Pedal_SpringReverb","KnobValues":{"Pedal_SpringReverb_Time":74,"Pedal_SpringReverb_Depth":50,"Pedal_SpringReverb_Mix":20}}}}
            """);
        var preset = Convert(tone, out var report);
        var spring = preset.Modules.Single(m => m.ModuleTypeId == "springreverb");
        Assert.Equal((74f - 1f) / 99f, Param(spring, "decay"), 0.001);
        Assert.Equal((50f - 1f) / 99f, Param(spring, "drip"), 0.001);
        Assert.Equal(RsToneConverter.RsReverbMixScale * (20f - 1f) / 99f, Param(spring, "mix"), 0.001);
        Assert.DoesNotContain(report.Notes, n => n.GearKey == "Pedal_SpringReverb");
    }

    [Fact]
    public void PlateReverb_MapsToPlateReverbModule()
    {
        // Rack_StudioPlate: Time→decay, Tone→inverted damping, Depth→width.
        var tone = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}},
              "Rack1":{"Type":"Racks","Key":"Rack_StudioPlate","KnobValues":{"Rack_StudioPlate_Time":66,"Rack_StudioPlate_Tone":60,"Rack_StudioPlate_Depth":80,"Rack_StudioPlate_Mix":50}}}}
            """);
        var preset = Convert(tone, out _);
        var plate = preset.Modules.Single(m => m.ModuleTypeId == "platereverb");
        Assert.Equal((66f - 1f) / 99f, Param(plate, "decay"), 0.001);
        Assert.Equal(1f - (60f - 1f) / 99f, Param(plate, "damping"), 0.001);
        Assert.Equal((80f - 1f) / 99f, Param(plate, "width"), 0.001);
        Assert.Equal(RsToneConverter.RsReverbMixScale * (50f - 1f) / 99f, Param(plate, "mix"), 0.001);
    }

    [Fact]
    public void TapeEcho_MapsToTapeDelayModule()
    {
        var tone = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}},
              "Rack1":{"Type":"Racks","Key":"Rack_TapeEcho","KnobValues":{"Rack_TapeEcho_Time":550,"Rack_TapeEcho_Feedback":44,"Rack_TapeEcho_Mix":62}}}}
            """);
        var preset = Convert(tone, out _);
        var tape = preset.Modules.Single(m => m.ModuleTypeId == "tapedelay");
        Assert.Equal(550f, Param(tape, "timeMs"), 0.001);   // ms passthrough
        Assert.Equal(0.44f, Param(tape, "feedback"), 0.001); // % 0..100
        Assert.Equal((62f - 1f) / 99f, Param(tape, "mix"), 0.001);
        Assert.DoesNotContain(preset.Modules, m => m.ModuleTypeId == "delay");
    }

    [Fact]
    public void AnalogDelay_MapsToTheBucketBrigadeModule()
    {
        var tone = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}},
              "PostPedal1":{"Type":"Pedals","Key":"Pedal_AnalogueDelay","KnobValues":{"Pedal_AnalogueDelay_Time":270,"Pedal_AnalogueDelay_Feedback":92}}}}
            """);
        // 0.9 gear wave: a bucket-brigade pedal gets the real BBD model
        // (`bbddelay`) rather than the tape machine, which models a different
        // instrument entirely.
        var preset = Convert(tone, out _);
        var bbd = preset.Modules.Single(m => m.ModuleTypeId == "bbddelay");
        Assert.Equal(270f, Param(bbd, "timeMs"), 0.001);
        // #451: Feedback 92 is a per-repeat gain of 0.92, NOT the BBD knob at
        // 0.92 — that is past the knob's self-oscillation point (~0.82) and the
        // repeats grew without end.
        Assert.Equal(0.92f, RsToneConverter.BbdRegen(Param(bbd, "feedback")), 0.001);
        Assert.True(Param(bbd, "feedback") < 0.82f);
        Assert.DoesNotContain(preset.Modules, m => m.ModuleTypeId == "tapedelay");
    }

    // ----------------------------------------- 2.2 retarget: modulation types

    [Fact]
    public void Phaser_MapsToPhaserModule_NoLongerApproximated()
    {
        var tone = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}},
              "PrePedal1":{"Type":"Pedals","Key":"Pedal_PlanePhase","KnobValues":{"Pedal_PlanePhase_Rate":25,"Pedal_PlanePhase_Depth":50,"Pedal_PlanePhase_Mix":50}}}}
            """);
        var preset = Convert(tone, out var report);

        var phaser = preset.Modules.Single(m => m.ModuleTypeId == "phaser");
        Assert.Equal(0.05f + (25f - 1f) / 99f * (8f - 0.05f), Param(phaser, "rateHz"), 0.001);
        Assert.Equal((50f - 1f) / 99f, Param(phaser, "depth"), 0.001);
        Assert.Equal((50f - 1f) / 99f, Param(phaser, "mix"), 0.001);
        Assert.DoesNotContain(preset.Modules, m => m.ModuleTypeId == "flanger");
        Assert.DoesNotContain(report.Notes, n => n.GearKey == "Pedal_PlanePhase");
    }

    [Fact]
    public void RackPhaser_HzRateKnob_PassesThrough()
    {
        // Rack_StereoPhaser's Rate is a REAL Hz knob (0.1..10) — no rescale.
        var tone = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}},
              "Rack1":{"Type":"Racks","Key":"Rack_StereoPhaser","KnobValues":{"Rack_StereoPhaser_Rate":0.5,"Rack_StereoPhaser_Depth":55}}}}
            """);
        var preset = Convert(tone, out _);
        var phaser = preset.Modules.Single(m => m.ModuleTypeId == "phaser");
        Assert.Equal(0.5f, Param(phaser, "rateHz"), 0.001);
        Assert.Equal((55f - 1f) / 99f, Param(phaser, "depth"), 0.001);
    }

    /// <summary>
    ///     A Rocksmith chorus plays what its soundbank says, not what its knob labels
    ///     say (owner report: Polkadot Stingray "Killer Record", tone "Chorus" —
    ///     <c>Pedal_Chorus</c> Depth 50 / Mix 55 / Rate 4 came out 7.9 ms deep, 55 % wet,
    ///     2.03 Hz, two voices). <c>pedal_chorus.bnk</c> is one Wwise Flanger at 12 ms:
    ///     Rate 4 is 0.1 + 9.9 t^3 = 0.705 Hz (t = 3.9 / 9.9), Mix 55 is
    ///     1 + 54.13 (1 - cos(0.55 pi/2)) = 19.98 % wet, Depth 50 is 55 % of the delay,
    ///     3.3 ms peak.
    /// </summary>
    [Fact]
    public void ChorusPedal_PlaysTheGamesOwnCurves()
    {
        using var game = SyntheticRsGame.Use();
        var bank = Game.Choruses["Pedal_Chorus"];
        var tone = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}},
              "PostPedal2":{"Type":"Pedals","Key":"Pedal_Chorus","KnobValues":{"Pedal_Chorus_Depth":50,"Pedal_Chorus_Mix":55,"Pedal_Chorus_Rate":4}}}}
            """);
        var preset = Convert(tone, out _);
        var chorus = preset.Modules.Single(m => m.ModuleTypeId == "chorus");
        // The "Hz" label is not the rate: the bank's curve is.
        Assert.Equal(bank.RateHz.Evaluate(4f), Param(chorus, "rateHz"), 0.002);
        Assert.NotEqual(4f, Param(chorus, "rateHz"), 0.5);
        Assert.Equal(bank.CrossfadeWet(bank.MixPercent.Evaluate(55f)), Param(chorus, "mix"), 0.001);
        Assert.Equal(RsFlangerChorus.PeakDeviationMs(bank.DepthPercent.Evaluate(50f), bank.DelayMs), Param(chorus, "depthMs"), 0.001);
        Assert.Equal(1f, Param(chorus, "voices"));
    }

    /// <summary>A bank of two Flangers is two voices; the bass chorus keeps its dry
    /// signal inside the Flanger (dry 1, feed-forward 1), so Mix 100 is HALF wet; the
    /// Studio Chorus's Delay knob sets the delay its Depth is a share of.</summary>
    [Fact]
    public void ChorusBanks_VoicesTheBassChorusDryAndTheStudioDelay()
    {
        using var game = SyntheticRsGame.Use();
        var twoVoices = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}},
              "Rack1":{"Type":"Racks","Key":"Rack_StudioChorus","KnobValues":{"Rack_StudioChorus_Depth":100,"Rack_StudioChorus_Mix":100}}}}
            """);
        Assert.Equal(2f, Param(Convert(twoVoices, out _).Modules.Single(m => m.ModuleTypeId == "chorus"), "voices"));

        var bass = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Bass_Amp_HT300B","KnobValues":{}},
              "PrePedal1":{"Type":"Pedals","Key":"Bass_Pedal_BassChorus","KnobValues":{"Bass_Pedal_BassChorus_Mix":100}}}}
            """);
        Assert.Equal(0.5f, Param(Convert(bass, out _).Modules.Single(m => m.ModuleTypeId == "chorus"), "mix"), 0.001);

        static float StudioDepth(float delayKnob)
        {
            var studio = ParseInline("{\"GearList\":{\"Amp\":{\"Type\":\"Amps\",\"Key\":\"Amp_Twin\",\"KnobValues\":{}},"
                + "\"Rack1\":{\"Type\":\"Racks\",\"Key\":\"Rack_StudioChorus\",\"KnobValues\":{\"Rack_StudioChorus_Depth\":100,\"Rack_StudioChorus_Delay\":"
                + delayKnob.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}}}");
            return Param(Convert(studio, out _).Modules.Single(m => m.ModuleTypeId == "chorus"), "depthMs");
        }

        Assert.Equal(0.40f * 8f / 2f, StudioDepth(0f), 0.01);     // depth 40 %; Delay 0 -> 8 ms
        Assert.Equal(0.40f * 40f / 2f, StudioDepth(100f), 0.01);  // Delay 100 -> 40 ms
    }

    [Fact]
    public void RotaryPedal_MapsToRotaryModule()
    {
        var tone = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}},
              "PrePedal1":{"Type":"Pedals","Key":"Pedal_BakedRotatoe","KnobValues":{"Pedal_BakedRotatoe_Rate":100,"Pedal_BakedRotatoe_Depth":75,"Pedal_BakedRotatoe_Mix":52}}}}
            """);
        var preset = Convert(tone, out var report);
        var rotary = preset.Modules.Single(m => m.ModuleTypeId == "rotary");
        Assert.Equal(1f, Param(rotary, "speed"), 0.001);
        Assert.Equal((75f - 1f) / 99f, Param(rotary, "depth"), 0.001);
        Assert.Equal((52f - 1f) / 99f, Param(rotary, "mix"), 0.001);
        Assert.DoesNotContain(report.Notes, n => n.GearKey == "Pedal_BakedRotatoe");
    }

    [Fact]
    public void VibeGear_MapsToUniVibeModule()
    {
        var tone = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}},
              "Rack1":{"Type":"Racks","Key":"Rack_RotaVibe","KnobValues":{"Rack_RotaVibe_Rate":100,"Rack_RotaVibe_Mix":70,"Rack_RotaVibe_Depth":48}}}}
            """);
        var preset = Convert(tone, out _);
        var vibe = preset.Modules.Single(m => m.ModuleTypeId == "univibe");
        Assert.Equal(8f, Param(vibe, "rateHz"), 0.001);          // Rate 100 → top of 0.1..8
        Assert.Equal(0.7f, Param(vibe, "mix"), 0.001);           // % 0..100
        Assert.Equal((48f - 1f) / 99f, Param(vibe, "depth"), 0.001);
    }

    [Fact]
    public void TremoloCategoryVibrato_MapsToVibratoModule()
    {
        // Pedal_MultiVibe (Tremolo category): Speed→rateHz, Mix folds into
        // depth (no wet/dry on a true vibrato), Waveform clamps to 0..1.
        var tone = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}},
              "PrePedal1":{"Type":"Pedals","Key":"Pedal_MultiVibe","Category":"Tremolo","KnobValues":{"Pedal_MultiVibe_Speed":74,"Pedal_MultiVibe_Mix":93,"Pedal_MultiVibe_Waveform":3}}}}
            """);
        var preset = Convert(tone, out _);
        var vib = preset.Modules.Single(m => m.ModuleTypeId == "vibrato");
        Assert.Equal(0.1f + (74f - 1f) / 99f * 9.9f, Param(vib, "rateHz"), 0.001);
        Assert.Equal(0.5f + (93f - 1f) / 99f * 11.5f, Param(vib, "depthMs"), 0.001);
        Assert.Equal(1f, Param(vib, "waveform"), 0.001); // enum 3 clamped to the 0..1 range
        Assert.DoesNotContain(preset.Modules, m => m.ModuleTypeId == "tremolo");
    }

    // -------------------------------------------- 2.2 retarget: wah + filter

    [Fact]
    public void WahPedal_MapsToWahModule()
    {
        var tone = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}},
              "PrePedal1":{"Type":"Pedals","Key":"Pedal_USWah","KnobValues":{"Pedal_USWah_Pedal":88,"Pedal_USWah_Sens":75,"Pedal_USWah_Auto":1}}}}
            """);
        var preset = Convert(tone, out var report);

        var wah = preset.Modules.Single(m => m.ModuleTypeId == "wah");
        Assert.Equal((88f - 1f) / 99f, Param(wah, "position"), 0.001);
        Assert.Equal((75f - 1f) / 99f, Param(wah, "sensitivity"), 0.001);
        Assert.Equal(1f, Param(wah, "mode"), 0.001); // Auto=ON
        Assert.DoesNotContain(preset.Modules, m => m.ModuleTypeId == "eq");
        Assert.DoesNotContain(report.Notes, n => n.GearKey == "Pedal_USWah" && n.Kind == ConversionNoteKind.Skipped);
    }

    [Fact]
    public void AutoFilter_MapsToEnvFilterModule()
    {
        var tone = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}},
              "PrePedal1":{"Type":"Pedals","Key":"Pedal_AutoFilter","KnobValues":{"Pedal_AutoFilter_Sens":62,"Pedal_AutoFilter_Res":95}}}}
            """);
        var preset = Convert(tone, out var report);

        var env = preset.Modules.Single(m => m.ModuleTypeId == "envfilter");
        Assert.Equal((62f - 1f) / 99f, Param(env, "sensitivity"), 0.001);
        Assert.Equal((95f - 1f) / 99f, Param(env, "resonance"), 0.001);
        Assert.DoesNotContain(report.Notes, n => n.GearKey == "Pedal_AutoFilter" && n.Kind == ConversionNoteKind.Skipped);
    }

    [Fact]
    public void GraphicEqPedal_MapsBandsToNearestGraphicEqBand()
    {
        // Pedal_EQ8's numeric pots ARE the centre frequencies: 50 Hz → band63,
        // 3200 Hz → band4k; gains are dB passthrough clamped to ±12.
        var tone = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}},
              "PrePedal1":{"Type":"Pedals","Key":"Pedal_EQ8","KnobValues":{"Pedal_EQ8_50":15,"Pedal_EQ8_3200":-1}}}}
            """);
        var preset = Convert(tone, out _);

        var geq = preset.Modules.Single(m => m.ModuleTypeId == "graphiceq");
        Assert.Equal(12f, Param(geq, "band63"), 0.001); // 15 dB clamped to the ±12 range
        Assert.Equal(-1f, Param(geq, "band4k"), 0.001);
        Assert.DoesNotContain(preset.Modules, m => m.ModuleTypeId == "eq");
    }

    [Fact]
    public void ParaGraphicEqRack_PlacesNamedBandsByTheirFreqKnob()
    {
        // Rack_StudioGraphicEQ: Mid gain sits at MidFreq (kHz → Hz → nearest band).
        var tone = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}},
              "Rack1":{"Type":"Racks","Key":"Rack_StudioGraphicEQ","KnobValues":{"Rack_StudioGraphicEQ_Bass":3,"Rack_StudioGraphicEQ_BassFreq":125,"Rack_StudioGraphicEQ_Mid":-5,"Rack_StudioGraphicEQ_MidFreq":0.6}}}}
            """);
        var preset = Convert(tone, out _);

        var geq = preset.Modules.Single(m => m.ModuleTypeId == "graphiceq");
        Assert.Equal(3f, Param(geq, "band125"), 0.001);  // 125 Hz exact
        Assert.Equal(-5f, Param(geq, "band500"), 0.001); // 0.6 kHz = 600 Hz → band500
    }

    // ------------------------------------------------ 2.2 retarget: pitch

    [Fact]
    public void PitchShiftGear_MapsToHarmonizer()
    {
        var tone = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}},
              "PostPedal1":{"Type":"Pedals","Key":"Pedal_MultiPitch","KnobValues":{"Pedal_MultiPitch_Pitch1":-12,"Pedal_MultiPitch_Mix":50}}}}
            """);
        var preset = Convert(tone, out var report);

        var harm = preset.Modules.Single(m => m.ModuleTypeId == "harmonizer");
        Assert.Equal(-12f, Param(harm, "semitones"), 0.001);
        Assert.Equal((50f - 1f) / 99f, Param(harm, "mix"), 0.001);
        Assert.DoesNotContain(report.Notes, n => n.GearKey == "Pedal_MultiPitch" && n.Kind == ConversionNoteKind.Skipped);
    }

    [Fact]
    public void PitchShift_BeyondModuleRange_ClampsAndReports()
    {
        var tone = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}},
              "Rack1":{"Type":"Racks","Key":"Rack_StudioPitch","KnobValues":{"Rack_StudioPitch_Pitch":-24,"Rack_StudioPitch_Mix":70}}}}
            """);
        var preset = Convert(tone, out var report);

        var harm = preset.Modules.Single(m => m.ModuleTypeId == "harmonizer");
        Assert.Equal(-12f, Param(harm, "semitones"), 0.001);
        Assert.Contains(report.Notes, n => n.GearKey == "Rack_StudioPitch" && n.Kind == ConversionNoteKind.Approximated);
    }

    [Fact]
    public void SubOctavePedal_MapsToOctaver()
    {
        var tone = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}},
              "PrePedal1":{"Type":"Pedals","Key":"Bass_Pedal_BassSubOctave","KnobValues":{"Bass_Pedal_BassSubOctave_Mix":70,"Bass_Pedal_BassSubOctave_Tone":88}}}}
            """);
        var preset = Convert(tone, out var report);

        var oct = preset.Modules.Single(m => m.ModuleTypeId == "octaver");
        Assert.Equal(0.7f, Param(oct, "sub1"), 0.001);   // Mix % 0..100
        Assert.Equal(500f + (88f - 1f) / 99f * 4500f, Param(oct, "toneHz"), 0.01);
        Assert.DoesNotContain(report.Notes, n => n.GearKey == "Bass_Pedal_BassSubOctave");
    }

    [Fact]
    public void OctaveUpPedal_MapsToTheOctaveFuzz_WithNoApology()
    {
        var tone = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}},
              "PrePedal1":{"Type":"Pedals","Key":"Pedal_OctaveUp","KnobValues":{"Pedal_OctaveUp_Mix":66}}}}
            """);
        // Until the 0.9 gear wave this landed on the SUB-octave octaver and the
        // converter apologised for it in the report — the wrong octave, not a
        // lossy approximation. `octfuzz` is the circuit the pedal actually is.
        var preset = Convert(tone, out var report);

        var oct = preset.Modules.Single(m => m.ModuleTypeId == "octfuzz");
        Assert.Equal((66f - 1f) / 99f, Param(oct, "octave"), 0.001);   // Mix, a 1..100 % knob
        Assert.DoesNotContain(preset.Modules, m => m.ModuleTypeId == "octaver");
        Assert.DoesNotContain(report.Notes, n => n.GearKey == "Pedal_OctaveUp");
    }

    // ------------------------------------------------ 2.2 retarget: dynamics

    [Fact]
    public void SustainerStylePedal_MapsToSustainerModule()
    {
        // Pedal_Swole is steered onto sustainer via the overrides table.
        var tone = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}},
              "PrePedal1":{"Type":"Pedals","Key":"Pedal_Swole","KnobValues":{"Pedal_Swole_Smash":66,"Pedal_Swole_Rate":81}}}}
            """);
        var preset = Convert(tone, out _);

        var sus = preset.Modules.Single(m => m.ModuleTypeId == "sustainer");
        Assert.Equal((66f - 1f) / 99f, Param(sus, "sustain"), 0.001);
        Assert.Equal((81f - 1f) / 99f, Param(sus, "attack"), 0.001);
        Assert.DoesNotContain(preset.Modules, m => m.ModuleTypeId == "compressor");
    }

    [Fact]
    public void Tremolo_MapsWaveformEnumToShape()
    {
        var tone = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}},
              "PrePedal1":{"Type":"Pedals","Key":"Pedal_Tremolo","Category":"Tremolo","KnobValues":{"Pedal_Tremolo_Waveform":2,"Pedal_Tremolo_Depth":50}}}}
            """);
        var preset = Convert(tone, out _);
        var trem = preset.Modules.Single(m => m.ModuleTypeId == "tremolo");
        Assert.Equal(2f, Param(trem, "shape"), 0.001);
    }

    // ------------------------------------------- fuzz-family targetModule

    [Fact]
    public void FuzzPedal_GetsTheRealFuzzModule()
    {
        // Pedal_BuzzOne (fuzz-face-like) → targetModule "fuzz"; Gain/Tone are 1..100.
        var tone = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_TW22","KnobValues":{}},
              "PrePedal1":{"Type":"Pedals","Key":"Pedal_BuzzOne","KnobValues":{"Pedal_BuzzOne_Gain":80,"Pedal_BuzzOne_Tone":40}}}}
            """);
        var preset = Convert(tone, out _);

        var fuzz = preset.Modules.Single(m => m.ModuleTypeId == "fuzz");
        Assert.Equal((80f - 1f) / 99f, Param(fuzz, "fuzz"), 0.001);
        Assert.Equal((40f - 1f) / 99f, Param(fuzz, "tone"), 0.001);
        Assert.DoesNotContain(preset.Modules, m => m.ModuleTypeId is "distortion" or "overdrive");
    }

    [Fact]
    public void MuffLikePedal_GetsTheBigMuffModule()
    {
        // Pedal_FuzzWasHe (muff-like sustain fuzz) → targetModule "bigmuff";
        // its Gain knob lands on the muff's sustain.
        var tone = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_TW22","KnobValues":{}},
              "PrePedal1":{"Type":"Pedals","Key":"Pedal_FuzzWasHe","KnobValues":{"Pedal_FuzzWasHe_Gain":90,"Pedal_FuzzWasHe_Tone":30}}}}
            """);
        var preset = Convert(tone, out _);

        var muff = preset.Modules.Single(m => m.ModuleTypeId == "bigmuff");
        Assert.Equal((90f - 1f) / 99f, Param(muff, "sustain"), 0.001);
        Assert.Equal((30f - 1f) / 99f, Param(muff, "tone"), 0.001);
    }

    // ----------------------------------------------- no-equivalent handling

    [Fact]
    public void NonEqFilterGear_IsSkippedAndReported()
    {
        // No catalogue gear is dropped any more (Pedal_LoFiFilter and
        // Pedal_BassEmulator, the last two, convert since 2026-10 — #77), so the
        // skip path is held with gear the catalogue does not know but whose tone
        // files it as a Filter with no EQ band: the skip note keeps the report
        // honest instead of emitting a no-op eq.
        var tone = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}},
              "PrePedal1":{"Type":"Pedals","Key":"Pedal_FutureFilter","Category":"Filter","KnobValues":{"Pedal_FutureFilter_Sweep":50}}}}
            """);
        var preset = Convert(tone, out var report);

        Assert.DoesNotContain(preset.Modules, m => m.ModuleTypeId is not ("amp" or "cab" or "micpos" or "gain"));
        Assert.True(report.SkippedCount >= 1);
        Assert.Contains(report.Notes, n => n.GearKey == "Pedal_FutureFilter" && n.Kind == ConversionNoteKind.Skipped
                                           && n.Message.Contains("non-EQ filter", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownGearKey_IsSkippedNeverThrows()
    {
        var tone = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}},
              "PrePedal1":{"Type":"Pedals","Key":"Pedal_FromFutureDLC","KnobValues":{"Pedal_FromFutureDLC_X":1}}}}
            """);
        var preset = Convert(tone, out var report);

        // amp + cab (+ the default cabinet's Ribbon/Cone mic layer) + gain always
        // present; the unknown pedal is dropped.
        Assert.Equal(new[] { "amp", "cab", "micpos", "gain" }, preset.Modules.Select(m => m.ModuleTypeId).ToArray());
        Assert.Contains(report.Notes, n => n.GearKey == "Pedal_FromFutureDLC" && n.Kind == ConversionNoteKind.Skipped);
    }

    [Fact]
    public void SyntheticAmp_IsEmittedAndReported()
    {
        var tone = ParseInline("""{"GearList":{"Cabinet":{"Type":"Cabinets","Key":"Cab_X","KnobValues":{}}}}""");
        var preset = Convert(tone, out var report);

        Assert.Equal("amp", preset.Modules[0].ModuleTypeId);
        Assert.Contains(report.Notes, n => n.Kind == ConversionNoteKind.Fallback);
    }

    // ------------------------------------------- 2.5 loudness consistency

    [Fact]
    public void TrailingGain_TrimsTheChainsStaticLevelGain()
    {
        // Two identical tones except the distortion's Level knob: the hot one's
        // trailing gain must sit LOWER by exactly the level delta, so a mid-song
        // switch between them doesn't step in perceived loudness. The pedal
        // sits POST-amp so no downstream saturating stage can absorb the delta.
        // (Pedal_MetalDistortion has no catalogue Level knob ⇒ 0..100 fallback:
        // 60 → 0.6 = the module default, 100 → 1.0.)
        var neutral = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}},
              "PostPedal1":{"Type":"Pedals","Key":"Pedal_MetalDistortion","KnobValues":{"Pedal_MetalDistortion_Level":60}}}}
            """);
        var hot = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}},
              "PostPedal1":{"Type":"Pedals","Key":"Pedal_MetalDistortion","KnobValues":{"Pedal_MetalDistortion_Level":100}}}}
            """);

        var neutralGain = Param(Convert(neutral, out _).Modules[^1], "gainDb");
        var hotGain = Param(Convert(hot, out _).Modules[^1], "gainDb");

        Assert.Equal(-20f * MathF.Log10(1f / 0.6f), hotGain - neutralGain, 0.001);
    }

    [Fact]
    public void RsVolume_IsDiscarded_AndReported()
    {
        // Two identical rigs whose ONLY difference is the RS mixer Volume must
        // convert to the SAME trailing gain — the RS value targets Rocksmith's
        // own mix bus and used to park converted tones ~15 dB under the
        // backing track here.
        var quiet = ParseInline("""
            {"Volume":"-20.5","GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}}}}
            """);
        var loud = ParseInline("""
            {"Volume":"-6.0","GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}}}}
            """);

        var quietPreset = Convert(quiet, out var quietReport);
        var loudPreset = Convert(loud, out _);

        Assert.Equal(
            Param(loudPreset.Modules[^1], "gainDb"),
            Param(quietPreset.Modules[^1], "gainDb"), 0.0001);

        // The discard is an explicit, badge-visible conversion note.
        Assert.Contains(quietReport.Notes, n =>
            n.GearKey == "Volume" &&
            n.Kind == ConversionNoteKind.Approximated &&
            n.Message.Contains("-20.5"));
    }

    [Fact]
    public void UnityRsVolume_ProducesNoVolumeNote()
    {
        var tone = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}}}}
            """);
        Convert(tone, out var report);
        Assert.DoesNotContain(report.Notes, n => n.GearKey == "Volume");
    }

    [Fact]
    public void TrailingGain_IsTheNormalizerSeam_PlusTheMeasuredModelTrim()
    {
        // The converter's trailing gain is DcRigLoudnessNormalizer's output over
        // the chain body — the one shared loudness seam with the factory preset
        // apply path — plus the measured level trim of the amp's model (the walker
        // cannot read a game chain's level; #88), found by the amp's KEY: the rows are
        // the install's (#755). The stock lead is the Marshall Plexi.
        using var game = SyntheticRsGame.Use();
        var preset = Convert(ToneFixtures.ParseFixture("default_lead.json"), out _);
        var body = preset.Modules.Take(preset.Modules.Count - 1).ToList();

        Assert.Equal(
            Math.Clamp(DcRigLoudnessNormalizer.ComputeTrailingGainDb(body) + RsAmpLevelTrims.ByKey["Amp_MarshallPlexi"], -24f, 24f),
            Param(preset.Modules[^1], "gainDb"), 0.0001);
    }

    [Fact]
    public void EmptyOverrides_StillConvertsGenerically()
    {
        using var game = SyntheticRsGame.Use();
        var tone = ToneFixtures.ParseFixture("default_lead.json");
        var preset = Converter.Convert(tone, RsGearCatalog.Shared, RsGearOverrides.Empty, out _);

        // Overrides steer pedals and cabs; an amp the game ships is its own
        // chain with or without them (#88).
        var amp = preset.Modules.Single(m => m.ModuleTypeId == "rsamp");
        Assert.Equal(SyntheticRsGame.Model("Amp_MarshallPlexi"), Param(amp, "model"), 0.001);
    }

    [Fact]
    public void AnAmpTheGameDoesNotShip_FallsBackToTheRosterGuess_AndSaysSo()
    {
        // A custom key has no chain: the guitar `amp` on the substring guess
        // (Marshall/Plexi-like → crunch = 1), reported.
        var tone = ParseInline("""
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_CustomPlexi","KnobValues":{"Amp_CustomPlexi_Gain":50}}}}
            """);
        var preset = Convert(tone, out var report);
        var amp = preset.Modules.Single(m => m.ModuleTypeId == "amp");
        Assert.Equal(1f, Param(amp, "voicing"), 0.001);
        Assert.Contains(report.Notes, n => n.GearKey == "Amp_CustomPlexi" && n.Kind == ConversionNoteKind.Fallback);
    }
}
