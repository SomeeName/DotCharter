using DotCharter.Localization;
using DotCharter.Tone.Ai;
using DotCharter.Presentation.Screens.Settings;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>
///     Settings ▸ AI. Uses the real <see cref="LocalizationService" /> (so a
///     missing resx key surfaces) and fakes for the two AI services.
///
///     <para>
///         The security-shaped assertions are the ones worth having:
///         <see cref="AiSettingsViewModel.Load" /> must never put the stored key
///         back in the box, and an EMPTY box on save must not erase it. Both
///         faults are invisible — the first renders perfectly, the second only
///         shows up the next time the user saves any other setting on the page.
///     </para>
/// </summary>
public sealed class AiSettingsViewModelTests
{
    private sealed class FakeSecretStore : IAiSecretStore
    {
        public readonly Dictionary<AiProviderKind, string?> Secrets = [];
        public int Writes;

        public string? Read(AiProviderKind kind) =>
            Secrets.TryGetValue(kind, out var value) ? value : null;

        public void Write(AiProviderKind kind, string? secret)
        {
            Writes++;
            Secrets[kind] = secret;
        }

        /// <summary>Settable, because the Settings page's session-only row is
        /// exactly what the interface member exists to let a fake answer for.</summary>
        public bool IsPersistent { get; set; } = true;
    }

    private sealed class FakeModel : IToneDesignModel
    {
        public AiProviderKind Kind { get; init; }
        public AiProbeResult Probe = new(true, "ok", "claude-opus-5");

        public Task<ToneDesignModelReply> CompleteAsync(ToneDesignPrompt prompt, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<AiProbeResult> ProbeAsync(CancellationToken ct) => Task.FromResult(Probe);
    }

    private sealed class FakeFactory : IToneDesignModelFactory
    {
        public readonly List<AiProviderSettings> Created = [];
        public FakeModel Model = new();
        public Exception? Throw;

        /// <summary>Which kinds this build claims to mint. Settable so the picker
        /// test can watch a row follow the factory rather than a list in the UI.</summary>
        public HashSet<AiProviderKind> Supported = [AiProviderKind.Claude, AiProviderKind.OpenAiCompatible];

        public bool Supports(AiProviderKind kind) => Supported.Contains(kind);

        public IToneDesignModel Create(AiProviderSettings settings)
        {
            Created.Add(settings);
            if (Throw != null) throw Throw;
            return Model;
        }
    }

    private static AiSettingsViewModel NewVm(
        out FakeConfigurationService config, out FakeSecretStore secrets, out FakeFactory factory)
    {
        config = new FakeConfigurationService();
        secrets = new FakeSecretStore();
        factory = new FakeFactory();
        return AiTabRig.Create(config, secrets, factory);
    }

