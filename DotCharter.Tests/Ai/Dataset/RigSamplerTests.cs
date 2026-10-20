using DotCharter.Services.Ai.Match;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Ai.Dataset;

/// <summary>
///     <see cref="RigSampler" /> IS the training corpus's label distribution, so a
///     silent regression here is a corpus that teaches the wrong thing and a model
///     nobody can debug months later. Every case below is a property of the
///     DISTRIBUTION rather than of one draw, and each is checked over a fixed seed so
///     a failure reproduces exactly.
/// </summary>
public sealed class RigSamplerTests
{
    private const int Draws = 10_000;

    private static IReadOnlyList<DcRigPreset> Draw(int count, int seed = 20260912,
        RigSamplingProfile? profile = null)
    {
        var sampler = new RigSampler();
        var rng = new Random(seed);
        var p = profile ?? new RigSamplingProfile();
        var rigs = new List<DcRigPreset>(count);
        for (var i = 0; i < count; i++) rigs.Add(sampler.Sample(rng, p));
        return rigs;
    }

    // ------------------------------------------------------------- validation

    [Fact]
    public void EveryDraw_IsAModuleAndAKnobTheCatalogueHas_InRange()
    {
        var known = DcRigTargetCatalog.ModuleTypeIds.ToHashSet(StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (var rig in Draw(Draws))
        {
            foreach (var mod in rig.Modules)
            {
                if (!known.Contains(mod.ModuleTypeId))
                {
                    failures.Add($"unknown module '{mod.ModuleTypeId}'");
                    continue;
                }

                foreach (var (paramId, value) in mod.Params)
                {
                    if (!DcRigTargetCatalog.TryGetParam(mod.ModuleTypeId, paramId, out var spec))
                    {
                        failures.Add($"{mod.ModuleTypeId}.{paramId} is not a knob it has");
                        continue;
                    }

                    if (!float.IsFinite(value))
                        failures.Add($"{mod.ModuleTypeId}.{paramId} = {value} (non-finite)");
                    else if (value < spec.Min || value > spec.Max)
                        failures.Add($"{mod.ModuleTypeId}.{paramId} = {value} outside [{spec.Min}, {spec.Max}]");
                }
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} sampled values are not valid rigs; first 10: "
            + string.Join("; ", failures.Take(10)));
    }

    [Fact]
    public void EveryDraw_SpecifiesEveryKnobTheModuleHas()
    {
        // A sparse label is a label the trainer would have to fill from defaults —
        // which is the "the model learns the default" failure one level up.
        foreach (var rig in Draw(500))
        {
            foreach (var mod in rig.Modules)
            {
                var specs = DcRigTargetCatalog.AllModules
                    .First(m => m.ModuleTypeId == mod.ModuleTypeId).Params;
                Assert.Equal(specs.Count, mod.Params.Count);
            }
        }
    }

    [Fact]
    public void NoKnob_SitsExactlyOnItsCatalogueDefault()
    {
        var offenders = new List<string>();
        foreach (var rig in Draw(2_000))
        {
            foreach (var mod in rig.Modules)
            {
                foreach (var (paramId, value) in mod.Params)
                {
                    if (!DcRigTargetCatalog.TryGetParam(mod.ModuleTypeId, paramId, out var spec)) continue;
                    // Discrete knobs are exempt: a cab MODEL has no "slightly off default".
                    if (IsDiscreteInPractice(mod.ModuleTypeId, paramId, spec)) continue;
                    // The Rocksmith-conversion params (#155, #157) are held at their
                    // defaults on purpose; the next test pins that.
                    if (RigSampler.HeldAtDefault.Contains((mod.ModuleTypeId, paramId))) continue;
                    if (Math.Abs(value - spec.Default) < 1e-6f)
                        offenders.Add($"{mod.ModuleTypeId}.{paramId} = {value} (the default)");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} knobs landed exactly on the catalogue default; first 10: "
            + string.Join("; ", offenders.Take(10)));
    }

    [Fact]
    public void TheRocksmithConversionParams_AreHeldAtTheirDefaults()
    {
        var held = Draw(2_000).SelectMany(r => r.Modules)
            .SelectMany(m => m.Params.Select(p => (Module: m.ModuleTypeId, Param: p.Key, p.Value)))
            .Where(k => RigSampler.HeldAtDefault.Contains((k.Module, k.Param)))
            .ToList();
        Assert.NotEmpty(held);
        Assert.All(held, k =>
        {
            Assert.True(DcRigTargetCatalog.TryGetParam(k.Module, k.Param, out var spec));
            Assert.Equal(spec.Default, k.Value);
        });
    }

    private static bool IsDiscreteInPractice(string typeId, string paramId,
        DcRigTargetCatalog.ParamSpec spec)
    {
        // Mirrors RigSampler's own table closely enough for the exemption; the
        // authoritative one is private because it is an implementation detail.
        string[] discrete =
        [
            "model", "voicing", "waveform", "lfoShape", "voices", "bits", "downsample",
            "taps", "steps", "bands", "pattern", "key", "chord", "octave", "scale",
            "carrier", "curve", "tubes", "mic", "clip", "law", "polarity", "source",
            "filterType", "space", "division", "repeats", "harmonic", "sweep", "mode",
            "direction", "shape", "stages", "sync", "trails", "pingPong", "invertA",
            "invertB", "lowLatency", "formant", "reverse", "halfSpeed", "undo", "swap",
            "midPhase", "sidePhase", "phase", "piezoFix", "cabSim", "bright", "freeze",
            "trigger", "capture", "limit", "humFreq", "spread", "lowShift", "highShift"
        ];
        if (typeId == "compressor" && paramId == "detector") return false;
        _ = spec;
        return discrete.Contains(paramId, StringComparer.Ordinal);
    }

    // --------------------------------------------------------------- coverage

    [Fact]
    public void TenThousandDraws_ReachEveryModule_AndNoneTakesMoreThanAFifthOfTheCorpus()
    {
        // `namamp` is the one module that is gated on a resource rather than on a
        // prior — no captures, no namamp — so the coverage draw hands it a library.
        // (That gate is itself pinned by NamCaptures_AreOnlyDrawnWhenALibraryIsGiven.)
        var dir = Directory.CreateTempSubdirectory("rigsampler-coverage");
        File.WriteAllText(Path.Combine(dir.FullName, "capture.nam"), "{}");
        IReadOnlyList<DcRigPreset> rigs;
        try
        {
            rigs = Draw(Draws, profile: new RigSamplingProfile(AllowNamCaptures: true)
            {
                NamCaptureDirectory = dir.FullName
            });
        }
        finally
        {
            dir.Delete(recursive: true);
        }

        var slots = rigs.Sum(r => r.Modules.Count);
        var perModule = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var rig in rigs)
        {
            foreach (var mod in rig.Modules)
                perModule[mod.ModuleTypeId] = perModule.GetValueOrDefault(mod.ModuleTypeId) + 1;
        }

        var missing = RigSampler.SamplableTypeIds.Where(id => !perModule.ContainsKey(id)).ToArray();
        Assert.True(missing.Length == 0,
            $"{missing.Length} of {RigSampler.SamplableTypeIds.Count} modules never appeared in "
            + $"{Draws} draws — the model could never name them: {string.Join(", ", missing)}");

        // "No module more than 20 %" is a share of SLOTS, not of draws, and that is
        // the only reading compatible with the sampler's own rules: a cab follows an
        // amp, so `cab` is in ~60 % of RIGS by construction and always will be. What
        // matters for training is that no single module dominates the module
        // INSTANCES the model sees.
        var top = perModule.OrderByDescending(kv => kv.Value).First();
        Assert.True(top.Value <= slots * 0.20,
            $"'{top.Key}' is {top.Value / (double)slots:P1} of the {slots} module slots "
            + "(budget 20 %) — the corpus is dominated by one module.");
    }

    // ----------------------------------------------------------- chain length

    [Fact]
    public void ChainsAreMostlyTwoToFiveModulesLong()
    {
        var lengths = Draw(Draws).Select(r => r.Modules.Count).ToArray();
        var inBand = lengths.Count(n => n is >= 2 and <= 5) / (double)lengths.Length;
        var hist = lengths.GroupBy(n => n).OrderBy(g => g.Key)
            .Select(g => $"{g.Key}:{g.Count()}");
        Assert.True(inBand >= 0.70,
            $"only {inBand:P1} of chains are 2–5 modules long (budget ≥ 70 %); histogram {string.Join(" ", hist)}");
        Assert.True(lengths.Max() <= RigSampler.HardMaxModules,
            $"a chain of {lengths.Max()} exceeds the hard cap {RigSampler.HardMaxModules}");
        Assert.All(lengths, n => Assert.True(n >= 1));
    }

    // ------------------------------------------------------------ determinism

    [Fact]
    public void TheSameSeed_ReproducesBitIdenticalRigs()
    {
        var a = Draw(300, seed: 4711);
        var b = Draw(300, seed: 4711);

        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(
                a[i].Modules.Select(m => m.ModuleTypeId),
                b[i].Modules.Select(m => m.ModuleTypeId));
            for (var m = 0; m < a[i].Modules.Count; m++)
            {
                foreach (var (paramId, value) in a[i].Modules[m].Params)
                {
                    Assert.Equal(BitConverter.SingleToInt32Bits(value),
                    BitConverter.SingleToInt32Bits(b[i].Modules[m].Params[paramId]));
                }
            }
        }
    }

