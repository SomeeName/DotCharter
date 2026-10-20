using Avalonia.Media;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Timeline2D;
using DotCharter.UI.Constants;
using DotCharter.UI.Controls.Themes;

namespace DotCharter.UI.Controls.Layers.Helpers;

public static class HighwayNoteHeadRenderer
{
    /// <summary>
    ///     Where each of a muted head's four X ticks runs, per axis from the head's centre, for an X of
    ///     half-size <paramref name="s" />. The X must not cross the whole head, straight through the
    ///     fret number, and a grey plate behind a dark number would be the lowest-contrast
    ///     number on the timeline, with the X's ends poking out of the plate's corners.
    ///     The ticks start clear of a two-digit number
    ///     (<see cref="FretNumberHalfWidthPx" />, <see cref="FretNumberHalfHeightPx" />) and end a
    ///     little past the rim, so the head reads as framed by an X.
    /// </summary>
    internal static (float From, float To) MuteTickSpan(float s) => (1.0f * s, 1.5f * s);

    /// <summary>Half the box a two-digit fret number takes at the head's text scale.</summary>
    internal const float FretNumberHalfWidthPx = 7.5f;

    /// <summary>Half that box's height.</summary>
    internal const float FretNumberHalfHeightPx = 5.5f;

    public static void Draw(TimelineBatcher batcher, ChartRenderContext rc, in NoteDrawData data,
        List<DeferredLabel> deferredLabels, float nextHeadX = float.PositiveInfinity)
    {
        var x = (float)((data.Time - rc.ViewStartMs) * rc.CurrentZoom);

        // Screen-X cull, like every other 2D layer. The head itself sits at x,
        // but the bend curve and its labels run to the sustain end, so the span
        // tested is the whole sound. See NoteCulling.
        if (NoteCulling.IsOffScreen(x, (float)(Math.Max(0.0, data.Sustain) * rc.CurrentZoom), rc.Width)) return;

        var y = rc.LaneTopFor(data.StringIndex) + rc.LaneHeight / 2f;
        var c = rc.StringColorFor(data.StringIndex);

        var isLinkedTarget = (data.Flags & NoteFlags.LinkPrevious) != 0;
        var isAccent = (data.Flags & NoteFlags.Accent) != 0;
        var isSelected = (data.Flags & NoteFlags.IsSelected) != 0;
        var isHovered = (data.Flags & NoteFlags.IsHovered) != 0;
        var isGhost = (data.Flags & NoteFlags.IsGhost) != 0;

        var alpha = isGhost ? (byte)80 : data.IsPreview ? (byte)128 : (byte)255;

        // Marks LOD (0.19 B1): below ~6px per note column the full gem +
        // icons + border sprites just overdraw each other. One small
        // lane-colored mark per note keeps density readable at a fraction
        // of the vertex cost. Selection/hover still pop via color.
        if (rc.NoteLod == NoteLevelOfDetail.Marks)
        {
            var markColor = isSelected
                ? Color.FromArgb(alpha, ChartTheme.WhiteColor.R, ChartTheme.WhiteColor.G, ChartTheme.WhiteColor.B)
                : isHovered
                    ? Color.FromArgb(alpha, ChartTheme.LightGrayColor.R, ChartTheme.LightGrayColor.G,
                        ChartTheme.LightGrayColor.B)
                    : Color.FromArgb(alpha, c.R, c.G, c.B);
            var mw = ChartEditorConstants.RenderMetrics.NoteLodMarkWidth;
            var mh = ChartEditorConstants.RenderMetrics.NoteLodMarkHeight;
            batcher.AddRect(x - mw / 2f, y - mh / 2f, mw, mh, markColor);
            return;
        }

        // A repeat chord's HEAD — gem fill, resting rim, specular cap, fret number — is drawn
        // from `hc`, the string colour at RepeatChordInk.Dim brightness, still opaque.
        // Every technique glyph and the selection/hover ink below keep the full colour, so they
        // read at full contrast on a dimmed strum.
        var hc = RepeatChordInk.Darken(c, data.Dimmed);
        var baseColor = Color.FromArgb(alpha, c.R, c.G, c.B);
        var darkColor = Color.FromArgb(alpha, (byte)(hc.R / 2), (byte)(hc.G / 2), (byte)(hc.B / 2));

        // 0.14.3 A gem polish: subtle top→bottom gradient fill + a rim in a
        // darker shade of the string colour instead of the flat half-tone
        // disc on a near-black ring. Vertex-colour only (AddSpriteGradientV),
        // gated behind the polish toggle; Marks LOD never reaches here.
        var polish = Timeline2DVisuals.PolishEnabled;
        var gemTop = polish
            ? Color.FromArgb(alpha, (byte)(hc.R * 72 / 100), (byte)(hc.G * 72 / 100), (byte)(hc.B * 72 / 100))
            : darkColor;
        var gemBottom = polish
            ? Color.FromArgb(alpha, (byte)(hc.R * 38 / 100), (byte)(hc.G * 38 / 100), (byte)(hc.B * 38 / 100))
            : darkColor;

        // Hover placement GHOST: hollow, not a paler copy. At the old flat 50%
        // the preview still read as a committed note, and the live audit
        // watched stray clicks "silently" mutate the chart — the click DID
        // print feedback, it just looked like a note that was already there.
        // The rim keeps the 50% alpha; the FILL drops to a wash.
        gemTop = GhostFill(gemTop, data.IsPreview);
        gemBottom = GhostFill(gemBottom, data.IsPreview);
        darkColor = GhostFill(darkColor, data.IsPreview);

        Color borderColor;
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
        else if (polish)
        {
            borderColor = Color.FromArgb(alpha, (byte)(c.R * 30 / 100), (byte)(c.G * 30 / 100),
                (byte)(c.B * 30 / 100));
        }
        else
        {
            borderColor = Color.FromArgb(alpha, ChartTheme.TopMarginBgColor.R, ChartTheme.TopMarginBgColor.G,
                ChartTheme.TopMarginBgColor.B);
        }

        // The gem's rim and its number: dimmed with the head at rest, full when selected or hovered —
        // a grey number on the selection's light core could not be read (GPU capture).
        var active = isSelected || isHovered;
        var headRim = active ? borderColor : RepeatChordInk.Darken(borderColor, data.Dimmed);

        var whiteAlpha =
            Color.FromArgb(alpha, ChartTheme.WhiteColor.R, ChartTheme.WhiteColor.G, ChartTheme.WhiteColor.B);
        var numberColor = active ? whiteAlpha : RepeatChordInk.Darken(whiteAlpha, data.Dimmed);

        var mute = (MuteType)data.Mute;
        var hopo = (HopoType)data.Hopo;
        var harmonic = (HarmonicType)data.Harmonic;

        var strokeThick = isSelected
            ? ChartEditorConstants.RenderMetrics.SelectedNoteStrokeThickness
            : ChartEditorConstants.RenderMetrics.NoteStrokeThickness;
        var baseRadius = ChartEditorConstants.RenderMetrics.BaseNoteRadius;
        var outerR = baseRadius + strokeThick / 2f;
        var innerR = baseRadius - strokeThick / 2f;

        if (data.BendCount > 0)
        {
            // The curve, its arrowhead and the held tail after it, in a light tint of the
            // string's colour: pure white read as a selection underline under a slight bend.
            var bendInk = BendLabelPlacement.CurveColor(c, alpha);
            var bendStartY = y + ChartEditorConstants.RenderMetrics.BendStartYOffset;
            var lastBendY = bendStartY;
            var maxBendSteps = ChartEditorConstants.RenderMetrics.MaxBendSteps;
            var tailHeight = ChartEditorConstants.RenderMetrics.BendTailHeight;
            var prevX = x;
            var prevY = bendStartY;
            // Previous-previous point so the 0.14.3 arrowhead can align with
            // the final segment's direction (rise = bend, fall = release).
            var prevPrevX = x;
            var prevPrevY = bendStartY;

            // The bend curve must never leave the note it belongs to — the data
            // invariant (Core BendGeometry, enforced at import AND on every
            // save/edit) already guarantees this, but the renderer clamps too so a
            // stray point from any path can never draw a bend past the sustain
            // (owner report, repeatedly: "the bends go outside of the note
            // sustain"). Sustain is 0 for a strike; then the curve pins at the head.
            var sustainEndX = x + (float)(Math.Max(0.0, data.Sustain) * rc.CurrentZoom);
            // This note's STANDING labels, so no two of its labels ever overlap
            // (BendLabelPlacement.ResolveAgainst): their right edges and steps, while the labels
            // themselves are deferredLabels[firstLabel..] — nothing else appends during this loop.
            // Stack spans, not a list: this runs per note per frame. A note carries a handful of
            // bend points; past maxStandingBendLabels a further label is simply not drawn.
            const int maxStandingBendLabels = 8;
            Span<float> standingRights = stackalloc float[maxStandingBendLabels];
            Span<float> standingSteps = stackalloc float[maxStandingBendLabels];
            var standing = 0;
            var firstLabel = deferredLabels.Count;
            for (var b = 0; b < data.BendCount; b++)
            {
                var bend = rc.Snapshot.BendValues[data.BendOffset + b];
                var timeOffsetMs = (float)(bend.Time - data.Time);
                var bendX = Math.Min(x + (float)(timeOffsetMs * rc.CurrentZoom), sustainEndX);
                var clampedStep = Math.Min(bend.Step, maxBendSteps);
                var bendY = bendStartY - clampedStep / maxBendSteps * tailHeight;

                batcher.AddThickLine(prevX, prevY, bendX, bendY, 2f, bendInk);
                prevPrevX = prevX;
                prevPrevY = prevY;
                prevX = bendX;
                prevY = bendY;
                lastBendY = bendY;

                if (bend.Step > 0 && rc.DrawNoteLabels)
                {
                    var label = RenderStringCache.BendArrowLabel(bend.Step, FormatBendLabel(bend.Step));
                    var textScale = ChartEditorConstants.RenderMetrics.BendTextScale;
                    var th = (batcher.FontAtlas?.RowHeight ?? ChartEditorConstants.RenderMetrics.DefaultFontSize) *
                             textScale;

                    // Keep the label INSIDE the note (bends must not read as running over
                    // the sustain). The label box grows
                    // rightward from its bend point, and GP routinely authors the
                    // destination point at 98-99% of the sustain, so an
                    // un-clamped box sat almost entirely past the note's end and
                    // read as a bend belonging to the NEXT sound. The bend data
                    // itself is in range — GpPostProcessor.ClampBendsToSustain
                    // guarantees it — so this is purely where the glyph goes.
                    // …and never over the head: labels draw after the heads, so a
                    // label there hid the fret number (see BendLabelPlacement).
                    var boxW = TextRenderHelper.MeasureScaledTextWidth(batcher, label, textScale) + 4f;
                    var labelX = BendLabelPlacement.LabelX(bendX, x + outerR, sustainEndX, data.Sustain > 0, boxW);
                    // No room before the next head on the string: the curve still shows the bend.
                    if (!BendLabelPlacement.FitsBeforeNextHead(labelX, boxW, nextHeadX, outerR)) continue;

                    var collision = BendLabelPlacement.ResolveAgainst(standingRights[..standing],
                        standingSteps[..standing], labelX, bend.Step, out var keep);
                    if (collision == BendLabelPlacement.Collision.SkipThis) continue;
                    if (collision == BendLabelPlacement.Collision.ReplacePrevious)
                    {
                        deferredLabels.RemoveRange(firstLabel + keep, standing - keep);
                        standing = keep;
                    }

                    if (standing == maxStandingBendLabels) continue;

                    var placed = new DeferredLabel
                    {
                        Text = label,
                        X = labelX,
                        Y = bendY - 2f - th,
                        Scale = textScale,
                        TextYOffset = 1f,
                        TextColor = whiteAlpha,
                        BgColor = Color.FromArgb(alpha, (byte)(c.R / 4), (byte)(c.G / 4), (byte)(c.B / 4)),
                        OutlineColor = default,
                        HasBox = true
                    };
                    deferredLabels.Add(placed);
                    standingRights[standing] = labelX + boxW;
                    standingSteps[standing] = bend.Step;
                    standing++;
                }
            }

            // 0.14.3 A technique refresh: arrowhead on the bend curve's tip,
            // aligned with the final segment so a rise reads "bend up" and a
            // release reads "bend down" at a glance. Skipped for flat holds.
            if (polish)
            {
                var segDx = prevX - prevPrevX;
                var segDy = prevY - prevPrevY;
                if (Math.Abs(segDy) > 2f)
                {
                    var segLen = (float)Math.Sqrt(segDx * segDx + segDy * segDy);
                    var ux = segDx / segLen;
                    var uy = segDy / segLen;
                    var nx = -uy;
                    var ny = ux;
                    const float headLen = 6f;
                    const float headHalfW = 3.2f;
                    // The arrowhead tip extends headLen past the last bend point,
                    // which sits AT the sustain end when a bend reaches its peak
                    // there — so the tip pokes past the note end (and thus past the
                    // hand-shape bar, owner report M30). Keep the whole arrowhead
                    // inside the note: cap the tip at the sustain end and shift the
                    // base back with it so a full-size head still points to the end.
                    var tipX = prevX + ux * headLen;
                    var baseX = prevX;
                    if (tipX > sustainEndX)
                    {
                        var back = tipX - sustainEndX;
                        tipX -= back;
                        baseX -= back;
                    }

                    batcher.AddTriangle(tipX, prevY + uy * headLen,
                        baseX + nx * headHalfW, prevY + ny * headHalfW,
                        baseX - nx * headHalfW, prevY - ny * headHalfW, bendInk);
                }
            }

            if (data.Sustain > 0)
            {
                batcher.AddThickLine(prevX, prevY, x + (float)(data.Sustain * rc.CurrentZoom), lastBendY, 2f,
                    bendInk);
            }
        }

        // Selection / hover halo (0.15.5 D2): a soft ring sprite just outside
        // the head (existing type-2 annulus, one quad, only for the few
        // selected/hovered notes per frame) so the active note glows instead
        // of relying on the border colour alone.
        if (isSelected || isHovered)
        {
            var glowR = outerR * 1.5f;
            var glowColor = Color.FromArgb(isSelected ? (byte)90 : (byte)60,
                borderColor.R, borderColor.G, borderColor.B);
            batcher.AddSprite(x - glowR, y - glowR, glowR * 2f, glowR * 2f, glowColor, 2f);
        }

        if (isLinkedTarget)
        {
            DrawGemBody(batcher, x, y, outerR, innerR, headRim, gemTop, gemBottom, darkColor, alpha, data.Dimmed,
                polish, isSelected, isHovered);

            if (rc.DrawNoteLabels)
            {
                TextRenderHelper.DrawScaledText(batcher, RenderStringCache.Fret(data.Fret), x, y,
                    ChartEditorConstants.RenderMetrics.DefaultTextScale, numberColor, true, true);
            }

            return;
        }

        if (isAccent)
        {
            if (harmonic != HarmonicType.None)
            {
                var hOuter = ChartEditorConstants.RenderMetrics.HarmonicOuterSize;
                batcher.AddThickLine(x, y - hOuter, x - hOuter, y, 2.5f, baseColor);
                batcher.AddThickLine(x - hOuter, y, x, y + hOuter, 2.5f, baseColor);
                batcher.AddThickLine(x, y + hOuter, x + hOuter, y, 2.5f, baseColor);
                batcher.AddThickLine(x + hOuter, y, x, y - hOuter, 2.5f, baseColor);
            }
            else
            {
                batcher.AddSprite(x - 16f, y - 16f, 32f, 32f, baseColor, 2f);
            }
        }

        if (harmonic != HarmonicType.None)
        {
            // The diamond IS the harmonic's technique glyph, so a repeat keeps it at full colour.
            var diamondFill = GhostFill(Color.FromArgb(alpha, (byte)(c.R / 2), (byte)(c.G / 2), (byte)(c.B / 2)),
                data.IsPreview);
            batcher.AddQuad(x, y - innerR, x - innerR, y, x, y + innerR, x + innerR, y, diamondFill, 0f, 0, 0, 0, 0);
            batcher.AddThickLine(x, y - outerR, x - outerR, y, strokeThick, borderColor);
            batcher.AddThickLine(x - outerR, y, x, y + outerR, strokeThick, borderColor);
            batcher.AddThickLine(x, y + outerR, x + outerR, y, strokeThick, borderColor);
            batcher.AddThickLine(x + outerR, y, x, y - outerR, strokeThick, borderColor);
            if (harmonic == HarmonicType.Pinch)
                batcher.AddThickLine(x - outerR, y - outerR, x - outerR, y + outerR, 2f, baseColor);
        }
        else
        {
            DrawGemBody(batcher, x, y, outerR, innerR, headRim, gemTop, gemBottom, darkColor, alpha, data.Dimmed,
                polish, isSelected, isHovered);
        }

        if (mute is MuteType.Full or MuteType.Palm)
        {
            var outerMuteColor = Color.FromArgb(alpha, ChartTheme.GrayColor.R, ChartTheme.GrayColor.G,
                ChartTheme.GrayColor.B);
            var innerMuteColor = mute == MuteType.Full
                ? whiteAlpha
                : Color.FromArgb(alpha, ChartTheme.TopMarginBgColor.R, ChartTheme.TopMarginBgColor.G,
                    ChartTheme.TopMarginBgColor.B);
            var s = baseRadius * ChartEditorConstants.RenderMetrics.MuteIconScale;
            // 0.14.3 A: consistent cross-hatch weights — the outline and core
            // strokes match the other technique glyphs' outline/fill ratio
            // (4.0/1.8 under polish; the pre-0.14.3 4.5/2.0 otherwise).
            var muteOuterW = polish ? 4.0f : 4.5f;
            var muteInnerW = polish ? 1.8f : 2.0f;
            // The X as four ticks at the rim, framing a clear centre (see MuteTickSpan).
            var (from, to) = MuteTickSpan(s);
            for (var dx = -1; dx <= 1; dx += 2)
            {
                for (var dy = -1; dy <= 1; dy += 2)
                {
                    batcher.AddThickLine(x + dx * from, y + dy * from, x + dx * to, y + dy * to, muteOuterW, outerMuteColor);
                    batcher.AddThickLine(x + dx * from, y + dy * from, x + dx * to, y + dy * to, muteInnerW, innerMuteColor);
                }
            }
        }

        DrawHopoIcon(batcher, x, y, baseRadius, hopo, alpha, whiteAlpha, polish);
        DrawBassPickingIcon(batcher, x, y, baseRadius, (BassPickingType)data.BassPicking, alpha, whiteAlpha);

        if (rc.DrawNoteLabels)
        {
            var textScale = ChartEditorConstants.RenderMetrics.DefaultTextScale;
            // A muted head's centre is clear (MuteTickSpan), so its number is white like
            // every other head's; no grey plate is needed to rescue it from the X.
            TextRenderHelper.DrawScaledText(batcher, RenderStringCache.Fret(data.Fret), x, y, textScale, numberColor, true, true);
        }
    }

