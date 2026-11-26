using DotCharter.Core.Linking;

namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     D90: a note or chord sustain that ends closer to the next note or chord than a 32nd note (1/8
///     beat in 4/4), the gap community charters leave before the next sound. D91: a note sustain
///     shorter than 5 ms, too short to see or hear. What the next sound is, the gap and both fixes are
///     <see cref="ChartSpacingFix" />'s.
///     <para>
///         The spacing clean-up the editor runs after each change and before export
///         (<c>ChartSpacingEnforcer</c>) keeps the app's minimum note distance (1/16 beat by default),
///         which is all Rocksmith needs, so exports keep their sustains; this check and its fix are where
///         the 32nd note applies. A sound that starts less than a 32nd note before the next one is never
///         reported (a fast strum keeps its strike sustain), nor is a Link-Next note, a slide (its
///         sustain is the slide's length), a chord with either, or a chord that lets other notes pass.
///     </para>
///     <para>
///         Judged per level, reported once per position and code across levels, on the sound of the
///         level with the greatest <c>DifficultyIndex</c> (the Issues tab hides easier levels by default).
///     </para>
/// </summary>
public sealed class SustainSpacingRule : IArrangementRule
{
    private const string GapKey = "Validation_SustainTooCloseToNext";
    private const string ShortKey = "Validation_SustainUnderFiveMs";

    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var beats = context.Beats;
        var found = new SortedDictionary<(FractionalPosition, ArrangementIssueType), (ArrangementIssue Issue, int Difficulty)>();
        foreach (var level in context.Arrangement.Levels)
        {
            var positions = ChartSpacingFix.SoundPositions(level);
            foreach (var note in level.Notes)
            {
                if (note.LinkNext || note.Sustain <= FractionalPosition.Zero) continue;
                var slides = note.SlideTo.HasValue || note.UnpitchedSlide;
                var issue = ChartSpacingFix.IsUnderFiveMs(note.Position, note.Sustain, beats)
                    ? ArrangementIssues.Note(ArrangementIssueType.SustainUnderFiveMs, note, note.Position.GetPositionMs(beats), ShortKey)
                    : !slides && TooClose(positions, note.Position, note.Sustain, beats) is { } gap
                        ? ArrangementIssues.Note(ArrangementIssueType.SustainTooCloseToNext, note,
                            note.Position.GetPositionMs(beats), GapKey, args: [gap])
                        : null;
                Keep(found, note.Position, issue, level.DifficultyIndex);
            }

            foreach (var chord in level.Chords)
            {
                if (chord.PassOtherNotes || chord.ChordNotes.Any(cn => cn.LinkNext || cn.SlideTo.HasValue || cn.UnpitchedSlide))
                    continue;
                if (TooClose(positions, chord.Position, chord.EndPosition - chord.Position, beats) is not { } gap) continue;
                Keep(found, chord.Position, ArrangementIssues.Chord(ArrangementIssueType.SustainTooCloseToNext, chord,
                    chord.Position.GetPositionMs(beats), GapKey, args: [gap]), level.DifficultyIndex);
            }
        }

        foreach (var (issue, _) in found.Values) issues.Add(issue);
    }

    /// <summary>The gap the message names when the sustain ends too close to the next sound, else null.</summary>
    private static string? TooClose(FractionalPosition[] positions, FractionalPosition position, FractionalPosition sustain,
        IList<BeatMarker> beats)
    {
        if (sustain <= FractionalPosition.Zero || ChartSpacingFix.After(positions, position) is not { } next) return null;
        if (ChartSpacingFix.LongestSustain(position, next, beats) is not { } longest || sustain <= longest) return null;
        return HandShapeGapRule.Beats(SpacingGap.ThirtySecondNote(position, beats));
    }

    private static void Keep(SortedDictionary<(FractionalPosition, ArrangementIssueType), (ArrangementIssue Issue, int Difficulty)> found,
        FractionalPosition position, ArrangementIssue? issue, int difficulty)
    {
        if (issue == null) return;
        var key = (position, issue.Type);
        if (found.TryGetValue(key, out var seen) && seen.Difficulty > difficulty) return;
        found[key] = (issue, difficulty);
    }
}