    [Fact]
    public void ADifferentSeed_ProducesADifferentCorpus()
    {
        // Guards the guard above: a Sample() that ignored its Random would pass
        // determinism perfectly and be useless.
        var a = Draw(200, seed: 1);
        var b = Draw(200, seed: 2);
        var same = a.Zip(b).Count(p =>
            p.First.Modules.Select(m => m.ModuleTypeId)
                .SequenceEqual(p.Second.Modules.Select(m => m.ModuleTypeId)));
        Assert.True(same < 60, $"{same}/200 chains are identical across seeds — the seed is not driving the draw.");
    }

    // ----------------------------------------------------------------- order

    [Fact]
    public void TheCanonicalFamilyOrder_HoldsInAtLeastNinetyPercentOfDraws()
    {
        var rigs = Draw(Draws);
        var ordered = rigs.Count(IsCanonicallyOrdered);
        var share = ordered / (double)rigs.Count;
        Assert.True(share >= 0.90,
            $"only {share:P1} of chains follow {string.Join(" → ", RigSampler.FamilyOrder)} (budget ≥ 90 %)");
        Assert.True(share <= 0.99,
            $"{share:P1} of chains are canonically ordered — the deviation prior is not firing, "
            + "so the model would learn the ORDER RULE rather than the sound.");
    }

