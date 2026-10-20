using Avalonia.Media;
using DotCharter.Rendering.Timeline2D;
using DotCharter.UI.Constants;
using DotCharter.UI.Controls.Layers.Helpers;
using DotCharter.UI.Controls.Themes;

namespace DotCharter.UI.Controls.Layers;

public class CursorLayer : ITimelineRenderLayer
{
    public void Render(TimelineBatcher batcher, ChartRenderContext rc)
    {
        if (!rc.DrawLabels) return;

        DrawHoveredLane(batcher, rc);
        DrawHoveredTimeCursor(batcher, rc);
    }

    private void DrawHoveredLane(TimelineBatcher batcher, ChartRenderContext rc)
    {
        if (rc.HoveredStringIndex < 0 || rc.HoveredStringIndex >= rc.StringCount) return;

        var laneTop = rc.LaneTopFor(rc.HoveredStringIndex);
        var laneH = rc.LaneHeight;
        var width = (float)rc.Width;

        // Vertical "spotlight" gradient: subtle on the edges, brighter in the
        // middle of the lane. Replaces the previous flat fill for a more
        // premium feel without obscuring underlying notes.
        var c = ChartTheme.HoverLaneColor;
        var edge = Color.FromArgb((byte)(c.A / 4), c.R, c.G, c.B);
        var mid = c;

        // Top half: edge → mid
        batcher.AddQuadGradient(
            0, laneTop, 0, laneTop + laneH * 0.5f,
            width, laneTop + laneH * 0.5f, width, laneTop,
            edge, mid, mid, edge);
        // Bottom half: mid → edge
        batcher.AddQuadGradient(
            0, laneTop + laneH * 0.5f, 0, laneTop + laneH,
            width, laneTop + laneH, width, laneTop + laneH * 0.5f,
            mid, edge, edge, mid);
    }

    private void DrawHoveredTimeCursor(TimelineBatcher batcher, ChartRenderContext rc)
    {
        if (rc.HoveredTimeMs < 0) return;
        var x = (float)((rc.HoveredTimeMs - rc.ViewStartMs) * rc.CurrentZoom);
        if (x < 0 || x > rc.Width) return;

        var endY = (float)rc.Height - rc.BottomMargin;
        const float startY = 0f;

        var dashLen = ChartEditorConstants.RenderMetrics.CursorDashLength;
        var gapLen = ChartEditorConstants.RenderMetrics.CursorGapLength;
        var thickness = ChartEditorConstants.RenderMetrics.CursorThickness;
        var color = ChartTheme.CursorLineColor;

        // Pre-grow the general buffer once (each AddVerticalLineAa pushes one
        // 6-vertex general quad). Eliminates per-iteration capacity branch.
        var iterations = (int)Math.Ceiling((endY - startY) / (dashLen + gapLen)) + 1;
        batcher.EnsureQuadsCapacity(0, iterations);

        // Hoist Color → float so the inner loop is allocation- and conversion-free.
        float r = color.R / 255f, g = color.G / 255f, b = color.B / 255f, a = color.A / 255f;

        var y = startY;
        while (y < endY)
        {
            var h = Math.Min(dashLen, endY - y);
            batcher.AddVerticalLineAaRaw(x, y, thickness, h, r, g, b, a);
            y += dashLen + gapLen;
        }

        // Small floating timestamp pill near the cursor (top of the lane area)
        // for fast snap-time feedback while hovering. Placed slightly to the
        // right so it doesn't visually fight the playhead badge.
        var pillText = RenderStringCache.MmSsFf(rc.HoveredTimeMs);
        var pillW = batcher.MeasureTextWidth(pillText) + 10f;
        const float pillH = 14f;
        var pillX = x + 4f;
        if (pillX + pillW > rc.Width) pillX = x - 4f - pillW;
        var pillY = rc.TopMargin + 24f;
        var pillBg = Color.FromArgb(190, 24, 24, 27);
        batcher.AddRoundRect(pillX, pillY, pillW, pillH, 3f, pillBg);
        batcher.AddText(pillText, pillX + 5f, pillY + pillH * 0.5f, ChartTheme.WhiteSmokeColor, centerY: true);
    }
}
