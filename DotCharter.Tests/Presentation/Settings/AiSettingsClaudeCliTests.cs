using DotCharter.Presentation.Screens.Settings;
using DotCharter.Tone.Ai;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>
///     Settings ▸ AI, the Claude Code (local CLI) row. The view model only — the
///     tab's markup is covered by the construction smoke test, and none of this
///     needs a window.
/// </summary>
public sealed class AiSettingsClaudeCliTests
{
    private static AiSettingsViewModel NewViewModel(bool cliSupported = true) =>
        AiTabRig.Create(new FakeConfigurationService(), new InMemoryAiSecretStore(), new FakeFactory(cliSupported));

    /// <summary>
    ///     The API-key row is HIDDEN for the local CLI: it takes no key, and a
    ///     password box that quietly ignores what is pasted into it is worse than no
    ///     box. The other two providers keep theirs — the assertion is the
    ///     DIFFERENCE, so it cannot pass by hiding the row for everyone.
    /// </summary>
    [Fact]
    public void TheApiKeyRow_IsHiddenForTheLocalCli_AndShownForTheOthers()
    {
        var vm = NewViewModel();

        vm.ProviderKind = AiProviderKind.Claude;
        Assert.True(vm.Shape.ShowKey);

        vm.ProviderKind = AiProviderKind.OpenAiCompatible;
        Assert.True(vm.Shape.ShowKey);

        vm.ProviderKind = AiProviderKind.ClaudeCli;
        Assert.False(vm.Shape.ShowKey);
    }

    /// <summary>The subscription note and the path box are shown for this kind ONLY
    /// — the note is the one line that tells the user whose usage they are
    /// spending, so a kind that showed it wrongly would be a false cost claim.</summary>
    [Fact]
    public void TheNoteAndThePathRow_BelongToTheLocalCliAlone()
    {
        var vm = NewViewModel();

        foreach (var kind in new[]
                 {
                     AiProviderKind.None, AiProviderKind.Claude,
                     AiProviderKind.OpenAiCompatible, AiProviderKind.BuiltIn
                 })
        {
            vm.ProviderKind = kind;
            Assert.False(vm.Shape.ShowClaudeCliNote);
            Assert.False(vm.Shape.ShowCliPath);
        }

        vm.ProviderKind = AiProviderKind.ClaudeCli;
        Assert.True(vm.Shape.ShowClaudeCliNote);
        Assert.True(vm.Shape.ShowCliPath);
        // …and the base-URL row stays the OpenAI-compatible one, even though both
        // persist through the same field.
        Assert.False(vm.Shape.ShowBaseUrl);
    }

    /// <summary>The picker offers the row, and offers it disabled on a machine with
    /// no binary — the enabled flag comes from the FACTORY, never from a list kept
    /// here.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheProviderPicker_CarriesTheCliRow_EnabledByTheFactory(bool supported)
    {
        var row = Assert.Single(
            NewViewModel(supported).Providers, p => p.Kind == AiProviderKind.ClaudeCli);
        Assert.Equal(supported, row.IsAvailable);
    }

    /// <summary>Picking it with an empty model box fills in the shipping default,
    /// the same courtesy the API Claude row gets.</summary>
    [Fact]
    public void PickingIt_SeedsTheDefaultModelId()
    {
        var vm = NewViewModel();
        vm.SelectedProvider = vm.Providers.First(p => p.Kind == AiProviderKind.ClaudeCli);

        Assert.Equal(AiProviderKind.ClaudeCli, vm.ProviderKind);
        Assert.Equal(AiSettingsViewModel.DefaultClaudeModel, vm.ModelId);
    }

    /// <summary>The presets fill in the CURRENT generation. Literals on purpose: a
    /// comparison against the same constant the command reads passes whatever the
    /// constant says.</summary>
    [Fact]
    public void TheModelPresets_SetTheThreeOfferedIds()
    {
        var vm = NewViewModel();
        vm.UseClaudeSonnetCommand.Execute(null);
        Assert.Equal("claude-sonnet-5-5", vm.ModelId);
        Assert.Equal(ClaudeCliToneModel.AlternateModelId, vm.ModelId);
        vm.UseClaudeOpusCommand.Execute(null);
        Assert.Equal("claude-opus-5-5", vm.ModelId);
        Assert.Equal(ClaudeCliToneModel.OpusModelId, vm.ModelId);
        vm.UseClaudeHaikuCommand.Execute(null);
        Assert.Equal("claude-haiku-5-5", vm.ModelId);
        Assert.Equal(ClaudeCliToneModel.DefaultModelId, vm.ModelId);
    }

    /// <summary>The owner's call (2026-10-07): Claude Code designs tones with Haiku
    /// 5.5 unless told otherwise. The API provider keeps Opus 5.5.</summary>
    [Fact]
    public void ClaudeCode_DefaultsToHaiku55_TheApiToOpus55()
    {
        Assert.Equal("claude-haiku-5-5", AiSettingsViewModel.DefaultClaudeModel);
        Assert.Equal("claude-haiku-5-5", ClaudeCliToneModel.DefaultModelId);
        Assert.Equal("claude-opus-5-5", ClaudeToneModel.DefaultModelId);

        var vm = NewViewModel();
        vm.SelectedProvider = vm.Providers.First(p => p.Kind == AiProviderKind.ClaudeCli);
        Assert.Equal("claude-haiku-5-5", vm.ModelId);
        vm.ModelId = "";
        vm.SelectedProvider = vm.Providers.First(p => p.Kind == AiProviderKind.Claude);
        Assert.Equal("claude-opus-5-5", vm.ModelId);
    }

