using DotCharter.Tone.Ai;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Services.Ai;

/// <summary>
///     The validator is the only thing standing between a model's confident guess
///     and the live audio engine. Every case here is a mistake a real model makes.
/// </summary>
public sealed class ToneDraftValidatorTests
{
    private const string GoodDraft = """
        {
          "name": "Glass Clean",
          "rationale": "A blackface-style clean with a hint of spring.",
          "confidence": 0.85,
          "modules": [
            { "type": "compressor", "params": { "ratio": 3 }, "bypassed": false },
            { "type": "amp", "params": { "gain": 0.2, "master": 0.6 }, "bypassed": false },
            { "type": "cab", "params": { "lowCutHz": 90 }, "bypassed": false },
            { "type": "reverb", "params": { "mix": 0.18 }, "bypassed": false }
          ]
        }
        """;

    [Fact]
    public void AGoodDraft_MapsToAUserPresetWithAFreshAiId_AndNoWarnings()
    {
        var result = ToneDraftValidator.Validate(GoodDraft);

        Assert.NotNull(result.Preset);
        Assert.Empty(result.Warnings);
        Assert.Equal("Glass Clean", result.Preset!.Name);
        Assert.StartsWith("ai-", result.Preset.Id, StringComparison.Ordinal);
        Assert.Equal(DspToneOrigin.User, result.Preset.Origin);
        Assert.Equal(
            new[] { "compressor", "amp", "cab", "reverb" },
            result.Preset.Rig.Modules.Select(m => m.ModuleTypeId));
    }

