namespace DotCharter.UI.Input.Tools.EventInteractionGestures;

/// <summary>
/// Pure clamp math for the live beat-drag preview.
///
/// When the user drags a beat marker, its new time must stay strictly
/// between the previous and next "fixed" beats (anchors and the timeline
/// endpoints are fixed; non-anchor beats between them are repriced live).
/// The clamp leaves enough room for every interior beat to satisfy the
/// minimum per-segment spacing.
/// </summary>
public static class BeatDragBounds
{
    /// <summary>Default minimum per-beat spacing in milliseconds.</summary>
    public const double DefaultEpsilonMs = 10.0;

    /// <summary>
    /// Computes the inclusive [lower, upper] window the dragged beat may
    /// move to without forcing any other beat below <paramref name="epsilonMs"/>
    /// from its neighbour.
    ///
    /// • Beats before the previous anchor (or beat 0) supply the lower bound.
    /// • Beats after the next anchor (or last beat) supply the upper bound.
    /// • Bounds are returned as ±∞ when no fixed beat exists on that side
    ///   (e.g. dragging beat 0 has no lower bound).
    /// </summary>
    public static (double Lower, double Upper) ComputeBounds(
        IReadOnlyList<BeatMarker> originalBeats, int dragIndex, double epsilonMs = DefaultEpsilonMs)
    {
        if (originalBeats == null) throw new ArgumentNullException(nameof(originalBeats));
        if (dragIndex < 0 || dragIndex >= originalBeats.Count)
            throw new ArgumentOutOfRangeException(nameof(dragIndex));
        if (epsilonMs < 0) throw new ArgumentOutOfRangeException(nameof(epsilonMs));

        var lower = double.NegativeInfinity;
        var upper = double.PositiveInfinity;

        var beatsBefore = 0;
        for (var i = dragIndex - 1; i >= 0; i--)
        {
            beatsBefore++;
            if (i == 0 || originalBeats[i].IsAnchor)
            {
                lower = originalBeats[i].Time + epsilonMs * beatsBefore;
                break;
            }
        }

        var beatsAfter = 0;
        for (var i = dragIndex + 1; i < originalBeats.Count; i++)
        {
            beatsAfter++;
            if (i == originalBeats.Count - 1 || originalBeats[i].IsAnchor)
            {
                upper = originalBeats[i].Time - epsilonMs * beatsAfter;
                break;
            }
        }

        return (lower, upper);
    }

    /// <summary>Clamps <paramref name="value"/> into the [lower, upper] window.</summary>
    public static double Clamp(double value, double lower, double upper)
    {
        if (value < lower) return lower;
        if (value > upper) return upper;
        return value;
    }
}
