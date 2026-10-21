using DotCharter.Core.Domain.Project;
using DotCharter.Core.Linking;
using DotCharter.Localization;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>How the linked-measures dialogs print a group: per-arrangement counts, measure ranges, a name.</summary>
internal static class LinkGroupText
{
    /// <summary>"Lead ×3, Rhythm ×3", in the order the arrangements first appear.</summary>
    public static string PerArrangement(ILocalizationService localization, IEnumerable<Arrangement?> arrangements) =>
        string.Join(", ", arrangements.OfType<Arrangement>()
            .GroupBy(a => a)
            .Select(g => localization.Format("LinkGroups_PerArrangement", g.Key.Name, g.Count())));

    /// <summary>"M5" or "M5–M8", as the timeline's measure pills print them, from a 0-based first measure.</summary>
    public static string Bars(int first, int count) => MeasureRangeLabel.Format(first + 1, count);

    public static string Name(ILocalizationService localization, string name) =>
        string.IsNullOrWhiteSpace(name) ? localization["LinkGroups_Unnamed"] : name;
}
