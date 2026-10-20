using DotCharter.UI.Constants;

namespace DotCharter.UI.Input;

/// <summary>
///     Pure vertical-zone predicates for the 2D timeline, shared by
///     <see cref="Tools.ToolCoordinator" /> (routing) and the cursor policy so
///     the two can never disagree about where a band starts.
/// </summary>
public static class TimelineZones
{
    /// <summary>The time ruler owns the bottom strip of the viewport (ticks at
    /// H-20, labels at H-18 — see the RenderMetrics bottom-band comment).</summary>
    public const double TimeRulerHeightPx = 20.0;

    /// <summary>
    ///     A press here is a SEEK, never a tool gesture. Letting ruler
    ///     clicks fall through the band routing into the highway tool would
    ///     arm the marquee and CLEAR the selection on release,
    ///     and a click a few pixels above would land in the unlabeled FHP band and
    ///     silently place an amber anchor — which reads as "clicking the time
    ///     ruler created a yellow region labeled 1".
    ///     DAW convention: the ruler seeks, drags scrub.
    /// </summary>
    public static bool IsTimeRuler(double y, double viewportHeight) =>
        viewportHeight > 0 && y >= viewportHeight - TimeRulerHeightPx && y <= viewportHeight;

    /// <summary>The hand-shape editing band (mirror of the routing numbers in
    /// <c>ToolCoordinator.DetermineTool</c>).</summary>
    public static bool IsHandShapeBand(double y, double viewportHeight, double bottomMargin)
    {
        var laneBottom = viewportHeight - bottomMargin;
        var barY = laneBottom + ChartEditorConstants.RenderMetrics.HandShapeBarYOffset;
        return y >= barY && y <= barY + ChartEditorConstants.RenderMetrics.HandShapeActiveBarHeight + 4;
    }

    /// <summary>The FHP (fret-hand-position) editing band.</summary>
    public static bool IsFhpBand(double y, double viewportHeight, double bottomMargin)
    {
        var laneBottom = viewportHeight - bottomMargin;
        var fhpY = laneBottom + ChartEditorConstants.RenderMetrics.FhpYOffset;
        return y >= fhpY && y <= fhpY + ChartEditorConstants.RenderMetrics.AnchorTailHeight;
    }
}
