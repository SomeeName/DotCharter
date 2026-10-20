namespace DotCharter.Services.Editing.Selection;

/// <summary>
///     Resolves the elements that belong to a sound as one authored decision:
///     the hand shape drawn over it and the FHP anchor holding it.
///     <para>
///         <b>A chord, its hand shape and its anchor are one thing the charter
///         decided, and every operation on one of them has to say what happens to
///         the other two.</b> <c>PatternCompanions</c> already encodes exactly
///         this for select-alike; this is the same rule at EDIT time, where it was
///         missing. The symptom the owner reported: re-fingering a chord left the
///         bar above it describing the replaced shape.
///     </para>
///     <para>
///         Pure and allocation-light — no selection service, no history, no view
///         model. It answers "which elements go with these sounds" and nothing
///         else; deciding whether to ACT on them stays with the caller, because
///         the answer differs per verb (a fingering edit repoints the hand shape,
///         a copy clones it, a delete must NOT cascade to it).
///     </para>
/// </summary>
public static class SoundCompanions
{
    /// <summary>
    ///     Hand shapes covering any of <paramref name="positions" />, excluding
    ///     any already in <paramref name="already" />.
    ///     <para>
    ///         Coverage is <c>HandShape.Covers</c> — start inclusive, end
    ///         EXCLUSIVE — the same span rule the matcher and the anchor generator
    ///         use. Abutting per-chord shapes must not both claim the sound on
    ///         their shared boundary.
    ///     </para>
    /// </summary>
    public static List<HandShape> CoveringHandShapes(
        IEnumerable<FractionalPosition> positions,
        IEnumerable<HandShape> candidates,
        IReadOnlyCollection<HandShape>? already = null)
    {
        var found = new List<HandShape>();
        var positionList = positions as IReadOnlyList<FractionalPosition> ?? positions.ToList();
        if (positionList.Count == 0) return found;

        foreach (var handShape in candidates)
        {
            if (already != null && already.Contains(handShape)) continue;
            if (found.Contains(handShape)) continue;

            foreach (var position in positionList)
            {
                if (!handShape.Covers(position)) continue;
                found.Add(handShape);
                break;
            }
        }

        return found;
    }

    /// <summary>
    ///     The last anchor at or before <paramref name="position" /> — the hand
    ///     position a sound is actually played under. Null when nothing anchors it.
    /// </summary>
    public static FretAnchor? GoverningAnchor(
        FractionalPosition position, IEnumerable<FretAnchor> anchors)
    {
        FretAnchor? best = null;
        foreach (var anchor in anchors)
        {
            if (anchor.Position > position) continue;
            if (best == null || anchor.Position > best.Position) best = anchor;
        }

        return best;
    }
}
