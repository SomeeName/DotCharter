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

    /// <summary>Set to <c>1</c> to rewrite the committed sidecar from the type.</summary>
    public const string RegenVariable = "DOTCHARTER_REGEN_RIG_VOCAB";

    private const string SidecarRelativePath = "tools/rig-model/rig_label_vocab.json";

    [Fact]
    public void Sidecar_RoundTrips()
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
    }

    /// <summary>
    ///     The JSON <c>tools/rig-model/vocab.py</c> reads is a BUILD PRODUCT of this
    ///     type, never hand-edited, and this is what keeps python and C# agreeing:
    ///     the committed file must be exactly what the type exports.
    ///     <para>
    ///         It used to be REFRESHED here on every run and then only checked to
    ///         exist: a catalogue change rewrote a tracked file from inside the
    ///         gate (a dirty checkout, and in a worktree a change nobody reviews),
    ///         and a stale sidecar could never fail. Now a default run only
    ///         compares; <see cref="RegenVariable" /> is the explicit regeneration.
    ///         Line endings are not content: the file is checked in with CRLF and
    ///         the serializer writes the platform's.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheCommittedSidecar_IsExactlyWhatTheTypeExports()
    {
        var json = RigLabelVocabulary.ToJson();
        var target = Path.Combine(TestPaths.RepoRoot(), SidecarRelativePath);

        if (Environment.GetEnvironmentVariable(RegenVariable) == "1")
            File.WriteAllText(target, json.ReplaceLineEndings("\r\n"));

        Assert.True(File.Exists(target), $"missing {SidecarRelativePath}");
        var committed = File.ReadAllText(target);
        Assert.True(committed.ReplaceLineEndings("\n") == json.ReplaceLineEndings("\n"),
            $"{SidecarRelativePath} is stale: the label vocabulary changed ({StaleDetail(committed, json)}). " +
            $"Regenerate it with {RegenVariable}=1 and review the diff: the trainer reads this file.");
    }

    /// <summary>Where the committed file and the export first differ, for the failure message.</summary>
    private static string StaleDetail(string committed, string exported)
    {
        var old = committed.ReplaceLineEndings("\n").Split('\n');
        var now = exported.ReplaceLineEndings("\n").Split('\n');
        for (var i = 0; i < Math.Min(old.Length, now.Length); i++)
        {
            if (old[i] != now[i]) return $"line {i + 1}: committed '{old[i].Trim()}', exported '{now[i].Trim()}'";
        }

        return $"{old.Length} lines committed, {now.Length} exported";
    }
}
