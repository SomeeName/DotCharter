namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     D40 (<see cref="ArrangementIssueType.NoteNearlyOnGrid" />) and D41
///     (<see cref="ArrangementIssueType.NoteOffGrid" />): a note or chord off the beat grid. The grid, its
///     straight and triplet subdivisions and the millisecond bands are <see cref="BeatGridSnap" />'s; a D40 is
///     offered <see cref="BeatGridSnap.Snap" />. Judged on every level; one finding per position, so the
///     notes of one strum are not reported once each.
/// </summary>
public sealed class OffGridRule : IArrangementRule
{
    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var beats = context.Beats;
        if (beats.Count < 2) return;

        foreach (var level in context.Arrangement.Levels)
        {
            var seen = new HashSet<FractionalPosition>();
            foreach (var chord in level.Chords)
            {
                if (!seen.Add(chord.Position) || Off(chord.Position, beats) is not { } off) continue;
                issues.Add(ArrangementIssues.Make(off.Type, Key(off.Type), chord.Position.GetPositionMs(beats),
                    chord: chord, args: [off.Ms]));
            }

            foreach (var note in level.Notes)
            {
                if (!seen.Add(note.Position) || Off(note.Position, beats) is not { } off) continue;
                issues.Add(ArrangementIssues.Make(off.Type, Key(off.Type), note.Position.GetPositionMs(beats),
                    note: note, stringIndex: note.StringIndex, args: [off.Ms]));
            }
        }
    }

    private static (ArrangementIssueType Type, double Ms)? Off(FractionalPosition position, IList<BeatMarker> beats) =>
        BeatGridSnap.Nearest(position, beats) is { } near && BeatGridSnap.Judge(near.OffMs) is { } type
            ? (type, near.OffMs)
            : null;

    private static string Key(ArrangementIssueType type) =>
        type == ArrangementIssueType.NoteNearlyOnGrid ? "Validation_NoteNearlyOnGrid" : "Validation_NoteOffGrid";
}
