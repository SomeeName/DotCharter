using DotCharter.Tone.Ai;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Online.Tone3000;

namespace DotCharter.Tests.Services.Ai;

/// <summary>
///     W8: the designer may pick a NEURAL CAPTURE, by name, from a closed list.
///
///     <para>
///         The three ways this feature rots silently, and the case that catches each:
///         a capture block offered to a player who has none (the model then names a
///         capture nobody has), a block whose ORDER moves between runs (it sits in
///         the cacheable system prefix, so a reordered block is a cache miss on every
///         process restart), and a validated <c>namamp</c> whose capture never became
///         a real file path (the module loads nothing and plays silence, which is
///         exactly the failure shape <c>NamCaptureIndex.ExtensionFor</c> exists for).
///     </para>
/// </summary>
public sealed class ToneDesignCaptureTests
{
    // ---- the catalogue ---------------------------------------------------------

    private static NamCaptureEntry Entry(
        string fileName, string title, long modelId, string author = "Someone",
        string gearType = "amp", string format = "nam", string sizeTier = "standard")
        => new(
            new NamCaptureManifest(
                modelId, modelId, title, author, gearType, format, sizeTier,
                "MIT", "https://example.invalid/license", "https://example.invalid/attribution",
                fileName, "sha", 1024, DateTimeOffset.UnixEpoch),
            Path.Combine(@"C:\captures", fileName));

    /// <summary>A bundled capture, i.e. one carrying the negative synthetic id
    /// <see cref="BundledNamCapture.SyntheticId" /> mints.</summary>
    private static NamCaptureEntry Bundled(string fileName, string title)
        => Entry(fileName, title, BundledNamCapture.SyntheticId(fileName));

    private static NamCaptureCatalog Catalog(params NamCaptureEntry[] entries)
        => NamCaptureCatalog.FromEntries(entries);

    [Fact]
    public void TheCatalogue_PutsBundledCapturesFirst_AndOrdersEachBlockById()
    {
        var catalog = Catalog(
            Entry("900-my-own-capture-standard.nam", "My Own Capture", 900),
            Bundled("voxac15-jangle.nam", "AC15 Jangle"),
            Entry("100-another-download-feather.nam", "Another Download", 100),
            Bundled("5150-blockletter.nam", "5150 Block Letter"));

        Assert.Equal(
            ["5150-blockletter", "voxac15-jangle", "100-another-download-feather", "900-my-own-capture-standard"],
            catalog.Options.Select(o => o.Id));
        Assert.Equal([true, true, false, false], catalog.Options.Select(o => o.Bundled));
    }

    /// <summary>The order must not depend on the order the library happened to
    /// enumerate the directory in — that is the difference between a cacheable prefix
    /// and a new one on every process start.</summary>
    [Fact]
    public void TheCatalogue_IsOrderedIndependentlyOfTheLibrarysOwnOrder()
    {
        var entries = new[]
        {
            Bundled("voxac15-jangle.nam", "AC15 Jangle"),
            Entry("900-my-own-capture-standard.nam", "My Own Capture", 900),
            Bundled("5150-blockletter.nam", "5150 Block Letter")
        };

        var forwards = NamCaptureCatalog.FromEntries(entries);
        var backwards = NamCaptureCatalog.FromEntries(entries.Reverse());

        Assert.Equal(forwards.Options.Select(o => o.Id), backwards.Options.Select(o => o.Id));
        Assert.Equal(forwards.Fingerprint, backwards.Fingerprint);
        Assert.Equal(forwards.Block, backwards.Block);
    }

    [Fact]
    public void TheFingerprint_MovesWhenTheLibraryDoes_AndOnlyThen()
    {
        var one = Catalog(Bundled("5150-blockletter.nam", "5150 Block Letter"));
        var same = Catalog(Bundled("5150-blockletter.nam", "5150 Block Letter"));
        var more = Catalog(
            Bundled("5150-blockletter.nam", "5150 Block Letter"),
            Bundled("voxac15-jangle.nam", "AC15 Jangle"));

        Assert.Equal(one.Fingerprint, same.Fingerprint);
        Assert.NotEqual(one.Fingerprint, more.Fingerprint);
        Assert.Equal("none", NamCaptureCatalog.Empty.Fingerprint);
    }