    /// <summary>
    ///     A model id the user already SAVED is theirs: a new default changes what an
    ///     empty box is filled with, never a stored choice. Loaded, re-picked and
    ///     saved again, the previous generation's id comes back unchanged.
    /// </summary>
    [Theory]
    [InlineData(AiProviderKind.ClaudeCli, "claude-opus-5")]
    [InlineData(AiProviderKind.Claude, "claude-sonnet-5")]
    public void ASavedModelId_IsNeverRewrittenByTheNewDefault(AiProviderKind kind, string saved)
    {
        var service = new FakeConfigurationService();
        service.AppConfig.Ai = new AiProviderSettings(kind, saved, null);
        var vm = AiTabRig.Create(service, new InMemoryAiSecretStore(), new FakeFactory(true));

        vm.Load();
        Assert.Equal(saved, vm.ModelId);

        vm.SelectedProvider = vm.Providers.First(p => p.Kind == kind);
        vm.Save();
        Assert.Equal(saved, vm.ModelId);
        Assert.Equal(saved, service.AppConfig.Ai.ModelId);
    }

    [Fact]
    public void TheCodexPresets_SetGpt6SolAndLuna()
    {
        var vm = NewViewModel();
        vm.ProviderKind = AiProviderKind.CodexCli;

        vm.UseCodexSolCommand.Execute(null);
        Assert.Equal("gpt-6-sol", vm.ModelId);
        vm.UseCodexLunaCommand.Execute(null);
        Assert.Equal("gpt-6-luna", vm.ModelId);
    }

    /// <summary>Each CLI's presets are its OWN: a GPT id offered to Claude Code (or a
    /// Claude id to Codex) is a button that fails the first design. Driven over
    /// every kind so it cannot pass by showing both for one.</summary>
    [Theory]
    [InlineData(AiProviderKind.None, false, false)]
    [InlineData(AiProviderKind.Claude, false, false)]
    [InlineData(AiProviderKind.OpenAiCompatible, false, false)]
    [InlineData(AiProviderKind.ClaudeCli, true, false)]
    [InlineData(AiProviderKind.BuiltIn, false, false)]
    [InlineData(AiProviderKind.CodexCli, false, true)]
    public void EachCli_ShowsOnlyItsOwnModelPresets(AiProviderKind kind, bool claude, bool codex)
    {
        var vm = NewViewModel();
        vm.ProviderKind = kind;

        Assert.Equal(claude, vm.AreClaudeModelPresetsVisible);
        Assert.Equal(codex, vm.AreCodexModelPresetsVisible);
    }

    /// <summary>The presets are part of the model row, so a search that hides the
    /// row hides them too.</summary>
    [Fact]
    public void TheCodexPresets_FollowTheModelRow_UnderSearch()
    {
        var vm = NewViewModel();
        vm.ProviderKind = AiProviderKind.CodexCli;
        Assert.True(vm.AreCodexModelPresetsVisible);

        vm.ApplySearch("zzzz-no-such-setting");
        Assert.False(vm.IsModelRowVisible);
        Assert.False(vm.AreCodexModelPresetsVisible);
    }

    /// <summary>The path box persists through <c>BaseUrl</c>, which this kind reuses
    /// as a path. Round-tripped through Save/Load because that reuse is exactly the
    /// kind of thing a later refactor drops on the floor.</summary>
    [Fact]
    public void TheCliPath_RoundTripsThroughSaveAndLoad()
    {
        var service = new FakeConfigurationService();
        var vm = AiTabRig.Create(service, new InMemoryAiSecretStore(), new FakeFactory(true));

        vm.SelectedProvider = vm.Providers.First(p => p.Kind == AiProviderKind.ClaudeCli);
        vm.BaseUrl = @"C:\bin\claude.exe";
        vm.Save();

        Assert.Equal(AiProviderKind.ClaudeCli, service.AppConfig.Ai.Kind);
        Assert.Equal(@"C:\bin\claude.exe", service.AppConfig.Ai.BaseUrl);

        vm.Load();
        Assert.Equal(@"C:\bin\claude.exe", vm.BaseUrl);
        Assert.Equal(AiProviderKind.ClaudeCli, vm.ProviderKind);
    }

    // ---- fakes -----------------------------------------------------------------

    private sealed class FakeFactory(bool cliSupported) : IToneDesignModelFactory
    {
        public IToneDesignModel Create(AiProviderSettings settings)
            => throw new NotSupportedException();

        public bool Supports(AiProviderKind kind) => kind switch
        {
            AiProviderKind.ClaudeCli => cliSupported,
            AiProviderKind.Claude or AiProviderKind.OpenAiCompatible => true,
            _ => false
        };
    }
}
