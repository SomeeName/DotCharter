using System.Text.RegularExpressions;
using DotCharter.Tone.Ai;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Online.Tone3000;

namespace DotCharter.Tests.Services.Ai;

/// <summary>
///     The factory (settings → a provider, or a NotConfigured that says which knob
///     is missing) and the service (prompt → model → validate → prepare).
/// </summary>
public sealed class ToneDesignServiceTests
{
    private const string ToneJson = """
        {
          "name": "Glass Clean",
          "rationale": "Blackface sparkle with a little spring.",
          "confidence": 0.82,
          "modules": [
            { "type": "amp", "params": { "gain": 0.2 }, "bypassed": false },
            { "type": "cab", "params": {}, "bypassed": false },
            { "type": "reverb", "params": { "mix": 0.18 }, "bypassed": false }
          ]
        }
        """;

    // ---- the factory -----------------------------------------------------------

    [Fact]
    public void NoProvider_IsNotConfigured()
    {
        var ex = Assert.Throws<ToneDesignException>(() =>
            new ToneDesignModelFactory(new InMemoryAiSecretStore()).Create(AiProviderSettings.Disabled));

        Assert.Equal(ToneDesignFault.NotConfigured, ex.Fault);
        Assert.Contains("No AI provider is selected", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ClaudeWithoutAKey_IsNotConfigured_AndSaysSo()
    {
        var ex = Assert.Throws<ToneDesignException>(() =>
            new ToneDesignModelFactory(new InMemoryAiSecretStore())
                .Create(new AiProviderSettings(AiProviderKind.Claude, "claude-opus-5", null)));

        Assert.Equal(ToneDesignFault.NotConfigured, ex.Fault);
        Assert.Contains("API key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyModelId_IsNotConfigured()
    {
        var secrets = new InMemoryAiSecretStore();
        secrets.Write(AiProviderKind.Claude, "sk-ant-test");

        var ex = Assert.Throws<ToneDesignException>(() =>
            new ToneDesignModelFactory(secrets).Create(new AiProviderSettings(AiProviderKind.Claude, "", null)));

        Assert.Equal(ToneDesignFault.NotConfigured, ex.Fault);
        Assert.Contains("model id", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenAiCompatibleWithoutABaseUrl_IsNotConfigured_AndNamesThePresets()
    {
        var ex = Assert.Throws<ToneDesignException>(() =>
            new ToneDesignModelFactory(new InMemoryAiSecretStore())
                .Create(new AiProviderSettings(AiProviderKind.OpenAiCompatible, "qwen3:8b", null)));

        Assert.Equal(ToneDesignFault.NotConfigured, ex.Fault);
        Assert.Contains(AiProviderPresets.LmStudioBaseUrl, ex.Message, StringComparison.Ordinal);
        Assert.Contains(AiProviderPresets.OllamaBaseUrl, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuiltIn_IsNotConfigured_WithAComingLaterMessage()
    {
        var ex = Assert.Throws<ToneDesignException>(() =>
            new ToneDesignModelFactory(new InMemoryAiSecretStore())
                .Create(new AiProviderSettings(AiProviderKind.BuiltIn, "bundled", null)));

        Assert.Equal(ToneDesignFault.NotConfigured, ex.Fault);
        Assert.Contains("not available yet", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AConfiguredProvider_IsBuilt()
    {
        var secrets = new InMemoryAiSecretStore();
        secrets.Write(AiProviderKind.Claude, "sk-ant-test");
        var factory = new ToneDesignModelFactory(secrets);

        Assert.Equal(AiProviderKind.Claude,
            factory.Create(new AiProviderSettings(AiProviderKind.Claude, "claude-opus-5", null)).Kind);

        // The OpenAI-compatible provider needs NO key: LM Studio and Ollama want none.
        using var local = (OpenAiCompatibleToneModel)factory.Create(
            new AiProviderSettings(AiProviderKind.OpenAiCompatible, "qwen3:8b", AiProviderPresets.OllamaBaseUrl));
        Assert.Equal(AiProviderKind.OpenAiCompatible, local.Kind);
    }

    // ---- the service -----------------------------------------------------------

    [Fact]
    public void IsConfigured_FollowsTheLiveSettings()
    {
        var config = new AppConfig();
        var service = new ToneDesignService(() => config, new FakeFactory(new FakeModel(ToneJson)), localization: FakeLocalization.English());

        Assert.False(service.IsConfigured);

        config.Ai = new AiProviderSettings(AiProviderKind.Claude, "claude-opus-5", null);
        Assert.True(service.IsConfigured);

        // A provider with no model id is not usable, and the contract says so.
        config.Ai = new AiProviderSettings(AiProviderKind.Claude, "", null);
        Assert.False(service.IsConfigured);
    }

    [Fact]
    public async Task ADesign_ValidatesAndPreparesLikeAnyOtherPreset()
    {
        var model = new FakeModel(ToneJson);
        var service = Service(model);

        var result = await service.DesignAsync(
            new ToneDesignRequest("clean and sparkly", [], null), null, TestContext.Current.CancellationToken);

        Assert.Equal("Glass Clean", result.Tone.Name);
        Assert.Equal(DspToneOrigin.User, result.Tone.Origin);
        Assert.Equal(0.82, result.Confidence, 3);
        Assert.Equal("Blackface sparkle with a little spring.", result.Rationale);
        Assert.Empty(result.Warnings);
        Assert.Equal(new[] { "amp", "cab", "reverb" }, result.Tone.Rig.Modules.Select(m => m.ModuleTypeId));

        // DcRigPresetPreparation fills every unspecified param with its catalogue
        // default — the guard against inheriting the previous tone's values.
        var amp = result.Tone.Rig.Modules[0];
        var expected = DcRigTargetCatalog.AllModules.Single(m => m.ModuleTypeId == "amp").Params;
        Assert.Equal(expected.Count, amp.Params.Count);
        Assert.Equal(0.2f, amp.Params["gain"]);
    }

    [Fact]
    public async Task AUserToneIsNotLoudnessNormalized_SoThePipelineIsNotDoubleApplied()
    {
        // The normalizer runs on BuiltIn presets only. An AI tone is a USER preset,
        // so Prepare must leave its levels exactly as the validator set them.
        var result = await Service(new FakeModel(ToneJson)).DesignAsync(
            new ToneDesignRequest("clean", [], null), null, TestContext.Current.CancellationToken);

        var unprepared = ToneDraftValidator.Validate(ToneJson).Preset!;
        var normalized = DcRigLoudnessNormalizer.Normalize(DcRigTargetCatalog.FillDefaults(unprepared.Rig));
        var plain = DcRigTargetCatalog.FillDefaults(unprepared.Rig);

        Assert.Equal(plain.Modules.Count, result.Tone.Rig.Modules.Count);
        // If the service had normalized, the module COUNT or the trailing gain would
        // follow the normalized rig rather than the plain one.
        Assert.Equal(
            plain.Modules.Select(m => m.ModuleTypeId),
            result.Tone.Rig.Modules.Select(m => m.ModuleTypeId));
        Assert.True(normalized.Modules.Count >= plain.Modules.Count);
    }

    [Fact]
    public async Task TheWarnings_ReachTheCaller()
    {
        var model = new FakeModel("""
            {
              "name": "X", "rationale": "", "confidence": 0.5,
              "modules": [
                { "type": "tubescreamer", "params": {}, "bypassed": false },
                { "type": "gain", "params": { "gainDb": 100 }, "bypassed": false }
              ]
            }
            """);

        var result = await Service(model).DesignAsync(
            new ToneDesignRequest("anything", [], null), null, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Warnings.Count);
        Assert.Contains(result.Warnings, w => w.Contains("tubescreamer", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("set to 24", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEmptyChain_IsInvalidOutput()
    {
        var model = new FakeModel("""{ "name": "X", "rationale": "", "confidence": 1, "modules": [] }""");

        var ex = await Assert.ThrowsAsync<ToneDesignException>(() => Service(model).DesignAsync(
            new ToneDesignRequest("anything", [], null), null, TestContext.Current.CancellationToken));

        Assert.Equal(ToneDesignFault.InvalidOutput, ex.Fault);
    }

    [Fact]
    public async Task ThePromptTheModelSees_CarriesTheCatalogueAndTheRequest()
    {
        var model = new FakeModel(ToneJson);
        await Service(model).DesignAsync(
            new ToneDesignRequest("a djent rhythm tone", [], null), null, TestContext.Current.CancellationToken);

        Assert.NotNull(model.LastPrompt);
        Assert.Contains("# Module catalogue", model.LastPrompt!.System, StringComparison.Ordinal);
        Assert.Contains("a djent rhythm tone", model.LastPrompt.Messages[^1].Text, StringComparison.Ordinal);
        Assert.Equal(ToneDraftJson.SchemaName, model.LastPrompt.SchemaName);
    }

    [Fact]
    public async Task AFollowUp_HandsTheModelTheCurrentToneAndTheHistory()
    {
        var current = ToneDraftValidator.Validate(ToneJson).Preset!;
        var model = new FakeModel(ToneJson);

        await Service(model).DesignAsync(
            new ToneDesignRequest("less reverb",
                [
                    new ToneDesignTurn(ToneDesignRole.User, "clean and sparkly"),
                    new ToneDesignTurn(ToneDesignRole.Assistant, "Glass Clean it is.")
                ],
                current),
            null, TestContext.Current.CancellationToken);

        var messages = model.LastPrompt!.Messages;
        Assert.Equal(3, messages.Count);
        Assert.Equal("clean and sparkly", messages[0].Text);
        Assert.Contains("currently on the rig", messages[2].Text, StringComparison.Ordinal);
        Assert.Contains("\"reverb\"", messages[2].Text, StringComparison.Ordinal);
        Assert.Contains("less reverb", messages[2].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_IsReportedInHumanWords()
    {
        var reported = new List<string>();
        await Service(new FakeModel(ToneJson)).DesignAsync(
            new ToneDesignRequest("clean", [], null),
            new Progress<string>(line => { lock (reported) reported.Add(line); }),
            TestContext.Current.CancellationToken);

        // Progress<T> queues to the POOL, so the lines arrive on another thread.
        // A fixed sleep here is a coin flip wearing an assertion: it failed a
        // contended default-gate run on 2026-09-16 while passing in isolation.
        // Poll instead, with the cap as a DEADLOCK DETECTOR and never a budget.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            // Wait for the lines the case asserts, not for a count: "two lines" were
            // "Reading the gear catalogue…" and "Asking Claude…" on a contended run,
            // and the module line had not been reported yet.
            lock (reported)
            {
                if (reported.Any(l => l.Contains("Asking Claude", StringComparison.Ordinal))
                    && reported.Any(l => l.Contains("Checking 3 modules", StringComparison.Ordinal)))
                {
                    break;
                }
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        string[] lines;
        lock (reported) lines = [.. reported];

        Assert.Contains(lines, s => s.Contains("Asking Claude", StringComparison.Ordinal));
        Assert.Contains(lines, s => s.Contains("Checking 3 modules", StringComparison.Ordinal));
    }

    /// <summary>The same status lines, pinned by KEY rather than wording: the provider
    /// line names its provider and the module line takes the plural form for three.</summary>
    [Fact]
    public async Task Status_ChoosesItsKeys_IndependentOfTheirWording()
    {
        var config = new AppConfig { Ai = new AiProviderSettings(AiProviderKind.Claude, "claude-opus-5", null) };
        var service = new ToneDesignService(() => config, new FakeFactory(new FakeModel(ToneJson)),
            localization: FakeLocalization.KeysOnly());
        var reported = new List<string>();

        await service.DesignAsync(new ToneDesignRequest("clean", [], null), new SyncProgress(reported),
            TestContext.Current.CancellationToken);

        Assert.Contains("ToneDesign_Status_Asking(Claude)", reported);
        Assert.Contains("ToneDesign_Status_CheckingModules(3)", reported);
        Assert.Equal("ToneDesign_Status_Done", reported[^1]);
    }

    [Fact]
    public async Task Cancellation_PassesThroughUntouched()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(new FakeModel(ToneJson))
            .DesignAsync(new ToneDesignRequest("clean", [], null), null, cts.Token));
    }

    [Fact]
    public async Task AProviderThatThrowsSomethingElse_IsStillAToneDesignException()
    {
        // The net under a provider that forgets to translate: the UI must never
        // see an SDK or HTTP type.
        var model = new FakeModel(ToneJson) { Throw = new InvalidOperationException("raw SDK blowup") };

        var ex = await Assert.ThrowsAsync<ToneDesignException>(() => Service(model).DesignAsync(
            new ToneDesignRequest("clean", [], null), null, TestContext.Current.CancellationToken));

        Assert.Equal(ToneDesignFault.Unknown, ex.Fault);
        Assert.Contains("raw SDK blowup", ex.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProvidersOwnFault_ReachesTheCallerUnchanged()
    {
        var model = new FakeModel(ToneJson)
        {
            Throw = new ToneDesignException(ToneDesignFault.RateLimited, "slow down", "429")
        };

        var ex = await Assert.ThrowsAsync<ToneDesignException>(() => Service(model).DesignAsync(
            new ToneDesignRequest("clean", [], null), null, TestContext.Current.CancellationToken));

        Assert.Equal(ToneDesignFault.RateLimited, ex.Fault);
        Assert.Equal("slow down", ex.Message);
    }

    [Fact]
    public async Task ANotConfiguredProvider_FailsBeforeAnythingIsAsked()
    {
        var config = new AppConfig(); // Ai defaults to Disabled
        var service = new ToneDesignService(() => config, new ToneDesignModelFactory(new InMemoryAiSecretStore()), localization: FakeLocalization.English());

        var ex = await Assert.ThrowsAsync<ToneDesignException>(() => service.DesignAsync(
            new ToneDesignRequest("clean", [], null), null, TestContext.Current.CancellationToken));

        Assert.Equal(ToneDesignFault.NotConfigured, ex.Fault);
    }

    [Fact]
    public void AppConfig_DefaultsToDisabled_AndTheAiPropertyIsWhereTheUiExpectsIt()
    {
        var config = new AppConfig();
        Assert.Equal(AiProviderSettings.Disabled, config.Ai);
        Assert.False(config.Ai.IsEnabled);
    }

    /// <summary>The service MEASURES the design and hands the number back, so the
    /// panel's status line is wall time the user actually waited rather than
    /// anything the UI timed for itself. The clock spans the whole call — the
    /// provider round trip AND the validation/preparation after it.</summary>
    [Fact]
    public async Task TheResult_CarriesTheElapsedTimeOfTheWholeCall()
    {
        var model = new SlowModel(ToneJson, TimeSpan.FromMilliseconds(40));

        var result = await Service(model).DesignAsync(
            new ToneDesignRequest("a warm clean", [], null), null, CancellationToken.None);

        Assert.True(result.Elapsed >= TimeSpan.FromMilliseconds(30),
            $"elapsed was {result.Elapsed.TotalMilliseconds:0.0} ms; the model slept 40 ms.");
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(30));
    }

    /// <summary>A provider that takes measurable time, so the elapsed assertion is
    /// not comparing a stopwatch against its own resolution.</summary>
    private sealed class SlowModel(string json, TimeSpan delay) : IToneDesignModel
    {
        public AiProviderKind Kind => AiProviderKind.Claude;

        public async Task<ToneDesignModelReply> CompleteAsync(ToneDesignPrompt prompt, CancellationToken ct)
        {
            await Task.Delay(delay, ct);
            return new ToneDesignModelReply(json, new AiUsage("claude-opus-5", 10, 20));
        }

        public Task<AiProbeResult> ProbeAsync(CancellationToken ct)
            => Task.FromResult(new AiProbeResult(true, "ok", "claude-opus-5"));
    }

    private static ToneDesignService Service(IToneDesignModel model)
    {
        var config = new AppConfig { Ai = new AiProviderSettings(AiProviderKind.Claude, "claude-opus-5", null) };
        return new ToneDesignService(() => config, new FakeFactory(model), localization: FakeLocalization.English());
    }

    private sealed class FakeFactory(IToneDesignModel model) : IToneDesignModelFactory
    {
        public IToneDesignModel Create(AiProviderSettings settings) => model;

        public bool Supports(AiProviderKind kind)
            => kind is AiProviderKind.Claude or AiProviderKind.OpenAiCompatible;
    }

    private sealed class FakeModel(string json) : IToneDesignModel
    {
        public ToneDesignPrompt? LastPrompt { get; private set; }
        public Exception? Throw { get; init; }

        public AiProviderKind Kind => AiProviderKind.Claude;

        public Task<ToneDesignModelReply> CompleteAsync(ToneDesignPrompt prompt, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LastPrompt = prompt;
            if (Throw is not null) return Task.FromException<ToneDesignModelReply>(Throw);
            return Task.FromResult(new ToneDesignModelReply(json, new AiUsage("claude-opus-5", 9000, 200)));
        }

        public Task<AiProbeResult> ProbeAsync(CancellationToken ct)
            => Task.FromResult(new AiProbeResult(true, "ok", "claude-opus-5"));
    }

    // ---- the "ChatGPT (copy & paste)" route (2026-09-28) ------------------------

    /// <summary>What a chat actually sends back: a friendly sentence, the object in a
    /// fenced block, a closing line.</summary>
    private static string ChatAnswer(string json) =>
        "Sure! Here's a glassy clean tone built from your rig's catalogue:\n\n```json\n" + json +
        "\n```\n\nLet me know if you'd like more sparkle or a darker reverb.";

    /// <summary>A service with NO provider: the settings say None, and the factory
    /// fails the test if anything asks it for a model — the route must never.</summary>
    private static ToneDesignService Unconfigured(Func<NamCaptureCatalog>? captures = null) =>
        new(() => new AppConfig(), new NoModelFactory(), FakeLocalization.English(), captures: captures);

    private sealed class NoModelFactory : IToneDesignModelFactory
    {
        public IToneDesignModel Create(AiProviderSettings settings) =>
            throw new InvalidOperationException("the copy & paste route asked for a provider");

        public bool Supports(AiProviderKind kind) => false;
    }

    [Fact]
    public async Task TheCopiedPrompt_IsExactlyWhatAProviderIsSent_FlattenedAndEndingOnJsonOnly()
    {
        var request = new ToneDesignRequest("a glassy blackface clean", [], null, "Little Wing", "Jimi Hendrix");
        var model = new FakeModel(ToneJson);
        await Service(model).DesignAsync(request, null, TestContext.Current.CancellationToken);

        var manual = await Unconfigured().BuildManualPromptAsync(request, TestContext.Current.CancellationToken);

        // The structured prompt IS the provider's: same system prompt, same turns,
        // same schema.
        var sent = model.LastPrompt!;
        Assert.Equal(sent.System, manual.Prompt.System);
        Assert.Equal(sent.Messages, manual.Prompt.Messages);
        Assert.Equal(sent.JsonSchema, manual.Prompt.JsonSchema);

        // …and the text carries all of it: the gear catalogue, the request with its
        // song, the schema, and it ENDS on the answer-with-JSON-only instruction.
        Assert.Contains("# Module catalogue", manual.Text, StringComparison.Ordinal);
        Assert.Contains(sent.System.TrimEnd(), manual.Text, StringComparison.Ordinal);
        Assert.Contains("Request: a glassy blackface clean", manual.Text, StringComparison.Ordinal);
        Assert.Contains("Song: Little Wing", manual.Text, StringComparison.Ordinal);
        Assert.Contains(ToneDraftJson.Schema.Trim(), manual.Text, StringComparison.Ordinal);
        Assert.EndsWith(ToneDesignManualPrompts.FinalInstruction, manual.Text.TrimEnd(), StringComparison.Ordinal);
        Assert.True(manual.Text.IndexOf(ToneDraftJson.Schema.Trim(), StringComparison.Ordinal)
                    > manual.Text.IndexOf("Request: a glassy", StringComparison.Ordinal),
            "the schema must come after the request, right before the closing instruction");
        Assert.Equal(ToneDesignPromptBuilder.EstimateTokens(manual.Text), manual.EstimatedTokens);
        Assert.True(manual.EstimatedTokens > 1000, $"~{manual.EstimatedTokens} tokens — the catalogue is missing");
    }

    [Fact]
    public async Task TheCopiedPrompt_ForATweak_CarriesTheCurrentToneAndTheConversation()
    {
        var current = ToneDraftValidator.Validate(ToneJson).Preset!;
        var manual = await Unconfigured().BuildManualPromptAsync(
            new ToneDesignRequest("less reverb",
                [
                    new ToneDesignTurn(ToneDesignRole.User, "clean and sparkly"),
                    new ToneDesignTurn(ToneDesignRole.Assistant, "Glass Clean"),
                    new ToneDesignTurn(ToneDesignRole.User, "less reverb")
                ],
                current),
            TestContext.Current.CancellationToken);

        Assert.Contains("## Your previous answer\nGlass Clean", manual.Text, StringComparison.Ordinal);
        Assert.Contains("currently on the rig", manual.Text, StringComparison.Ordinal);
        Assert.Contains("\"reverb\"", manual.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCopiedPrompt_AsksForResearchOnlyWhileTheSettingAllowsIt()
    {
        var config = new AppConfig();
        var service = new ToneDesignService(() => config, new NoModelFactory(), localization: FakeLocalization.English());
        var request = new ToneDesignRequest("that tone", [], null, "Little Wing", "Jimi Hendrix");

        var on = await service.BuildManualPromptAsync(request, TestContext.Current.CancellationToken);
        config.Ai = AiProviderSettings.Disabled with { WebResearch = false };
        var off = await service.BuildManualPromptAsync(request, TestContext.Current.CancellationToken);

        Assert.Contains(ToneDesignResearch.Instruction, on.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(ToneDesignResearch.Instruction, off.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChatAnswer_ImportsToTheSamePresetTheProviderPathMakesFromTheSameJson()
    {
        var provider = await Service(new FakeModel(ToneJson)).DesignAsync(
            new ToneDesignRequest("clean and sparkly", [], null), null, TestContext.Current.CancellationToken);

        var reported = new List<string>();
        var imported = await Unconfigured().ImportReplyAsync(
            new ToneDesignRequest("clean and sparkly", [], null), ChatAnswer(ToneJson),
            new SyncProgress(reported), TestContext.Current.CancellationToken);

        // Everything but the preset's fresh id is the provider path's answer.
        Assert.Equal(provider.Tone, imported.Tone, PresetComparer.Instance);
        Assert.Equal(provider.Rationale, imported.Rationale);
        Assert.Equal(provider.Confidence, imported.Confidence);
        Assert.Equal(provider.Warnings, imported.Warnings);
        Assert.Equal(DspToneOrigin.User, imported.Tone.Origin);
        // …and it says it was pasted: nothing metered, nothing to price.
        Assert.True(imported.Usage.IsManual);
        Assert.Equal(0, imported.Usage.InputTokens + imported.Usage.OutputTokens);
        Assert.Null(AiModelPricing.FormatUsd(imported.Usage));
        Assert.Equal(FakeLocalization.English()["ToneDesign_Status_ReadingReply"], reported[0]);
    }

    public static TheoryData<string> UnusableAnswers => new(
        "",
        "   ",
        "Sorry, I can't help with designing guitar tones.",
        "```json\n{ \"name\": \"Broken\", \"modules\": [ \n```",
        // Parses, and every module is one this build has never heard of.
        ChatAnswer("""{ "name": "Flux", "rationale": "", "confidence": 1, "modules": [ { "type": "flux-capacitor", "params": {} } ] }"""));

    [Theory]
    [MemberData(nameof(UnusableAnswers))]
    public async Task AnAnswerThatIsNotAUsableTone_IsInvalidOutput(string answer)
    {
        var ex = await Assert.ThrowsAsync<ToneDesignException>(() => Unconfigured().ImportReplyAsync(
            new ToneDesignRequest("anything", [], null), answer, null, TestContext.Current.CancellationToken));

        Assert.Equal(ToneDesignFault.InvalidOutput, ex.Fault);
    }

    [Fact]
    public async Task TheRouteWorks_WithNoProviderConfigured()
    {
        var service = Unconfigured();
        Assert.False(service.IsConfigured);

        var manual = await service.BuildManualPromptAsync(
            new ToneDesignRequest("a warm clean", [], null), TestContext.Current.CancellationToken);
        var result = await service.ImportReplyAsync(
            new ToneDesignRequest("a warm clean", [], null), ChatAnswer(ToneJson), null,
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(manual.Text);
        Assert.Equal("Glass Clean", result.Tone.Name);
    }

    /// <summary>A capture the copied prompt OFFERED is one the import ACCEPTS: both
    /// read the capture library the same live way.</summary>
    [Fact]
    public async Task ACaptureTheCopiedPromptListed_ValidatesOnImport()
    {
        var entry = new NamCaptureEntry(
            new NamCaptureManifest(
                -7, -7, "5150 Block Letter", "Someone", "amp", "nam", "standard",
                "MIT", "https://example.invalid/license", "https://example.invalid/attribution",
                "5150-blockletter.nam", "sha", 1024, DateTimeOffset.UnixEpoch),
            Path.Combine(@"C:\captures", "5150-blockletter.nam"));
        var catalog = NamCaptureCatalog.FromEntries([entry]);
        var service = Unconfigured(() => catalog);
        var id = Assert.Single(catalog.Options).Id;

        var manual = await service.BuildManualPromptAsync(
            new ToneDesignRequest("that 5150", [], null), TestContext.Current.CancellationToken);
        Assert.Contains(id, manual.Text, StringComparison.Ordinal);

        var result = await service.ImportReplyAsync(
            new ToneDesignRequest("that 5150", [], null),
            ChatAnswer($$"""
                {"name":"Fifty","rationale":"r","confidence":0.8,"modules":[
                  {"type":"namamp","params":{},"bypassed":false,"capture":"{{id}}"},
                  {"type":"cab","params":{},"bypassed":false}]}
                """),
            null, TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
        Assert.Contains(result.Tone.Rig.Modules, m => m.ModuleTypeId == "namamp");
    }

    [Fact]
    public async Task AnImport_HonoursCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Unconfigured().ImportReplyAsync(
            new ToneDesignRequest("x", [], null), ChatAnswer(ToneJson), null, cts.Token));
    }

    /// <summary>An <see cref="IProgress{T}" /> that records synchronously.</summary>
    private sealed class SyncProgress(List<string> lines) : IProgress<string>
    {
        public void Report(string value) => lines.Add(value);
    }

    /// <summary>Two presets are the same TONE: name, category, description, origin
    /// and every module's type, bypass and params — everything but the fresh id.
    /// (The record's own equality compares the rig's module LIST by reference.)</summary>
    private sealed class PresetComparer : IEqualityComparer<DspTonePreset>
    {
        public static readonly PresetComparer Instance = new();

        public bool Equals(DspTonePreset? a, DspTonePreset? b) =>
            a is not null && b is not null
            && a.Name == b.Name && a.Category == b.Category && a.Description == b.Description
            && a.Origin == b.Origin
            && a.Rig.Modules.Count == b.Rig.Modules.Count
            && a.Rig.Modules.Zip(b.Rig.Modules).All(p =>
                p.First.ModuleTypeId == p.Second.ModuleTypeId
                && p.First.Bypassed == p.Second.Bypassed
                && p.First.Params.OrderBy(kv => kv.Key).SequenceEqual(p.Second.Params.OrderBy(kv => kv.Key)));

        public int GetHashCode(DspTonePreset obj) => obj.Name.GetHashCode(StringComparison.Ordinal);
    }

    /// <summary>The designer's status line is read by the user ("Asking Claude…", "Checking 3
    /// modules…"); it was English string literals whatever the language, with "module{s}" for
    /// a plural. Every line goes through the resx now.</summary>
    [Fact]
    public void EveryStatusLine_IsLocalized()
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone", "Ai", "ToneDesignService.cs"));

        Assert.True(Regex.Matches(source, @"status\?\.Report\(").Count >= 6, "the status reports moved — repoint this lint");
        var literal = Regex.Matches(source, @"status\?\.Report\(\$?""[^""]*""").Select(m => m.Value).ToList();
        Assert.True(literal.Count == 0, "status lines still written as literals: " + string.Join(", ", literal));
    }
}
