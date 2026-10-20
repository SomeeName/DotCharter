using System.Text.Json;
using DotCharter.Services.Ai.Match.Model;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Ai.Model;

/// <summary>
///     The label space the python trainer and the C# estimator share. These are
///     the guards that stop the two drifting: the vocabulary must cover the
///     whole catalogue, the flat knob vector's offsets must be consistent, the
///     normalisation must round-trip, and the sidecar the trainer reads must be
///     the one this code produces.
/// </summary>
public sealed class RigLabelVocabularyTests
{
    [Fact]
    public void EveryCatalogueModule_HasANativeCategory()
    {
        // The fallback in BuildModules ("utility") exists so a brand-new
        // catalogue module cannot crash the estimator — but a module that HITS
        // it is mislabelled in every training row, so it must never happen
        // silently. This is the test that says so.
        var missing = DcRigTargetCatalog.ModuleTypeIds
            .Where(id => RigLabelVocabulary.CategoryOf(id) is null)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "These catalogue modules have no row in RigLabelVocabulary.CategoryByModule and would "
            + "silently train as 'utility'. Read each one's category out of the native catalogue "
            + "(JuceSharp/src/JuceSharp.Native/src/dsp/DspModuleFactory.cpp and src/dsp/gear/*.cpp) "
            + "and add it: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryCategory_IsOneOfTheNativeFixedSet()
    {
        // GearEntry.h's fixed palette set (ABI v33). A category is not free
        // text: a new one has to be added there first.
        string[] native =
        [
            "amp", "cab", "delay", "drive", "dynamics", "eq", "filter",
            "modulation", "pitch", "reverb", "routing", "synth", "utility"
        ];

        Assert.Equal(native, RigLabelVocabulary.Families);
        Assert.Equal(13, RigLabelVocabulary.Families.Count);

        foreach (var module in RigLabelVocabulary.Modules)
            Assert.Contains(module.Category, native);
    }

    [Fact]
    public void Vocabulary_CoversTheWholeCatalogue_InOrdinalOrder()
    {
        Assert.Equal(
            DcRigTargetCatalog.ModuleTypeIds.OrderBy(id => id, StringComparer.Ordinal).ToList(),
            RigLabelVocabulary.Modules.Select(m => m.TypeId).ToList());

        // The slot softmax is 1 + N wide, with none at 0. 101 since `rsamp` (#88).
        Assert.Equal(101, RigLabelVocabulary.Modules.Count);
    }

    [Fact]
    public void KnobVector_OffsetsArePackedAndConsistent()
    {
        var expected = 0;
        foreach (var module in RigLabelVocabulary.Modules)
        {
            Assert.Equal(expected, module.ParamOffset);
            Assert.NotEmpty(module.Params);
            expected += module.Params.Count;
        }

        Assert.Equal(expected, RigLabelVocabulary.KnobDimension);
    }

    [Fact]
    public void EveryParamRange_MatchesTheCatalogueExactly()
    {
        foreach (var module in RigLabelVocabulary.Modules)
        {
            foreach (var param in module.Params)
            {
                Assert.True(DcRigTargetCatalog.TryGetParam(module.TypeId, param.Id, out var spec),
                    $"{module.TypeId}.{param.Id} is in the vocabulary but not in the catalogue");
                Assert.Equal(spec.Min, param.Min);
                Assert.Equal(spec.Max, param.Max);
                Assert.Equal(spec.Default, param.Default);
            }
        }
    }

    [Fact]
    public void Normalise_RoundTripsThroughDenormalise()
    {
        foreach (var module in RigLabelVocabulary.Modules)
        {
            foreach (var param in module.Params)
            {
                foreach (var t in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
                {
                    var raw = param.Min + t * (param.Max - param.Min);
                    var unit = RigLabelVocabulary.Normalise(param, raw);
                    var back = RigLabelVocabulary.Denormalise(param, unit);

                    Assert.InRange(unit, 0f, 1f);
                    var tolerance = MathF.Max(1e-4f, MathF.Abs(param.Max - param.Min) * 1e-5f);
                    Assert.True(MathF.Abs(back - raw) <= tolerance,
                        $"{module.TypeId}.{param.Id}: {raw} -> {unit} -> {back}");
                }
            }
        }
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Normalise_TreatsANonFiniteValueAsTheDefault(float bad)
    {
        // A NaN passes every `<` and `>` comparison, so an unguarded clamp
        // carries it straight into the training tensor — where it poisons the
        // whole batch's gradient. The python side does the same thing.
        var spec = new ParamRange("gainDb", -24f, 24f, 0f);
        Assert.Equal(RigLabelVocabulary.Normalise(spec, spec.Default), RigLabelVocabulary.Normalise(spec, bad));
        Assert.True(float.IsFinite(RigLabelVocabulary.Normalise(spec, bad)));
    }

    [Fact]
    public void Normalise_ClampsOutOfRange()
    {
        var spec = new ParamRange("drive", 0f, 1f, 0.5f);
        Assert.Equal(0f, RigLabelVocabulary.Normalise(spec, -5f));
        Assert.Equal(1f, RigLabelVocabulary.Normalise(spec, 5f));
    }

    [Fact]
    public void Sidecar_RoundTripsAndIsExportedForTheTrainer()
    {
        var json = RigLabelVocabulary.ToJson();
        var parsed = JsonSerializer.Deserialize<RigLabelVocabularySnapshot>(json);

        Assert.NotNull(parsed);
        Assert.Equal(RigLabelVocabulary.SchemaVersion, parsed!.SchemaVersion);
        Assert.Equal(48000, parsed.SampleRate);
        Assert.Equal(5f, parsed.ClipSeconds);
        Assert.Equal(7, parsed.Slots);
        Assert.Equal(RigLabelVocabulary.KnobDimension, parsed.KnobDimension);
        Assert.Equal(RigLabelVocabulary.Modules.Count, parsed.Modules.Count);
        Assert.Equal(RigLabelVocabulary.Families, parsed.Families);

        // Export it where tools/rig-model/vocab.py reads it. This is the "export
        // the table to JSON from the tool so python and C# agree" step: the JSON
        // in the tree is a BUILD PRODUCT of this type, never hand-edited, and
        // running the gate refreshes it. Line endings are not content: the file
        // is checked in with CRLF and the serializer writes the platform's, so
        // comparing them raw rewrote a tracked file on every non-Windows run.
        var target = Path.Combine(TestPaths.RepoRoot(), "tools", "rig-model", "rig_label_vocab.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var existing = File.Exists(target) ? File.ReadAllText(target) : null;
        if (existing?.ReplaceLineEndings() != json.ReplaceLineEndings()) File.WriteAllText(target, json);

        Assert.True(File.Exists(target));
    }
}
