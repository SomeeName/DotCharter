using DotCharter.UI.Constants;
using DotCharter.UI.Editing.Tracks;

namespace DotCharter.UI.Input;

/// <summary>
///     Where the timeline's in-place editors sit, in the timeline's own pixels. Pure, so a test can pan the
///     view and watch the editor follow: the horizontal position maps through the ONE time-to-screen anchor
///     (<c>ProjectState.TimelineViewAnchorEffectiveMs</c>), the vertical ones through the renderer's own rows.
/// </summary>
public static class InlineEditPlacement
{
    /// <summary>
    ///     Left edge of an editor <paramref name="editorWidth" /> wide whose time is <paramref name="timeMs" />,
    ///     kept inside a timeline <paramref name="viewportWidth" /> wide.
    /// </summary>
    public static double Left(double timeMs, double viewAnchorMs, double pixelsPerMs, double viewportWidth, double editorWidth)
    {
        var bounds = ChartHitTester.GetViewportBounds(viewAnchorMs, pixelsPerMs, viewportWidth);
        var x = (timeMs - bounds.StartMs) * pixelsPerMs;
        return Math.Clamp(x, 0, Math.Max(0, viewportWidth - editorWidth));
    }

    /// <summary>Top of the fret-hand position row: the same Y the anchors are drawn and hit-tested at.</summary>
    public static double FhpTop(double viewportHeight, double topMargin, double bottomMargin) =>
        topMargin + (viewportHeight - topMargin - bottomMargin) + ChartEditorConstants.RenderMetrics.FhpYOffset;

    /// <summary>Just under the Sections row, where the popover hangs.</summary>
    public static double SectionPopoverTop(RibbonLayout ribbon) => ribbon.SectionY + ribbon.RowPitch;
}