    private static bool IsCanonicallyOrdered(DcRigPreset rig)
    {
        var keys = rig.Modules.Select(m => RigSampler.OrderKey(m.ModuleTypeId)).ToArray();
        for (var i = 1; i < keys.Length; i++)
        {
            if (keys[i].Family < keys[i - 1].Family
                || (keys[i].Family == keys[i - 1].Family && keys[i].Sub < keys[i - 1].Sub))
            {
                return false;
            }
        }

        return true;
    }

    // ------------------------------------------------------ structural rules

    [Fact]
    public void NoRigHasTwoAmps_AndASpeakerAmpAlmostAlwaysHasACab()
    {
        // TrueAmps, not the "amp" FAMILY — `tubepre` is a preamp PEDAL filed under
        // drive, and one in front of an amp is an ordinary rig, not two amps.
        var ampFamily = RigSampler.TrueAmps;
        string[] diAmps = ["acpre"];
        var rigs = Draw(Draws);

        foreach (var rig in rigs)
        {
            var amps = rig.Modules.Count(m => ampFamily.Contains(m.ModuleTypeId));
            Assert.True(amps <= 1,
                $"two amps in one chain: {string.Join(" → ", rig.Modules.Select(m => m.ModuleTypeId))}");
        }

        var speakerAmpRigs = rigs.Where(r =>
            r.Modules.Any(m => ampFamily.Contains(m.ModuleTypeId) && !diAmps.Contains(m.ModuleTypeId))).ToArray();
        var withCab = speakerAmpRigs.Count(r =>
            r.Modules.Any(m => m.ModuleTypeId is "cab" or "acousticsim"));
        var share = withCab / (double)speakerAmpRigs.Length;
        Assert.True(share >= 0.80,
            $"only {share:P1} of the {speakerAmpRigs.Length} speaker-amp rigs carry a cab (budget ≥ 80 %)");
    }

    [Fact]
    public void AMicIsNeverOnANonExistentCabinet()
    {
        foreach (var rig in Draw(Draws))
        {
            if (!rig.Modules.Any(m => m.ModuleTypeId == "micpos")) continue;
            Assert.Contains(rig.Modules, m => m.ModuleTypeId == "cab");
        }
    }

    [Fact]
    public void ExcludedModules_NeverAppear()
    {
        foreach (var rig in Draw(Draws))
        {
            foreach (var mod in rig.Modules)
                Assert.DoesNotContain(mod.ModuleTypeId, RigSampler.Excluded);
        }
    }

