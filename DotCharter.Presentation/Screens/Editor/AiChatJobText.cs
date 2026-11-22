using DotCharter.Core.Utilities;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Ai.Chat;

namespace DotCharter.Presentation.Screens.Editor;

/// <summary>The pane's eight tasks in picker order, each with its label, empty-pane line and hint;
/// chord names has one entry per style (<see cref="AiChatChordNamesKey" />).</summary>
internal static class AiChatJobText
{
    private static readonly (string Label, AiChatScopeKind Kind)[] ScopeTable =
    [
        ("AiChat_Scope_Selection", AiChatScopeKind.Selection),
        ("AiChat_Scope_Section", AiChatScopeKind.Section),
        ("AiChat_Scope_Arrangement", AiChatScopeKind.Arrangement)
    ];

    private static readonly JobText[] Table =
    [
        new(AiChartJob.FingeringOnly, "AiChat_Job_FingeringOnly", "AiChat_Empty_FingeringOnly", "AiChat_Hint_FingeringOnly"),
        new(AiChartJob.FhpAndFingering, "AiChat_Job_Fingering", "AiChat_Empty_Fingering", "AiChat_Hint_Fingering"),
        new(AiChartJob.FhpOnly, "AiChat_Job_FhpOnly", "AiChat_Empty_FhpOnly", "AiChat_Hint_FhpOnly"),
        new(AiChartJob.ChordNames, "AiChat_Job_ChordNamesEof", "AiChat_Empty_ChordNames", "AiChat_Hint_ChordNames",
            ChordNamingStyle.Eof),
        new(AiChartJob.ChordNames, "AiChat_Job_ChordNamesMusical", "AiChat_Empty_ChordNames", "AiChat_Hint_ChordNames",
            ChordNamingStyle.Musical),
        new(AiChartJob.SectionsAndPhrases, "AiChat_Job_Structure", "AiChat_Empty_Structure", "AiChat_Hint_Structure"),
        new(AiChartJob.Explain, "AiChat_Job_Explain", "AiChat_Empty_Explain", "AiChat_Hint_Explain"),
        new(AiChartJob.Tones, "AiChat_Job_Tones", "AiChat_Empty_Tones", "AiChat_Hint_Tones")
    ];

    /// <summary>The Task picker's entries, keyed by their <see cref="AiChartJob" />.</summary>
    public static IReadOnlyList<IAiChatOption> Options(ILocalizationService localization) =>
        [.. Table.Select(t => new AiChatOptionItem(localization[t.Label], "", t.Key) { LabelKey = t.Label })];

    /// <summary>The Scope picker's entries, keyed by their <see cref="AiChatScopeKind" />.</summary>
    public static IReadOnlyList<IAiChatOption> ScopeOptions(ILocalizationService localization) =>
        [.. ScopeTable.Select(s => new AiChatOptionItem(localization[s.Label], "", s.Kind) { LabelKey = s.Label })];

    /// <summary>Reads every picker label again after a language switch; the items, and so the selection, stay.</summary>
    public static void Relabel(IEnumerable<IAiChatOption> options, ILocalizationService localization)
    {
        foreach (var option in options.OfType<AiChatOptionItem>()) option.Relabel(localization);
    }

    /// <summary>The task and chord style a key names; an unknown key is the default task.</summary>
    public static (AiChartJob Job, ChordNamingStyle Style) Resolve(object? key) => key switch
    {
        AiChartJob job => (job, ChordNamingStyle.Eof),
        AiChatChordNamesKey names => (AiChartJob.ChordNames, names.Style),
        _ => (Table[0].Job, ChordNamingStyle.Eof)
    };

    /// <summary>The picker entry's key for a task, with the style of the chord names task.</summary>
    public static object KeyOf(AiChartJob job, ChordNamingStyle style = ChordNamingStyle.Eof)
        => job == AiChartJob.ChordNames ? new AiChatChordNamesKey(style) : job;

    /// <summary>The line a fix task sends when the box is empty; null for a task that needs the charter's words.</summary>
    public static string? DefaultInstructionKey(AiChartJob job) => job switch
    {
        AiChartJob.FingeringOnly => "AiChat_Default_FingeringOnly",
        AiChartJob.FhpAndFingering => "AiChat_Default_Fingering",
        AiChartJob.FhpOnly => "AiChat_Default_FhpOnly",
        AiChartJob.ChordNames => "AiChat_Default_ChordNames",
        _ => null
    };

    public static string EmptyKey(AiChartJob job) => Row(job).Empty;

    public static string HintKey(AiChartJob job) => Row(job).Hint;

    private static JobText Row(AiChartJob job) => Array.Find(Table, t => t.Job == job) ?? Table[0];

    private sealed record JobText(AiChartJob Job, string Label, string Empty, string Hint, ChordNamingStyle? Style = null)
    {
        public object Key => Style is { } style ? new AiChatChordNamesKey(style) : Job;
    }
}

/// <summary>The picker key of a chord names entry.</summary>
public sealed record AiChatChordNamesKey(ChordNamingStyle Style);
