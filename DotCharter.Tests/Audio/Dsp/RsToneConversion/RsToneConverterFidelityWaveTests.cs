using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Tones;
using static DotCharter.Tests.Audio.Dsp.RsToneConversion.Fidelity.RsToneFidelityTones;

namespace DotCharter.Tests.Audio.Dsp.RsToneConversion;

/// <summary>
///     The 2026-10 fidelity wave (issue #53, phase 1): gear the converter used to
///     drop, fold or stand in for. One case per closed gap, each asserting the
///     module, the knob values and — where it was a drop or a fold — that the old
///     behaviour is gone. The gear mapped through the game's own numbers runs on the
///     SYNTHETIC install (<see cref="SyntheticRsGame" />, #755): the expectations are its
///     invented curves, the logic is the converter's.
/// </summary>
public sealed class RsToneConverterFidelityWaveTests
{
    private static RsGameCurveSet Game => SyntheticRsGame.Tables.Curves;

    private static DcRigModule Single(DcRigPreset preset, string module)
        => Assert.Single(preset.Modules, m => m.ModuleTypeId == module);

    private static IReadOnlyList<DcRigModule> Gear(RsGear gear, out ConversionReport report)
    {
        report = new ConversionReport();
        return RsToneConverter.ConvertGear(gear, RsGearCatalog.Shared, RsGearOverrides.Shared, report);
    }

    private static RsCab Cab(string key) => new(key, null, new Dictionary<string, float>());

    private static RsPedal Pedal(string key, params (string Pot, float Value)[] knobs)
        => new(RsGearRole.PrePedal, 1, key, RsGearType.Pedal, RsGearCatalog.Shared.GetCategory(key),
            knobs.ToDictionary(k => key + "_" + k.Pot, k => k.Value, StringComparer.Ordinal));

    // ------------------------------------------------------------ cab mic layer

    [Theory]
    [InlineData("Cab_GB412CMKIII_57_Cone", 0f, 0f)]
    [InlineData("Cab_GB412CMKIII_57_Edge", 0f, 1f)]
    [InlineData("Cab_GB412CMKIII_Ribbon_Cone", 1f, 0f)]
    [InlineData("Cab_GB412CMKIII_Condenser_Edge", 3f, 1f)]
    [InlineData("Cab_GB412CMKIII_Tube_Cone", 4f, 0f)]
    public void EveryMicCabinet_GetsAMicLayerBehindIt(string key, float mic, float position)
    {
        var modules = Gear(Cab(key), out var report);

        Assert.Equal(["cab", "micpos"], modules.Select(m => m.ModuleTypeId));
        Assert.Equal(mic, modules[1].Params["mic"]);
        Assert.Equal(position, modules[1].Params["position"]);
        Assert.Equal(RsToneConverter.MicLayerDistanceCm, modules[1].Params["distanceCm"]);
        // 1/1.3: the micpos core multiplies level by 1.3, so the layer is unity.
        Assert.Equal(1f, modules[1].Params["level"] * 1.3f, 4);
        // On axis: the angle stays at its 0 default (the module's pre-#75 path).
        Assert.False(modules[1].Params.ContainsKey("angleDeg") && modules[1].Params["angleDeg"] != 0f);
        Assert.Empty(report.Notes);
    }

    /// <summary>
    ///     #75: Tube and OffAxis are mapped EXACTLY — the tube condenser curve
    ///     (mic 4, fitted to the U 67) and the capsule turned 45° on the cap
    ///     (`angleDeg`, the mic's published polar response) — so neither is
    ///     reported as an approximation any more. Until 2026-10 they rode the
    ///     condenser curve and "half-way to the edge", with two notes.
    /// </summary>
    [Theory]
    [InlineData("Cab_GB412CMKIII_Tube_OffAxis", 4f)]
    [InlineData("Cab_GB412CMKIII_57_OffAxis", 0f)]
    [InlineData("Bass_Cab_AT810BC_Condenser_OffAxis", 3f)]
    [InlineData("Cab_OrangePPC212OB_Ribbon_OffAxis", 1f)]
    public void TubeMicAndOffAxis_AreMappedExactly(string key, float mic)
    {
        var modules = Gear(Cab(key), out var report);

        Assert.Equal(mic, modules[1].Params["mic"]);
        Assert.Equal(0f, modules[1].Params["position"]);
        Assert.Equal(RsToneConverter.MicLayerOffAxisDegrees, modules[1].Params["angleDeg"]);
        Assert.Equal(45f, RsToneConverter.MicLayerOffAxisDegrees);
        Assert.Empty(report.Notes);
    }