    /// <summary>Circular gem body (0.14.3 A): border ring + fill + specular
    /// cap. With polish on, the fill is a top→bottom vertical gradient
    /// (lighter crown, darker base — one gradient sprite instead of the flat
    /// disc) and selected/hovered heads get a brighter core so the active
    /// note pops beyond its halo. Polish off restores the flat 0.15.5 disc.</summary>
    /// <summary>Alpha for a preview ghost's FILL. Strictly far below the ghost
    /// rim's 128 so the gem reads hollow — an insertion point, not a note.</summary>
    internal const byte PreviewFillAlpha = 30;

    /// <summary>Hollow-ghost fill: previews keep their colour but drop the
    /// fill to a wash (<see cref="PreviewFillAlpha" />); committed notes pass
    /// through untouched. Pinned by <c>UI/Input/PreviewGhostStyleTests</c>.</summary>
    internal static Color GhostFill(Color fill, bool isPreview) =>
        isPreview ? Color.FromArgb(PreviewFillAlpha, fill.R, fill.G, fill.B) : fill;

    private static void DrawGemBody(TimelineBatcher batcher, float x, float y, float outerR, float innerR,
        Color borderColor, Color gemTop, Color gemBottom, Color darkColor, byte alpha, bool dimmed,
        bool polish, bool isSelected, bool isHovered)
    {
        batcher.AddSprite(x - outerR, y - outerR, outerR * 2f, outerR * 2f, borderColor, 1f);
        if (polish)
        {
            batcher.AddSpriteGradientV(x - innerR, y - innerR, innerR * 2f, innerR * 2f, gemTop, gemBottom, 1f);
            if (isSelected || isHovered)
            {
                // Brighter core: a soft white centre disc under the fret
                // label — only for the handful of active notes per frame.
                var coreR = innerR * 0.55f;
                var coreColor = Color.FromArgb((byte)(alpha * (isSelected ? 0.30f : 0.18f)), 255, 255, 255);
                batcher.AddSprite(x - coreR, y - coreR, coreR * 2f, coreR * 2f, coreColor, 1f);
            }
        }
        else
        {
            batcher.AddSprite(x - innerR, y - innerR, innerR * 2f, innerR * 2f, darkColor, 1f);
        }

        DrawSpecularCap(batcher, x, y, innerR, alpha, dimmed);
    }

