using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Services.Ai.Chart;

namespace DotCharter.Presentation.Screens.Editor;

/// <summary>The pane's five tasks in picker order (fingering only first), each
/// with its words: the label, the empty pane's line (what the task does, what to say) and the hint.</summary>
internal static class AiChatJobText
{
    private static readonly JobText[] Table =
    [
        new(AiChartJob.FingeringOnly, "AiChat_Job_FingeringOnly", "AiChat_Empty_FingeringOnly", "AiChat_Hint_FingeringOnly"),
        new(AiChartJob.FhpAndFingering, "AiChat_Job_Fingering", "AiChat_Empty_Fingering", "AiChat_Hint_Fingering"),
        new(AiChartJob.SectionsAndPhrases, "AiChat_Job_Structure", "AiChat_Empty_Structure", "AiChat_Hint_Structure"),
        new(AiChartJob.Explain, "AiChat_Job_Explain", "AiChat_Empty_Explain", "AiChat_Hint_Explain"),
        new(AiChartJob.Tones, "AiChat_Job_Tones", "AiChat_Empty_Tones", "AiChat_Hint_Tones")
    ];

    /// <summary>The Task picker's entries, keyed by their <see cref="AiChartJob" />.</summary>
    public static IReadOnlyList<IAiChatOption> Options(ILocalizationService localization) =>
        [.. Table.Select(t => new AiChatOptionItem(localization[t.Label], "", t.Job))];

    /// <summary>A task the picker does not offer takes the default task's words.</summary>
    public static string EmptyKey(AiChartJob job) => Row(job).Empty;

    public static string HintKey(AiChartJob job) => Row(job).Hint;

    private static JobText Row(AiChartJob job) => Array.Find(Table, t => t.Job == job) ?? Table[0];

    private sealed record JobText(AiChartJob Job, string Label, string Empty, string Hint);
}