    [Fact]
    public void OneOffCabinets_HaveNoMicLayer()
    {
        Assert.Equal(["cab"], Gear(Cab("Cab_Gramophone"), out _).Select(m => m.ModuleTypeId));
        Assert.Equal(["cab"], Gear(Cab("Cab_PA600C"), out _).Select(m => m.ModuleTypeId));
    }

    // ------------------------------------------------------------ roster picks

    [Theory]
    [InlineData("Bass_Cab_AT810BC_57_Cone", 10f)]      // 8x10 -> Bass Fridge
    [InlineData("Bass_Cab_BT410BC_57_Cone", 9f)]       // 4x10 -> 4x10 Bass
    [InlineData("Bass_Cab_AT1150BC_57_Cone", 11f)]     // 1x15 -> 1x15 Vintage Bass
    [InlineData("Cab_GB412CMKIII_57_Cone", 7f)]        // greenback-era 4x12
    [InlineData("Cab_CA412C_57_Cone", 8f)]             // modern V30-class 4x12
    [InlineData("Cab_OrangePPC212OB_Ribbon_Cone", 5f)] // open-back 2x12
    // 2026-10 (#74): the layouts 0-11 lacked, on IRs built from measured drivers
    [InlineData("Cab_TW410C_57_Cone", 12f)]            // 4x10 guitar
    [InlineData("Cab_BT410C_Tube_Edge", 12f)]          // 4x10 guitar
    [InlineData("Cab_TW110C_57_Cone", 13f)]            // 1x10 combo
    [InlineData("Cab_HG215C_57_Cone", 14f)]            // 2x15 guitar
    [InlineData("Cab_CS1515C_Condenser_Cone", 14f)]    // 2x15 guitar
    [InlineData("Cab_CA215C_57_OffAxis", 14f)]         // 2x15 guitar
    [InlineData("Cab_PA1152C", 15f)]                   // 2-way PA, 15 + horn
    [InlineData("Cab_PA600C", 15f)]                    // 2-way PA, bin + horn
    [InlineData("Cab_PA999C", 16f)]                    // PA column
    [InlineData("Cab_Audiophile", 17f)]                // hi-fi towers
    [InlineData("Bass_Cab_CH210BC_57_Cone", 18f)]      // 2x10
    [InlineData("Bass_Cab_CH310BC_57_Cone", 19f)]      // 3x10
    [InlineData("Bass_Cab_EdenD610XST_57_Cone", 20f)]  // 6x10
    [InlineData("Bass_Cab_CS112BC_57_Cone", 21f)]      // 1x12 bass
    [InlineData("Bass_Cab_BT212BC_57_Cone", 22f)]      // 2x12 bass
    [InlineData("Bass_Cab_EdenD212XLT_Tube_Cone", 22f)] // 2x12 bass
    [InlineData("Bass_Cab_TW215BC_57_Cone", 23f)]      // 2x15 bass
    [InlineData("Bass_Cab_GB415BC_57_Cone", 24f)]      // 4x15
    [InlineData("Bass_Cab_CA1510BC_57_Cone", 25f)]     // 1x15 + 1x10
    [InlineData("Cab_Marshall1960TV_57_Cone", 7f)]     // closed 4x12
    [InlineData("Cab_Marshall1960a_57_Cone", 2f)]      // closed 4x12 (angled)
    [InlineData("Cab_OrangeJimmyBean_57_Cone", 8f)]    // closed 4x12
    public void Cabinets_LandOnTheirSpeakerLayout(string key, float model)
        => Assert.Equal(model, Gear(Cab(key), out _)[0].Params["model"]);