    /// <summary>Subtle spherical shading (0.15.5 D2): a very-low-alpha white
    /// cap on the upper third of circular note heads. One extra AA-circle
    /// sprite per head, colour computed on the stack — no allocation. The
    /// fret label draws after it, so text contrast is untouched. Harmonic
    /// diamonds skip it (shape mismatch).</summary>
    private static void DrawSpecularCap(TimelineBatcher batcher, float x, float y, float innerR, byte alpha,
        bool dimmed)
    {
        var capR = innerR * 0.55f;
        var capColor = RepeatChordInk.Darken(Color.FromArgb((byte)(alpha * 0.14f), 255, 255, 255), dimmed);
        batcher.AddSprite(x - capR, y - capR - innerR * 0.28f, capR * 2f, capR * 2f, capColor, 1f);
    }

    /// <summary>
    /// Hammer-on / pull-off / tap icons drawn above the note head with
    /// distinct shapes so they're never confused at a glance:
    ///   • Hammer-on: solid up-triangle (white) — energy goes UP into the note
    ///   • Pull-off : solid down-triangle (white) — energy goes DOWN off the note
    ///   • Tap      : circle with inner dot (sky-blue) — clearly different shape
    /// </summary>
    private static void DrawHopoIcon(TimelineBatcher batcher, float x, float y, float baseRadius, HopoType hopo,
        byte alpha, Color whiteAlpha, bool polish)
    {
        if (hopo == HopoType.None) return;

        var iconY = y - baseRadius - 6f;
        var outlineCol = Color.FromArgb(alpha, ChartTheme.TopMarginBgColor.R, ChartTheme.TopMarginBgColor.G,
            ChartTheme.TopMarginBgColor.B);

        // 0.14.3 A: soft halo behind the HOPO/tap glyph so the technique
        // marker reads at a glance against busy waveform/grid backgrounds.
        // One low-alpha AA-circle sprite; tap keeps its sky-blue identity.
        if (polish)
        {
            const float haloR = 8.5f;
            var haloColor = hopo == HopoType.Tap
                ? Color.FromArgb((byte)(alpha * 0.20f), ChartTheme.SkyBlueColor.R, ChartTheme.SkyBlueColor.G,
                    ChartTheme.SkyBlueColor.B)
                : Color.FromArgb((byte)(alpha * 0.15f), 255, 255, 255);
            batcher.AddSprite(x - haloR, iconY - haloR, haloR * 2f, haloR * 2f, haloColor, 1f);
        }

        if (hopo == HopoType.Tap)
        {
            // Distinct circle-with-dot glyph in sky-blue. Gives Tap a
            // categorically different shape from HammerOn/PullOff so they
            // can never be confused at a glance.
            var tapColor = Color.FromArgb(alpha, ChartTheme.SkyBlueColor.R, ChartTheme.SkyBlueColor.G,
                ChartTheme.SkyBlueColor.B);
            const float ringR = 5f;
            const float dotR = 1.6f;
            // Outline ring + inner fill ring — one extra sprite for ring effect.
            batcher.AddSprite(x - ringR - 1f, iconY - ringR - 1f, (ringR + 1f) * 2f, (ringR + 1f) * 2f, outlineCol, 1f);
            batcher.AddSprite(x - ringR, iconY - ringR, ringR * 2f, ringR * 2f, tapColor, 1f);
            batcher.AddSprite(x - dotR, iconY - dotR, dotR * 2f, dotR * 2f, whiteAlpha, 1f);
            return;
        }

        // Hammer-on / Pull-off — solid filled triangles. Direction encodes
        // the technique (up = hammer, down = pull) — matches musical intuition.
        const float hw = 5f;
        const float hh = 6f;
        var dir = hopo == HopoType.PullOff ? -hh : hh;
        var sign = Math.Sign(dir);

        // Outline triangle (1px larger on all sides)
        batcher.AddTriangle(x - hw - 1f, iconY - dir - sign * 1f, x, iconY + sign * 1f,
            x + hw + 1f, iconY - dir - sign * 1f, outlineCol);
        batcher.AddTriangle(x - hw, iconY - dir, x, iconY, x + hw, iconY - dir, whiteAlpha);
    }

