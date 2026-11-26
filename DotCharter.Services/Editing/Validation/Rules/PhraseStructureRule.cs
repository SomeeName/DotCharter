namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     The shape of a chart's phrases and sections, as charters build it. Findings only: a phrase or
///     section the charter placed is never moved or renamed.
///     <list type="bullet">
///         <item>D80 (<see cref="ArrangementIssueType.PhraseTooLong" />): a phrase over
///         <see cref="MaxMeasures" /> measures. Rocksmith levels a song up phrase by phrase, so a very long one
///         is a long wait; EOF, DLC Builder and Charter flag none, so only a phrase twice the usual eight is judged.</item>
///         <item>D81 (<see cref="ArrangementIssueType.PhraseTooShort" />): a phrase under
///         <see cref="MinMeasures" /> measures.</item>
/// ///         <item>D83 (<see cref="ArrangementIssueType.SectionWithoutPhrase" />): a section with no phrase
///         at its position. Rocksmith starts a section at a phrase.</item>
///         <item>D84 (<see cref="ArrangementIssueType.NoGuitarSectionSplit" />): a silent stretch split
///         into several noguitar sections, where one covers it.</item>
///     </list>
///     A phrase or section off the bar line is no finding: charters place one there on purpose (EOF's moveR).
///     A phrase whose start has no fret hand position is E06's finding (<see cref="PhraseAnchorRule" />).
///     Measure counts read the beat map's bar lines; a beat map that marks none is not judged on them.
/// </summary>
public sealed class PhraseStructureRule : IArrangementRule
{
    public const int MaxMeasures = 16;
    public const int MinMeasures = 2;

    private const string NoGuitar = "noguitar";

    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var beats = context.Beats;
        var sections = context.Arrangement.EventPoints
            .Where(point => !string.IsNullOrWhiteSpace(point.Section))
            .OrderBy(point => point.Position)
            .ToList();

        ReportSectionsWithoutPhrase(context, sections, issues);
        ReportSplitSilence(context, sections, issues);
        if (!beats.Any(beat => beat.IsMeasure)) return;

        ReportPhraseLengths(context, issues);
    }

    private static void ReportPhraseLengths(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var phrases = context.Phrases;
        for (var i = 0; i + 1 < phrases.Count; i++)
        {
            if (PhraseMarkers.IsStructural(phrases[i].Name)) continue;
            var measures = MeasuresBetween(context.Beats, phrases[i].Position, phrases[i + 1].Position);
            var at = phrases[i].Position.GetPositionMs(context.Beats);
            if (measures > MaxMeasures)
            {
                issues.Add(ArrangementIssues.Make(ArrangementIssueType.PhraseTooLong, "Validation_PhraseTooLong", at,
                    args: [phrases[i].Name, measures, MaxMeasures]));
            }
            else if (measures < MinMeasures)
            {
                issues.Add(ArrangementIssues.Make(ArrangementIssueType.PhraseTooShort, "Validation_PhraseTooShort", at,
                    args: [phrases[i].Name, MinMeasures]));
            }
        }
    }

    private static void ReportSectionsWithoutPhrase(
        ArrangementValidationContext context, List<EventPoint> sections, ICollection<ArrangementIssue> issues)
    {
        var phraseAt = context.Phrases.Select(phrase => phrase.Position).ToHashSet();
        foreach (var section in sections)
        {
            if (phraseAt.Contains(section.Position)) continue;
            issues.Add(ArrangementIssues.Make(ArrangementIssueType.SectionWithoutPhrase, "Validation_SectionWithoutPhrase",
                section.GetTimeMs(context.Beats), args: [section.Section.Trim()]));
        }
    }

    private static void ReportSplitSilence(
        ArrangementValidationContext context, List<EventPoint> sections, ICollection<ArrangementIssue> issues)
    {
        for (var i = 1; i < sections.Count; i++)
        {
            if (!IsNoGuitar(sections[i].Section) || !IsNoGuitar(sections[i - 1].Section)) continue;
            if (SoundsBetween(context.Arrangement, sections[i - 1].Position, sections[i].Position)) continue;
            issues.Add(ArrangementIssues.Make(ArrangementIssueType.NoGuitarSectionSplit, "Validation_NoGuitarSectionSplit",
                sections[i].GetTimeMs(context.Beats)));
        }
    }

    /// <summary>Bar lines in [<paramref name="start" />, <paramref name="end" />): a phrase from bar to bar
    /// spans as many measures as it holds bar lines, and one from a pickup counts its partial bar.</summary>
    internal static int MeasuresBetween(IList<BeatMarker> beats, FractionalPosition start, FractionalPosition end)
    {
        var count = 0;
        var from = Math.Max(0, (int)Math.Ceiling(start.DoubleValue - 1e-9));
        for (var i = from; i < beats.Count && i < end.DoubleValue - 1e-9; i++)
        {
            if (beats[i].IsMeasure) count++;
        }

        if (!IsOnBarLine(beats, start) && count > 0) count++;
        return count;
    }

    internal static bool IsOnBarLine(IList<BeatMarker> beats, FractionalPosition position)
    {
        var beat = (int)Math.Round(position.DoubleValue);
        return Math.Abs(position.DoubleValue - beat) < 1e-9 && beat >= 0 && beat < beats.Count && beats[beat].IsMeasure;
    }

    private static bool IsNoGuitar(string? section) =>
        string.Equals(section?.Trim(), NoGuitar, StringComparison.OrdinalIgnoreCase);

    private static bool SoundsBetween(Arrangement arrangement, FractionalPosition start, FractionalPosition end) =>
        arrangement.Levels.Any(level =>
            level.Notes.Any(note => note.Position.IsWithin(start, end))
            || level.Chords.Any(chord => chord.Position.IsWithin(start, end)));
}
