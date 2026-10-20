using DotCharter.Presentation.Screens.Settings;
using DotCharter.Tone.Ai;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>
///     Settings ▸ AI, the Codex CLI row in DotCharter's picker (RigLab has its
///     own view model, pinned by <c>RigLabAiSettingsTests</c>), and the provider
///     table <see cref="AiProviderShape" /> that decides every provider's rows.
/// </summary>
public sealed class AiSettingsCodexCliTests
{
    private static AiSettingsViewModel NewViewModel(bool codexSupported = true) =>
        AiTabRig.Create(new FakeConfigurationService(), new InMemoryAiSecretStore(), new FakeFactory(codexSupported));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThePicker_OffersCodex_EnabledByTheFactory(bool supported)
    {
        var row = Assert.Single(NewViewModel(supported).Providers, p => p.Kind == AiProviderKind.CodexCli);
        Assert.Equal(supported, row.IsAvailable);
    }

    /// <summary>Codex gets its OWN note, path row words and presets, and none of
    /// Claude Code's — the note is a claim about whose usage a design spends.</summary>
    [Fact]
    public void PickingCodex_ShowsItsOwnNotePathAndPresets()
    {
        var vm = NewViewModel();
        vm.SelectedProvider = vm.Providers.First(p => p.Kind == AiProviderKind.CodexCli);

        Assert.True(vm.IsCodexCliNoteVisible);
        Assert.False(vm.IsClaudeCliNoteVisible);
        Assert.True(vm.IsCliPathRowVisible);
        Assert.True(vm.AreCodexModelPresetsVisible);
        Assert.False(vm.AreClaudeModelPresetsVisible);
        Assert.False(vm.IsApiKeyRowVisible);
        Assert.True(vm.IsWebResearchRowVisible);
    }

    /// <summary>The path and model rows say "Codex", not "Claude Code".</summary>
    [Fact]
    public void ThePathAndModelRows_ChangeTheirWordsWithTheProvider()
    {
        var vm = NewViewModel();
        vm.ProviderKind = AiProviderKind.ClaudeCli;
        var claudeLabel = vm.CliPathLabel;
        var claudeModelDesc = vm.ModelDesc;

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.ProviderKind = AiProviderKind.CodexCli;

        Assert.NotEqual(claudeLabel, vm.CliPathLabel);
        Assert.NotEqual(claudeModelDesc, vm.ModelDesc);
        Assert.Contains(nameof(vm.CliPathLabel), raised);
        Assert.Contains(nameof(vm.CliPathDesc), raised);
        Assert.Contains(nameof(vm.ModelDesc), raised);
    }

    /// <summary>An empty model box means "Codex's own default", so picking Codex
    /// seeds nothing and Test is available with the box empty — unlike the
    /// OpenAI-compatible server, which cannot guess a model.</summary>
    [Fact]
    public void AnEmptyModel_IsValidForCodexAlone()
    {
        var vm = NewViewModel();
        vm.SelectedProvider = vm.Providers.First(p => p.Kind == AiProviderKind.CodexCli);
        vm.ModelId = "";
        Assert.True(vm.TestConnectionCommand.CanExecute(null));

        vm.SelectedProvider = vm.Providers.First(p => p.Kind == AiProviderKind.OpenAiCompatible);
        vm.ModelId = "";
        Assert.False(vm.TestConnectionCommand.CanExecute(null));
    }

    /// <summary>The owner's call (2026-10-07): Codex designs tones with GPT-6 Luna.
    /// Picked fresh it seeds Luna; picked after Claude Code it replaces Claude
    /// Code's untouched default rather than handing a Claude model id to Codex.</summary>
    [Fact]
    public void Codex_DefaultsToGpt6Luna_EvenWhenPickedAfterClaudeCode()
    {
        var vm = NewViewModel();
        vm.SelectedProvider = vm.Providers.First(p => p.Kind == AiProviderKind.CodexCli);
        Assert.Equal("gpt-6-luna", vm.ModelId);

        vm.SelectedProvider = vm.Providers.First(p => p.Kind == AiProviderKind.ClaudeCli);
        Assert.Equal("claude-haiku-5-5", vm.ModelId);
        vm.SelectedProvider = vm.Providers.First(p => p.Kind == AiProviderKind.CodexCli);
        Assert.Equal("gpt-6-luna", vm.ModelId);
    }

    /// <summary>An uninstalled Codex bounces back and says why — in Codex's words,
    /// not Claude Code's.</summary>
    [Fact]
    public void PickingAnUninstalledCodex_BouncesBackWithItsOwnReason()
    {
        var vm = NewViewModel(codexSupported: false);
        vm.SelectedProvider = vm.Providers.First(p => p.Kind == AiProviderKind.CodexCli);

        Assert.Equal(AiProviderKind.None, vm.ProviderKind);
        Assert.Equal("Ai_Provider_CodexCliMissing", AiProviderShape.For(AiProviderKind.CodexCli).MissingKey);
        Assert.True(vm.HasStatus);
        Assert.NotEqual(AiProviderShape.For(AiProviderKind.ClaudeCli).MissingKey,
            AiProviderShape.For(AiProviderKind.CodexCli).MissingKey);
    }

    /// <summary>The whole table, one row per provider, so a new flag cannot quietly
    /// turn on for a provider it does not belong to.</summary>
    [Theory]
    [InlineData(AiProviderKind.None, false, false, false, false, false, false)]
    [InlineData(AiProviderKind.Claude, true, false, false, false, false, false)]
    [InlineData(AiProviderKind.OpenAiCompatible, true, true, false, false, false, false)]
    [InlineData(AiProviderKind.ClaudeCli, false, false, true, true, true, false)]
    [InlineData(AiProviderKind.CodexCli, false, false, true, true, false, true)]
    [InlineData(AiProviderKind.BuiltIn, false, false, false, false, false, false)]
    public void TheProviderTable(AiProviderKind kind, bool key, bool baseUrl, bool cliPath, bool research,
        bool claudeNote, bool codexNote)
    {
        var shape = AiProviderShape.For(kind);
        Assert.Equal(key, shape.ShowKey);
        Assert.Equal(baseUrl, shape.ShowBaseUrl);
        Assert.Equal(cliPath, shape.ShowCliPath);
        Assert.Equal(research, shape.ShowWebResearch);
        Assert.Equal(claudeNote, shape.ShowClaudeCliNote);
        Assert.Equal(codexNote, shape.ShowCodexCliNote);
        Assert.Equal(claudeNote, shape.ShowClaudePresets);
        Assert.Equal(codexNote, shape.ShowCodexPresets);
        Assert.Equal(kind == AiProviderKind.CodexCli, shape.ModelIsOptional);
        Assert.Equal(kind is AiProviderKind.Claude or AiProviderKind.ClaudeCli or AiProviderKind.CodexCli,
            shape.DefaultModel is not null);
    }

    private sealed class FakeFactory(bool codexSupported) : IToneDesignModelFactory
    {
        public IToneDesignModel Create(AiProviderSettings settings) => throw new NotSupportedException();

        public bool Supports(AiProviderKind kind) => kind switch
        {
            AiProviderKind.CodexCli => codexSupported,
            AiProviderKind.Claude or AiProviderKind.OpenAiCompatible or AiProviderKind.ClaudeCli => true,
            _ => false
        };
    }
}
