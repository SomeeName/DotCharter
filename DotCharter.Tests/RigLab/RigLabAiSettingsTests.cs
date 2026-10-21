using DotCharter.RigLab;
using DotCharter.Tone.Ai;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.Online;
using DotCharter.Tone.Online.Tone3000;

namespace DotCharter.Tests.RigLab;

/// <summary>
///     RigLab's AI tab: a change applies at once (riglab.json + the design
///     service told), an API key is sealed and never written to the file, a CLI
///     row offers no key box, a provider this machine cannot run bounces back
///     with its reason, and the TONE3000 half says whether a design can fetch a
///     capture.
/// </summary>
public sealed class RigLabAiSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "riglab-ai-" + Guid.NewGuid().ToString("N"));
    private readonly RigLabConfigService _config;
    private readonly InMemoryAiSecretStore _secrets = new();
    private readonly FakeDesign _design = new();
    private readonly FakeAuth _auth = new();

    public RigLabAiSettingsTests()
    {
        Directory.CreateDirectory(_dir);
        _config = new RigLabConfigService(Path.Combine(_dir, RigLabConfigService.FileName), TimeSpan.Zero);
    }

    public void Dispose()
    {
        _config.Dispose();
        BestEffortDelete.Directory(_dir);
    }

    private AiSettingsViewModel Tab(params AiProviderKind[] supported) =>
        new(_config, _secrets, new FakeFactory(supported), _design, _auth, new GatedInlineDispatcher(), FakeLocalization.English());

    private static RigLabAiProvider Row(AiSettingsViewModel vm, AiProviderKind kind) => vm.Providers.Single(p => p.Kind == kind);

    [Fact]
    public void EveryProvider_IsOffered_AndNoneIsTheDefault()
    {
        var vm = Tab(AiProviderKind.Claude, AiProviderKind.OpenAiCompatible, AiProviderKind.ClaudeCli, AiProviderKind.CodexCli);

        Assert.Equal(
            [AiProviderKind.None, AiProviderKind.Claude, AiProviderKind.OpenAiCompatible, AiProviderKind.ClaudeCli, AiProviderKind.CodexCli],
            vm.Providers.Select(p => p.Kind));
        Assert.Equal(AiProviderKind.None, vm.ProviderKind);
        Assert.False(vm.IsModelRowVisible);
    }

    [Fact]
    public void PickingCodex_IsSavedAtOnce_AndTellsTheDesigner()
    {
        var vm = Tab(AiProviderKind.CodexCli);

        vm.SelectedProvider = Row(vm, AiProviderKind.CodexCli);

        Assert.Equal(AiProviderKind.CodexCli, _config.Load().Ai.Kind);
        Assert.Equal(1, _design.Notified);
        // A CLI takes no key, so there is no box to put one in — and whose usage it
        // spends is said on the tab.
        Assert.False(vm.IsApiKeyRowVisible);
        Assert.True(vm.IsCodexCliNoteVisible);
        Assert.True(vm.IsCliPathRowVisible);
        // Picking Codex seeds GPT-6 Luna; the model stays optional (empty also means Luna).
        Assert.Equal("gpt-6-luna", vm.ModelId);
        Assert.Equal("gpt-6-luna", _config.Load().Ai.ModelId);
        vm.ModelId = "";
        Assert.True(vm.TestConnectionCommand.CanExecute(null));
    }

    /// <summary>Online research: offered for the two CLIs (the only providers with
    /// web tools), off by default, and switching it on is saved at once.</summary>
    [Fact]
    public void TheResearchSwitch_IsForTheClis_AndIsSavedAtOnce()
    {
        var vm = Tab(AiProviderKind.Claude, AiProviderKind.CodexCli);
        vm.SelectedProvider = Row(vm, AiProviderKind.Claude);
        Assert.False(vm.IsWebResearchRowVisible);

        vm.SelectedProvider = Row(vm, AiProviderKind.CodexCli);
        Assert.True(vm.IsWebResearchRowVisible);
        Assert.False(vm.WebResearch);
        Assert.False(_config.Load().Ai.WebResearch);

        vm.WebResearch = true;
        Assert.True(_config.Load().Ai.WebResearch);
        vm.Load();
        Assert.True(vm.WebResearch);
    }

    [Fact]
    public void PickingClaude_FillsTheDefaultModel()
    {
        var vm = Tab(AiProviderKind.Claude);

        vm.SelectedProvider = Row(vm, AiProviderKind.Claude);

        Assert.Equal("claude-opus-5-5", vm.ModelId);
        Assert.Equal("claude-opus-5-5", _config.Load().Ai.ModelId);
        Assert.True(vm.IsApiKeyRowVisible);
    }

    /// <summary>Haiku 5.5 is what an empty Claude Code box gets, and the three Claude
    /// presets fill in the current generation — literals, so a stale constant
    /// cannot pass.</summary>
    [Fact]
    public void TheClaudePresets_AreTheCurrentGeneration_AndAreSavedAtOnce()
    {
        var vm = Tab(AiProviderKind.ClaudeCli);
        vm.SelectedProvider = Row(vm, AiProviderKind.ClaudeCli);
        Assert.Equal("claude-haiku-5-5", vm.ModelId);
        Assert.Equal("claude-haiku-5-5", _config.Load().Ai.ModelId);

        vm.UseClaudeSonnetCommand.Execute(null);
        Assert.Equal("claude-sonnet-5-5", vm.ModelId);
        Assert.Equal("claude-sonnet-5-5", _config.Load().Ai.ModelId);

        vm.UseClaudeOpusCommand.Execute(null);
        Assert.Equal("claude-opus-5-5", vm.ModelId);
        Assert.Equal("claude-opus-5-5", _config.Load().Ai.ModelId);

        vm.UseClaudeHaikuCommand.Execute(null);
        Assert.Equal("claude-haiku-5-5", vm.ModelId);
        Assert.Equal("claude-haiku-5-5", _config.Load().Ai.ModelId);
    }

    [Fact]
    public void TheCodexPresets_SetGpt6SolAndLuna_AndAreSavedAtOnce()
    {
        var vm = Tab(AiProviderKind.CodexCli);
        vm.SelectedProvider = Row(vm, AiProviderKind.CodexCli);
        Assert.Equal("gpt-6-luna", vm.ModelId);

        vm.UseCodexSolCommand.Execute(null);
        Assert.Equal("gpt-6-sol", vm.ModelId);
        Assert.Equal("gpt-6-sol", _config.Load().Ai.ModelId);

        vm.UseCodexLunaCommand.Execute(null);
        Assert.Equal("gpt-6-luna", vm.ModelId);
        Assert.Equal("gpt-6-luna", _config.Load().Ai.ModelId);
    }

    /// <summary>Each provider's presets are its own; driven over every kind so it
    /// cannot pass by showing both rows for one.</summary>
    [Theory]
    [InlineData(AiProviderKind.None, false, false)]
    [InlineData(AiProviderKind.Claude, true, false)]
    [InlineData(AiProviderKind.OpenAiCompatible, false, false)]
    [InlineData(AiProviderKind.ClaudeCli, true, false)]
    [InlineData(AiProviderKind.CodexCli, false, true)]
    public void EachProvider_ShowsOnlyItsOwnModelPresets(AiProviderKind kind, bool claude, bool codex)
    {
        var vm = Tab(AiProviderKind.Claude, AiProviderKind.OpenAiCompatible, AiProviderKind.ClaudeCli, AiProviderKind.CodexCli);
        vm.SelectedProvider = Row(vm, kind);

        Assert.Equal(kind, vm.ProviderKind);
        Assert.Equal(claude, vm.AreClaudePresetsVisible);
        Assert.Equal(codex, vm.AreCodexPresetsVisible);
    }

    /// <summary>A model id already in riglab.json is the user's: opening the tab and
    /// re-picking its provider never swaps it for the new default.</summary>
    [Fact]
    public void ASavedModelId_IsNeverRewrittenByTheNewDefault()
    {
        _config.Update(c => c.Ai = new AiProviderSettings(AiProviderKind.ClaudeCli, "claude-opus-5", null));

        var vm = Tab(AiProviderKind.ClaudeCli);
        Assert.Equal("claude-opus-5", vm.ModelId);
        vm.SelectedProvider = Row(vm, AiProviderKind.ClaudeCli);

        Assert.Equal("claude-opus-5", vm.ModelId);
        Assert.Equal("claude-opus-5", _config.Load().Ai.ModelId);
    }

    [Fact]
    public void AProviderThisMachineCannotRun_BouncesBack_AndSaysWhy()
    {
        var vm = Tab(); // no CLI installed

        vm.SelectedProvider = Row(vm, AiProviderKind.CodexCli);

        Assert.Equal(AiProviderKind.None, vm.ProviderKind);
        Assert.Equal(AiProviderKind.None, vm.SelectedProvider.Kind);
        Assert.True(vm.HasStatus, "a silent bounce reads as a broken control");
        Assert.Equal(AiProviderKind.None, _config.Load().Ai.Kind);
    }

    [Fact]
    public void AKey_IsSealed_NeverWrittenToTheFile_AndTheBoxEmpties()
    {
        var vm = Tab(AiProviderKind.OpenAiCompatible);
        vm.SelectedProvider = Row(vm, AiProviderKind.OpenAiCompatible);
        vm.UseLmStudioCommand.Execute(null);
        vm.ModelId = "llama-3.1-8b-instruct";

        vm.ApiKeyEntry = "sk-secret-123";
        vm.SaveKeyCommand.Execute(null);

        Assert.Equal("sk-secret-123", _secrets.Read(AiProviderKind.OpenAiCompatible));
        Assert.Equal("", vm.ApiKeyEntry);
        Assert.True(vm.HasStoredKey);
        _config.Flush();
        Assert.True(File.Exists(_config.FilePath), $"not written: {_config.LastWriteError}");
        var file = File.ReadAllText(_config.FilePath);
        Assert.DoesNotContain("sk-secret-123", file, StringComparison.Ordinal);
        Assert.Equal(AiProviderPresets.LmStudioBaseUrl, _config.Load().Ai.BaseUrl);

        vm.ClearKeyCommand.Execute(null);
        Assert.Null(_secrets.Read(AiProviderKind.OpenAiCompatible));
    }

    [Fact]
    public void TheTone3000Half_SaysWhetherADesignCanFetchACapture()
    {
        var vm = Tab();
        Assert.False(vm.SignInCommand.CanExecute(null)); // no client id yet

        vm.Tone3000ClientId = "  my-client  ";
        Assert.Equal("my-client", _config.Load().Tone3000ClientId);
        Assert.True(vm.SignInCommand.CanExecute(null));
        var signedOut = vm.CaptureSearchText;

        _auth.SignedIn = true;
        _auth.Raise();

        Assert.True(vm.IsTone3000SignedIn);
        Assert.NotEqual(signedOut, vm.CaptureSearchText);
        Assert.False(vm.SignInCommand.CanExecute(null));
        Assert.True(vm.SignOutCommand.CanExecute(null));
    }

    [Fact]
    public void AClientIdInTheFile_WinsOverTheBuildDefault_AndAnEmptyOneTakesIt()
    {
        Assert.Equal(RigLabBuild.Tone3000ClientId, _config.Load().Tone3000ClientId);

        _config.Update(c => c.Tone3000ClientId = "t3k_pub_from_the_file");
        _config.Flush();
        using var reread = new RigLabConfigService(_config.FilePath, TimeSpan.Zero);

        Assert.Equal("t3k_pub_from_the_file", reread.Load().Tone3000ClientId);
    }

    // ---- Reasoning (#644): the app's row, on RigLab's tab --------------------------

    /// <summary>Hidden with None (which sends nothing), shown for every provider, and disabled
    /// with its reason for a Claude model older than the effort parameter — the app's rule and
    /// the app's resx keys, read through the one <see cref="AiReasoningText" />.</summary>
    [Fact]
    public void TheReasoningRow_IsHiddenWithNone_AndSaysWhyAnOldClaudeModelCannotTakeIt()
    {
        var loc = FakeLocalization.English();
        var vm = Tab(AiProviderKind.Claude, AiProviderKind.OpenAiCompatible, AiProviderKind.CodexCli);
        Assert.False(vm.IsEffortRowVisible);
        Assert.Equal(
            ["Ai_Effort_Default", "Ai_Effort_Low", "Ai_Effort_Medium", "Ai_Effort_High"],
            vm.EffortLevels.Select(l => AiReasoningText.Key(l.Effort)));
        Assert.Equal(loc["Ai_Effort_High"], vm.EffortLevels[3].Label);

        vm.SelectedProvider = Row(vm, AiProviderKind.Claude);
        Assert.True(vm.IsEffortRowVisible);
        Assert.True(vm.IsEffortSupported);
        Assert.Equal(loc["Ai_EffortDesc"], vm.EffortDesc);

        vm.ModelId = "claude-haiku-4-5";
        Assert.True(vm.IsEffortRowVisible);
        Assert.False(vm.IsEffortSupported);
        Assert.Equal(loc["Ai_Effort_Unsupported_ClaudeModel"], vm.EffortDesc);

        vm.SelectedProvider = Row(vm, AiProviderKind.OpenAiCompatible);
        Assert.True(vm.IsEffortSupported);
        Assert.Equal(loc["Ai_EffortDesc_OpenAi"], vm.EffortDesc);
        vm.SelectedProvider = Row(vm, AiProviderKind.CodexCli);
        Assert.True(vm.IsEffortRowVisible);

        vm.SelectedProvider = Row(vm, AiProviderKind.None);
        Assert.False(vm.IsEffortRowVisible);
    }

    /// <summary>The picked level is in riglab.json ON DISK (a second store reads it back), a
    /// fresh tab shows it, and the design service hands it to the provider, whose request then
    /// carries it.</summary>
    [Fact]
    public async Task TheReasoningLevel_IsSavedToRiglabJson_AndReachesTheToneModelRequest()
    {
        var vm = Tab(AiProviderKind.OpenAiCompatible);
        vm.SelectedProvider = Row(vm, AiProviderKind.OpenAiCompatible);
        vm.ModelId = "qwen3";
        vm.BaseUrl = AiProviderPresets.OllamaBaseUrl;
        var notified = _design.Notified;

        vm.SelectedEffort = vm.EffortLevels.Single(l => l.Effort == AiReasoningEffort.High);

        Assert.Equal(AiReasoningEffort.High, _config.Load().Ai.Effort);
        Assert.Equal(notified + 1, _design.Notified);
        _config.Flush();
        using (var reread = new RigLabConfigService(Path.Combine(_dir, RigLabConfigService.FileName), TimeSpan.Zero))
            Assert.Equal(AiReasoningEffort.High, reread.Load().Ai.Effort);
        Assert.Equal(AiReasoningEffort.High, Tab(AiProviderKind.OpenAiCompatible).SelectedEffort.Effort);

        // RigLabHost's design service reads riglab.json the same way; the factory gets the level...
        var factory = new CapturingFactory();
        var design = new ToneDesignService(() => _config.Load(), factory, FakeLocalization.English());
        await Assert.ThrowsAsync<ToneDesignException>(() =>
            design.DesignAsync(new ToneDesignRequest("a clean tone", [], null), null, CancellationToken.None));
        var sent = Assert.Single(factory.Created);
        Assert.Equal(AiReasoningEffort.High, sent.Effort);

        // ...and the provider it mints puts it on the wire.
        using var model = new OpenAiCompatibleToneModel(sent);
        using var body = System.Text.Json.JsonDocument.Parse(
            model.BuildRequestBody(ToneDesignPromptBuilder.Build(new ToneDesignRequest("a clean tone", [], null))));
        Assert.Equal("high", body.RootElement.GetProperty("reasoning_effort").GetString());
    }

    private sealed class CapturingFactory : IToneDesignModelFactory
    {
        public List<AiProviderSettings> Created { get; } = [];

        public IToneDesignModel Create(AiProviderSettings settings)
        {
            Created.Add(settings);
            throw new ToneDesignException(ToneDesignFault.NotConfigured, "captured, not called");
        }

        public bool Supports(AiProviderKind kind) => true;
    }

    private sealed class FakeFactory(AiProviderKind[] supported) : IToneDesignModelFactory
    {
        public IToneDesignModel Create(AiProviderSettings settings) =>
            throw new ToneDesignException(ToneDesignFault.NotConfigured, "not in this test");

        public bool Supports(AiProviderKind kind) => supported.Contains(kind);
    }

    private sealed class FakeDesign : IToneDesignService
    {
        /// <summary>The copy &amp; paste route is not exercised by this fake.</summary>
        public Task<DotCharter.Tone.Ai.ToneDesignManualPrompt> BuildManualPromptAsync(
            DotCharter.Tone.Ai.ToneDesignRequest request, CancellationToken ct) =>
            throw new NotSupportedException("this fake never builds a copy & paste prompt");

        /// <summary>The copy &amp; paste route is not exercised by this fake.</summary>
        public Task<DotCharter.Tone.Ai.ToneDesignResult> ImportReplyAsync(
            DotCharter.Tone.Ai.ToneDesignRequest request, string reply, IProgress<string>? status,
            CancellationToken ct) =>
            throw new NotSupportedException("this fake never imports a pasted answer");

        public int Notified { get; private set; }
        public bool IsConfigured => false;
        public event EventHandler? ConfiguredChanged { add { } remove { } }
        public void NotifyConfigurationChanged() => Notified++;

        public Task<ToneDesignResult> DesignAsync(ToneDesignRequest request, IProgress<string>? status, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class FakeAuth : ITone3000AuthService
    {
        public bool SignedIn { get; set; }
        public bool IsConfigured => true;
        public bool IsSignedIn => SignedIn;
        public string UserName => SignedIn ? "player" : "";
        public event Action? AuthStateChanged;
        public void Raise() => AuthStateChanged?.Invoke();

        public Task<OnlineResult<Tone3000User>> SignInAsync(CancellationToken ct = default)
            => Task.FromResult(OnlineResult<Tone3000User>.Fail(OnlineError.NotConfigured));

        public void SignOut() => SignedIn = false;
        public void InvalidateSession(string reason) => SignedIn = false;
        public Task<string?> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    }
}
