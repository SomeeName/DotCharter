using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.Tone.Parsing.Psarc;

namespace DotCharter.Tests.Audio.Dsp.RsToneConversion;

/// <summary>
/// The 0.9 gear wave's RS→module retargets (native catalogue 57 → 100).
///
/// <para>Each case pins BOTH halves of its change — the module that is now
/// emitted AND the knob values it carries — because a retarget that lands on
/// the right module with default knobs is not a conversion, it is a shrug.
/// Where the OLD target is still in the catalogue the test also asserts the
/// converted tone no longer contains it, so a half-reverted routing table fails
/// here instead of shipping two drives in one chain.</para>
///
/// <para>Every expected number is computed from the RS knob's own catalogue
/// range, never pasted from a run.</para>
/// </summary>
public sealed class RsToneConverterGearWaveTests
{
    private static readonly RsToneConverter Converter = new();

    private static DcRigPreset Convert(RsTone tone, out ConversionReport report)
        => Converter.Convert(tone, RsGearCatalog.Shared, RsGearOverrides.Shared, out report);

    private static float Param(DcRigModule m, string id)
    {
        Assert.True(m.Params.TryGetValue(id, out var v), $"module '{m.ModuleTypeId}' is missing param '{id}'");
        return v;
    }

    private static RsTone ParseInline(string json) =>
        RsToneJsonParser.Parse(new PsarcToneDefinition("K", "N", json));

    private static string Knobs(string key, (string Pot, string Value)[] knobs)
        => string.Join(",", knobs.Select(k => $"\"{key}_{k.Pot}\":{k.Value}"));

    private static DcRigPreset Pedal(out ConversionReport report, string key, params (string Pot, string Value)[] knobs)
        => Convert(ParseInline(
            "{\"GearList\":{\"Amp\":{\"Type\":\"Amps\",\"Key\":\"Amp_Twin\",\"KnobValues\":{}}," +
            $"\"PrePedal1\":{{\"Type\":\"Pedals\",\"Key\":\"{key}\",\"KnobValues\":{{{Knobs(key, knobs)}}}}}}}}}"), out report);

    private static DcRigPreset Rack(out ConversionReport report, string key, params (string Pot, string Value)[] knobs)
        => Convert(ParseInline(
            "{\"GearList\":{\"Amp\":{\"Type\":\"Amps\",\"Key\":\"Amp_Twin\",\"KnobValues\":{}}," +
            $"\"Rack1\":{{\"Type\":\"Racks\",\"Key\":\"{key}\",\"KnobValues\":{{{Knobs(key, knobs)}}}}}}}}}"), out report);

    private static DcRigPreset AmpSlot(out ConversionReport report, string key, params (string Pot, string Value)[] knobs)
        => Convert(ParseInline(
            $"{{\"GearList\":{{\"Amp\":{{\"Type\":\"Amps\",\"Key\":\"{key}\",\"KnobValues\":{{{Knobs(key, knobs)}}}}}}}}}"), out report);

    /// <summary>An RS 1..100 "number" knob normalized the way the converter does.</summary>
    private static float N(float v) => (v - 1f) / 99f;

    // ------------------------------------------------------------- drive

    [Fact]
    public void VintageDistortion_MapsToTheRodent_WithAnInvertedFilterKnob()
    {
        var preset = Pedal(out _, "Pedal_VintageDistortion", ("Gain", "60"), ("Tone", "80"));
        var rat = preset.Modules.Single(m => m.ModuleTypeId == "rat");

        Assert.Equal(N(60), Param(rat, "distortion"), 0.001);
        // THE point of this case: the Rodent's filter pot is DARKER clockwise
        // while every RS "Tone" is BRIGHTER clockwise, so a pass-through mapping
        // parks a bright RS tone at the dark end of its own control.
        Assert.Equal(1f - N(80), Param(rat, "filter"), 0.001);
        Assert.NotEqual(N(80), Param(rat, "filter"), 3);
        Assert.DoesNotContain(preset.Modules, m => m.ModuleTypeId is "distortion" or "overdrive");
    }

