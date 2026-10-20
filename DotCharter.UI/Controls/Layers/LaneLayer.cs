using Avalonia.Media;
using DotCharter.Rendering.Timeline2D;
using DotCharter.UI.Controls.Themes;

namespace DotCharter.UI.Controls.Layers;

public class LaneLayer : ITimelineRenderLayer
{
    /// <summary>Width of the string-edit-mask pip drawn at the very left of a
    /// masked lane, just inside the tuning label.</summary>
    private const float MaskPipWidth = 3f;

    /// <summary>Alpha applied to a lane's guide line and tuning label while a
    /// mask is set and this string is NOT in it. Dim, not hidden: the lane is
    /// still a lane, it just is not a keyboard target right now.</summary>
    private const double MaskedOutAlphaScale = 0.35;

    public void Render(TimelineBatcher batcher, ChartRenderContext rc)
    {

        var hovered = rc.HoveredStringIndex;
        var laneColors = ChartTheme.BaseStringLaneColors;
        var laneHover = ChartTheme.BaseStringLaneHoverColors;
        var width = (float)rc.Width;

        // v0.35 Stage 1: the per-string edit mask is what every per-string
        // keyboard verb (fret ±/set, mute, HOPO, harmonic, picking, vibrato,
        // tremolo, link-next) targets, so it must be VISIBLE — invisible modal
        // state that silently changes what a key does is the one thing the
        // editing-rework audit says not to copy from rscharter
        // (`typingNumber`). An EMPTY mask means every string, and draws nothing
        // at all, so this is a strict no-op until the user presses Ctrl+1..9.
        var mask = rc.StringEditMask;
        var maskActive = !mask.IsEmpty;

        // 0.14.3 A string-lane tinting (opt-in, default off): a whisper-faint
        // (~4% alpha) full-lane wash in the string's own colour so the eye
        // tracks lanes without extra gridlines. One rect per string; colours
        // pre-baked in ChartTheme so themes restyle it. Suppressed in Marks
        // LOD — at that density the washes only add background noise.
        if (Timeline2DVisuals.LaneTintEnabled && rc.NoteLod != Helpers.NoteLevelOfDetail.Marks)
        {
            var tints = ChartTheme.BaseStringLaneTintColors;
            for (var i = 0; i < rc.StringCount; i++)
            {
                var laneTop = rc.LaneTopFor(i);
                batcher.AddRect(0, laneTop, width, rc.LaneHeight, tints[ChartTheme.StringSlot(i, rc.StringCount)]);
            }
        }

        for (var i = 0; i < rc.StringCount; i++)
        {
            var laneTop = rc.LaneTopFor(i);
            var y = laneTop + rc.LaneHeight / 2f;
            var palIdx = ChartTheme.StringSlot(i, rc.StringCount);
            var isActive = i == hovered;
            var isMasked = maskActive && mask.IsExplicit(i);
            var isMaskedOut = maskActive && !isMasked;

            // Hovered lane gets a brighter, slightly thicker guide for clearer
            // visual hierarchy. All colors are pre-baked to avoid per-frame allocs.
            var lineColor = isActive ? laneHover[palIdx] : laneColors[palIdx];
            if (isMaskedOut) lineColor = Fade(lineColor);
            var thickness = isActive ? 2.5f : 2f;
            batcher.AddHorizontalLineAa(0, y - thickness * 0.5f, width, thickness, lineColor);

            if (isMasked)
            {
                batcher.AddRect(0, laneTop + 1f, MaskPipWidth, Math.Max(1f, rc.LaneHeight - 2f),
                    rc.StringColors[palIdx]);
            }

            // The tuning letter is deliberately NOT drawn here (at x=5 inside the chart
            // surface. `TimelineGutterControl` names every string in the
            // gutter immediately to the left — with the note name, the string
            // number and LOW/HIGH — so drawing it again a few pixels further
            // right is the same fact twice, and it sits on top of the leftmost
            // notes of the chart. Removed; the gutter is the label.
            //
            // Kept OUT of the stacked/preview path too, which is what
            // `DrawLabels` gated: those rows have no gutter, but they are
            // secondary views of a chart whose strings are already named by the
            // main timeline above them.
        }
    }

    private static Color Fade(Color color) =>
        Color.FromArgb((byte)(color.A * MaskedOutAlphaScale), color.R, color.G, color.B);
}
