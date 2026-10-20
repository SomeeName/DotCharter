using DotCharter.Localization;
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Editor;

/// <summary>The chat's fingering default, remembered pick and reason (<see cref="AiFingeringDefaults" />),
/// and the reasoning label beside the model.</summary>
public sealed class AiChatTaskDefaults(IConfigurationService config, ILocalizationService localization)
{
    /// <summary>The task to switch to for this arrangement type, or null to keep
    /// <paramref name="current" />: a non-fingering task is the charter's switch away and stays.</summary>
    public AiChartJob? StartOn(AiChartJob current, bool isBass)
        => AiFingeringDefaults.IsFingeringJob(current) ? AiFingeringDefaults.For(config.LoadAppConfig(), isBass) : null;

    public void Picked(AiChartJob job, bool isBass)
    {
        var app = config.LoadAppConfig();
        if (AiFingeringDefaults.Remember(app, isBass, job)) config.SaveAppConfig(app);
    }

    public string Note(AiChartJob job, bool isBass)
        => AiFingeringDefaults.NoteKey(job, isBass) is { } key ? localization[key] : "";

    /// <summary>Settings ▸ AI's level, or Default where the request would not carry one.</summary>
    public string EffortLabel(string modelId)
    {
        var settings = config.LoadAppConfig().Ai ?? AiProviderSettings.Disabled;
        var level = AiReasoning.Support(settings.Kind, modelId) == AiReasoningUnsupported.None
            ? settings.Effort
            : AiReasoningEffort.Default;
        return localization.Format("AiChat_Effort", localization[AiReasoningText.Key(level)]);
    }
}