    [Fact]
    public void EveryCallMintsANewId_SoTwoTonesNeverCollide()
    {
        var a = ToneDraftValidator.Validate(GoodDraft).Preset!;
        var b = ToneDraftValidator.Validate(GoodDraft).Preset!;
        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void AnUnknownModule_IsDroppedWithAWarningNamingIt()
    {
        var json = """
            {
              "name": "X", "rationale": "", "confidence": 0.5,
              "modules": [
                { "type": "tubescreamer", "params": {}, "bypassed": false },
                { "type": "amp", "params": {}, "bypassed": false }
              ]
            }
            """;

        var result = ToneDraftValidator.Validate(json);

        Assert.NotNull(result.Preset);
        Assert.Equal(new[] { "amp" }, result.Preset!.Rig.Modules.Select(m => m.ModuleTypeId));
        Assert.Contains(result.Warnings, w =>
            w.Contains("tubescreamer", StringComparison.Ordinal) &&
            w.Contains("not a module DotCharter has", StringComparison.Ordinal));
    }

    [Fact]
    public void ASplitterOrMerger_IsDroppedWithItsOwnReason()
    {
        var json = """
            {
              "name": "X", "rationale": "", "confidence": 0.5,
              "modules": [
                { "type": "splitter", "params": {}, "bypassed": false },
                { "type": "amp", "params": {}, "bypassed": false }
              ]
            }
            """;

        var result = ToneDraftValidator.Validate(json);

        Assert.Single(result.Preset!.Rig.Modules);
        Assert.Contains(result.Warnings, w => w.Contains("parallel split", StringComparison.Ordinal));
    }

    [Fact]
    public void RocksmithsOwnAmpChain_IsDroppedWithAWarning_BecauseOnlyTheConverterWritesIt()
    {
        // `rsamp`'s k1..k10 mean a different knob on each of its 63 amps (#88).
        var json = """
            {
              "name": "X", "rationale": "", "confidence": 0.5,
              "modules": [
                { "type": "rsamp", "params": { "model": 17, "k1": 0.8 }, "bypassed": false },
                { "type": "amp", "params": {}, "bypassed": false }
              ]
            }
            """;

        var result = ToneDraftValidator.Validate(json);

        Assert.Equal("amp", Assert.Single(result.Preset!.Rig.Modules).ModuleTypeId);
        Assert.Contains(result.Warnings, w => w.Contains("Rocksmith's own amp chain", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnknownParam_IsIgnoredWithAWarning_AndTheModuleSurvives()
    {
        var json = """
            {
              "name": "X", "rationale": "", "confidence": 0.5,
              "modules": [ { "type": "amp", "params": { "gain": 0.5, "sparkle": 1 }, "bypassed": false } ]
            }
            """;

        var result = ToneDraftValidator.Validate(json);

        var module = Assert.Single(result.Preset!.Rig.Modules);
        Assert.Equal(0.5f, module.Params["gain"]);
        Assert.False(module.Params.ContainsKey("sparkle"));
        Assert.Contains(result.Warnings, w => w.Contains("no \"sparkle\" knob", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOutOfRangeValue_IsClampedWithAWarningNamingOldAndNew()
    {
        // gainDb is -24..24; a model that thinks in percent writes 100.
        var json = """
            {
              "name": "X", "rationale": "", "confidence": 0.5,
              "modules": [ { "type": "gain", "params": { "gainDb": 100 }, "bypassed": false } ]
            }
            """;

        var result = ToneDraftValidator.Validate(json);

        Assert.Equal(24f, Assert.Single(result.Preset!.Rig.Modules).Params["gainDb"]);
        Assert.Contains(result.Warnings, w =>
            w.Contains("gain.gainDb 100", StringComparison.Ordinal) &&
            w.Contains("set to 24", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryClampedValue_LandsInsideTheCataloguesOwnRange()
    {
        var json = """
            {
              "name": "X", "rationale": "", "confidence": 0.5,
              "modules": [
                { "type": "compressor", "params": { "ratio": 9999, "thresholdDb": -9999 }, "bypassed": false }
              ]
            }
            """;

        var module = Assert.Single(ToneDraftValidator.Validate(json).Preset!.Rig.Modules);
        foreach (var (paramId, value) in module.Params)
        {
            Assert.True(DcRigTargetCatalog.TryGetParam("compressor", paramId, out var spec));
            Assert.InRange(value, spec.Min, spec.Max);
        }
    }

    [Fact]
    public void AChainLongerThanTwelve_IsTruncatedWithAWarning()
    {
        var modules = string.Join(",",
            Enumerable.Repeat("""{ "type": "gain", "params": {}, "bypassed": false }""", 20));
        var json = $$"""{ "name": "X", "rationale": "", "confidence": 0.5, "modules": [{{modules}}] }""";

        var result = ToneDraftValidator.Validate(json);

        Assert.Equal(ToneDraftValidator.MaxModules, result.Preset!.Rig.Modules.Count);
        Assert.Contains(result.Warnings, w => w.Contains("more than 12 modules", StringComparison.Ordinal));
    }

    [Fact]
    public void ABypassedFlag_IsHonoured()
    {
        var json = """
            {
              "name": "X", "rationale": "", "confidence": 0.5,
              "modules": [
                { "type": "amp", "params": {}, "bypassed": false },
                { "type": "boost", "params": {}, "bypassed": true }
              ]
            }
            """;

        var modules = ToneDraftValidator.Validate(json).Preset!.Rig.Modules;
        Assert.False(modules[0].Bypassed);
        Assert.True(modules[1].Bypassed);
    }

    [Fact]
    public void AnEmptyChain_HasNoPreset()
    {
        var result = ToneDraftValidator.Validate(
            """{ "name": "X", "rationale": "", "confidence": 0.5, "modules": [] }""");

        Assert.Null(result.Preset);
        Assert.NotNull(result.Draft);
        Assert.Contains(result.Warnings, w => w.Contains("None of the modules", StringComparison.Ordinal));
    }

    [Fact]
    public void AChainOfNothingButUnknownModules_HasNoPreset()
    {
        var result = ToneDraftValidator.Validate("""
            {
              "name": "X", "rationale": "", "confidence": 0.5,
              "modules": [ { "type": "ts9", "params": {}, "bypassed": false } ]
            }
            """);

        Assert.Null(result.Preset);
        Assert.Contains(result.Warnings, w => w.Contains("ts9", StringComparison.Ordinal));
    }

    [Fact]
    public void MalformedJson_IsAWarningAndANullPreset_NeverAThrow()
    {
        var result = ToneDraftValidator.Validate("{ not json at all");
        Assert.Null(result.Preset);
        Assert.Null(result.Draft);
        Assert.NotEmpty(result.Warnings);
    }

    [Theory]
    [InlineData("clean")]
    [InlineData("bass")]
    public void ANonFiniteValue_FallsBackToTheDefault(string _)
    {
        // A model writing "NaN" is rare; a model writing 1e400 (which parses to
        // infinity) is not. Both must land on a number the engine accepts.
        var json = """
            {
              "name": "X", "rationale": "", "confidence": 0.5,
              "modules": [ { "type": "gain", "params": { "gainDb": 1e400 }, "bypassed": false } ]
            }
            """;

        var module = Assert.Single(ToneDraftValidator.Validate(json).Preset!.Rig.Modules);
        Assert.True(float.IsFinite(module.Params["gainDb"]));
    }

    [Theory]
    [InlineData("Modern Djent Rhythm", "brutal chugging metal", "amp", DspToneCategory.HighGain)]
    [InlineData("Motown Fingers", "round vintage bass", "bassamp", DspToneCategory.Bass)]
    [InlineData("Singing Lead", "soaring solo sustain", "amp", DspToneCategory.Lead)]
    [InlineData("Cathedral", "huge ambient wash pad", "reverb", DspToneCategory.Ambient)]
    [InlineData("Something", "no keywords here", "amp", DspToneCategory.Clean)]
    public void TheCategory_IsPickedFromTheDraftsWordsAndItsChain(
        string name, string rationale, string moduleType, DspToneCategory expected)
    {
        var draft = new ToneDraft(name, rationale, 0.8,
            [new ToneDraftModule(moduleType, new Dictionary<string, float>())]);

        Assert.Equal(expected, ToneDraftValidator.CategoryFor(draft));
    }

    [Fact]
    public void ABassChain_IsABassToneWhateverTheProseSays()
    {
        // Structure beats keywords: a bassamp rig called "Clean Sparkle" is bass.
        var draft = new ToneDraft("Clean Sparkle", "bright clean chime", 0.8,
            [new ToneDraftModule("bassamp", new Dictionary<string, float>())]);

        Assert.Equal(DspToneCategory.Bass, ToneDraftValidator.CategoryFor(draft));
    }

    [Theory]
    [InlineData("```json\n{\"name\":\"A\",\"rationale\":\"\",\"confidence\":1,\"modules\":[]}\n```")]
    [InlineData("Sure! Here is your tone:\n\n{\"name\":\"A\",\"rationale\":\"\",\"confidence\":1,\"modules\":[]}\n\nHope that helps!")]
    [InlineData("```\n{\"name\":\"A\",\"rationale\":\"\",\"confidence\":1,\"modules\":[]}\n```")]
    public void FencedOrChattyReplies_StillYieldTheObject(string reply)
    {
        var json = ToneDraftJson.ExtractJsonObject(reply);
        Assert.NotNull(json);
        Assert.StartsWith("{", json!.Trim(), StringComparison.Ordinal);
        Assert.Contains("\"name\":\"A\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ASentenceWithABraceAfterTheObject_DoesNotSwallowTheTail()
    {
        // First-{ to last-} would produce a span that parses as nothing.
        const string reply = """
            {"name":"A","rationale":"","confidence":1,"modules":[]}
            You can tweak {gain} to taste.
            """;

        var json = ToneDraftJson.ExtractJsonObject(reply);
        Assert.Equal("""{"name":"A","rationale":"","confidence":1,"modules":[]}""", json);
    }

    [Fact]
    public void ABraceInsideAString_DoesNotEndTheObject()
    {
        const string reply = """{"name":"A }","rationale":"","confidence":1,"modules":[]}""";
        Assert.Equal(reply, ToneDraftJson.ExtractJsonObject(reply));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("I cannot help with that.")]
    public void AReplyWithNoObject_ExtractsToNull(string reply)
        => Assert.Null(ToneDraftJson.ExtractJsonObject(reply));
}
