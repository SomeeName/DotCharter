using DotCharter.Localization;
using DotCharter.Services.Gameplay.Scoring;

namespace DotCharter.Presentation.Shared.Library;

/// <summary>The RECORD filter's ladder — any / at least grade X, best first / full combo only — as ONE
/// list the combo, the filter and the chip share, over the scorer's own <see cref="LetterGrade" /> scale.</summary>
public static class PlayRecordFilter
{
    public static IReadOnlyList<LetterGrade> GradesBestFirst { get; } =
        Enum.GetValues<LetterGrade>().OrderByDescending(g => (int)g).ToArray();

    /// <summary>Index 0 is "any"; 1..<see cref="GradesBestFirst" />.Count are grade floors; this is the last.</summary>
    public static int FullComboIndex => GradesBestFirst.Count + 1;

    public static int OptionCount => FullComboIndex + 1;

    /// <summary>The grade floor an index demands; null for "any" and "full combo only".</summary>
    public static LetterGrade? MinGradeFor(int index)
        => index >= 1 && index <= GradesBestFirst.Count ? GradesBestFirst[index - 1] : null;

    public static bool IsFullComboOnly(int index) => index == FullComboIndex;

    public static IReadOnlyList<string> OptionLabels(ILocalizationService loc)
    {
        var labels = new List<string>(OptionCount) { loc["PlayMode_Filter_AnyRecord"] };
        foreach (var grade in GradesBestFirst)
            labels.Add(loc.Format("PlayMode_Filter_GradeAtLeastFmt", GradeCalculator.Label(grade)));
        labels.Add(loc["PlayMode_Filter_FullComboOnly"]);
        return labels;
    }
}