    [Theory]
    [InlineData("Amp_MarshallPlexi", 0f)]
    [InlineData("Amp_TestA", 2f)]
    [InlineData("Bass_Amp_TestB", 4f)]
    [InlineData("DI_Amp_TestC", 5f)]
    public void EveryAmp_IsItsOwnGameChain(string key, float model)
    {
        // #88: no roster pick any more; the row of Rocksmith's own chains.
        using var game = SyntheticRsGame.Use();
        var modules = Gear(new RsAmp(key, "Amp", new Dictionary<string, float>()), out _);
        Assert.Equal(["rsamp"], modules.Select(m => m.ModuleTypeId));
        Assert.Equal(model, modules[0].Params["model"]);
    }

    // ------------------------------------------------------------ four-band EQ

    [Fact]
    public void StudioEq_HiMid_GetsItsOwnBell_InsteadOfBeingFolded()
    {
        using var game = SyntheticRsGame.Use();
        var preset = Convert("Amp_MarshallJCM800", "Cab_GB412CMKIII_57_Cone",
            racks: [("Rack_StudioEQ", Pots(("LoMid", -4), ("LoMidFreq", 300), ("HiMid", 6), ("HiMidFreq", 2.5f)))]);

        var eqs = preset.Modules.Where(m => m.ModuleTypeId == "eq").ToList();
        Assert.Equal(2, eqs.Count);
        Assert.Equal(-4f, eqs[0].Params["midGainDb"]);
        Assert.Equal(Game.StudioEq!.LoMidFreqHz.Evaluate(300f), eqs[0].Params["midFreq"]);
        Assert.Equal(6f, eqs[1].Params["midGainDb"]);
        Assert.Equal(Game.StudioEq!.HiMidFreqHz.Evaluate(2.5f), eqs[1].Params["midFreq"]);
        Assert.Equal(0f, eqs[1].Params["lowGainDb"]);
        Assert.Equal(0f, eqs[1].Params["highGainDb"]);
    }

    // ------------------------------------------- #68: Rack_StudioEQ, game curves

    private static RsPedal StudioEq(params (string Pot, float Value)[] knobs)
        => new(RsGearRole.Rack, 1, "Rack_StudioEQ", RsGearType.Rack, "Filter",
            knobs.ToDictionary(k => "Rack_StudioEQ_" + k.Pot, k => k.Value, StringComparer.Ordinal));

    [Theory]
    // Every curve end lands: before 2026-10 the eq's 40-400 / 200-4000 / 2000-12000 Hz
    // ranges clamped most of the game's ends.
    [InlineData("BassFreq", 30f, "lowFreq", 30f)]
    [InlineData("BassFreq", 400f, "lowFreq", 400f)]
    [InlineData("LoMidFreq", 150f, "midFreq", 150f)]
    [InlineData("LoMidFreq", 1800f, "midFreq", 1800f)]
    [InlineData("TrebleFreq", 3f, "highFreq", 3000f)]
    [InlineData("TrebleFreq", 18f, "highFreq", 18000f)]
    public void StudioEq_EveryFrequencyEnd_Lands_Unclamped_AndUnreported(string pot, float knob, string param, float hz)
    {
        using var game = SyntheticRsGame.Use();
        var modules = Gear(StudioEq(("Bass", 3), ("LoMid", 3), ("HiMid", 3), ("Treble", 3), (pot, knob)), out var report);
        Assert.Equal(hz, modules[0].Params[param], hz * 1e-4f);
        Assert.Empty(report.Notes);
    }

    [Theory]
    [InlineData(1f, 1000f)]
    [InlineData(7f, 7000f)]
    public void StudioEq_HiMidEnds_LandOnTheSecondBell(float knob, float hz)
    {
        using var game = SyntheticRsGame.Use();
        var modules = Gear(StudioEq(("LoMid", 1), ("HiMid", 3), ("HiMidFreq", knob)), out var report);
        Assert.Equal(hz, modules[1].Params["midFreq"], hz * 1e-4f);
        Assert.Empty(report.Notes);
    }