    [Fact]
    public void PlainDistortionPedal_MapsToTheOrangeBox()
    {
        var preset = Pedal(out _, "Pedal_Distortion", ("Gain", "80"), ("Tone", "25"));
        var ds1 = preset.Modules.Single(m => m.ModuleTypeId == "ds1");

        Assert.Equal(N(80), Param(ds1, "dist"), 0.001);
        Assert.Equal(N(25), Param(ds1, "tone"), 0.001);
        Assert.DoesNotContain(preset.Modules, m => m.ModuleTypeId is "distortion" or "overdrive");
    }

    [Fact]
    public void MetalDistortion_MapsToTheChainsaw_WithItsToneKnobAsTheColourTilt()
    {
        var preset = Pedal(out _, "Pedal_MetalDistortion", ("Gain", "90"), ("Tone", "71"));
        var hm2 = preset.Modules.Single(m => m.ModuleTypeId == "hm2");

        Assert.Equal(N(90), Param(hm2, "dist"), 0.001);
        // hm2's two colour controls are BOOST-only resonant bells, not a
        // bass/treble pair, so one RS Tone knob becomes the tilt between them —
        // and the two must move in OPPOSITE directions or the knob is just a
        // loudness control.
        Assert.Equal(N(71) * 12f, Param(hm2, "highDb"), 0.001);
        Assert.Equal((1f - N(71)) * 12f, Param(hm2, "lowDb"), 0.001);
        Assert.True(Param(hm2, "highDb") > Param(hm2, "lowDb"));
        Assert.DoesNotContain(preset.Modules, m => m.ModuleTypeId == "distortion");
    }

    /// <summary>
    ///     #157: <c>Bass_Pedal_BassOverdrive</c> through the game's bank. Its Blend is
    ///     the Guitar Distortion's wet/dry with the DRY leg at unity, and its driven leg
    ///     tops out at 0.5 x an output gain Blend also moves, in the game's +18 dB
    ///     frame: <c>bassod</c>'s blend is the DRIVEN share of that sum and its level
    ///     the sum over its 1.4 — so the clean bass leads, as in the game, where the
    ///     label read gave a fuzz with the clean leg far under it. Tone is the
    ///     post-EQ's treble CUT, never a boost. On the synthetic install's curves.
    /// </summary>
    [Fact]
    public void BassOverdrive_FollowsTheGamesBlendAndLevels()
    {
        using var game = SyntheticRsGame.Use();
        var preset = Pedal(out var report, "Bass_Pedal_BassOverdrive",
            ("Gain", "80"), ("Tone", "40"), ("Blend", "55"), ("Filter", "75"));
        var od = preset.Modules.Single(m => m.ModuleTypeId == "bassod");
        var (blend, level) = GameBlend(55f);

        Assert.Equal(blend, Param(od, "blend"), 0.001);
        Assert.True(Param(od, "blend") < 0.5f, "the clean leg leads");
        Assert.Equal(level, Param(od, "level"), 0.001);
        Assert.Equal(1.0, Param(od, "drive"), 0.001);
        Assert.Equal(SyntheticRsGame.Tables.Curves.BassOverdrive!.TrebleDb.Evaluate(40f), Param(od, "treble"), 0.01);
        Assert.True(Param(od, "treble") < 0f);
        Assert.Equal(0.0, Param(od, "bass"), 0.001);
        Assert.DoesNotContain(preset.Modules, m => m.ModuleTypeId == "overdrive");
        Assert.Contains(report.Notes, n =>
            n.GearKey == "Bass_Pedal_BassOverdrive" && n.Kind == ConversionNoteKind.Approximated);
    }

    [Fact]
    public void BassOverdrive_AHighBlend_StaysCleanLed()
    {
        using var game = SyntheticRsGame.Use();
        var od = Pedal(out _, "Bass_Pedal_BassOverdrive",
            ("Gain", "100"), ("Tone", "76"), ("Blend", "70"), ("Filter", "69")).Modules.Single(m => m.ModuleTypeId == "bassod");
        var (blend, level) = GameBlend(70f);
        Assert.Equal(blend, Param(od, "blend"), 0.001);
        Assert.True(blend < 0.5f);
        Assert.Equal(level, Param(od, "level"), 0.001);
    }

