using DotCharter.Services.Editing.HandShapes;

namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     D11: a chord no hand shape covers. Every other hand-shape rule starts FROM a
///     shape (<see cref="HandShapeRule" />, <see cref="HandShapeContentRule" />,
///     <see cref="TemplateAndOverlapRule" />), and <see cref="ChordRule" /> never asks
///     whether one covers the chord — so a chord outside every bar had no finding at
///     all, while the export ships it with no grip.
///     <para>
///         Coverage is <see cref="ChordHandShapeCoverage" />'s, which is the predicate
///         the "Generate hand shapes" planner (and so this finding's auto-fix) uses:
///         the half-open span, so a chord ON a bar's end is outside it. Judged per
///         level, reported once per chord POSITION: a dynamic-difficulty chart repeats
///         the same chord on many levels, and one row per level is how I29 once buried
///         every real finding. The row points at the chord of the HIGHEST level that
///         misses it.
///     </para>
/// </summary>
public sealed class ChordOutsideHandShapeRule : IArrangementRule
{
    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var arrangement = context.Arrangement;
        SortedDictionary<FractionalPosition, (Chord Chord, ChartLevel Level, int Levels)>? found = null;
        List<Chord>? uncovered = null;

        for (var l = 0; l < arrangement.Levels.Count; l++)
        {
            var level = arrangement.Levels[l];
            uncovered?.Clear();
            uncovered = ChordHandShapeCoverage.Uncovered(level, uncovered);
            if (uncovered == null || uncovered.Count == 0) continue;

            found ??= [];
            for (var i = 0; i < uncovered.Count; i++)
            {
                var chord = uncovered[i];
                found[chord.Position] = found.TryGetValue(chord.Position, out var seen)
                    ? (chord, level, seen.Levels + 1)
                    : (chord, level, 1);
            }
        }

        if (found == null) return;

        foreach (var (_, (chord, level, levels)) in found)
        {
            var where = levels > 1 ? $" (on {levels} difficulty levels)" : string.Empty;
            // Every finding measured on a finished chart is this case: the shape
            // before the chord ENDS on it (the span is half-open). DLC Builder
            // called it I19 and lengthens such a shape on build.
            var what = EndsOn(level, chord)
                ? $"Chord sits exactly on the end of a hand shape{where}, so the shape stops as it is played"
                : $"Chord is inside no hand shape{where}, so it exports with no grip: no chord frame or name in the game";
            issues.Add(new ArrangementIssue(
                ArrangementIssueType.ChordOutsideHandShape,
                what + ". Auto-fix (or Tools ▸ Hand shapes ▸ Generate hand shapes) adds a shape.",
                chord.Position.GetPositionMs(context.Beats),
                Chord: chord));
        }
    }

    /// <summary>Whether a shape on the chord's own level ends exactly where it
    /// starts. Asked once per finding, so a scan is fine.</summary>
    private static bool EndsOn(ChartLevel level, Chord chord)
    {
        for (var i = 0; i < level.HandShapes.Count; i++)
        {
            if (level.HandShapes[i].EndPosition == chord.Position) return true;
        }

        return false;
    }
}