    [Fact]
    public void Load_reads_the_persisted_provider_and_never_the_key()
    {
        var vm = NewVm(out var config, out var secrets, out _);
        config.AppConfig.Ai = new AiProviderSettings(AiProviderKind.OpenAiCompatible, "llama", "http://host:1");
        secrets.Secrets[AiProviderKind.OpenAiCompatible] = "sk-super-secret";

        vm.Load();

        Assert.Equal(AiProviderKind.OpenAiCompatible, vm.ProviderKind);
        Assert.Equal("llama", vm.ModelId);
        Assert.Equal("http://host:1", vm.BaseUrl);
        Assert.True(vm.Shape.ShowBaseUrl);
        // The indicator says a key exists; the BOX does not carry it.
        Assert.True(vm.HasStoredKey);
        Assert.Equal(string.Empty, vm.ApiKeyEntry);
        Assert.DoesNotContain("sk-super-secret", vm.ApiKeyEntry, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_persists_the_non_secret_half_through_AppConfig()
    {
        var vm = NewVm(out var config, out _, out _);
        vm.Load();
        vm.SelectedProvider = vm.Providers.Single(p => p.Kind == AiProviderKind.OpenAiCompatible);
        vm.ModelId = "qwen2.5-coder";
        vm.BaseUrl = AiProviderPresets.OllamaBaseUrl;

        vm.Save();

        Assert.Equal(AiProviderKind.OpenAiCompatible, config.AppConfig.Ai.Kind);
        Assert.Equal("qwen2.5-coder", config.AppConfig.Ai.ModelId);
        Assert.Equal(AiProviderPresets.OllamaBaseUrl, config.AppConfig.Ai.BaseUrl);
        Assert.True(config.AppConfig.Ai.IsEnabled);
    }

    [Fact]
    public void Save_writes_a_typed_key_through_the_secret_store_and_clears_the_box()
    {
        var vm = NewVm(out _, out var secrets, out _);
        vm.Load();
        vm.SelectedProvider = vm.Providers.Single(p => p.Kind == AiProviderKind.Claude);
        vm.ApiKeyEntry = "  sk-abc  ";

        vm.Save();

        Assert.Equal("sk-abc", secrets.Read(AiProviderKind.Claude));
        Assert.Equal(string.Empty, vm.ApiKeyEntry);
        Assert.True(vm.HasStoredKey);
    }

    [Fact]
    public void An_empty_box_leaves_the_stored_key_alone()
    {
        var vm = NewVm(out _, out var secrets, out _);
        secrets.Secrets[AiProviderKind.Claude] = "sk-existing";
        vm.Load();
        vm.SelectedProvider = vm.Providers.Single(p => p.Kind == AiProviderKind.Claude);

        vm.Save();

        Assert.Equal("sk-existing", secrets.Read(AiProviderKind.Claude));
    }

    [Fact]
    public void Clear_is_the_erase()
    {
        var vm = NewVm(out _, out var secrets, out _);
        secrets.Secrets[AiProviderKind.Claude] = "sk-existing";
        vm.Load();
        vm.SelectedProvider = vm.Providers.Single(p => p.Kind == AiProviderKind.Claude);
        Assert.True(vm.HasStoredKey);

        vm.ClearKeyCommand.Execute(null);

        Assert.Null(secrets.Read(AiProviderKind.Claude));
        Assert.False(vm.HasStoredKey);
    }

    [Fact]
    public void Picking_Claude_fills_in_the_shipping_model_and_hides_the_base_url()
    {
        var vm = NewVm(out _, out _, out _);
        vm.Load();

        vm.SelectedProvider = vm.Providers.Single(p => p.Kind == AiProviderKind.Claude);

        Assert.Equal(ClaudeToneModel.DefaultModelId, vm.ModelId);
        Assert.False(vm.Shape.ShowBaseUrl);
        Assert.True(vm.Shape.ShowKey);
    }

    [Fact]
    public void Picking_an_OpenAI_compatible_server_leaves_the_model_empty_and_offers_the_two_presets()
    {
        var vm = NewVm(out _, out _, out _);
        vm.Load();

        vm.SelectedProvider = vm.Providers.Single(p => p.Kind == AiProviderKind.OpenAiCompatible);
        Assert.Equal(string.Empty, vm.ModelId);
        Assert.True(vm.Shape.ShowBaseUrl);

        vm.UseLmStudioCommand.Execute(null);
        Assert.Equal("http://localhost:1234", vm.BaseUrl);
        vm.UseOllamaCommand.Execute(null);
        Assert.Equal("http://localhost:11434", vm.BaseUrl);
    }

    [Fact]
    public void The_built_in_model_cannot_be_picked_and_says_why()
    {
        var vm = NewVm(out _, out _, out _);
        vm.Load();
        vm.SelectedProvider = vm.Providers.Single(p => p.Kind == AiProviderKind.Claude);

        vm.SelectedProvider = vm.Providers.Single(p => p.Kind == AiProviderKind.BuiltIn);

        Assert.Equal(AiProviderKind.Claude, vm.ProviderKind);
        Assert.Equal(AiProviderKind.Claude, vm.SelectedProvider.Kind);
        Assert.True(vm.HasStatus);
    }

    [Fact]
    public async Task Test_connection_probes_the_configured_model_and_reports_the_answer()
    {
        var vm = NewVm(out var config, out _, out var factory);
        vm.Load();
        vm.SelectedProvider = vm.Providers.Single(p => p.Kind == AiProviderKind.Claude);
        vm.ModelId = "claude-opus-5";

        await vm.TestConnectionCommand.ExecuteAsync(null);

        // It SAVED first: a probe of the settings on screen rather than the ones
        // in force would pass for a configuration nothing is using.
        Assert.Equal(AiProviderKind.Claude, config.AppConfig.Ai.Kind);
        var used = Assert.Single(factory.Created);
        Assert.Equal("claude-opus-5", used.ModelId);
        Assert.True(vm.HasStatus);
        Assert.False(vm.IsStatusError);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task A_failed_probe_is_reported_as_an_error_rather_than_thrown()
    {
        var vm = NewVm(out _, out _, out var factory);
        factory.Model = new FakeModel { Probe = new AiProbeResult(false, "401 Unauthorized", null) };
        vm.Load();
        vm.SelectedProvider = vm.Providers.Single(p => p.Kind == AiProviderKind.Claude);

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.True(vm.IsStatusError);
        Assert.Contains("401 Unauthorized", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_throwing_factory_is_reported_rather_than_taking_the_page_down()
    {
        var vm = NewVm(out _, out _, out var factory);
        factory.Throw = new ToneDesignException(ToneDesignFault.NotConfigured, "no key", "detail");
        vm.Load();
        vm.SelectedProvider = vm.Providers.Single(p => p.Kind == AiProviderKind.Claude);

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.True(vm.IsStatusError);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Without_the_AI_services_the_tab_reports_itself_unavailable()
    {
        var config = new FakeConfigurationService();
        var vm = AiTabRig.Create(config);
        vm.Load();
        vm.SelectedProvider = vm.Providers.Single(p => p.Kind == AiProviderKind.Claude);

        Assert.False(vm.IsAvailable);
        Assert.False(vm.HasStoredKey);
        Assert.False(vm.TestConnectionCommand.CanExecute(null));
    }

    /// <summary>
    ///     The change set tracks the four PERSISTED values and nothing else: not
    ///     the combo's option object, which it cannot compare, and above all not
    ///     the key box — the set keeps a baseline copy of every value it watches,
    ///     and a typed key has no business living in one.
    /// </summary>
    [Fact]
    public void The_change_set_counts_the_four_persisted_values_and_never_the_key()
    {
        var vm = NewVm(out _, out _, out _);
        vm.Load();
        Assert.Empty(vm.UnsavedSettings);

        vm.ApiKeyEntry = "sk-ant-not-a-real-key";
        Assert.Empty(vm.UnsavedSettings);

        vm.SelectedProvider = Row(vm, AiProviderKind.Claude);
        vm.ModelId = "claude-opus-5";
        vm.BaseUrl = "http://localhost:1234/v1";
        vm.UseLibraryTones = false;
        Assert.Equal(
            ["ai.ProviderKind", "ai.ModelId", "ai.BaseUrl", "ai.UseLibraryTones"],
            vm.UnsavedSettings);

        vm.Save();
        Assert.Empty(vm.UnsavedSettings);
    }

    /// <summary>Online research is a CLI capability: the row shows for Claude Code
    /// and Codex only (an API provider would ignore it), and the switch saves.</summary>
    [Fact]
    public void The_research_row_is_for_the_two_CLIs_and_its_switch_saves()
    {
        var vm = NewVm(out var config, out _, out _);
        config.AppConfig.Ai = new AiProviderSettings(AiProviderKind.ClaudeCli, "claude-opus-5", null, WebResearch: false);
        vm.Load();
        Assert.True(vm.IsWebResearchRowVisible);
        Assert.False(vm.WebResearch);

        vm.WebResearch = true;
        Assert.Contains("ai.WebResearch", vm.UnsavedSettings);
        vm.Save();
        Assert.True(config.AppConfig.Ai.WebResearch);

        config.AppConfig.Ai = new AiProviderSettings(AiProviderKind.Claude, "claude-opus-5", null);
        vm.Load();
        Assert.False(vm.IsWebResearchRowVisible);
    }

    // ---- the rest of the provider contract ------------------------------------

    /// <summary>A minimal design service, to watch the save path poke it.</summary>
    private sealed class PokeCountingDesignService : IToneDesignService
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

        public int Pokes;
        public bool IsConfigured => true;
        public event EventHandler? ConfiguredChanged;

        public void NotifyConfigurationChanged()
        {
            Pokes++;
            ConfiguredChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task<ToneDesignResult> DesignAsync(
            ToneDesignRequest request, IProgress<string>? status, CancellationToken ct)
            => throw new NotSupportedException("the settings tab never designs.");
    }

    /// <summary>
    ///     The picker's enabled rows come from the FACTORY. The build-in row is
    ///     disabled because nothing can mint it — not because a list in the view
    ///     model says so — so a factory that starts supporting it enables the row
    ///     with no UI change. Proven by moving the factory's answer and watching
    ///     the row follow.
    /// </summary>
    [Fact]
    public void The_picker_rows_follow_the_factory_and_not_a_hard_coded_list()
    {
        var vm = NewVm(out _, out _, out _);
        Assert.True(Row(vm, AiProviderKind.None).IsAvailable);
        Assert.True(Row(vm, AiProviderKind.Claude).IsAvailable);
        Assert.True(Row(vm, AiProviderKind.OpenAiCompatible).IsAvailable);
        Assert.False(Row(vm, AiProviderKind.BuiltIn).IsAvailable);

        // A build whose factory can mint the reserved slot, and one that has lost
        // Claude: both read off Supports, with no second list to keep in step.
        var config = new FakeConfigurationService();
        var factory = new FakeFactory { Supported = [AiProviderKind.BuiltIn] };
        var later = AiTabRig.Create(config, new FakeSecretStore(), factory);

        Assert.True(Row(later, AiProviderKind.BuiltIn).IsAvailable);
        Assert.False(Row(later, AiProviderKind.Claude).IsAvailable);
        // None is the OFF switch, never a provider: switching the feature off must
        // work whatever the factory can mint.
        Assert.True(Row(later, AiProviderKind.None).IsAvailable);
    }

    private static AiProviderOption Row(AiSettingsViewModel vm, AiProviderKind kind)
        => vm.Providers.First(p => p.Kind == kind);

    /// <summary>The session-only row reads the STORE it was handed. It named the
    /// concrete DPAPI type statically before, so the fake above could not answer
    /// for it and the row was really asserting "is this Windows".</summary>
    [Fact]
    public void The_session_only_row_reads_the_injected_store()
    {
        var config = new FakeConfigurationService();
        var sealing = new FakeSecretStore { IsPersistent = true };
        var forgetful = new FakeSecretStore { IsPersistent = false };

        Assert.False(AiTabRig.Create(config, sealing, new FakeFactory())
            .KeyIsSessionOnly);
        Assert.True(AiTabRig.Create(config, forgetful, new FakeFactory())
            .KeyIsSessionOnly);
        // No store at all: the tab is unavailable, and an unavailable tab must not
        // claim the key is about to be forgotten.
        Assert.False(AiTabRig.Create(config).KeyIsSessionOnly);
    }

    /// <summary>Saving a CHANGED provider pokes the design service, so the "Design
    /// a tone" panel re-reads without polling the config. A save that moved
    /// nothing must not — the panel would republish for a button press.</summary>
    [Fact]
    public void Save_pokes_the_design_service_only_when_the_settings_moved()
    {
        var config = new FakeConfigurationService();
        var design = new PokeCountingDesignService();
        var vm = AiTabRig.Create(config, new FakeSecretStore(), new FakeFactory(), design);

        vm.Load();
        vm.SelectedProvider = Row(vm, AiProviderKind.Claude);
        vm.ModelId = "claude-opus-5";
        vm.Save();
        Assert.Equal(1, design.Pokes);

        vm.Save();
        Assert.Equal(1, design.Pokes);

        vm.ModelId = "claude-sonnet-5";
        vm.Save();
        Assert.Equal(2, design.Pokes);
    }
}