    [Fact]
    public void StudioEq_TheKnobLabelIsNotTheCorner_TheGamesCurveIs()
    {
        // A Bass Freq. curve that is Linear in log10 puts a knob reading of 120 well
        // under 120 Hz: the shelf is the curve's, not the label's.
        using var game = SyntheticRsGame.Use();
        var modules = Gear(StudioEq(("Bass", 6), ("BassFreq", 120), ("LoMid", 0), ("HiMid", 0)), out _);
        Assert.Equal(Game.StudioEq!.BassFreqHz.Evaluate(120f), modules[0].Params["lowFreq"], 0.01f);
        Assert.True(modules[0].Params["lowFreq"] < 100f);
    }

    [Fact]
    public void StudioEq_QSwitch_IsTheGamesQCurve()
    {
        using var game = SyntheticRsGame.Use();
        var wide = Gear(StudioEq(("LoMid", 3), ("LoMidQ", 0), ("HiMid", 3), ("HiMidQ", 0)), out _);
        var narrow = Gear(StudioEq(("LoMid", 3), ("LoMidQ", 1), ("HiMid", 3), ("HiMidQ", 1)), out _);
        Assert.All(wide, m => Assert.Equal(0.5f, m.Params["midQ"], 1e-5f));
        Assert.All(narrow, m => Assert.Equal(3f, m.Params["midQ"], 1e-5f));
        Assert.Equal(2, wide.Count);
    }

    [Fact]
    public void StudioEq_AMissingFrequencyKnob_IsTheGamesDefault_NotTheModules()
    {
        // A tone the game writes carries every knob; a hand-made one may not. The
        // game would play its own default through its curve, not the eq module's.
        using var game = SyntheticRsGame.Use();
        Assert.True(RsGearCatalog.Shared.TryGetKnob("Rack_StudioEQ", "TrebleFreq", out var knob));
        var modules = Gear(StudioEq(("Treble", 4)), out _);
        Assert.Equal(Game.StudioEq!.TrebleFreqHz.Evaluate(knob!.DefaultValue), modules[0].Params["highFreq"]);
    }

    [Fact]
    public void OtherEqGear_StillReportsAClamp()
    {
        // The generic eq path keeps reporting a value it cannot place: Pedal_AmpEQ's
        // enum-valued "Freq" knobs are not Hz, and 0 / 1 Hz is outside every band.
        var modules = Gear(Pedal("Pedal_AmpEQ", ("Bass", 50), ("BassFreq", 1), ("Mid", 50), ("Treble", 50)), out var report);
        Assert.Equal(20f, modules[0].Params["lowFreq"]);
        Assert.Contains(report.Notes, n => n.Kind == ConversionNoteKind.Approximated && n.Message.Contains("BassFreq", StringComparison.Ordinal));
    }

    // ------------------------------------------- #69: Pedal_Compression

    [Fact]
    public void Compression_IsTheWwiseCompressor_AndEveryKnobLands()
    {
        using var game = SyntheticRsGame.Use();
        var modules = Gear(Pedal("Pedal_Compression", ("Comp", 66), ("Attack", 20), ("Release", 30)), out var report);

        var comp = Assert.Single(modules);
        var c = Game.Compression!;
        Assert.Equal("compressor", comp.ModuleTypeId);
        Assert.Equal(c.AttackMs.Evaluate(20f), comp.Params["attackMs"], 0.001f);    // was folded
        Assert.Equal(c.ReleaseMs.Evaluate(30f), comp.Params["releaseMs"], 0.001f);  // was folded
        Assert.Equal(c.ThresholdDb.Evaluate(66f), comp.Params["thresholdDb"], 0.01f);
        Assert.Equal(c.Ratio.Evaluate(66f), comp.Params["ratio"], 0.01f);
        Assert.Equal(c.OutputGainDb.Evaluate(66f), comp.Params["makeupDb"], 0.01f);
        Assert.Empty(report.Notes);
    }

