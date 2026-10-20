using System.Text.Json;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Model;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Ai.Model;

/// <summary>
///     The PUBLISHED rig estimator keeps working when the catalogue grows.
///     <para>
///         <c>rig-estimator-v1.zip</c> (v0.55.0, SHA-256 900f9eed…) was exported
///         against a 573-knob label space; the 2026-10 amp / bass-head params (#70,
///         #72) made today's 582. The model is decoded through its OWN sidecar
///         (offsets and ranges it was trained on) and then re-expressed in today's
///         catalogue BY NAME (<c>RigEstimatorOnnx.ConformToCatalogue</c>): a param
///         the model does not predict takes its catalogue default, a predicted one
///         keeps its value (clamped to today's range).
///     </para>
///     <para>
///         <c>Assets/rig-estimator-v1-label-space.json</c> is that zip's
///         <c>model.sidecar.json</c> with its <c>export</c> / <c>parity</c> blocks
///         removed — the label space only (the whole file's SHA-256 is df5e3263…,
///         byte-identical to the installed copy on the owner's machine). It is the
///         versioned compatibility vocabulary these tests decode v1 outputs with.
///     </para>
/// </summary>
public sealed class RigEstimatorV1CompatibilityTests
{
    private const string AssetName = "rig-estimator-v1-label-space.json";

