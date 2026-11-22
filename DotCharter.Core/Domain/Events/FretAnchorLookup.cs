using DotCharter.Core.Domain.Primitives;

namespace DotCharter.Core.Domain.Events;

/// <summary>
///     The fret-hand position in effect at a point of the chart, by POSITION. A level's anchors are
///     not kept sorted mid-session, so "the last stored anchor at or before the point" can pick an
///     earlier anchor that was merely stored later; every editor that seeds a fret or width
///     from the anchor in effect reads it here.
/// </summary>
public static class FretAnchorLookup
{
    /// <summary>The anchor with the latest position at or before <paramref name="at" />, or null.</summary>
    public static FretAnchor? InEffectAt(IReadOnlyList<FretAnchor> anchors, FractionalPosition at)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        FretAnchor? best = null;
        for (var i = 0; i < anchors.Count; i++)
        {
            var anchor = anchors[i];
            if (anchor.Position <= at && (best == null || anchor.Position > best.Position)) best = anchor;
        }

        return best;
    }
}
