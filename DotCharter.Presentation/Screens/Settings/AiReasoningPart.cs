using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Tone.Ai;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>One level of the Reasoning picker.</summary>
public sealed record AiEffortOption(AiReasoningEffort Effort, string Label) : IAiEffortOption;

/// <summary>
/// Settings ▸ AI ▸ Reasoning: the level, and whether the chosen provider and model
///     can take one at all (<see cref="AiReasoning.Support" />, the same rule the providers apply
///     when they build a request). Where they cannot, the picker is disabled and the row prints why.
/// </summary>
public sealed partial class AiReasoningPart : ObservableObject, IAiReasoningPart
{
    private readonly ILocalizationService _localization;

    [ObservableProperty] private AiEffortOption _selected;
    [ObservableProperty] private bool _isSupported = true;
    [ObservableProperty] private string _description;

    public AiReasoningPart(ILocalizationService localization)
    {
        _localization = localization;
        Levels = [.. AiReasoningText.Levels.Select(e => new AiEffortOption(e, localization[AiReasoningText.Key(e)]))];
        _selected = Levels[0];
        _description = localization["Ai_EffortDesc"];
    }

    public IReadOnlyList<AiEffortOption> Levels { get; }

    IReadOnlyList<IAiEffortOption> IAiReasoningPart.Levels => Levels;

    IAiEffortOption IAiReasoningPart.Selected
    {
        get => Selected;
        set => Selected = value as AiEffortOption ?? Levels[0];
    }

    /// <summary>The persisted value: what the change set tracks and the settings record carries.</summary>
    public AiReasoningEffort Effort
    {
        get => Selected.Effort;
        set => Selected = Levels.FirstOrDefault(l => l.Effort == value) ?? Levels[0];
    }

    /// <summary>Re-decides support for the provider and model on screen. The choice itself is
    /// kept either way, so switching back to a model that takes it restores it.</summary>
    public void Refresh(AiProviderKind kind, string modelId)
    {
        IsSupported = AiReasoningText.Support(kind, modelId) == AiReasoningUnsupported.None;
        Description = _localization[AiReasoningText.DescriptionKey(kind, modelId)];
    }
}