    private static RigLabelVocabularySnapshot V1()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Ai", "Model", "Assets", AssetName);
        return JsonSerializer.Deserialize<RigLabelVocabularySnapshot>(File.ReadAllText(path))
               ?? throw new InvalidDataException(AssetName + " did not deserialise");
    }

    /// <summary>Slot probabilities that put <paramref name="typeIds" /> in slots 0..n-1.</summary>
    private static float[] Slots(RigLabelVocabularySnapshot vocab, params string[] typeIds)
    {
        var classes = vocab.Modules.Count + 1;
        var slots = new float[vocab.Slots * classes];
        for (var s = 0; s < vocab.Slots; s++) slots[s * classes] = 1f;
        for (var s = 0; s < typeIds.Length; s++)
        {
            var cls = vocab.Modules.ToList().FindIndex(m => m.TypeId == typeIds[s]) + 1;
            Assert.True(cls > 0, $"{typeIds[s]} is not in the vocabulary");
            slots[s * classes] = 0f;
            slots[s * classes + cls] = 1f;
        }

        return slots;
    }

    /// <summary>A distinct unit value per knob index, so an offset error shows.</summary>
    private static float[] Knobs(int n)
    {
        var k = new float[n];
        for (var i = 0; i < n; i++) k[i] = (i * 37 % 101) / 100f;
        return k;
    }

    private static readonly string[] Probe = ["amp", "bassamp", "cab", "overdrive", "tremolo", "widener"];

    [Fact]
    public void TheV1LabelSpace_IsTheShippedOne_AndOlderThanToday()
    {
        var v1 = V1();
        Assert.Equal(RigLabelVocabulary.SchemaVersion, v1.SchemaVersion);
        Assert.Equal(573, v1.KnobDimension);
        Assert.Equal(573, v1.Modules.Sum(m => m.Params.Count));
        Assert.True(RigLabelVocabulary.KnobDimension > v1.KnobDimension,
            "today's label space is the v1 one; this test no longer pins a migration");
    }

    [Fact]
    public void V1Outputs_LandOnTodaysCatalogue_NewKnobsAtDefault_OldKnobsUnchanged()
    {
        var v1 = V1();
        var knobs = Knobs(v1.KnobDimension);
        var estimate = RigEstimatorOnnx.Decode(v1, new float[v1.Families.Count], Slots(v1, Probe), knobs, [0.5f]);

        Assert.Equal(Probe, estimate.Rig.Modules.Select(m => m.ModuleTypeId));
        var newKnobs = 0;
        foreach (var module in estimate.Rig.Modules)
        {
            var old = v1.Modules.Single(m => m.TypeId == module.ModuleTypeId);
            var today = DcRigTargetCatalog.AllModules.Single(m => m.ModuleTypeId == module.ModuleTypeId).Params;

            // Exactly today's param set — nothing missing, nothing stale.
            Assert.Equal(today.Select(p => p.Id).Order(), module.Params.Keys.Order());

            foreach (var spec in today)
            {
                var oldIx = old.Params.ToList().FindIndex(p => p.Id == spec.Id);
                if (oldIx < 0)
                {
                    // A knob v1 never predicted: today's default.
                    Assert.Equal(spec.Default, module.Params[spec.Id]);
                    newKnobs++;
                    continue;
                }

                // A knob v1 did predict: the value the v1 decode always gave it,
                // computed from the V1 sidecar alone (its offset, its range).
                var o = old.Params[oldIx];
                var unit = knobs[old.ParamOffset + oldIx];
                var expected = Math.Clamp(o.Min + unit * (o.Max - o.Min), spec.Min, spec.Max);
                Assert.True(Math.Abs(module.Params[spec.Id] - expected) < 1e-4,
                    $"{module.ModuleTypeId}.{spec.Id}: expected {expected}, got {module.Params[spec.Id]}");
            }
        }

        // amp: bright, resonance; bassamp: mid, midHz, midQ, lowMidOct, hiMidOct,
        // lowShift, highShift — the nine knobs 573 -> 582 added.
        Assert.Equal(9, newKnobs);
    }

    [Fact]
    public void AModuleV1NeverKnew_ShiftsTodaysSlotClasses_AndAV1RigNeverNamesIt()
    {
        // `rsamp` (#88, Rocksmith's own amp chains) is a MODULE v1 never saw, not
        // just new knobs: every module after it in ordinal order has a slot class
        // one higher today. Decoding through the model's OWN sidecar is what keeps
        // a v1 "widener" a widener, and nothing v1 outputs can become an rsamp.
        var v1 = V1();
        Assert.DoesNotContain(v1.Modules, m => m.TypeId == "rsamp");
        Assert.Contains(RigLabelVocabulary.Modules, m => m.TypeId == "rsamp");
        var v1Class = v1.Modules.ToList().FindIndex(m => m.TypeId == "widener");
        var todayClass = RigLabelVocabulary.Modules.ToList().FindIndex(m => m.TypeId == "widener");
        Assert.True(todayClass > v1Class, "the new module should sort before `widener`");

        var estimate = RigEstimatorOnnx.Decode(v1, new float[v1.Families.Count], Slots(v1, Probe), Knobs(v1.KnobDimension), [0.5f]);
        Assert.Equal(Probe, estimate.Rig.Modules.Select(m => m.ModuleTypeId));
        Assert.DoesNotContain(estimate.Rig.Modules, m => m.ModuleTypeId == "rsamp");
    }

    [Fact]
    public void EveryV1Module_StillDecodes()
    {
        // No module v1 can predict has left the catalogue, so a v1 rig never loses
        // a slot to the conformance step (routing excepted, by design).
        var v1 = V1();
        var missing = v1.Modules
            .Where(m => m.Category != "routing")
            .Where(m => RigEstimatorOnnx.ConformToCatalogue(m.TypeId, new Dictionary<string, float>()) is null)
            .Select(m => m.TypeId).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void ReadingV1Outputs_ThroughTodaysIndices_WouldBeWrong()
    {
        // Non-vacuity of the by-NAME mapping: v1's knob vector read at TODAY'S
        // offsets puts the wrong numbers on every module after `amp` — which is
        // exactly what decoding a v1 model without its own sidecar would do.
        var v1 = V1();
        var today = RigLabelVocabulary.Snapshot();
        var knobs = Knobs(today.KnobDimension);
        Array.Copy(Knobs(v1.KnobDimension), knobs, v1.KnobDimension);

        var right = RigEstimatorOnnx.Decode(v1, new float[v1.Families.Count], Slots(v1, "overdrive"), knobs, [0.5f]);
        var wrong = RigEstimatorOnnx.Decode(today, new float[today.Families.Count], Slots(today, "overdrive"), knobs, [0.5f]);
        Assert.NotEqual(right.Rig.Modules[0].Params["drive"], wrong.Rig.Modules[0].Params["drive"]);
    }

    [Fact]
    public async Task TheInstalledModel_LoadsAndEstimates_OntoTodaysCatalogue()
    {
        // Opt-in by presence, like RigEstimatorParityTests: the 141 MB model is not
        // in the repo. On a machine that has run "download match model" it is.
        using var estimator = new RigEstimatorOnnx();
        Assert.SkipUnless(estimator.IsAvailable,
            $"no rig estimator installed at {RigEstimatorOnnx.DefaultModelDirectory}");

        var fixture = RigEstimatorParityTests.BuildFixture(48000 * 6, 48000);
        var estimate = await estimator.EstimateAsync(new MonoAudio(fixture, 48000), CancellationToken.None);

        Assert.NotEmpty(estimate.Rig.Modules);
        foreach (var module in estimate.Rig.Modules)
        {
            var today = DcRigTargetCatalog.AllModules.Single(m => m.ModuleTypeId == module.ModuleTypeId).Params;
            Assert.Equal(today.Select(p => p.Id).Order(), module.Params.Keys.Order());
            foreach (var spec in today)
                Assert.InRange(module.Params[spec.Id], spec.Min, spec.Max);
        }
    }
}
