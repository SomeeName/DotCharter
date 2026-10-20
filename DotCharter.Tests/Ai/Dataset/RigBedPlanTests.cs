using DotCharter.Tools.RigDataset;
using DotCharter.Tools.RigDataset.Beds;
using JuceSharp.Dsp;

namespace DotCharter.Tests.Ai.Dataset;

/// <summary>
///     The bed generator's PURE half: the plan, the sidecar and the synthesis.
///     <para>
///         No engine and no audio device — <see cref="BedPlanner" /> takes its param
///         ranges as an <see cref="IParamRanges" />, so everything here runs on a
///         fake table. That is the whole reason the interface exists; a bed generator
///         whose musical decisions could only be tested through the JUCE null device
///         would be tested by nobody.
///     </para>
/// </summary>
public sealed class RigBedPlanTests
{
    private const int Seed = 20260913;

    /// <summary>A 0…1 range for every knob the bed rigs declare — enough to plan
    /// against, and deliberately NOT a mirror of the catalogue.</summary>
    private static IParamRanges Ranges => FixedParamRanges.UnitDefaults(BedPlanner.DeclaredKnobs);

    [Fact]
    public void TheSameSeed_ReproducesTheSameBedPlan_AndADifferentIndexDoesNot()
    {
        for (var i = 0; i < BedPlanner.DefaultCount; i++)
        {
            var a = BedPlanner.Plan(Seed, i, Ranges);
            var b = BedPlanner.Plan(Seed, i, Ranges);
            Assert.Equal(Json(a), Json(b));
        }

        var distinct = Enumerable.Range(0, BedPlanner.DefaultCount)
            .Select(i => Json(BedPlanner.Plan(Seed, i, Ranges)))
            .Distinct(StringComparer.Ordinal)
            .Count();
        Assert.True(distinct == BedPlanner.DefaultCount,
            $"only {distinct} of {BedPlanner.DefaultCount} bed plans are distinct — the index is not "
            + "driving the draw, so the bed pool is smaller than it claims to be.");
    }

    [Fact]
    public void EverySidecar_CarriesTheOursLicence_AndRoundTripsTheWholePlan()
    {
        var plan = BedPlanner.Plan(Seed, 3, Ranges);
        var audio = new float[4_800];
        for (var i = 0; i < audio.Length; i++) audio[i] = 0.1f * MathF.Sin(i * 0.01f);

        var sidecar = BedSidecar.For(plan, audio, DateTimeOffset.UnixEpoch);
        var json = sidecar.ToJson();

        Assert.Equal(BedSidecar.OursLicence, sidecar.Licence);
        Assert.Contains("No third-party audio", json, StringComparison.Ordinal);

        var back = BedSidecar.FromJson(json);
        Assert.NotNull(back);
        Assert.Equal(BedSidecar.CurrentSchema, back.Schema);
        Assert.Equal(BedSidecar.OursLicence, back.Licence);
        Assert.Equal(plan.Index, back.Plan.Index);
        Assert.Equal(plan.Seed, back.Plan.Seed);
        Assert.Equal(plan.TempoBpm, back.Plan.TempoBpm);
        Assert.Equal(plan.KeyName, back.Plan.KeyName);
        Assert.Equal(plan.Scale, back.Plan.Scale);
        Assert.Equal(plan.Progression, back.Plan.Progression);
        Assert.Equal(plan.Layers.Count, back.Plan.Layers.Count);
        for (var i = 0; i < plan.Layers.Count; i++)
        {
            Assert.Equal(plan.Layers[i].Notes.Count, back.Plan.Layers[i].Notes.Count);
            Assert.Equal(plan.Layers[i].Rig.Count, back.Plan.Layers[i].Rig.Count);
        }

        // The measured levels are what make a sidecar auditable without opening the
        // WAV: a bed that came out silent must not read as a good one.
        Assert.InRange(back.RmsDbfs, -60f, 0f);
        Assert.InRange(back.PeakDbfs, -60f, 0.1f);
    }