    [Theory]
    // The bank stores seconds; the module takes ms.
    [InlineData("Attack", 0f, "attackMs", 2f)]
    [InlineData("Attack", 100f, "attackMs", 90f)]
    [InlineData("Release", 0f, "releaseMs", 50f)]
    [InlineData("Release", 100f, "releaseMs", 500f)]
    public void Compression_TimeKnobEnds_LandInMs(string pot, float knob, string param, float ms)
    {
        using var game = SyntheticRsGame.Use();
        Assert.Equal(ms, Assert.Single(Gear(Pedal("Pedal_Compression", ("Comp", 50), (pot, knob)), out _)).Params[param], 0.001f);
    }

    [Fact]
    public void Compression_TheRatioTop_IsHeldAtTheModules20()
    {
        // A bank ratio curve that tops out past 20:1 stops at the module's 20:1.
        using var game = SyntheticRsGame.Use();
        var comp = Assert.Single(Gear(Pedal("Pedal_Compression", ("Comp", 100)), out _));
        Assert.True(Game.Compression!.Ratio.Evaluate(100f) > 20f);
        Assert.Equal(20f, comp.Params["ratio"]);
        Assert.Equal(-40f, comp.Params["thresholdDb"], 0.001f);
        Assert.Equal(10f, comp.Params["makeupDb"], 0.01f);
    }

    // ------------------------------------------- #77: the last two drops

    [Fact]
    public void LoFiFilter_IsALowCutThenAHighCut_AtTheGamesCorners()
    {
        // Knob keys are the game's own (and misleading): "Hi Cut" is _FilterType,
        // "Lo Cut" is _Mix.
        using var game = SyntheticRsGame.Use();
        var modules = Gear(Pedal("Pedal_LoFiFilter", ("FilterType", 30), ("Mix", 88)), out var report);

        Assert.Equal(["svf", "svf"], modules.Select(m => m.ModuleTypeId));
        Assert.Equal(2f, modules[0].Params["mode"]);       // HighPass = the low cut
        Assert.Equal(100f + 1900f * 0.88f, modules[0].Params["cutoffHz"], 0.05f);
        Assert.Equal(0f, modules[1].Params["mode"]);       // LowPass = the high cut
        Assert.Equal(16000f - 14000f * 0.3f, modules[1].Params["cutoffHz"], 0.5f);
        foreach (var svf in modules)
        {
            Assert.Equal(0f, svf.Params["source"]);    // manual: no sweep
            Assert.Equal(0f, svf.Params["resonance"]); // Butterworth
            Assert.Equal(1f, svf.Params["mix"]);
        }

        Assert.Equal(0, report.SkippedCount);
        Assert.Equal(1, report.ApproximatedCount); // the slopes are not decodable
    }

    [Fact]
    public void LoFiFilter_HiCutUp_IsMoreCut()
    {
        using var game = SyntheticRsGame.Use();
        var open = Gear(Pedal("Pedal_LoFiFilter", ("FilterType", 0), ("Mix", 0)), out _);
        var shut = Gear(Pedal("Pedal_LoFiFilter", ("FilterType", 100), ("Mix", 100)), out _);
        Assert.Equal((100f, 16000f), (open[0].Params["cutoffHz"], open[1].Params["cutoffHz"]));
        Assert.Equal((2000f, 2000f), (shut[0].Params["cutoffHz"], shut[1].Params["cutoffHz"]));
    }

