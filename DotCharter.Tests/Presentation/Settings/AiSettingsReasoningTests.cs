using DotCharter.Tone.Ai;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>
///     Settings ▸ AI ▸ Reasoning (#603): the level loads, saves through <c>AppConfig.Ai</c> and is
///     tracked as an unsaved change; the row is disabled with its reason exactly where the
///     providers would send nothing; and the cross-tab search finds it.
/// </summary>
public sealed class AiSettingsReasoningTests
{
    private readonly FakeLocalization _loc = FakeLocalization.English();

    [Fact]
    public void TheLevel_Loads_Saves_AndIsAnUnsavedChangeUntilSaved()
    {
        var config = new FakeConfigurationService();
        config.AppConfig.Ai = new AiProviderSettings(AiProviderKind.CodexCli, "gpt-6-sol", null, Effort: AiReasoningEffort.Low);
        var vm = AiTabRig.Create(config);
        vm.Load();

        Assert.Equal(AiReasoningEffort.Low, vm.Reasoning.Effort);
        Assert.Equal(_loc["Ai_Effort_Low"], vm.Reasoning.Selected.Label);
        Assert.Equal(
            [_loc["Ai_Effort_Default"], _loc["Ai_Effort_Low"], _loc["Ai_Effort_Medium"], _loc["Ai_Effort_High"]],
            vm.Reasoning.Levels.Select(l => l.Label));
        Assert.Empty(vm.UnsavedSettings);

        vm.Reasoning.Selected = vm.Reasoning.Levels.Single(l => l.Effort == AiReasoningEffort.High);
        Assert.Contains("ai.Effort", vm.UnsavedSettings);
        Assert.Equal(AiReasoningEffort.High, vm.CurrentSettings.Effort);

        vm.Save();

        Assert.Equal(AiReasoningEffort.High, config.AppConfig.Ai.Effort);
        Assert.Equal("gpt-6-sol", config.AppConfig.Ai.ModelId);
        Assert.Empty(vm.UnsavedSettings);
    }

    [Fact]
    public void AClaudeModelOlderThanEffort_DisablesThePicker_AndSaysWhy_WithoutLosingTheChoice()
    {
        var config = new FakeConfigurationService();
        config.AppConfig.Ai = new AiProviderSettings(AiProviderKind.Claude, "claude-opus-5-5", null, Effort: AiReasoningEffort.High);
        var vm = AiTabRig.Create(config);
        vm.Load();
        Assert.True(vm.Reasoning.IsSupported);
        Assert.Equal(_loc["Ai_EffortDesc"], vm.Reasoning.Description);

        vm.ModelId = "claude-haiku-4-5";

        Assert.False(vm.Reasoning.IsSupported);
        Assert.Equal(_loc["Ai_Effort_Unsupported_ClaudeModel"], vm.Reasoning.Description);
        Assert.Equal(AiReasoningEffort.High, vm.Reasoning.Effort);

        vm.ModelId = "claude-sonnet-5-5";
        Assert.True(vm.Reasoning.IsSupported);
    }

    [Fact]
    public void AnOpenAiCompatibleServer_SaysTheFieldIsRetriedWithout()
    {
        var config = new FakeConfigurationService();
        config.AppConfig.Ai = new AiProviderSettings(AiProviderKind.OpenAiCompatible, "qwen3", AiProviderPresets.OllamaBaseUrl);
        var vm = AiTabRig.Create(config);
        vm.Load();

        Assert.True(vm.Reasoning.IsSupported);
        Assert.Equal(_loc["Ai_EffortDesc_OpenAi"], vm.Reasoning.Description);
    }

    [Fact]
    public void TheRow_IsShownForEveryProvider_HiddenWithNone_AndFoundBySearch()
    {
        var config = new FakeConfigurationService();
        config.AppConfig.Ai = new AiProviderSettings(AiProviderKind.ClaudeCli, "", null);
        var vm = AiTabRig.Create(config);
        vm.Load();
        Assert.True(vm.IsEffortRowVisible);

        vm.ApplySearch(_loc["Ai_EffortLabel"].ToLowerInvariant());
        Assert.True(vm.IsEffortRowVisible);
        Assert.False(vm.IsCliPathRowVisible);

        vm.ApplySearch("");
        vm.SelectedProvider = vm.Providers.Single(p => p.Kind == AiProviderKind.None);
        Assert.False(vm.IsEffortRowVisible);
    }
}
