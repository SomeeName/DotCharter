using DotCharter.Services.Editing.Anchors;
namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     Flags hand shapes whose chord template uses neither index (1) nor thumb
///     (0) but still covers a fret equal to the active anchor — usually a sign
///     the template is missing the 1st-finger barre that actually anchors the
///     shape. Neighbour hand shapes that do supply a first-finger cover at the
///     same anchor excuse the check (the player's hand is already there).
///     <para>
///         <b>The template lookup goes through
///         <see cref="ArrangementValidationContext.HandShapeTemplate" />, and so
///         does <see cref="HandShapeContentRule" />'s.</b> This rule used the
///         raw id index with no negative guard while that one used
///         <c>ChordTemplateResolver.FindById</c> — so an untemplated shape
///         (<c>TemplateId == -1</c>, the field's own default) was judged here
///         against whichever UNNUMBERED template sat first in the collection and
///         skipped silently there: a false positive and a false negative on the
///         same input. A shape that declares no grip is now REPORTED (D07), once
///         per arrangement, instead of either.
///     </para>
/// </summary>
public sealed class HandShapeRule : IArrangementRule
{
    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var arr = context.Arrangement;
        var beats = context.Beats;
        List<HandShape>? gripless = null;

        foreach (var level in arr.Levels)
        {
            if (level.HandShapes.Count == 0) continue;

            for (var i = 0; i < level.HandShapes.Count; i++)
            {
                var hs = level.HandShapes[i];
                var template = context.HandShapeTemplate(hs);
                if (template == null)
                {
                    // Not "nothing to check" — the shape states nothing at all.
                    (gripless ??= []).Add(hs);
                    continue;
                }

                // The rest of the rule is about the ANCHOR under the shape.
                if (MissingBarreAnchor(context, level, i) is not { } activeAnchor) continue;

                issues.Add(new ArrangementIssue(
                    ArrangementIssueType.FingeringAnchorMismatch,
                    Describe(template, activeAnchor, arr.Capo),
                    hs.Position.GetPositionMs(beats),
                    HandShape: hs,
                    Anchor: activeAnchor));
            }
        }

        // D07: one summary per arrangement. Per-shape would repeat the same
        // fact once per difficulty level. The shapes ride along on the finding
        // (ArrangementIssue.HandShapes) so the timeline can mark each bar.
        if (gripless != null)
        {
            var counted = gripless.Count == 1 ? "1 hand shape names" : $"{gripless.Count} hand shapes name";
            issues.Add(new ArrangementIssue(
                ArrangementIssueType.HandShapeWithoutTemplate,
                $"{counted} no chord template, so nothing states the grip they cover — they export with " +
                "chordId -1 and the hand-shape checks (I46, E05) cannot judge them.",
                HandShapes: gripless));
        }
    }

    /// <summary>
    ///     The anchor I46 reports the hand shape at <paramref name="index" /> of
    ///     <paramref name="level" /> against, or null when it reports nothing.
    ///     <para>
    /// <b>Rule E04 asks this too.</b> A shape fingered 2-3-4 with
    ///         finger 2 on the anchor fret is ONE mistake, but E04 infers the index
    ///         a fret below and reported it again on every chord under the shape,
    ///         with the other fix ("move the anchor") — 68 of 242 E04 findings on
    ///         the verified corpus. I46 owns that case and its message offers both
    ///         fixes; E04 stays quiet on a chord with no finger 1 under a shape
    ///         this answers for (<c>IndexFingerAnchorRule</c>).
    ///     </para>
    /// </summary>
    public static FretAnchor? MissingBarreAnchor(ArrangementValidationContext context, ChartLevel level, int index)
    {
        if (level.Anchors.Count == 0 || index < 0 || index >= level.HandShapes.Count) return null;
        var hs = level.HandShapes[index];
        var template = context.HandShapeTemplate(hs);
        if (template == null) return null;

        var usesFirstOrThumb = template.Fingers.Values.Any(f => f is 0 or 1);
        if (usesFirstOrThumb) return null;

        var activeAnchor = context.ActiveAnchor(level, hs.Position);
        if (activeAnchor == null) return null;

        var mismatch = template.Fingers.Any(kv =>
        {
            if (kv.Value <= 0) return false;
            return template.Frets.TryGetValue(kv.Key, out var fret) && fret == activeAnchor.Fret;
        });

        return mismatch && !NeighbourCoversWithFirstFinger(context, level, index - 1, activeAnchor) &&
               !NeighbourCoversWithFirstFinger(context, level, index + 1, activeAnchor)
            ? activeAnchor
            : null;
    }

    /// <summary>
    ///     The I46 sentence. When the grip states an index finger (inferred,
    ///     <c>IndexFingerPosition</c>) it names it and BOTH fixes, because this
    /// finding is also the E04 one for every chord under the shape:
    ///     re-finger with the index on the anchor fret, or move the anchor.
    /// </summary>
    internal static string Describe(ChordTemplate template, FretAnchor anchor, int capo)
    {
        const string head = "Hand shape fingering doesn't cover the anchor fret — likely a missing 1st-finger barre.";
        if (IndexFingerPosition.Find(template, capo) is not { } index || index.Allows(anchor.Fret)) return head;
        return $"{head} Its finger {index.FromFinger} on fret {index.FromFret} puts the index finger on " +
               $"{index.Frets}{(index.Inferred ? " (inferred)" : "")}, but the fret-hand position is on fret " +
               $"{anchor.Fret}: re-finger it with the index on fret {anchor.Fret}, or move the anchor to fret " +
               $"{index.Nearest(anchor.Fret)}.";
    }

    private static bool NeighbourCoversWithFirstFinger(
        ArrangementValidationContext context, ChartLevel level, int index, FretAnchor activeAnchor)
    {
        if (index < 0 || index >= level.HandShapes.Count) return false;
        var neighbour = level.HandShapes[index];
        // Same lookup rule as the subject shape: a neighbour that declares no
        // grip cannot EXCUSE a finding, and the raw index would have let an
        // unnumbered template do exactly that.
        var neighbourTemplate = context.HandShapeTemplate(neighbour);
        if (neighbourTemplate == null) return false;
        if (!neighbourTemplate.Fingers.Values.Contains(1)) return false;
        var neighbourAnchor = context.ActiveAnchor(level, neighbour.Position);
        return neighbourAnchor != null && neighbourAnchor.Fret == activeAnchor.Fret
                                       && neighbourAnchor.Width == activeAnchor.Width;
    }
}