    [Fact]
    public void BassEmulator_IsAnOctaveDownHarmonizer_WithItsVoiceFilter()
    {
        using var game = SyntheticRsGame.Use();
        var modules = Gear(Pedal("Pedal_BassEmulator", ("Body", 100), ("Tone", 0)), out var report);

        Assert.Equal(["harmonizer", "eq"], modules.Select(m => m.ModuleTypeId));
        Assert.Equal(-12f, modules[0].Params["semitones"]);
        Assert.Equal(1f, modules[0].Params["mix"]);           // the game's dry is -96 dB
        Assert.Equal(6f, modules[1].Params["midGainDb"], 0.01f);
        Assert.Equal(200f, modules[1].Params["midFreq"], 0.01f);
        Assert.Equal(1f, modules[1].Params["midQ"]);
        Assert.Equal(0f, modules[1].Params["lowGainDb"]);
        Assert.Equal(0f, modules[1].Params["highGainDb"]);
        // The voice, the dry level, the filter TYPE (PEAKINGEQ) and both knobs are
        // the game's — nothing is reported.
        Assert.Empty(report.Notes);
    }

    // ------------------------------------------------------------ was dropped

    [Fact]
    public void RangeBooster_IsATrebleBooster_NotADrop()
    {
        var modules = Gear(Pedal("Pedal_RangeBooster", ("Boost", 50.5f)), out var report);

        var boost = Assert.Single(modules);
        Assert.Equal("boost", boost.ModuleTypeId);
        Assert.Equal(1f, boost.Params["character"]);               // Rangemaster-style
        Assert.Equal(12f, boost.Params["gainDb"], 2);               // 50.5 % of 1..100 -> 12 dB
        Assert.Equal(0, report.SkippedCount);
    }

    /// <summary>
    ///     #157: the Bit Cruncher is a McDSP FutzBox in the game's bank, and the
    ///     conversion follows it. Every knob reaches its FutzBox stage through the
    ///     bank's curve — Filter (key <c>Attack</c>; up = LESS filter), Rectify
    ///     (<c>Sens</c>), Downsample (value + 1) — and the fixed stages land too:
    ///     Amount, Intensity, input and output in the game's level frame (±18 dB).
    ///     Until #157 only bits, downsample (one short) and mix landed: a raw crush
    ///     as loud as the dry.
    /// </summary>
    [Fact]
    public void BitCruncher_IsTheGamesFutzBox()
    {
        using var game = SyntheticRsGame.Use();
        var modules = Gear(Pedal("Pedal_BitCruncher",
            ("FilterType", 4), ("Release", 14), ("Mix", 63), ("Attack", 72), ("Sens", 1)), out var report);

        var crush = Assert.Single(modules);
        Assert.Equal("bitcrusher", crush.ModuleTypeId);
        Assert.Equal(4f, crush.Params["bits"]);
        Assert.Equal(15f, crush.Params["downsample"]);
        Assert.Equal(0.63f, crush.Params["mix"], 4);
        Assert.Equal(28f, crush.Params["filter"], 2);
        Assert.Equal(30f, crush.Params["rectify"], 2);
        Assert.Equal(50f, crush.Params["drive"]);
        Assert.Equal(10f, crush.Params["intensity"]);
        Assert.Equal(7f + RsGameCurves.GameLevelDb, crush.Params["inputDb"]);
        Assert.Equal(-9f - RsGameCurves.GameLevelDb, crush.Params["outputDb"]);
        Assert.Equal(1, report.ApproximatedCount); // the inferred FutzBox laws, one note
    }

    [Fact]
    public void BitCruncher_TopOfItsKnobs_TakesTheFilterOut_AndRectifiesNothing()
    {
        using var game = SyntheticRsGame.Use();
        var crush = Assert.Single(Gear(Pedal("Pedal_BitCruncher",
            ("FilterType", 8), ("Release", 0), ("Mix", 100), ("Attack", 100), ("Sens", 100)), out _));
        Assert.Equal(8f, crush.Params["bits"]);
        Assert.Equal(1f, crush.Params["downsample"]);   // Downsample 0 = no decimation
        Assert.Equal(0f, crush.Params["filter"], 3);
        Assert.Equal(0f, crush.Params["rectify"], 3);
    }

    // ------------------------------------------------------------ was folded

    [Fact]
    public void ATremoloWithoutADepthKnob_TakesItsMixAsDepth()
    {
        var modules = Gear(Pedal("Pedal_Tremolo", ("Speed", 50), ("Mix", 75.25f)), out _);
        Assert.Equal(0.75f, Single(new DcRigPreset(modules), "tremolo").Params["depth"], 3);
    }

