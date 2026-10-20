using Avalonia.Media;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Timeline2D;
using DotCharter.UI.Constants;
using DotCharter.UI.Controls.Themes;

namespace DotCharter.UI.Controls.Layers.Helpers;

public static class HighwaySustainRenderer
{
    public static void Draw(TimelineBatcher batcher, ChartRenderContext rc, in NoteDrawData data,
        List<DeferredLabel> deferredLabels, float nextHeadX = float.PositiveInfinity, byte nextHeadFret = 0)
    {
        var x = (float)((data.Time - rc.ViewStartMs) * rc.CurrentZoom);
        var width = (float)(data.Sustain * rc.CurrentZoom);

        // Screen-X cull, like every other 2D layer. The snapshot reaches
        // ±5000 ms past the view, so at 1 px/ms roughly six sounds are outside
        // the viewport for every one inside it. See NoteCulling.
        if (NoteCulling.IsOffScreen(x, width, rc.Width)) return;

        var y = rc.LaneTopFor(data.StringIndex) + rc.LaneHeight / 2f;
        var c = rc.StringColorFor(data.StringIndex);

        var unpitched = (data.Flags & NoteFlags.UnpitchedSlide) != 0;
        var vibrato = (data.Flags & NoteFlags.Vibrato) != 0;
        var tremolo = (data.Flags & NoteFlags.Tremolo) != 0;
        var isSelected = (data.Flags & NoteFlags.IsSelected) != 0;
        var isHovered = (data.Flags & NoteFlags.IsHovered) != 0;
        var isGhost = (data.Flags & NoteFlags.IsGhost) != 0;

        var alpha = data.IsPreview ? (byte)80 : (byte)160;
        if (isSelected) alpha = 220;
        else if (isHovered) alpha = 190;
        if (isGhost) alpha = 50;

        // Marks LOD (0.19 B1): sustains collapse to a thin density strip —
        // the full body + 2 border lines + vibrato/tremolo/slide detail is
        // invisible at sub-gem zoom but still costs vertices.
        if (rc.NoteLod == NoteLevelOfDetail.Marks)
        {
            var stripH = ChartEditorConstants.RenderMetrics.NoteLodSustainStripHeight;
            batcher.AddRect(x, y - stripH / 2f, width, stripH, Color.FromArgb(alpha, c.R, c.G, c.B));
            return;
        }

        var fillColor = Color.FromArgb(alpha, c.R, c.G, c.B);
        if (isSelected)
        {
            fillColor = Color.FromArgb(alpha, (byte)Math.Min(255, c.R + 80), (byte)Math.Min(255, c.G + 80),
                (byte)Math.Min(255, c.B + 80));
        }
        else if (isHovered)
        {
            fillColor = Color.FromArgb(alpha, (byte)Math.Min(255, c.R + 40), (byte)Math.Min(255, c.G + 40),
                (byte)Math.Min(255, c.B + 40));
        }

        var sustainYOffset = ChartEditorConstants.RenderMetrics.SustainYOffset;
        // Tube shading (0.15.5 D2): edge → core → edge vertical gradient in
        // place of the old flat rect, so sustain tails read as a rounded
        // tube instead of a paint bar. Two gradient quads replace one flat
        // quad — no allocation, no new pipeline, same analytic-AA borders.
        var coreColor = Color.FromArgb(alpha,
            (byte)Math.Min(255, fillColor.R + 45),
            (byte)Math.Min(255, fillColor.G + 45),
            (byte)Math.Min(255, fillColor.B + 45));
        var edgeColor = Color.FromArgb((byte)(alpha * 3 / 4), fillColor.R, fillColor.G, fillColor.B);
        // A repeat chord's tube is the same tube at RepeatChordInk.Dim brightness; the
        // selection/hover rim, the vibrato/tremolo squiggles and the slide keep their full colour.
        coreColor = RepeatChordInk.Darken(coreColor, data.Dimmed);
        edgeColor = RepeatChordInk.Darken(edgeColor, data.Dimmed);
        batcher.AddQuadGradient(x, y - sustainYOffset, x, y, x + width, y, x + width, y - sustainYOffset,
            edgeColor, coreColor, coreColor, edgeColor);
        batcher.AddQuadGradient(x, y, x, y + sustainYOffset, x + width, y + sustainYOffset, x + width, y,
            coreColor, edgeColor, edgeColor, coreColor);

        var borderColor = Color.FromArgb(alpha, (byte)Math.Min(255, c.R + 60), (byte)Math.Min(255, c.G + 60),
            (byte)Math.Min(255, c.B + 60));
        if (isSelected)
        {
            borderColor = Color.FromArgb(alpha, ChartTheme.WhiteColor.R, ChartTheme.WhiteColor.G,
                ChartTheme.WhiteColor.B);
        }
        else if (isHovered)
        {
            borderColor = Color.FromArgb(alpha, ChartTheme.LightGrayColor.R, ChartTheme.LightGrayColor.G,
                ChartTheme.LightGrayColor.B);
        }
        else
        {
            borderColor = RepeatChordInk.Darken(borderColor, data.Dimmed);
        }

        batcher.AddThickLine(x, y - sustainYOffset, x + width, y - sustainYOffset, 1f, borderColor);
        batcher.AddThickLine(x, y + sustainYOffset, x + width, y + sustainYOffset, 1f, borderColor);

        // A faint inner highlight line so the tube reads lit from
        // above. Polish-gated; Marks LOD returned above.
        //
        // There is deliberately NO rounded END CAP: it would be a circle sprite
        // CENTRED on the sustain end, so it would bulge
        // `sustainYOffset` px PAST the note's real end. That would make it impossible
        // to read where a note actually stops — and it would also be half of why
        // bends look like they run past the sustain (see the bend-label
        // clamp in HighwayNoteHeadRenderer). A sustain now ends exactly where
        // its data ends. Do not reintroduce a cap that overhangs the end.
        if (Timeline2DVisuals.PolishEnabled && width > sustainYOffset)
        {
            var highlight = RepeatChordInk.Darken(Color.FromArgb((byte)(alpha * 0.16f), 255, 255, 255), data.Dimmed);
            batcher.AddThickLine(x + 2f, y - sustainYOffset * 0.45f, x + width - 2f, y - sustainYOffset * 0.45f,
                1f, highlight);
        }

        var noteRadius = ChartEditorConstants.RenderMetrics.SustainNoteRadius;

        // Clip the squiggle loops to the VISIBLE span. They stepped the whole
        // sustain in ~3 px increments regardless of the viewport, so a long
        // drone emitted thousands of quads that were then scissor-clipped away:
        // a 5000 ms sustain at 1 px/ms is ~1660 iterations x 3 quads for ONE
        // note. The snapshot window reaches +/-5000 ms beyond the view, so most
        // of that work was for geometry off screen entirely. Costs nothing when
        // the sustain is already on screen (the clamps are no-ops).
        var squiggleFrom = Math.Max(0f, -x - noteRadius);
        var squiggleTo = Math.Min(width - noteRadius, (float)rc.Width - x - noteRadius);

        if (vibrato)
        {
            var prevWx = squiggleFrom;
            var prevWy = (float)Math.Sin(squiggleFrom * ChartEditorConstants.RenderMetrics.VibratoFrequency) *
                           ChartEditorConstants.RenderMetrics.VibratoHeight;
            for (var wx = squiggleFrom + ChartEditorConstants.RenderMetrics.VibratoStepPx;
                 wx <= squiggleTo;
                 wx += ChartEditorConstants.RenderMetrics.VibratoStepPx)
            {
                var wy = (float)Math.Sin(wx * ChartEditorConstants.RenderMetrics.VibratoFrequency) *
                         ChartEditorConstants.RenderMetrics.VibratoHeight;
                batcher.AddThickLine(x + noteRadius + prevWx, y + prevWy, x + noteRadius + wx, y + wy, 1.5f,
                    Color.FromArgb(alpha, ChartTheme.LightGrayColor.R, ChartTheme.LightGrayColor.G,
                        ChartTheme.LightGrayColor.B));
                prevWx = wx;
                prevWy = wy;
            }
        }

        if (tremolo)
        {
            var prevWx = squiggleFrom;
            float prevWy = 0;
            // Keep the zig-zag phase tied to the sustain's own origin so
            // clipping never shifts the pattern as the note scrolls in.
            var stepsSkipped = (int)(squiggleFrom / ChartEditorConstants.RenderMetrics.TremoloStepPx);
            var up = stepsSkipped % 2 == 0;
            for (var wx = squiggleFrom + ChartEditorConstants.RenderMetrics.TremoloStepPx;
                 wx <= squiggleTo;
                 wx += ChartEditorConstants.RenderMetrics.TremoloStepPx)
            {
                var wy = up
                    ? -ChartEditorConstants.RenderMetrics.TremoloHeight
                    : ChartEditorConstants.RenderMetrics.TremoloHeight;
                batcher.AddThickLine(x + noteRadius + prevWx, y + prevWy, x + noteRadius + wx, y + wy, 2f,
                    Color.FromArgb(alpha, c.R, c.G, c.B));
                up = !up;
                prevWx = wx;
                prevWy = wy;
            }
        }

        if (data.SlideTo > 0)
        {
            var tailH = ChartEditorConstants.RenderMetrics.SustainDrawHeight;
            var slideStartY = data.SlideTo < data.Fret ? y + tailH / 2 : y - tailH / 2;
            var slideEndY = data.SlideTo < data.Fret ? y - tailH / 2 : y + tailH / 2;
            var slideLineColor =
                unpitched
                    ? Color.FromArgb(alpha, ChartTheme.GrayColor.R, ChartTheme.GrayColor.G, ChartTheme.GrayColor.B)
                    : Color.FromArgb(alpha, 255, 255, 255);

            // 0.14.3 A technique refresh: tapered slide arrow — thick at the
            // note, thinning toward the target, finished with an arrowhead at
            // the destination fret. One solid quad + one triangle instead of
            // the uniform 2px line; polish-off restores the plain line.
            var sx = x + noteRadius;
            var ex = x + width;
            var sdx = ex - sx;
            var sdy = slideEndY - slideStartY;
            var slen = (float)Math.Sqrt(sdx * sdx + sdy * sdy);
            if (Timeline2DVisuals.PolishEnabled && slen > 10f)
            {
                var ux = sdx / slen;
                var uy = sdy / slen;
                var nx = -uy;
                var ny = ux;
                const float wStart = 2.3f;
                const float wEnd = 0.7f;
                const float headLen = 7f;
                const float headHalfW = 3.6f;
                // Body stops where the arrowhead begins.
                var bx = ex - ux * headLen;
                var by = slideEndY - uy * headLen;
                batcher.AddQuadRaw(
                    sx + nx * wStart, slideStartY + ny * wStart,
                    sx - nx * wStart, slideStartY - ny * wStart,
                    bx - nx * wEnd, by - ny * wEnd,
                    bx + nx * wEnd, by + ny * wEnd,
                    slideLineColor.R / 255f, slideLineColor.G / 255f, slideLineColor.B / 255f,
                    slideLineColor.A / 255f, 0f, 0f, 0f, 0f, 0f);
                batcher.AddTriangle(ex, slideEndY,
                    bx + nx * headHalfW, by + ny * headHalfW,
                    bx - nx * headHalfW, by - ny * headHalfW, slideLineColor);
            }
            else
            {
                batcher.AddThickLine(sx, slideStartY, ex, slideEndY, 2f, slideLineColor);
            }

            // The target box is left out when the next head on the string already
            // shows that fret right where the box would go (SlideLabelPolicy).
            if (rc.DrawNoteLabels && !SlideLabelPolicy.NextHeadRepeatsTarget(ex, nextHeadX, data.SlideTo, nextHeadFret))
            {
                var textScale = ChartEditorConstants.RenderMetrics.DefaultTextScale;
                var th = (batcher.FontAtlas?.RowHeight ?? ChartEditorConstants.RenderMetrics.DefaultFontSize) *
                         textScale;
                var boxAlpha = data.IsPreview ? (byte)128 : (byte)255;

                deferredLabels.Add(new DeferredLabel
                {
                    Text = RenderStringCache.Fret(data.SlideTo),
                    X = x + width,
                    Y = slideEndY - 8f,
                    Scale = textScale,
                    TextYOffset = (16f - th) / 2f,
                    TextColor = unpitched
                        ? Color.FromArgb(boxAlpha, ChartTheme.WhiteColor.R, ChartTheme.WhiteColor.G,
                            ChartTheme.WhiteColor.B)
                        : Color.FromArgb(boxAlpha, ChartTheme.TopMarginBgColor.R, ChartTheme.TopMarginBgColor.G,
                            ChartTheme.TopMarginBgColor.B),
                    BgColor =
                        unpitched
                            ? Color.FromArgb(boxAlpha, ChartTheme.TopMarginBgColor.R, ChartTheme.TopMarginBgColor.G,
                                ChartTheme.TopMarginBgColor.B)
                            : Color.FromArgb(boxAlpha, ChartTheme.WhiteColor.R, ChartTheme.WhiteColor.G,
                                ChartTheme.WhiteColor.B),
                    OutlineColor = unpitched
                        ? Color.FromArgb(boxAlpha, ChartTheme.GrayColor.R, ChartTheme.GrayColor.G,
                            ChartTheme.GrayColor.B)
                        : Color.FromArgb(boxAlpha, c.R, c.G, c.B),
                    HasBox = true
                });
            }
        }
    }
}
