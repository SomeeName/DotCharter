using System.Text.Json;
using DotCharter.Tone.Ai;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Services.Ai;

/// <summary>
///     The grounding half of the feature. A model can only be as good as the
///     catalogue it is handed, and the two ways this rots silently are a module the
///     table forgot (unreachable forever) and a range the prompt and the validator
///     disagree about (the model is told 0..1, the validator clamps to 0..0.9, and
///     every tone comes back "corrected" for no reason).
/// </summary>
public sealed class ToneDesignPromptTests
{
    [Fact]
    public void GearNotes_CoverEveryCatalogueModule_AndNameNoneTheCatalogueLacks()
    {
        var catalogue = DcRigTargetCatalog.ModuleTypeIds.ToHashSet(StringComparer.Ordinal);
        var described = AiGearNotes.All.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);

        var missing = catalogue.Except(described).Except(AiGearNotes.SeriesExcluded).Except(AiGearNotes.NotDesignable)
            .OrderBy(x => x).ToList();
        var invented = described.Except(catalogue).OrderBy(x => x).ToList();

        Assert.True(missing.Count == 0, $"Catalogue modules with no gear note: {string.Join(", ", missing)}");
        Assert.True(invented.Count == 0, $"Gear notes for modules that do not exist: {string.Join(", ", invented)}");
    }

    [Fact]
    public void GearNotes_HaveNoDuplicateIds_AndEveryNoteIsShortProse()
    {
        Assert.Equal(AiGearNotes.All.Count, AiGearNotes.All.Select(n => n.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (var note in AiGearNotes.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(note.Name), note.Id);
            Assert.False(string.IsNullOrWhiteSpace(note.Category), note.Id);
            // Ten words each, give or take: the block is ~100 lines and every word
            // of it is in every request's cacheable prefix.
            var words = note.Note.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            Assert.True(words is >= 3 and <= 16, $"{note.Id}: {words} words — \"{note.Note}\"");
        }
    }

    [Fact]
    public void TheCatalogueBlock_NamesEveryDesignableModuleId()
    {
        var block = ToneDesignPromptBuilder.CatalogueBlock;

        foreach (var id in DcRigTargetCatalog.ModuleTypeIds)
        {
            if (AiGearNotes.SeriesExcluded.Contains(id) || AiGearNotes.NotDesignable.Contains(id)) continue;
            Assert.Contains($"\n{id} — ", block, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheCatalogueBlock_LeavesTheSeriesExcludedModulesOut()
    {
        // v1 designs series chains only; naming a splitter is inviting a rig the
        // engine would wire differently from what the model described.
        var block = ToneDesignPromptBuilder.CatalogueBlock;
        foreach (var id in AiGearNotes.SeriesExcluded)
            Assert.DoesNotContain($"\n{id} — ", block, StringComparison.Ordinal);
    }

    [Theory]
    // Three sampled modules from three different drawers, every param spelled out.
    [InlineData("rat")]
    [InlineData("bassamp")]
    [InlineData("hallreverb")]
    public void TheCatalogueBlock_ListsEveryParamOfASampledModule(string moduleTypeId)
    {
        var block = ToneDesignPromptBuilder.CatalogueBlock;
        var specs = DcRigTargetCatalog.AllModules.Single(m => m.ModuleTypeId == moduleTypeId).Params;
        Assert.NotEmpty(specs);

        foreach (var spec in specs)
            Assert.Contains($"{spec.Id} (", block, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCatalogueBlock_PrintsTheCatalogueRangesAndNotInventedOnes() =>
        // The one number this file exists to protect. "gainDb (-24..24, def 0 dB)".
        Assert.Contains("gainDb (-24..24, def 0 dB)", ToneDesignPromptBuilder.CatalogueBlock, StringComparison.Ordinal);

    [Theory]
    [InlineData("thresholdDb", "dB")]
    [InlineData("lowCutHz", "Hz")]
    [InlineData("dampingKHz", "kHz")]
    [InlineData("attackMs", "ms")]
    [InlineData("decayS", "s")]
    [InlineData("lengthPct", "%")]
    [InlineData("detuneCents", "cents")]
    [InlineData("heelSemis", "semitones")]
    [InlineData("distanceCm", "cm")]
    [InlineData("phaseDeg", "degrees")]
    [InlineData("mix", null)]
    [InlineData("voicing", null)]
    public void UnitOf_FollowsTheEnginesOwnParamIdConvention(string paramId, string? expected)
        => Assert.Equal(expected, AiGearNotes.UnitOf(paramId));

    [Fact]
    public void TheSystemPrompt_CarriesTheRules_TheCatalogue_AndFourExamples()
    {
        var prompt = ToneDesignPromptBuilder.SystemPrompt;

        Assert.Contains("SERIES chain", prompt, StringComparison.Ordinal);
        Assert.Contains("Put a cab AFTER an amp", prompt, StringComparison.Ordinal);
        Assert.Contains("Never two amps", prompt, StringComparison.Ordinal);
        Assert.Contains("bassamp", prompt, StringComparison.Ordinal);
        Assert.Contains("# Module catalogue", prompt, StringComparison.Ordinal);
        Assert.Contains("# Examples", prompt, StringComparison.Ordinal);

        var byId = DspTonePresetLibrary.All.ToDictionary(p => p.Id, StringComparer.Ordinal);
        foreach (var id in ToneDesignPromptBuilder.FewShotPresetIds)
        {
            Assert.True(byId.ContainsKey(id), $"Few-shot preset \"{id}\" is no longer in the factory library.");
            Assert.Contains(byId[id].Description, prompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheSystemPrompt_IsStableAcrossCalls() =>
        // It is the cacheable prefix. Two calls producing two strings is a cache
        // miss on every request and the catalogue charged at full price each time.
        Assert.Same(ToneDesignPromptBuilder.SystemPrompt, ToneDesignPromptBuilder.SystemPrompt);

    [Fact]
    public void TheSystemPrompt_FitsTheBudget()
    {
        var tokens = ToneDesignPromptBuilder.EstimateTokens(ToneDesignPromptBuilder.SystemPrompt);
        Assert.True(tokens < 12_000,
            $"The system prompt is ~{tokens} estimated tokens; the budget is 12,000. " +
            "Shorten a gear note or drop an example rather than raising this.");
    }

    [Fact]
    public void EveryFewShotExample_RoundTripsThroughTheValidatorUnchanged()
    {
        // An example the validator would correct is an example that teaches the
        // model to produce something this build then has to fix.
        var byId = DspTonePresetLibrary.All.ToDictionary(p => p.Id, StringComparer.Ordinal);

        foreach (var id in ToneDesignPromptBuilder.FewShotPresetIds)
        {
            var draft = ToneDesignPromptBuilder.ToDraft(byId[id]);
            var json = ToneDraftJson.Serialize(draft);

            var validation = ToneDraftValidator.Validate(json);

            Assert.NotNull(validation.Preset);
            Assert.Empty(validation.Warnings);
            Assert.Equal(
                draft.Modules.Select(m => m.Type),
                validation.Preset!.Rig.Modules.Select(m => m.ModuleTypeId));

            foreach (var (drafted, mapped) in draft.Modules.Zip(validation.Preset.Rig.Modules))
            {
                foreach (var (paramId, value) in drafted.Params)
                    Assert.Equal(value, mapped.Params[paramId]);
            }
        }
    }

    [Fact]
    public void AFreshRequest_IsOneUserTurnCarryingTheDescriptionAndContext()
    {
        var prompt = ToneDesignPromptBuilder.Build(new ToneDesignRequest(
            "warm blues lead", [], null, "Texas Flood", "SRV"));

        var turn = Assert.Single(prompt.Messages);
        Assert.Equal(ToneDesignRole.User, turn.Role);
        Assert.Contains("warm blues lead", turn.Text, StringComparison.Ordinal);
        Assert.Contains("Texas Flood", turn.Text, StringComparison.Ordinal);
        Assert.Contains("SRV", turn.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("currently on the rig", turn.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AFollowUp_ReplaysTheHistory_AndQuotesTheCurrentToneAsADraft()
    {
        var current = DspTonePresetLibrary.All.Single(p => p.Id == "crunch-plexi");
        var prompt = ToneDesignPromptBuilder.Build(new ToneDesignRequest(
            "less gain",
            [
                new ToneDesignTurn(ToneDesignRole.User, "plexi crunch please"),
                new ToneDesignTurn(ToneDesignRole.Assistant, "Here is a plexi rig.")
            ],
            current));

        Assert.Equal(3, prompt.Messages.Count);
        Assert.Equal("plexi crunch please", prompt.Messages[0].Text);
        Assert.Equal(ToneDesignRole.Assistant, prompt.Messages[1].Role);

        var last = prompt.Messages[2];
        Assert.Contains("currently on the rig", last.Text, StringComparison.Ordinal);
        Assert.Contains("return the FULL chain", last.Text, StringComparison.Ordinal);
        Assert.Contains("less gain", last.Text, StringComparison.Ordinal);
        foreach (var module in current.Rig.Modules)
            Assert.Contains($"\"{module.ModuleTypeId}\"", last.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ADraftDropsStringParams_SoAnExampleNeverTeachesAFieldTheSchemaForbids()
    {
        // A capture-backed preset carries a modelPath the draft cannot express.
        var captureBacked = DspTonePresetLibrary.All
            .FirstOrDefault(p => p.Rig.Modules.Any(m => m.StringParams is { Count: > 0 }));
        Assert.NotNull(captureBacked);

        var json = ToneDraftJson.Serialize(ToneDesignPromptBuilder.ToDraft(captureBacked!));
        Assert.DoesNotContain("modelPath", json, StringComparison.Ordinal);
        Assert.DoesNotContain("stringParams", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheSchema_MatchesTheToneDraftRecordsProperties()
    {
        // The schema is hand-written; this is what keeps it honest when the
        // contract's records gain a property.
        using var doc = JsonDocument.Parse(ToneDraftJson.Schema);
        var root = doc.RootElement.GetProperty("properties");

        AssertPropertiesMatch<ToneDraft>(root);

        var moduleSchema = root.GetProperty("modules").GetProperty("items").GetProperty("properties");
        AssertPropertiesMatch<ToneDraftModule>(moduleSchema);
    }

    private static void AssertPropertiesMatch<T>(JsonElement schemaProperties)
    {
        var expected = typeof(T).GetProperties()
            .Where(p => p.CanRead && p.Name != "EqualityContract")
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var actual = schemaProperties.EnumerateObject()
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TheSchema_IsValidJson_AndTheBuildCarriesItVerbatim()
    {
        var prompt = ToneDesignPromptBuilder.Build(new ToneDesignRequest("anything", [], null));
        Assert.Equal(ToneDraftJson.Schema, prompt.JsonSchema);
        Assert.Equal(ToneDraftJson.SchemaName, prompt.SchemaName);
        using var doc = JsonDocument.Parse(prompt.JsonSchema);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }
}
