namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     D17 (<see cref="ArrangementIssueType.SustainCrossesBoundary" />) and D18
///     (<see cref="ArrangementIssueType.HandShapeCrossesBoundary" />): a note or chord sustain, a
///     Link-Next chain or a hand shape that runs across a phrase start, a section start or past END.
///     Rocksmith rebuilds every phrase on its own for dynamic difficulty, so a span carried across one is
///     cut there in game; past END nothing is played at all. What counts as crossed is
///     <see cref="BoundaryCrossing.Crossed" />.
///     <para>
///         A Link-Next NOTE whose chain crosses a non-mover phrase is I10's finding
///         (<see cref="ArrangementIssueType.PhraseChangeOnLinkNextNote" />), so it is not reported here
///         again; its chain across a section only, a mover phrase or END still is. Judged on every level,
///         like the note rules.
///     </para>
/// </summary>
public sealed class BoundaryCrossingRule : IArrangementRule
{
    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var boundaries = BoundaryCrossing.Of(context.Arrangement);
        if (boundaries.Count == 0) return;

        foreach (var level in context.Arrangement.Levels)
        {
            foreach (var note in level.Notes)
            {
                if (note.Sustain == FractionalPosition.Zero && !note.LinkNext) continue;
                if (BoundaryCrossing.Crossed(boundaries, note.Position, note.EndPosition, note.LinkNext) is not { } at) continue;
                if (note.LinkNext && at.Kind != BoundaryKind.End && context.HasPhraseChangeOnSustain(note)) continue;
                issues.Add(ArrangementIssues.Make(ArrangementIssueType.SustainCrossesBoundary,
                    Key(note.LinkNext ? "LinkNext" : "Sustain", at.Kind), note.Position.GetPositionMs(context.Beats),
                    note: note, stringIndex: note.StringIndex, args: Args(at)));
            }

            foreach (var chord in level.Chords)
            {
                var linked = chord.HasLinkNext();
                if (chord.ChordNotes.Count == 0) continue;
                if (BoundaryCrossing.Crossed(boundaries, chord.Position, chord.EndPosition, linked) is not { } at) continue;
                issues.Add(ArrangementIssues.Make(ArrangementIssueType.SustainCrossesBoundary,
                    Key(linked ? "LinkNext" : "Sustain", at.Kind), chord.Position.GetPositionMs(context.Beats),
                    chord: chord, args: Args(at)));
            }

            foreach (var shape in level.HandShapes)
            {
                if (BoundaryCrossing.Crossed(boundaries, shape.Position, shape.EndPosition, linked: false) is not { } at)
                    continue;
                issues.Add(ArrangementIssues.Make(ArrangementIssueType.HandShapeCrossesBoundary,
                    Key("HandShape", at.Kind), shape.Position.GetPositionMs(context.Beats), handShape: shape,
                    args: Args(at)));
            }
        }
    }

    /// <summary><c>Validation_SustainCrossesPhrase</c>, <c>Validation_HandShapePastEnd</c>, …</summary>
    internal static string Key(string subject, BoundaryKind kind) => (subject, kind) switch
    {
        ("LinkNext", BoundaryKind.End) => "Validation_LinkNextPastEnd",
        ("LinkNext", BoundaryKind.Section) => "Validation_LinkNextCrossesSection",
        ("LinkNext", _) => "Validation_LinkNextCrossesPhrase",
        ("HandShape", BoundaryKind.End) => "Validation_HandShapePastEnd",
        ("HandShape", BoundaryKind.Section) => "Validation_HandShapeCrossesSection",
        ("HandShape", _) => "Validation_HandShapeCrossesPhrase",
        (_, BoundaryKind.End) => "Validation_SustainPastEnd",
        (_, BoundaryKind.Section) => "Validation_SustainCrossesSection",
        _ => "Validation_SustainCrossesPhrase"
    };

    private static object[]? Args(ChartBoundary at) => at.Kind == BoundaryKind.End ? null : [at.Name];
}
