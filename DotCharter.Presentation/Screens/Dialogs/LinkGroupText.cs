using DotCharter.Core.Domain.Project;
using DotCharter.Core.Linking;
using System.Globalization;
using DotCharter.Core.Domain.Primitives;
using DotCharter.Localization;
using DotCharter.Services.Editing.Linking;

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

    /// <summary>
    ///     A near match's first difference in charter words: "M30, beat 4: sustain 1/16 shorter",
    ///     "M209, beat 1: FHP 12 vs 11".
    /// </summary>
    public static string Mismatch(ILocalizationService localization, LinkMismatch mismatch)
    {
        var bar = Bars(mismatch.Measure, 1);
        var difference = mismatch.Difference;
        var beat = difference?.Beat is { } at ? Beat(at) : "1";
        return mismatch.Kind switch
        {
            LinkMismatchKind.Edge => localization.Format("LinkNear_Edge", bar),
            LinkMismatchKind.Join => localization.Format("LinkNear_Join", bar),
            LinkMismatchKind.Refused => localization.Format("LinkNear_Refused", bar),
            _ when difference == null => localization.Format("LinkNear_Sound", bar, beat),
            _ => difference.Kind switch
            {
                MeasureContentDifferenceKind.Measures => localization.Format("LinkNear_Measures", bar),
                MeasureContentDifferenceKind.Sustain when difference.SustainChange is { } change =>
                    localization.Format(change < Fraction.Zero ? "LinkNear_SustainShorter" : "LinkNear_SustainLonger", bar, beat,
                        NoteValue(change < Fraction.Zero ? -change : change)),
                MeasureContentDifferenceKind.Sustain => localization.Format("LinkNear_SustainHeld", bar, beat),
                MeasureContentDifferenceKind.FretHandPosition => localization.Format("LinkNear_Fhp", bar, beat,
                    Fret(localization, difference.Reference), Fret(localization, difference.Other)),
                MeasureContentDifferenceKind.HandShape => localization.Format("LinkNear_HandShape", bar, beat),
                MeasureContentDifferenceKind.FretHandMute => localization.Format("LinkNear_Mute", bar, beat),
                MeasureContentDifferenceKind.Sound => localization.Format("LinkNear_Sound", bar, beat),
                _ => localization.Format("LinkNear_Other", bar)
            }
        };
    }

    /// <summary>1-based beat in the measure: "4", or "2+1/2" between beats.</summary>
    private static string Beat(Fraction beat)
    {
        var whole = (int)Math.Floor(beat.DoubleValue);
        var part = beat - whole;
        return part == Fraction.Zero
            ? (whole + 1).ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{whole + 1}+{part}");
    }

    /// <summary>A length in whole notes as a note value: "1/16", "3/8", "1".</summary>
    private static string NoteValue(Fraction wholeNotes) =>
        wholeNotes.Denominator == 1 ? wholeNotes.Numerator.ToString(CultureInfo.InvariantCulture) : wholeNotes.ToString();

    private static string Fret(ILocalizationService localization, string fret) => fret == "-" ? localization["LinkNear_None"] : fret;
}