    /// <summary>
    /// Slap / Pop icons drawn below the note head. Slap = bold "S" glyph
    /// (rounded square with stroke), Pop = upward arrow burst — visually
    /// distinct from each other and from hopo techniques above the note.
    /// </summary>
    private static void DrawBassPickingIcon(TimelineBatcher batcher, float x, float y, float baseRadius,
        BassPickingType pick, byte alpha, Color whiteAlpha)
    {
        if (pick == BassPickingType.None) return;

        var iconY = y + baseRadius + 6f;
        var outlineCol = Color.FromArgb(alpha, ChartTheme.TopMarginBgColor.R, ChartTheme.TopMarginBgColor.G,
            ChartTheme.TopMarginBgColor.B);

        if (pick == BassPickingType.Slap)
        {
            // Bold rounded "S" tile in amber — slap = strike, hot color.
            var slapColor = Color.FromArgb(alpha, ChartTheme.AmberColor.R, ChartTheme.AmberColor.G,
                ChartTheme.AmberColor.B);
            const float w = 11f;
            const float h = 9f;
            batcher.AddRoundRect(x - w * 0.5f - 1f, iconY - h * 0.5f - 1f, w + 2f, h + 2f, 3f, outlineCol);
            batcher.AddRoundRect(x - w * 0.5f, iconY - h * 0.5f, w, h, 2.5f, slapColor);
            // Inset white "S"-like stripes — top bar, mid bar, bottom bar offset.
            batcher.AddRect(x - 3.5f, iconY - 3f, 7f, 1.5f, whiteAlpha);
            batcher.AddRect(x - 3.5f, iconY - 0.75f, 7f, 1.5f, whiteAlpha);
            batcher.AddRect(x - 3.5f, iconY + 1.5f, 7f, 1.5f, whiteAlpha);
            return;
        }

        // Pop = upward chevron / pluck arrow in emerald — pull-up gesture.
        var popColor = Color.FromArgb(alpha, ChartTheme.EmeraldColor.R, ChartTheme.EmeraldColor.G,
            ChartTheme.EmeraldColor.B);
        const float ph = 6f;
        const float pw = 5f;
        // Outline + filled upward triangle (apex points up = pluck direction).
        batcher.AddTriangle(x - pw - 1f, iconY + ph * 0.5f + 1f, x + pw + 1f, iconY + ph * 0.5f + 1f,
            x, iconY - ph * 0.5f - 1f, outlineCol);
        batcher.AddTriangle(x - pw, iconY + ph * 0.5f, x + pw, iconY + ph * 0.5f, x, iconY - ph * 0.5f, popColor);
        // Small dot above the arrow apex = "snap" effect.
        batcher.AddSprite(x - 1.2f, iconY - ph * 0.5f - 4f, 2.4f, 2.4f, popColor, 1f);
    }

    private static string FormatBendLabel(float step)
    {
        return Math.Abs(step - 1f) < 0.01f ? "1/2" :
            Math.Abs(step - 2f) < 0.01f ? "Full" :
            Math.Abs(step - 3f) < 0.01f ? "1 1/2" :
            Math.Abs(step - 4f) < 0.01f ? "2" :
            RenderStringCache.BendStepLabel(step);
    }
}