    // ------------------------------------------------------------- archetypes

    [Theory]
    [InlineData(RigProfileKinds.Clean, 0.05, 0.40)]
    [InlineData(RigProfileKinds.Crunch, 0.28, 0.68)]
    [InlineData(RigProfileKinds.HighGain, 0.52, 1.00)]
    public void TheArchetypeDrivesTheGain(string kind, double lo, double hi)
    {
        // The one conditioning that matters most: the same knob must mean a different
        // thing in a clean rig and a metal rig, or the two are indistinguishable at
        // the label and the model is being asked to learn a contradiction.
        var rigs = Draw(2_000, profile: new RigSamplingProfile { Kind = kind });
        var gains = rigs
            .SelectMany(r => r.Modules)
            .Where(m => m.ModuleTypeId is "amp" or "namamp")
            .Select(m => (double)m.Params["gain"])
            .ToArray();

        Assert.NotEmpty(gains);
        var mean = gains.Average();
        Assert.True(mean > lo && mean < hi,
            $"{kind}: mean amp gain {mean:F3} is outside [{lo}, {hi}] over {gains.Length} amps");
    }

    [Fact]
    public void TheBassArchetype_ReachesForABassAmp_AndNeverAGuitarAmp()
    {
        var rigs = Draw(1_000, profile: new RigSamplingProfile { Kind = RigProfileKinds.Bass });
        Assert.DoesNotContain(rigs.SelectMany(r => r.Modules), m => m.ModuleTypeId == "amp");
        var withBassAmp = rigs.Count(r => r.Modules.Any(m => m.ModuleTypeId == "bassamp"));
        Assert.True(withBassAmp > 700, $"only {withBassAmp}/1000 bass rigs have a bassamp");
    }

    [Fact]
    public void AllowBassFalse_RemovesTheWholeBassDrawer()
    {
        var rigs = Draw(2_000, profile: new RigSamplingProfile(AllowBass: false));
        foreach (var mod in rigs.SelectMany(r => r.Modules))
            Assert.DoesNotContain(mod.ModuleTypeId, new[] { "bassamp", "bassod", "basssynth" });
    }

    [Fact]
    public void OnlyTypeIds_ConfinesTheDraw()
    {
        string[] only = ["overdrive", "amp", "cab", "reverb"];
        var rigs = Draw(500, profile: new RigSamplingProfile(OnlyTypeIds: only));
        foreach (var mod in rigs.SelectMany(r => r.Modules))
            Assert.Contains(mod.ModuleTypeId, only);
    }