    [Fact]
    public void ChorusStereoAndLowFilter_LandOnSpreadAndTheWetHighPass()
    {
        using var game = SyntheticRsGame.Use();
        var modules = Gear(Pedal("Pedal_DigitalChorus", ("Rate", 1), ("Mix", 50), ("Depth", 50), ("LoFilter", 100), ("HiFilter", 50)), out _);
        var chorus = Single(new DcRigPreset(modules), "chorus");
        Assert.Equal(400f, chorus.Params["wetHpfHz"]);

        var rack = new RsPedal(RsGearRole.Rack, 1, "Rack_StudioChorus", RsGearType.Rack, "Modulation",
            new Dictionary<string, float> { ["Rack_StudioChorus_Stereo"] = 100 });
        // The synthetic Stereo curve tops out at 90 degrees: half the module's spread.
        Assert.Equal(0.5f, Single(new DcRigPreset(Gear(rack, out _)), "chorus").Params["spread"], 1e-4f);
    }

    [Fact]
    public void NoiseGateRate_IsItsRelease_FasterIsShorter()
    {
        var slow = Single(new DcRigPreset(Gear(Pedal("Pedal_NoiseGate", ("Thresh", -60), ("Rate", 1)), out _)), "gate");
        var fast = Single(new DcRigPreset(Gear(Pedal("Pedal_NoiseGate", ("Thresh", -60), ("Rate", 100)), out _)), "gate");
        Assert.Equal(500f, slow.Params["releaseMs"]);
        Assert.Equal(5f, fast.Params["releaseMs"]);
    }

    [Fact]
    public void ABassHeadsGraphicSection_IsPartOfItsOwnChain_NotAnExtraNode()
    {
        // The CLH-350B's graphic faders are bands of its own soundbank chain
        // (#88); until then they were folded onto a trailing graphiceq. The chain
        // is the install's (#755): owner-only.
        using var tables = TestRsGameTables.UseReal();
        var head = new RsAmp("Bass_Amp_CH350B", "Amp", new Dictionary<string, float>
        {
            ["Bass_Amp_CH350B_Gain"] = 30,
            ["Bass_Amp_CH350B_90"] = 4,
            ["Bass_Amp_CH350B_2500"] = -6,
            ["Bass_Amp_CH350B_15000"] = 3
        });
        var modules = Gear(head, out _);

        Assert.Equal(["rsamp"], modules.Select(m => m.ModuleTypeId));
        var row = RsAmpChains.Models[(int)modules[0].Params["model"]];
        var k90 = row.Knobs.ToList().FindIndex(k => k.Key == "Bass_Amp_CH350B_90");
        Assert.Equal((4f + 12f) / 24f, modules[0].Params["k" + (k90 + 1)], 0.001);
    }

    // ------------------------------------------------------------ loudness

    [Fact]
    public void TheWalker_ReadsTheMicLayerAsUnity_AndTheBoostByItsGain()
    {
        var micLayer = new DcRigModule("micpos", new Dictionary<string, float> { ["level"] = RsToneConverter.MicLayerUnityLevel });
        Assert.Equal(-18f, DcRigLoudnessNormalizer.EstimateChainOutputDb([micLayer]), 3);

        // A clean boost adds its gain; a treble booster is capped by its own
        // tanh make-up (1 / (1 + 2c) at character c).
        var clean = new DcRigModule("boost", new Dictionary<string, float> { ["gainDb"] = 12, ["character"] = 0 });
        Assert.Equal(-6f, DcRigLoudnessNormalizer.EstimateChainOutputDb([clean]), 3);
        var treble = new DcRigModule("boost", new Dictionary<string, float> { ["gainDb"] = 24, ["character"] = 1 });
        Assert.Equal(20f * MathF.Log10(1f / 3f), DcRigLoudnessNormalizer.EstimateChainOutputDb([treble]), 3);
    }
}