    /// <summary>An IR wav in the same library is a cabinet impulse, not something
    /// <c>namamp</c> can load. Offering one produces a rig that fails at apply time
    /// with nothing on screen saying why.</summary>
    [Fact]
    public void TheCatalogue_OffersNeuralModelsOnly()
    {
        var catalog = Catalog(
            Entry("501-a-cab-ir.wav", "A Cab IR", 501, format: "ir"),
            Entry("502-an-aida-model-standard.aidax", "An AIDA Model", 502, format: "aidax"),
            Bundled("5150-blockletter.nam", "5150 Block Letter"));

        Assert.Equal(["5150-blockletter", "502-an-aida-model-standard"], catalog.Options.Select(o => o.Id));
    }

    [Fact]
    public void TheBlock_NamesEveryCapturesIdAndTitle_AndSaysWhichAreBundled()
    {
        var block = Catalog(
            Bundled("5150-blockletter.nam", "5150 Block Letter"),
            Entry("900-my-own-capture-standard.nam", "My Own Capture", 900)).Block;

        Assert.Contains("Bundled with DotCharter", block, StringComparison.Ordinal);
        Assert.Contains("Downloaded by this player", block, StringComparison.Ordinal);
        Assert.Contains("5150-blockletter — 5150 Block Letter", block, StringComparison.Ordinal);
        Assert.Contains("900-my-own-capture-standard — My Own Capture", block, StringComparison.Ordinal);

        // The author and the size tier are deliberately absent — see the budget case
        // below and the comment in NamCaptureCatalog.AppendGroup. A non-amp gear type
        // IS printed, because it changes what the capture is.
        Assert.DoesNotContain("Someone", block, StringComparison.Ordinal);
        Assert.DoesNotContain("My Own Capture — standard", block, StringComparison.Ordinal);
        Assert.Contains(
            "600-a-captured-pedal — A Captured Pedal (pedal)",
            Catalog(Entry("600-a-captured-pedal.nam", "A Captured Pedal", 600, gearType: "pedal")).Block,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyCatalogue_RendersNothingAtAll()
        => Assert.Equal("", NamCaptureCatalog.Empty.Block);

    [Fact]
    public void ACaptureIdIsMatchedCaseInsensitively_AndAnUnknownOneIsNull()
    {
        var catalog = Catalog(Bundled("5150-blockletter.nam", "5150 Block Letter"));

        Assert.Equal("5150-blockletter", catalog.Find("5150-BlockLetter")?.Id);
        Assert.Equal("5150-blockletter.nam", catalog.Find(" 5150-blockletter ")?.FileName);
        Assert.Null(catalog.Find("a-capture-nobody-has"));
        Assert.Null(catalog.Find(null));
    }

    // ---- the prompt ------------------------------------------------------------

    [Fact]
    public void TheSystemPrompt_CarriesTheCaptureBlock_OnlyWhenThereAreCaptures()
    {
        var withNone = ToneDesignPromptBuilder.SystemPromptFor(NamCaptureCatalog.Empty);
        var withSome = ToneDesignPromptBuilder.SystemPromptFor(
            Catalog(Bundled("5150-blockletter.nam", "5150 Block Letter")));

        Assert.DoesNotContain("# Neural amp captures", withNone, StringComparison.Ordinal);
        Assert.Contains("# Neural amp captures", withSome, StringComparison.Ordinal);
        Assert.Contains("5150-blockletter — 5150 Block Letter", withSome, StringComparison.Ordinal);

        // …and a null catalogue is the same prompt as an empty one, so a build with
        // no capture library behaves like a player who has deleted every capture.
        Assert.Same(withNone, ToneDesignPromptBuilder.SystemPrompt);
    }

    /// <summary>The block goes AFTER the module catalogue and BEFORE the examples:
    /// everything above it is build-constant and identical on every machine, so that
    /// part stays a shared cacheable prefix.</summary>
    [Fact]
    public void TheCaptureBlock_SitsBetweenTheCatalogueAndTheExamples()
    {
        var prompt = ToneDesignPromptBuilder.SystemPromptFor(
            Catalog(Bundled("5150-blockletter.nam", "5150 Block Letter")));

        var catalogue = prompt.IndexOf("# Module catalogue", StringComparison.Ordinal);
        var captures = prompt.IndexOf("# Neural amp captures", StringComparison.Ordinal);
        var examples = prompt.IndexOf("# Examples", StringComparison.Ordinal);

        Assert.True(catalogue >= 0 && captures > catalogue && examples > captures,
            $"catalogue={catalogue}, captures={captures}, examples={examples}");
    }

    /// <summary>It IS the cacheable prefix: the same library must produce the same
    /// string, reference-identical, however many designs are asked for.</summary>
    [Fact]
    public void TheSystemPrompt_ForOneLibrary_IsBuiltOnce()
    {
        var a = ToneDesignPromptBuilder.SystemPromptFor(
            Catalog(Bundled("jcm2000-crunch.nam", "JCM2000 Crunch")));
        var b = ToneDesignPromptBuilder.SystemPromptFor(
            Catalog(Bundled("jcm2000-crunch.nam", "JCM2000 Crunch")));

        Assert.Same(a, b);
        Assert.NotSame(a, ToneDesignPromptBuilder.AnchoredSystemPromptFor(
            Catalog(Bundled("jcm2000-crunch.nam", "JCM2000 Crunch"))));
    }

    /// <summary>Rule 12 and the namamp gear note both flip on the presence of
    /// captures. With none, the module is off the table and the prompt says only
    /// that; with some, it is a real choice that must name one.</summary>
    [Fact]
    public void TheRuleAndTheGearNote_FollowTheCaptureLibrary()
    {
        var withNone = ToneDesignPromptBuilder.SystemPromptFor(NamCaptureCatalog.Empty);
        var withSome = ToneDesignPromptBuilder.SystemPromptFor(
            Catalog(Bundled("5150-blockletter.nam", "5150 Block Letter")));

        Assert.Contains("Do not use namamp, convreverb", withNone, StringComparison.Ordinal);
        Assert.Contains(AiGearNotes.NamAmpNoteWithoutCaptures, withNone, StringComparison.Ordinal);

        Assert.DoesNotContain("Do not use namamp, convreverb", withSome, StringComparison.Ordinal);
        Assert.Contains("Do not use convreverb, miku", withSome, StringComparison.Ordinal);
        Assert.DoesNotContain(AiGearNotes.NamAmpNoteWithoutCaptures, withSome, StringComparison.Ordinal);
        Assert.Contains(
            AiGearNotes.All.Single(n => n.Id == AiGearNotes.NamAmpId).Note,
            withSome, StringComparison.Ordinal);
    }

    /// <summary>The fifth few-shot exists so the model has seen the shape. It must
    /// name a capture the list beside it actually carries — an example naming one it
    /// does not teaches that inventing an id is fine.</summary>
    [Fact]
    public void TheCaptureFewShot_IsAddedWithTheBlock_AndNamesAListedCapture()
    {
        var catalog = Catalog(
            Bundled("5150-blockletter-boosted.nam", "5150 Block Letter (Boosted)"),
            Bundled("voxac15-jangle.nam", "AC15 Jangle"));

        var withSome = ToneDesignPromptBuilder.SystemPromptFor(catalog);
        var preset = DspTonePresetLibrary.All
            .Single(p => p.Id == ToneDesignPromptBuilder.CaptureFewShotPresetId);

        Assert.Contains(preset.Description, withSome, StringComparison.Ordinal);
        Assert.Contains("\"capture\": \"5150-blockletter-boosted\"", withSome, StringComparison.Ordinal);
        Assert.Contains("Five tones from the factory library", withSome, StringComparison.Ordinal);

        // …and it is NOT there when the block is not.
        Assert.DoesNotContain(preset.Description,
            ToneDesignPromptBuilder.SystemPromptFor(NamCaptureCatalog.Empty), StringComparison.Ordinal);
    }

    /// <summary>The preset's own capture is missing from this player's library, so
    /// the example falls back to a listed one rather than naming a file nobody
    /// has.</summary>
    [Fact]
    public void TheCaptureFewShot_FallsBackToAListedCapture_WhenThePresetsOwnIsGone()
    {
        var prompt = ToneDesignPromptBuilder.SystemPromptFor(
            Catalog(Entry("900-my-own-capture-standard.nam", "My Own Capture", 900)));

        Assert.Contains("\"capture\": \"900-my-own-capture-standard\"", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("5150-blockletter-boosted", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryFewShot_StillRoundTripsThroughTheValidatorUnchanged()
    {
        // The same guard ToneDesignPromptTests keeps for the four generic examples,
        // extended to the capture one: an example the validator would correct teaches
        // the model to produce something this build then has to fix.
        var catalog = Catalog(Bundled("5150-blockletter-boosted.nam", "5150 Block Letter (Boosted)"));
        var preset = DspTonePresetLibrary.All
            .Single(p => p.Id == ToneDesignPromptBuilder.CaptureFewShotPresetId);

        var draft = ToneDesignPromptBuilder.ToDraft(preset, catalog);
        var validation = ToneDraftValidator.Validate(ToneDraftJson.Serialize(draft), catalog);

        Assert.NotNull(validation.Preset);
        Assert.Empty(validation.Warnings);
        Assert.Equal(
            preset.Rig.Modules.Select(m => m.ModuleTypeId),
            validation.Preset!.Rig.Modules.Select(m => m.ModuleTypeId));
    }

    /// <summary>
    ///     The capture block and the fifth few-shot are inside the SAME 12,000-token
    ///     budget the rest of the system prompt lives in, measured against a
    ///     catalogue the size of the one the app ships: <b>63 bundled
    ///     <c>.nam</c> files under <c>DotCharter.UI/NamCaptures/</c></b>, counted
    ///     2026-09-16. A player's own downloads sit on top of that, so the headroom
    ///     is real headroom, not slack.
    ///     <para>Re-measure by tripping this rather than carrying the number
    ///     forward — the capture block grows by one line per capture a player
    ///     downloads.</para>
    /// </summary>
    [Fact]
    public void TheCapturePrompt_FitsTheSameBudgetAsTheRest()
    {
        var bundledCount = 63;
        var many = NamCaptureCatalog.FromEntries(
            Enumerable.Range(0, bundledCount).Select(i =>
                Bundled($"bundled-capture-number-{i:00}.nam", $"Bundled Capture Number {i:00}")));

        Assert.Equal(bundledCount, many.Options.Count);

        var plain = ToneDesignPromptBuilder.EstimateTokens(ToneDesignPromptBuilder.SystemPrompt);
        var withCaptures = ToneDesignPromptBuilder.EstimateTokens(
            ToneDesignPromptBuilder.SystemPromptFor(many));

        Assert.True(withCaptures > plain, "the capture block and its few-shot cost something");

        // …and what ONE more capture costs, because the bundled set is only the FLOOR
        // and the player's own downloads sit on top of it. That rate is the
        // arithmetic a reader needs — roughly (12,000 − withCaptures) / perCapture
        // further downloads fit — and no single total gives it.
        var twice = NamCaptureCatalog.FromEntries(
            Enumerable.Range(0, bundledCount * 2).Select(i =>
                Bundled($"bundled-capture-number-{i:000}.nam", $"Bundled Capture Number {i:000}")));
        var perCapture =
            (ToneDesignPromptBuilder.EstimateTokens(ToneDesignPromptBuilder.SystemPromptFor(twice)) - withCaptures)
            / (double)bundledCount;
        Assert.True(perCapture is > 0 and < 20,
            $"a capture line costs ~{perCapture:F1} estimated tokens; past 20 the block has " +
            "grown a field that does not describe a sound.");

        Assert.True(withCaptures < 12_000,
            $"the capture prompt is ~{withCaptures} estimated tokens against ~{plain} without; " +
            "the budget is 12,000. Shorten a capture line or drop an example rather than raising this.");
    }

    // ---- the validator ---------------------------------------------------------

    private static string Reply(string type, string capture)
        => $$"""
            {"name":"T","rationale":"r","confidence":0.8,"modules":[
              {"type":"{{type}}","params":[],"bypassed":false,"capture":"{{capture}}"},
              {"type":"cab","params":[],"bypassed":false,"capture":""}]}
            """;

    [Fact]
    public void AKnownCapture_IsAccepted_AndBecomesTheModulesFileName()
    {
        var catalog = Catalog(Bundled("5150-blockletter.nam", "5150 Block Letter"));

        var validation = ToneDraftValidator.Validate(Reply("namamp", "5150-blockletter"), catalog);

        Assert.NotNull(validation.Preset);
        Assert.Empty(validation.Warnings);
        var namamp = validation.Preset!.Rig.Modules[0];
        Assert.Equal("namamp", namamp.ModuleTypeId);
        Assert.Equal("5150-blockletter.nam", namamp.StringParams!["modelPath"]);
    }

    [Fact]
    public void AnUnknownCapture_DropsTheModule_AndTheWarningNamesTheId()
    {
        var catalog = Catalog(Bundled("5150-blockletter.nam", "5150 Block Letter"));

        var validation = ToneDraftValidator.Validate(Reply("namamp", "dumble-overdrive-special"), catalog);

        Assert.NotNull(validation.Preset);
        Assert.Equal(["cab"], validation.Preset!.Rig.Modules.Select(m => m.ModuleTypeId));
        var warning = Assert.Single(validation.Warnings);
        Assert.Contains("dumble-overdrive-special", warning, StringComparison.Ordinal);
        Assert.Contains("namamp", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ACaptureLessNamAmp_IsDropped_AndTheWarningNamesTheModule()
    {
        var catalog = Catalog(Bundled("5150-blockletter.nam", "5150 Block Letter"));

        var validation = ToneDraftValidator.Validate(Reply("namamp", ""), catalog);

        Assert.Equal(["cab"], validation.Preset!.Rig.Modules.Select(m => m.ModuleTypeId));

        // The message must be the NAMED-NONE one, not the unknown-id one: "" falls
        // through Find() to null either way, so an assertion that only checked the
        // module was dropped would pass with the empty-capture guard deleted.
        var warning = Assert.Single(validation.Warnings);
        Assert.Contains("namamp", warning, StringComparison.Ordinal);
        Assert.Contains("named none", warning, StringComparison.Ordinal);
    }

    /// <summary>With no catalogue at all every namamp is dropped — which is exactly
    /// what the prompt told that player's model, so nothing is inconsistent.</summary>
    [Fact]
    public void WithNoCaptureLibrary_EveryNamAmpIsDropped()
    {
        var validation = ToneDraftValidator.Validate(Reply("namamp", "5150-blockletter"));

        Assert.Equal(["cab"], validation.Preset!.Rig.Modules.Select(m => m.ModuleTypeId));
        Assert.Contains("5150-blockletter", Assert.Single(validation.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void ACaptureOnAModuleThatPlaysNone_IsIgnoredWithAWarning()
    {
        var catalog = Catalog(Bundled("5150-blockletter.nam", "5150 Block Letter"));

        var validation = ToneDraftValidator.Validate(Reply("amp", "5150-blockletter"), catalog);

        Assert.Equal(["amp", "cab"], validation.Preset!.Rig.Modules.Select(m => m.ModuleTypeId));
        Assert.Null(validation.Preset.Rig.Modules[0].StringParams);
        Assert.Contains("was ignored", Assert.Single(validation.Warnings), StringComparison.Ordinal);
    }

    // ---- the preparation seam --------------------------------------------------

    /// <summary>The validator answers a FILE NAME and the shared preparation step is
    /// what makes it absolute — the same two steps a factory capture preset goes
    /// through, so an AI tone and a saved tone resolve identically.</summary>
    [Theory]
    [InlineData("5150-blockletter", "5150-blockletter.nam")]
    [InlineData("900-my-own-capture-standard", "900-my-own-capture-standard.nam")]
    public void APreparedCapture_IsAnAbsolutePathUnderTheCaptureDirectory(string id, string fileName)
    {
        var directory = Path.Combine(Path.GetTempPath(), "dotcharter-capture-prep");
        var catalog = Catalog(
            Bundled("5150-blockletter.nam", "5150 Block Letter"),
            Entry("900-my-own-capture-standard.nam", "My Own Capture", 900));

        var validated = ToneDraftValidator.Validate(Reply("namamp", id), catalog).Preset;
        Assert.NotNull(validated);

        var prepared = DcRigPresetPreparation.Prepare(validated!, directory);

        Assert.Equal(
            Path.Combine(directory, fileName),
            prepared.Modules[0].StringParams!["modelPath"]);
    }

    // ---- end to end ------------------------------------------------------------

    /// <summary>The whole lane over a fake model: the prompt offers the capture, the
    /// model names it, and the tone that comes out carries a resolved path.</summary>
    [Fact]
    public async Task ADesign_OffersTheCaptures_AndComesBackWithAResolvedPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dotcharter-capture-e2e");
        var catalog = Catalog(Bundled("5150-blockletter-boosted.nam", "5150 Block Letter (Boosted)"));
        var model = new CapturingModel(Reply("namamp", "5150-blockletter-boosted"));

        var config = new AppConfig { Ai = new AiProviderSettings(AiProviderKind.Claude, "claude-opus-5", null) };
        var service = new ToneDesignService(
            () => config,
            new SingleModelFactory(model),
            logger: null,
            namCaptureDirectory: () => directory,
            anchors: null,
            captures: () => catalog,
            localization: FakeLocalization.English());

        var result = await service.DesignAsync(
            new ToneDesignRequest("the 5150 capture I downloaded", [], null),
            null, TestContext.Current.CancellationToken);

        Assert.Contains("# Neural amp captures", model.LastPrompt!.System, StringComparison.Ordinal);
        Assert.Empty(result.Warnings);
        var namamp = Assert.Single(result.Tone.Rig.Modules, m => m.ModuleTypeId == "namamp");
        Assert.Equal(
            Path.Combine(directory, "5150-blockletter-boosted.nam"),
            namamp.StringParams!["modelPath"]);
    }

    private sealed class SingleModelFactory(IToneDesignModel model) : IToneDesignModelFactory
    {
        public IToneDesignModel Create(AiProviderSettings settings) => model;
        public bool Supports(AiProviderKind kind) => kind is AiProviderKind.Claude;
    }

    private sealed class CapturingModel(string json) : IToneDesignModel
    {
        public ToneDesignPrompt? LastPrompt { get; private set; }
        public AiProviderKind Kind => AiProviderKind.Claude;

        public Task<ToneDesignModelReply> CompleteAsync(ToneDesignPrompt prompt, CancellationToken ct)
        {
            LastPrompt = prompt;
            return Task.FromResult(new ToneDesignModelReply(json, new AiUsage("claude-opus-5", 9000, 200)));
        }

        public Task<AiProbeResult> ProbeAsync(CancellationToken ct)
            => Task.FromResult(new AiProbeResult(true, "ok", "claude-opus-5"));
    }
}