    /// <summary>The game's mix in <c>bassod</c>'s terms: the driven leg
    /// <c>wet · out · 0.5 / frame</c> against the clean <c>1 - wet</c>.</summary>
    private static (float Blend, float Level) GameBlend(float blendKnob)
    {
        var c = SyntheticRsGame.Tables.Curves.BassOverdrive!;
        var wet = c.WetPct.Evaluate(blendKnob) / 100f;
        var driven = wet * MathF.Pow(10f, c.OutputDb.Evaluate(blendKnob) / 20f) * RsGameCurves.BassOverdriveShaperCeiling
                     / MathF.Pow(10f, RsGameCurves.GameLevelDb / 20f);
        var clean = 1f - wet;
        return (driven / (driven + clean), Math.Clamp((driven + clean) / 1.4f, 0f, 1f));
    }

    // --------------------------------------------------------- amp slot

    /// <summary>
    ///     Bass heads and DI preamps are amps the game ships too (#88): each one runs
    ///     ITS OWN chain, its knobs found by key whatever order the tone JSON uses
    ///     (here, reversed). On the synthetic install's rows.
    /// </summary>
    [Theory]
    [InlineData("Bass_Amp_EdenWT550")]
    [InlineData("Bass_Amp_TestB")]
    [InlineData("DI_Amp_TestC")]
    public void BassHeadsAndDiPreamps_RunTheirOwnGameChain(string key)
    {
        using var game = SyntheticRsGame.Use();
        var model = SyntheticRsGame.Model(key);
        var row = RsAmpChains.Models[model];
        Assert.Equal(key, row.Key);
        var knobs = row.Knobs.Reverse().Select(k => (k.Key[(key.Length + 1)..], (k.Min + 0.25f * (k.Max - k.Min)).ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        var preset = AmpSlot(out _, key, knobs);

        var amp = preset.Modules.Single(m => m.ModuleTypeId == "rsamp");
        Assert.Equal(model, Param(amp, "model"), 0.001);
        for (var i = 0; i < row.Knobs.Count; i++)
            Assert.Equal(0.25f, Param(amp, "k" + (i + 1)), 0.001);
        Assert.DoesNotContain(preset.Modules, m => m.ModuleTypeId is "amp" or "bassamp" or "tubepre" or "bassod");
    }

    // -------------------------------------------------------- dynamics

    [Fact]
    public void LimiterPedal_MapsToTheLimiter_WithBothKnobsDescending()
    {
        var preset = Pedal(out _, "Pedal_Limiter", ("Limit", "25"), ("Rate", "60"));
        var lim = preset.Modules.Single(m => m.ModuleTypeId == "limiter");

        // More "Limit" is a LOWER ceiling and a higher "Rate" is a SHORTER
        // release: both map backwards, which is exactly what a generic
        // normalization gets inverted.
        Assert.Equal(N(25) * -20f, Param(lim, "ceilingDb"), 0.001);
        Assert.Equal(500f - N(60) * 495f, Param(lim, "releaseMs"), 0.001);
        Assert.DoesNotContain(preset.Modules, m => m.ModuleTypeId == "compressor");
    }

    [Fact]
    public void MultibandCompPedal_MapsToMultiband_AndItsTransitionBandBecomesACrossover()
    {
        var preset = Pedal(out _, "Bass_Pedal_MBComp",
            ("Compress", "40"), ("Rate", "40"), ("Filter", "600"));
        var mb = preset.Modules.Single(m => m.ModuleTypeId == "multiband");

        var ratio = 1f + N(40) * 19f;
        Assert.Equal(ratio, Param(mb, "lowRatio"), 0.001);
        Assert.Equal(ratio, Param(mb, "midRatio"), 0.001);
        Assert.Equal(ratio, Param(mb, "highRatio"), 0.001);
        Assert.Equal(1000f - N(40) * 995f, Param(mb, "releaseMs"), 0.001);
        // The RS knob reaches 1 kHz; the module's low/mid split stops at 500 Hz,
        // so the value CLAMPS rather than landing somewhere it cannot go.
        Assert.Equal(500f, Param(mb, "lowXoverHz"), 0.001);
        Assert.DoesNotContain(preset.Modules, m => m.ModuleTypeId == "compressor");
    }

    [Fact]
    public void CompressionPedal_IsTheGamesWwiseCompressor_NotTheOpticalComp()
    {
        // The 0.9 wave routed it to `optocomp`, whose light cell has no attack or
        // release; the game's own bank (pedal_compression.bnk) is the Wwise
        // Compressor (#69). The per-knob curves are RsToneConverterFidelityWaveTests'.
        var preset = Pedal(out var report, "Pedal_Compression",
            ("Comp", "66"), ("Attack", "30"), ("Release", "200"));
        var comp = preset.Modules.Single(m => m.ModuleTypeId == "compressor");

        Assert.Equal(30f, Param(comp, "attackMs"), 0.001);
        Assert.Equal(200f, Param(comp, "releaseMs"), 0.001);
        Assert.DoesNotContain(report.Notes, n => n.GearKey == "Pedal_Compression");
        Assert.DoesNotContain(preset.Modules, m => m.ModuleTypeId == "optocomp");
    }

    // ---------------------------------------------------------- filter

    [Fact]
    public void SynthFilterRack_MapsToSvf_InsteadOfBeingDropped()
    {
        // Before 0.9 this whole rack was SKIPPED — a Filter-category effect with
        // no EQ gain band, so the converter emitted nothing and said so.
        var preset = Rack(out var report, "Rack_SynthFilter",
            ("Sens", "75"), ("Attack", "50"), ("Release", "160"), ("FilterType", "1"), ("Res", "36"));
        var svf = preset.Modules.Single(m => m.ModuleTypeId == "svf");

        Assert.Equal(2f, Param(svf, "source"), 0.001);        // envelope-swept
        Assert.Equal(N(75), Param(svf, "envSens"), 0.001);
        Assert.Equal(50f, Param(svf, "envAttackMs"), 0.001);  // ms passthrough
        Assert.Equal(160f, Param(svf, "envReleaseMs"), 0.001);
        Assert.Equal(1f, Param(svf, "mode"), 0.001);          // RS enum 1 = band-pass
        Assert.Equal(N(36), Param(svf, "mix"), 0.001);
        Assert.DoesNotContain(report.Notes, n => n.GearKey == "Rack_SynthFilter");
    }

    [Fact]
    public void AcousticEmulator_MapsToAcousticSim_InsteadOfBeingDropped()
    {
        // `acousticsim` has existed since the 0.15.2 wave; this key matched no
        // branch and was reported as having "no equivalent module", which was
        // never true.
        var preset = Pedal(out var report, "Pedal_AcousticEmulator",
            ("Body", "68"), ("Mid", "22"), ("Tone", "82"));
        var sim = preset.Modules.Single(m => m.ModuleTypeId == "acousticsim");

        Assert.Equal(N(68), Param(sim, "body"), 0.001);
        Assert.Equal(N(82), Param(sim, "brightness"), 0.001);
        Assert.DoesNotContain(report.Notes, n => n.Kind == ConversionNoteKind.Skipped);
    }

    /// <summary>
    ///     #73: the Mid-range and Mid Frequency knobs no longer fold. The game's bank
    ///     (a Guitar Distortion block used as an EQ) puts a cut that Mid-range sets on
    ///     a Q-1 band whose centre is Mid Frequency (stored log10(Hz) — like Studio
    ///     EQ's Bass Freq, the label is not what plays). It lands on a notch `svf`
    ///     blended with dry so the centre sits exactly that deep: mix = 1 − 10^(dB/20).
    ///     On the synthetic install's curves.
    /// </summary>
    [Fact]
    public void AcousticEmulatorMidRange_IsTheGamesNotchAtTheMidFrequency()
    {
        using var game = SyntheticRsGame.Use();
        var curves = SyntheticRsGame.Tables.Curves.Acoustic!;
        var preset = Pedal(out var report, "Pedal_AcousticEmulator",
            ("Body", "68"), ("Mid", "22"), ("MidShift", "775"), ("Tone", "82"));
        var notch = preset.Modules.Single(m => m.ModuleTypeId == "svf");

        Assert.Equal(3f, Param(notch, "mode"), 0.001);                 // notch
        Assert.Equal(curves.MidFreqHz.Evaluate(775f), Param(notch, "cutoffHz"), 0.05);
        Assert.NotEqual(775f, Param(notch, "cutoffHz"), 1f);
        Assert.Equal(1f - MathF.Pow(10f, curves.MidGainDb.Evaluate(22f) / 20f), Param(notch, "mix"), 0.0005);
        Assert.Equal(0.104f, Param(notch, "resonance"), 0.002);         // Q 1 = 0.707 * 28^res
        Assert.DoesNotContain(report.Notes, n => n.Message.Contains("folded", StringComparison.Ordinal));

        var shallow = Pedal(out _, "Pedal_AcousticEmulator", ("Mid", "100"), ("MidShift", "2000"))
            .Modules.Single(m => m.ModuleTypeId == "svf");
        Assert.Equal(1f - MathF.Pow(10f, curves.MidGainDb.Evaluate(100f) / 20f), Param(shallow, "mix"), 0.0005);
        Assert.Equal(curves.MidFreqHz.Evaluate(2000f), Param(shallow, "cutoffHz"), 0.05);
    }

    // ----------------------------------------------------------- delay

    [Fact]
    public void StudioRackDelay_MapsToMultitap_WithTwoHardPannedTaps()
    {
        var preset = Rack(out var report, "Rack_StudioDelay",
            ("TimeL", "360"), ("TimeR", "220"), ("Feedback", "38"), ("Mix", "40"), ("Filter", "30"));
        var tap = preset.Modules.Single(m => m.ModuleTypeId == "multitap");

        Assert.Equal(2f, Param(tap, "taps"), 0.001);
        Assert.Equal(1f, Param(tap, "pan"), 0.001);
        Assert.Equal(360f, Param(tap, "timeMs"), 0.001);
        Assert.Equal(0.38f, Param(tap, "feedback"), 0.001);
        Assert.Equal(N(30), Param(tap, "tone"), 0.001);
        // The single-tap `delay` this used to land on could not represent the
        // rack's second time knob at all; multitap approximates it and says so.
        Assert.DoesNotContain(preset.Modules, m => m.ModuleTypeId == "delay");
        Assert.Contains(report.Notes, n =>
            n.GearKey == "Rack_StudioDelay" && n.Kind == ConversionNoteKind.Approximated);
    }

    [Fact]
    public void BucketBrigadeDelay_LongerThanTheChipCanGo_IsClampedAndReported()
    {
        var preset = Pedal(out var report, "Pedal_NPNDelay", ("Time", "900"), ("Feedback", "50"));
        var bbd = preset.Modules.Single(m => m.ModuleTypeId == "bbddelay");

        Assert.Equal(600f, Param(bbd, "timeMs"), 0.001);
        Assert.Contains(report.Notes, n =>
            n.GearKey == "Pedal_NPNDelay" && n.Kind == ConversionNoteKind.Approximated);
    }

    // ------------------------------------------------------ modulation

    [Fact]
    public void RingModPedal_MapsToRingMod_NotToAChorus()
    {
        // Pre-existing routing gap: `ringmod` has existed since the 0.15.1 wave,
        // but Pedal_RingMod matched none of BuildModulation's substring tests and
        // fell through to the CHORUS fallback — an audibly wrong effect, not an
        // approximation. Its knob keys also disagree with its labels: the "Rate"
        // knob's key suffix is `Attack` and carries a PERIOD in ms.
        var preset = Pedal(out var report, "Pedal_RingMod",
            ("Attack", "25"), ("Depth", "50"), ("Sensitivity", "50"), ("Waveform", "0"));
        var rm = preset.Modules.Single(m => m.ModuleTypeId == "ringmod");

        Assert.Equal(1000f / 25f, Param(rm, "freqHz"), 0.001);   // 25 ms period → 40 Hz
        Assert.Equal(N(50), Param(rm, "mix"), 0.001);
        Assert.DoesNotContain(preset.Modules, m => m.ModuleTypeId == "chorus");
        Assert.Contains(report.Notes, n =>
            n.GearKey == "Pedal_RingMod" && n.Kind == ConversionNoteKind.Approximated);
    }

    // ------------------------------------------- loudness: the new modules

    [Fact]
    public void EveryNewLevelChangingModule_MovesTheLoudnessEstimate()
    {
        // A module the estimator does not know is level-NEUTRAL by omission,
        // which is silent and invisible — the exact shape of the bug this
        // guards. Each rig below is that one module wide open; drop its case
        // from EstimateChainOutputDb and the delta collapses to zero.
        (string Module, (string Id, float Value)[] Params)[] cases =
        [
            ("rat", [("distortion", 1f), ("volume", 1f)]),
            ("ds1", [("dist", 1f), ("level", 1f)]),
            ("hm2", [("dist", 1f), ("level", 1f)]),
            ("bassod", [("drive", 1f), ("blend", 1f), ("level", 1f)]),
            ("tubepre", [("gain", 1f), ("level", 1f)]),
            ("octfuzz", [("fuzz", 1f), ("volume", 1f)]),
            ("bassamp", [("gain", 1f), ("master", 1f)]),
            ("poweramp", [("level", 0.1f)]),
            ("micpos", [("level", 0.1f)]),
            ("acpre", [("level", 0.1f)]),
            ("tapesat", [("drive", 1f)]),
            ("optocomp", [("gainDb", 18f)]),
            ("multiband", [("midGainDb", 9f)]),
            ("tilt", [("level", 8f)]),
            ("midside", [("midDb", 6f)]),
            ("transient", [("attackDb", 9f)]),
            ("twelvestring", [("mix", 1f), ("octaveLevel", 1f), ("unisonLevel", 1f)]),
            ("basssynth", [("dryMix", 1f), ("synthMix", 1f)])
        ];

        foreach (var (module, ps) in cases)
        {
            var rig = DcRigTargetCatalog.FillDefaults(new DcRigPreset(new List<DcRigModule>
            {
                new(module, ps.ToDictionary(x => x.Id, x => x.Value, StringComparer.Ordinal))
            }));
            var est = DcRigLoudnessNormalizer.EstimateChainOutputDb(rig.Modules);
            Assert.True(MathF.Abs(est - DcRigLoudnessNormalizer.ReferenceInputPeakDb) > 1f,
                $"'{module}' is invisible to the loudness estimator (est {est:0.0} dBFS)");
        }
    }

    [Fact]
    public void TheLimitersCeiling_IsModelledExactly_NotAsACharacterStage()
    {
        // The one stage in the walker that can be exact rather than estimated:
        // a limiter's output never crosses its ceiling, whatever arrives.
        var rig = DcRigTargetCatalog.FillDefaults(new DcRigPreset(new List<DcRigModule>
        {
            new("distortion", new Dictionary<string, float> { ["gain"] = 1f, ["level"] = 1f }),
            new("limiter", new Dictionary<string, float> { ["ceilingDb"] = -9f, ["gainDb"] = 0f })
        }));

        Assert.Equal(-9f, DcRigLoudnessNormalizer.EstimateChainOutputDb(rig.Modules), 0.001);
    }

    [Fact]
    public void AVolumePedalAtTheHeel_IsHeardAsAMute_NotAsUnity()
    {
        var open = DcRigTargetCatalog.FillDefaults(new DcRigPreset(new List<DcRigModule>
            { new("volume", new Dictionary<string, float> { ["position"] = 1f }) }));
        var shut = DcRigTargetCatalog.FillDefaults(new DcRigPreset(new List<DcRigModule>
            { new("volume", new Dictionary<string, float> { ["position"] = 0f }) }));

        Assert.Equal(DcRigLoudnessNormalizer.ReferenceInputPeakDb,
            DcRigLoudnessNormalizer.EstimateChainOutputDb(open.Modules), 0.001);
        Assert.True(DcRigLoudnessNormalizer.EstimateChainOutputDb(shut.Modules) < -80f);
    }
}
