using DotCharter.Services.Export;
using DotCharter.Services.Export.Package;

namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     Warns when one section label covers most of an arrangement's playable sections.
///     Rocksmith's recommendations read section names, so a chart labelled "intro" throughout
///     gets poor ones. Names group by the label the game shows, the export's own
///     <see cref="RsManifestGenerator.GetSectionUiName" />: case and digits do not count, so
///     "Verse" and "verse2" are one label. No-guitar gaps are no part of the song to label and
///     count nowhere, which also keeps the closing noguitar an XML round trip adds from
///     tipping a chart over the line.
/// </summary>
public sealed class SectionNameVarietyRule : IArrangementRule
{
    /// <summary>Fewer sections than this never warn: a short song may honestly be riff, riff, riff, riff.</summary>
    public const int MinSections = 5;

    /// <summary>The dominant label must cover MORE than this share. Finished charts in the local corpus
    /// peak at 55 % (a chorus-heavy song), so more than half would flag real charts.</summary>
    public const double MaxDominantShare = 2.0 / 3.0;

    private static readonly string NoGuitarLabel = RsManifestGenerator.GetSectionUiName(EndNoGuitarSection.Name);

    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var sections = context.Arrangement.EventPoints
            .Where(point => !string.IsNullOrWhiteSpace(point.Section))
            .Select(point => (Point: point, Label: RsManifestGenerator.GetSectionUiName(point.Section)))
            .Where(section => section.Label != NoGuitarLabel)
            .OrderBy(section => section.Point.Position)
            .ToList();
        if (sections.Count < MinSections) return;

        var dominant = sections.GroupBy(section => section.Label, StringComparer.Ordinal).MaxBy(group => group.Count())!;
        var count = dominant.Count();
        if (count <= sections.Count * MaxDominantShare) return;

        var first = dominant.First().Point;
        issues.Add(ArrangementIssues.Make(ArrangementIssueType.SectionNameVariety, "Validation_SectionNamesNotVaried",
            first.GetTimeMs(context.Beats), args: [count, sections.Count, first.Section.Trim()]));
    }
}