    [Fact]
    public void NamCaptures_AreOnlyDrawnWhenALibraryIsGiven_AndAlwaysCarryAPath()
    {
        // Without a directory: never a namamp, however much the amp pool wants one.
        var bare = Draw(2_000, profile: new RigSamplingProfile(AllowNamCaptures: true));
        Assert.DoesNotContain(bare.SelectMany(r => r.Modules), m => m.ModuleTypeId == "namamp");

        var dir = Directory.CreateTempSubdirectory("rigsampler-nam");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "a.nam"), "{}");
            File.WriteAllText(Path.Combine(dir.FullName, "b.nam"), "{}");
            var rigs = Draw(2_000, profile: new RigSamplingProfile(AllowNamCaptures: true)
            {
                NamCaptureDirectory = dir.FullName
            });
            var nams = rigs.SelectMany(r => r.Modules).Where(m => m.ModuleTypeId == "namamp").ToArray();
            Assert.NotEmpty(nams);
            foreach (var nam in nams)
            {
                Assert.NotNull(nam.StringParams);
                Assert.True(nam.StringParams!.TryGetValue("modelPath", out var path));
                Assert.True(File.Exists(path), $"namamp names a capture that is not there: {path}");
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // ------------------------------------------------------------ knob priors

    [Fact]
    public void AModulationOrTimeEffectsMixIsRarelyFullyWet()
    {
        var mixes = Draw(Draws)
            .SelectMany(r => r.Modules)
            .Where(m => m.ModuleTypeId is "chorus" or "flanger" or "phaser" or "delay"
                or "tapedelay" or "reverb" or "platereverb" or "hallreverb" or "springreverb")
            .Where(m => m.Params.ContainsKey("mix"))
            .Select(m => m.Params["mix"])
            .ToArray();

        Assert.NotEmpty(mixes);
        var wet = mixes.Count(v => v > 0.75f) / (double)mixes.Length;
        Assert.True(wet < 0.02, $"{wet:P1} of time/modulation mixes are above 75 % wet ({mixes.Length} samples)");
        Assert.True(mixes.Average() is > 0.12f and < 0.45f,
            $"mean time/modulation mix {mixes.Average():F3} is not a blend a player would dial");
    }

    [Fact]
    public void ACabinetIsEssentiallyFullyWet()
    {
        var mixes = Draw(2_000).SelectMany(r => r.Modules)
            .Where(m => m.ModuleTypeId == "cab").Select(m => m.Params["mix"]).ToArray();
        Assert.NotEmpty(mixes);
        Assert.All(mixes, v => Assert.True(v >= 0.80f, $"a cabinet at {v:F2} wet is a half-bypassed cabinet"));
    }

    [Fact]
    public void DelayTimesLandOnMusicalSubdivisions()
    {
        var times = Draw(Draws).SelectMany(r => r.Modules)
            .Where(m => m.ModuleTypeId is "delay" or "tapedelay" or "bbddelay")
            .Select(m => m.Params["timeMs"]).ToArray();

        Assert.NotEmpty(times);
        // 60000/bpm * subdivision with bpm in [70,170] and subdivisions 1/4..2 beats
        // spans 88..1714 ms; three quarters of draws are on that grid, so the bulk of
        // the distribution must sit inside it rather than at the catalogue's 2 s top.
        var inBand = times.Count(t => t is >= 80f and <= 1750f) / (double)times.Length;
        Assert.True(inBand >= 0.85, $"only {inBand:P1} of delay times are tempo-plausible");
        Assert.True(times.Count(t => t > 1750f) > 0, "no delay time is ever off the grid — the 25 % free draw is dead");
    }

    [Fact]
    public void TheAmpsBrightSwitch_IsASometimesThing()
    {
        // #70 gave `amp` a bright cap; at a low volume setting it lifts the top
        // by up to ~20 dB, so the corpus draws it on rarely, never as a coin flip.
        var brights = Draw(Draws).SelectMany(r => r.Modules)
            .Where(m => m.ModuleTypeId == "amp")
            .Select(m => m.Params["bright"]).ToArray();
        Assert.NotEmpty(brights);
        Assert.All(brights, b => Assert.True(b is 0f or 1f, $"bright drawn as {b}"));
        Assert.InRange(brights.Count(b => b == 1f) / (double)brights.Length, 0.10, 0.20);
    }

    [Fact]
    public void FeedbackNeverSitsOnTheSelfOscillationCeiling()
    {
        var offenders = Draw(Draws).SelectMany(r => r.Modules)
            .Where(m => m.Params.ContainsKey("feedback"))
            .Where(m => DcRigTargetCatalog.TryGetParam(m.ModuleTypeId, "feedback", out var s) && s.Min >= 0f)
            .Where(m => m.Params["feedback"] > 0.90f)
            .ToArray();
        Assert.True(offenders.Length == 0,
            $"{offenders.Length} unsigned feedbacks are above 0.90 — a runaway tail is not a tone: "
            + string.Join(", ", offenders.Take(5).Select(m => $"{m.ModuleTypeId}={m.Params["feedback"]:F2}")));
    }

    [Fact]
    public void PhaserStagesStayEven()
    {
        // ORDINAL with step 2: the core forces the allpass count even, so an odd
        // label describes a rig the engine did not run.
        var stages = Draw(Draws).SelectMany(r => r.Modules)
            .Where(m => m.ModuleTypeId is "phaser" or "barberpole")
            .Select(m => m.Params["stages"]).ToArray();
        Assert.NotEmpty(stages);
        Assert.All(stages, s => Assert.Equal(0f, s % 2f));
    }

    [Fact]
    public void DiscreteKnobsAreWholeNumbers()
    {
        var offenders = new List<string>();
        foreach (var mod in Draw(2_000).SelectMany(r => r.Modules))
        {
            foreach (var name in new[] { "model", "voicing", "mode", "waveform", "curve", "mic", "clip", "space" })
            {
                if (!mod.Params.TryGetValue(name, out var v)) continue;
                if (Math.Abs(v - MathF.Round(v)) > 1e-4f) offenders.Add($"{mod.ModuleTypeId}.{name}={v}");
            }
        }

        Assert.True(offenders.Count == 0,
            "discrete knobs drawn as continuous: " + string.Join(", ", offenders.Take(10)));
    }
}