    [Fact]
    public void ThirtyBeds_CoverEveryKey_BothScales_AndTheWholeTempoRange()
    {
        var plans = Enumerable.Range(0, BedPlanner.DefaultCount)
            .Select(i => BedPlanner.Plan(Seed, i, Ranges))
            .ToArray();

        var keys = plans.Select(p => p.KeyName).Distinct(StringComparer.Ordinal).Count();
        Assert.True(keys == 12, $"the bed pool covers only {keys} of 12 keys — a separator that has "
                                + "only ever seen three tonalities is a separator the corpus taught a key.");

        var scales = plans.Select(p => p.Scale).Distinct(StringComparer.Ordinal).ToArray();
        Assert.Equal(2, scales.Length);

        var tempos = plans.Select(p => p.TempoBpm).Distinct().ToArray();
        Assert.True(tempos.Length >= 8, $"only {tempos.Length} distinct tempos in the bed pool.");
        Assert.All(plans, p => Assert.InRange(p.Seconds, 60d, 120d));
        Assert.All(plans, p => Assert.Equal(3, p.Layers.Count));
    }

    [Fact]
    public void EveryLayer_SynthesisesAudibleAudioAtTheReferenceLevel()
    {
        // Short beds: the synthesis is O(seconds) and this test does not need 120 s
        // of every layer to prove a voice makes sound.
        var plan = BedPlanner.Plan(Seed, 5, Ranges) with { Seconds = 4d };
        for (var ordinal = 0; ordinal < plan.Layers.Count; ordinal++)
        {
            var audio = BedSynth.RenderLayer(plan, ordinal);
            var rms = 20f * MathF.Log10(MathF.Max(Rms(audio), 1e-12f));
            Assert.True(Math.Abs(rms - BedSynth.LayerRmsDbfs) < 0.5f,
                $"layer '{plan.Layers[ordinal].Name}' came out at {rms:F1} dBFS, not "
                + $"{BedSynth.LayerRmsDbfs} — it is silent, or the normalisation did not run.");
            Assert.All(audio, v => Assert.True(float.IsFinite(v),
                $"layer '{plan.Layers[ordinal].Name}' produced a non-finite sample."));
        }
    }

    [Fact]
    public void TheSynthesisIsBitIdenticalForTheSamePlan()
    {
        var plan = BedPlanner.Plan(Seed, 7, Ranges) with { Seconds = 3d };
        var a = BedSynth.RenderLayer(plan, 0); // drums: the one voice that uses noise
        var b = BedSynth.RenderLayer(plan, 0);
        Assert.Equal(a, b);
    }

    /// <summary>
    ///     The bed rigs' param ids against the REAL catalogue.
    ///     <para>
    ///         A param id the catalogue does not have is dropped silently by the
    ///         planner — correct at runtime and invisible, because the bed then
    ///         renders through that module at its DEFAULTS and the sidecar simply
    ///         does not mention the knob. Six of the twelve ids were wrong on the
    ///         first draft of this feature (guessed <c>compressor.threshold</c> for
    ///         <c>thresholdDb</c>, <c>bassamp.level</c> for <c>master</c>, …), and
    ///         nothing but this test would have said so.
    ///     </para>
    /// </summary>
    [Fact]
    public void EveryKnobTheBedRigsDeclare_IsAKnobTheEngineCatalogueHas()
    {
        IReadOnlyList<JuceDspModuleInfo> modules;
        try
        {
            modules = JuceDspModuleCatalog.Modules;
        }
        catch (DllNotFoundException ex)
        {
            Assert.Skip($"juce_sharp.dll is not next to the test binary ({ex.Message}).");
            return;
        }

        var byId = modules.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var missing = BedPlanner.DeclaredKnobs
            .Where(k => !byId.TryGetValue(k.Module, out var m)
                        || m.Params.All(p => !string.Equals(p.Id, k.Param, StringComparison.Ordinal)))
            .Select(k => $"{k.Module}.{k.Param}")
            .ToArray();

        Assert.True(missing.Length == 0,
            $"{missing.Length} bed-rig knobs are not in the engine catalogue and are therefore set on "
            + $"NOTHING: {string.Join(", ", missing)}");
    }

    private static float Rms(float[] x)
    {
        double acc = 0;
        foreach (var v in x) acc += (double)v * v;
        return (float)Math.Sqrt(acc / Math.Max(1, x.Length));
    }

    private static string Json(BedPlan plan)
        => BedSidecar.For(plan, [], DateTimeOffset.UnixEpoch).ToJson();
}
