// ReSharper disable InconsistentNaming -- private static brush/pen fields use _camelCase to pair with same-named public PascalCase properties.
using Avalonia.Media;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Timeline2D;
using DotCharter.UI.Constants;
using DotCharter.UI.Controls.Layers.Helpers;
using DotCharter.UI.Controls.Themes;
using DotCharter.UI.Editing.Tracks;

namespace DotCharter.UI.Controls.Layers;

public class BackgroundLayer : ITimelineRenderLayer
{
    /// <summary>When true (the default) <see cref="Render"/> also paints
    /// beat / measure / subdivision lines. Set to false when a separate
    /// <see cref="BeatGridLayer"/> is responsible for those — see
    /// <see cref="ChartRenderer"/> where the grid is intentionally drawn
    /// after the waveform so it remains visible against it.</summary>
    public bool IncludeBeatGrid { get; set; } = true;

    public void Render(TimelineBatcher batcher, ChartRenderContext rc)
    {
        if (!rc.DrawLabels) return;

        DrawBackgroundRects(batcher, rc);
        DrawHorizontalGuides(batcher, rc);
        if (IncludeBeatGrid) DrawBeatsAndMeasures(batcher, rc);
        DrawRuler(batcher, rc);
        DrawLoopRegion(batcher, rc);
        DrawBookmarks(batcher, rc);
        DrawAnnotations(batcher, rc);
    }

    // The editor loop region (v0.35 stage 3). It silently re-seeks the transport
    // while it is on, so it must be visible — the same reason the string-edit
    // mask is drawn on the lane headers. A marked-but-disabled region still
    // draws, dimmed, because the punch points are worth keeping on screen.
    private static readonly Color LoopBandColor = Color.FromArgb(26, 96, 165, 250);
    private static readonly Color LoopBandIdleColor = Color.FromArgb(12, 148, 163, 184);
    private static readonly Color LoopPostColor = Color.FromArgb(220, 96, 165, 250);
    private static readonly Color LoopPostIdleColor = Color.FromArgb(120, 148, 163, 184);

    private void DrawLoopRegion(TimelineBatcher batcher, ChartRenderContext rc)
    {
        var topY = rc.TopMargin;
        var bottomY = (float)rc.Height - rc.BottomMargin;

        if (rc.LoopRegion is { } loop)
        {
            // Cull as a SPAN, not per edge: a loop wider than the viewport has
            // both posts off-screen and still has to tint the whole visible run.
            if (loop.EndMs >= rc.ViewStartMs && loop.StartMs <= rc.ViewEndMs)
            {
                var startX = (float)((loop.StartMs - rc.ViewStartMs) * rc.CurrentZoom);
                var endX = (float)((loop.EndMs - rc.ViewStartMs) * rc.CurrentZoom);
                var clampedStart = Math.Max(0f, startX);
                var clampedEnd = Math.Min((float)rc.Width, endX);

                if (clampedEnd > clampedStart)
                {
                    batcher.AddRect(clampedStart, topY, clampedEnd - clampedStart, bottomY - topY,
                        rc.LoopEnabled ? LoopBandColor : LoopBandIdleColor);
                }

                var postColor = rc.LoopEnabled ? LoopPostColor : LoopPostIdleColor;
                if (startX >= 0 && startX <= rc.Width)
                    batcher.AddVerticalLineAa(startX, topY, 2f, bottomY - topY, postColor);
                if (endX >= 0 && endX <= rc.Width)
                    batcher.AddVerticalLineAa(endX, topY, 2f, bottomY - topY, postColor);
            }
        }
        else if (rc.LoopPendingStartMs is { } pending)
        {
            // Half-marked: loop start punched in, end not. Without this the arm
            // is invisible and the user cannot tell whether the key registered.
            var x = (float)((pending - rc.ViewStartMs) * rc.CurrentZoom);
            if (x >= 0 && x <= rc.Width)
                batcher.AddVerticalLineAa(x, topY, 2f, bottomY - topY, LoopPostIdleColor);
        }
    }

    /// <summary>Public entry point used by <see cref="BeatGridLayer"/> to
    /// paint just the beat / measure / subdivision lines.</summary>
    internal void RenderBeatGrid(TimelineBatcher batcher, ChartRenderContext rc)
    {
        if (!rc.DrawLabels) return;
        DrawBeatsAndMeasures(batcher, rc);
    }

    private void DrawBookmarks(TimelineBatcher batcher, ChartRenderContext rc)
    {
        var bookmarks = rc.Editor.Bookmarks;
        if (bookmarks.Count == 0) return;

        var topY = rc.TopMargin;
        var bottomY = (float)rc.Height - rc.BottomMargin;
        var bmColor = ChartTheme.BookmarkColor;
        // Indexed, never foreach: Bookmarks is an ObservableCollection<T>, whose
        // enumerator is only reachable through IEnumerable<T> and is therefore
        // BOXED — one allocation per layer per frame. See the rule comments in
        // FrameSnapshot.Build.cs / SnapshotUpdateContext.cs; pinned here by
        // UI/Rendering/LayerCollectionEnumerationLintTests.
        for (var i = 0; i < bookmarks.Count; i++)
        {
            var bm = bookmarks[i];
            if (bm.Time < rc.ViewStartMs - ChartEditorConstants.RenderMetrics.CullingBufferMs ||
                bm.Time > rc.ViewEndMs + ChartEditorConstants.RenderMetrics.CullingBufferMs)
            {
                continue;
            }

            var x = (float)((bm.Time - rc.ViewStartMs) * rc.CurrentZoom);
            if (x < 0 || x > rc.Width) continue;
            batcher.AddVerticalLineAa(x, topY, 2f, bottomY - topY, bmColor);
            // Small downward-pointing triangle flag at the top of the bookmark
            // line — gives an at-a-glance landmark in the ruler area.
            batcher.AddTriangle(x - 5f, topY, x + 5f, topY, x, topY + 7f, bmColor);
            batcher.AddText(RenderStringCache.Fret(bm.Index), x + 6f, topY + 1f, bmColor);
        }
    }

    // Charter annotations (Notes panel): pennant markers in the top margin so
    // inline notes/reminders/marks are visible in context on the timeline,
    // not only in the inspector list. Resolved items stay as dimmed stubs.
    private static readonly Color AnnotationNoteColor = Color.Parse("#60A5FA");
    private static readonly Color AnnotationReminderColor = Color.Parse("#F59E0B");
    private static readonly Color AnnotationMarkColor = Color.Parse("#7AB6FF");

    private void DrawAnnotations(TimelineBatcher batcher, ChartRenderContext rc)
    {
        var annotations = rc.Editor.Annotations;
        if (annotations.Count == 0) return;

        var topY = rc.TopMargin;
        // Indexed for the same reason DrawBookmarks is — Annotations is an
        // ObservableCollection<T> and a foreach boxes its enumerator every frame.
        for (var i = 0; i < annotations.Count; i++)
        {
            var a = annotations[i];
            if (a.TimeMs < rc.ViewStartMs - ChartEditorConstants.RenderMetrics.CullingBufferMs ||
                a.TimeMs > rc.ViewEndMs + ChartEditorConstants.RenderMetrics.CullingBufferMs)
            {
                continue;
            }

            var x = (float)((a.TimeMs - rc.ViewStartMs) * rc.CurrentZoom);
            if (x < 0 || x > rc.Width) continue;

            var color = a.Kind switch
            {
                ChartAnnotationKind.Reminder => AnnotationReminderColor,
                ChartAnnotationKind.Mark => AnnotationMarkColor,
                _ => AnnotationNoteColor
            };
            if (a.Resolved) color = Color.FromArgb(90, color.R, color.G, color.B);

            // Pennant flag pointing right (visually distinct from the
            // bookmark's downward triangle) on a short stem in the ruler.
            batcher.AddVerticalLineAa(x, topY - 14f, 1.5f, 14f, color);
            batcher.AddTriangle(x, topY - 14f, x + 9f, topY - 10.5f, x, topY - 7f, color);
            if (!a.Resolved && a.Text.Length is > 0 and <= 32)
                batcher.AddText(a.Text, x + 12f, topY - 15f, color);
        }
    }

    private void DrawBackgroundRects(TimelineBatcher batcher, ChartRenderContext rc)
    {
        var w = (float)rc.Width;
        var h = (float)rc.Height;
        batcher.AddRect(0, 0, w, rc.TopMargin, ChartTheme.TopMarginBgColor);
        batcher.AddRect(0, h - rc.BottomMargin, w, rc.BottomMargin, ChartTheme.BottomMarginBgColor);

        // Subtle row banding inside the ribbon: alternate visible rows get a
        // very faint white wash (alpha 6) to give the ribbon a structured
        // "list" feel without explicit separator lines. This reads better
        // than hairlines because the bands are continuous and don't create
        // hard edges that compete with the chip outlines.
        DrawRibbonRowBanding(batcher, rc, w);

        // Soft inset shadows under the top margin and over the bottom margin
        // give the playfield a subtle "recessed panel" depth cue. One quad
        // each, fully transparent at the bottom — zero impact on note
        // legibility but a clear modern touch.
        const float shadowDepth = 8f;
        batcher.AddQuadGradient(
            0, rc.TopMargin, 0, rc.TopMargin + shadowDepth,
            w, rc.TopMargin + shadowDepth, w, rc.TopMargin,
            InnerShadowOpaque, InnerShadowTransparent, InnerShadowTransparent, InnerShadowOpaque);
        batcher.AddQuadGradient(
            0, h - rc.BottomMargin - shadowDepth, 0, h - rc.BottomMargin,
            w, h - rc.BottomMargin, w, h - rc.BottomMargin - shadowDepth,
            InnerShadowTransparent, InnerShadowOpaque, InnerShadowOpaque, InnerShadowTransparent);
    }

    private static void DrawRibbonRowBanding(TimelineBatcher batcher, ChartRenderContext rc, float width)
    {
        var rowH = rc.Ribbon.RowPitch;
        // Tint every other visible row. Order matters so the banding alternates
        // even when the user hides individual ribbon rows.
        var bandIndex = 0;
        if (rc.Ribbon.ShowTones)
        {
            bandIndex++;
        }
        if (rc.Ribbon.ShowSections)
        {
            if ((bandIndex++ & 1) == 1)
                batcher.AddRect(0, rc.Ribbon.SectionY, width, rowH, RibbonRowBand);
        }
        if (rc.Ribbon.ShowPhrases)
        {
            if ((bandIndex++ & 1) == 1)
                batcher.AddRect(0, rc.Ribbon.PhraseY, width, rowH, RibbonRowBand);
        }
        if (rc.Ribbon.ShowLyrics)
        {
            if ((bandIndex++ & 1) == 1)
                batcher.AddRect(0, rc.Ribbon.LyricY, width, rowH, RibbonRowBand);
        }
        if (rc.Ribbon.ShowLinks)
        {
            if ((bandIndex & 1) == 1)
                batcher.AddRect(0, rc.Ribbon.LinkY, width, rowH, RibbonRowBand);
        }
    }

    private static readonly Color RibbonRowBand = Color.FromArgb(8, 255, 255, 255);

    private static readonly Color InnerShadowOpaque = Color.FromArgb(60, 0, 0, 0);
    private static readonly Color InnerShadowTransparent = Color.FromArgb(0, 0, 0, 0);
    private static readonly Color AlternateMeasureTint = Color.FromArgb(8, 255, 255, 255);

    private void DrawHorizontalGuides(TimelineBatcher batcher, ChartRenderContext rc)
    {
        for (var i = 1; i <= ChartEditorConstants.RenderMetrics.MaxGuideLines; i++)
        {
            batcher.AddHorizontalLineAa(0, ChartEditorConstants.RenderMetrics.GuideLineSpacing * i, (float)rc.Width,
                ChartEditorConstants.RenderMetrics.StandardLineWidth, ChartTheme.GuideColor);
        }

        // Structural separator bar between the event ribbon and the note
        // lanes — RibbonLayout reserves SeparatorHeight for it, and it uses
        // the same visual language as the pane splitters (solid bar with
        // light hairlines) so the event area and the charting area read as
        // two distinct regions.
        var sepTop = rc.TopMargin - Editing.Tracks.RibbonLayout.SeparatorHeight;
        batcher.AddRect(0, sepTop, (float)rc.Width, Editing.Tracks.RibbonLayout.SeparatorHeight, SeparatorFill);
        batcher.AddRect(0, sepTop, (float)rc.Width, 1f, SeparatorEdge);
        batcher.AddRect(0, rc.TopMargin - 1f, (float)rc.Width, 1f, SeparatorEdge);

        batcher.AddHorizontalLineAa(0, (float)rc.Height - rc.BottomMargin, (float)rc.Width, 1.5f,
            ChartTheme.BorderColor);
    }

    // The ribbon/lane separator bar. It sits directly under the ruler and is
    // the one solid, fully opaque block the timeline draws, so its hue is what
    // the eye compares the surrounding chrome against — it was #22242C, a
    // blue-grey, beside an editor whose every other surface is violet-dark.
    // Deliberately NOT a theme token: it is structural, like the pane
    // splitters it copies, and it must stay legible under every pack.
    private static readonly Color SeparatorFill = Color.FromArgb(255, 26, 23, 38);
    private static readonly Color SeparatorEdge = Color.FromArgb(110, 169, 159, 196);

    // Visual hierarchy (0.15.5 D3): measure > beat > subdivision. The theme
    // supplies the base colours; these multipliers guarantee the opacity
    // STEPS between the three line ranks regardless of the active palette,
    // so the grid reads calmer at a glance. Applied to the colour's own
    // alpha at draw time — no theme/token loading is touched. 0.14.3 B
    // widens the steps (0.78/0.55 → 0.72/0.42) when the polish pass is on
    // and adds a spacing-driven fade ramp so the hierarchy reads at every
    // zoom instead of popping at the 2px skip threshold.
    private const float BeatLineOpacity = 0.78f;
    private const float SubdivisionOpacity = 0.55f;
    private const float BeatLineOpacityPolish = 0.72f;
    private const float SubdivisionOpacityPolish = 0.42f;

    private static Color Fade(Color c, float opacity)
        => Color.FromArgb((byte)(c.A * opacity), c.R, c.G, c.B);

    /// <summary>Spacing-driven alpha ramp: 0 at the 2px skip
    /// threshold, 1 at <see cref="ChartEditorConstants.RenderMetrics.GridLineFadeStartPx"/>
    /// and above. Extends the existing sub-pixel skip logic — a line that
    /// would otherwise pop in at full alpha right above 2px now eases in.</summary>
    private static float SpacingFade(float spacingPx)
    {
        const float min = ChartEditorConstants.RenderMetrics.MinGridLineSpacingPx;
        const float full = ChartEditorConstants.RenderMetrics.GridLineFadeStartPx;
        if (spacingPx >= full) return 1f;
        if (spacingPx <= min) return 0f;
        return (spacingPx - min) / (full - min);
    }

    private void DrawBeatsAndMeasures(TimelineBatcher batcher, ChartRenderContext rc)
    {
        // The measure row shows at EVERY zoom. The pills are planned after the walk, from the
        // measures it collected (MeasurePillPolicy.Plan thins them by a stride).
        _measureSlots.Clear();
        _measureSlotBeats.Clear();

        // Tracks the most recent off-screen-left measure so its metadata
        // label stays pinned at the viewport edge until the next visible
        // measure replaces it (mirrors the FHP active-label sticky pattern
        // and fixes the bug where measure text vanishes the moment its
        // measure line scrolls out of view).
        var stickyMeasureIdx = -1;

        // Alternating measure stripe: every odd-numbered measure gets a
        // very faint white wash so the eye picks up measure boundaries
        // even at high zoom-out where individual lines blur together.
        // Tracks the previous measure x to know where each stripe starts.
        var prevMeasureX = float.NaN;
        var prevMeasureNumber = -1;
        var stripeTop = rc.TopMargin;
        var stripeBottom = (float)rc.Height - rc.BottomMargin;

        // Sub-pixel gridline thinning: beats iterate in time order
        // so x is monotonic — skip any line landing closer than
        // MinGridLineSpacingPx to the last DRAWN line of its class.
        // Below ~2px apart the lines fuse into a solid wash on screen while
        // still costing an AA quad (or a stack of dashes) each.
        var lastBeatLineX = float.NegativeInfinity;
        var lastMeasureLineX = float.NegativeInfinity;
        var minLineSpacing = ChartEditorConstants.RenderMetrics.MinGridLineSpacingPx;

        // 0.14.3 B: hierarchy steps. Polish widens the alpha steps, gives
        // beats a width rank between measure and subdivision, and eases
        // tight lines in via the spacing fade ramp instead of a hard pop.
        var polish = Timeline2DVisuals.PolishEnabled;
        var beatOpacity = polish ? BeatLineOpacityPolish : BeatLineOpacity;
        var beatWidth = polish
            ? ChartEditorConstants.RenderMetrics.BeatLineWidth
            : ChartEditorConstants.RenderMetrics.StandardLineWidth;

        for (var i = 0; i < rc.Snapshot.BeatCount; i++)
        {
            var beat = rc.Snapshot.Beats[i];

            // Culling bounds check
            if (beat.Time < rc.ViewStartMs - ChartEditorConstants.RenderMetrics.CullingBufferMs ||
                beat.Time > rc.ViewEndMs + ChartEditorConstants.RenderMetrics.CullingBufferMs)
            {
                if (beat.IsMeasure && beat.Time <= rc.ViewStartMs) stickyMeasureIdx = i;
                continue;
            }

            var x = (float)((beat.Time - rc.ViewStartMs) * rc.CurrentZoom);

            if (beat.IsMeasure)
            {
                // Draw the stripe spanning [prevMeasureX, x] when the previous
                // measure is odd-numbered. Clipped to the playfield region.
                if (!float.IsNaN(prevMeasureX) && (prevMeasureNumber & 1) == 1)
                {
                    var sx = Math.Max(0f, prevMeasureX);
                    var ex = Math.Min((float)rc.Width, x);
                    if (ex > sx)
                        batcher.AddRect(sx, stripeTop, ex - sx, stripeBottom - stripeTop, AlternateMeasureTint);
                }

                prevMeasureX = x;
                prevMeasureNumber = beat.MeasureNumber;
            }

            if (x >= 0 && x <= rc.Width)
            {
                var width = beat.IsMeasure
                    ? ChartEditorConstants.RenderMetrics.MeasureLineWidth
                    : beatWidth;
                var color = beat.IsMeasure
                    ? ChartTheme.MeasureColor
                    : Fade(ChartTheme.BeatColor, beatOpacity);

                if (beat.IsMeasure)
                {
                    if (x - lastMeasureLineX >= minLineSpacing)
                    {
                        // Fade ramp: measure lines squeezing together while
                        // zooming out ease toward the 2px skip instead of
                        // popping out at full alpha.
                        if (polish)
                            color = Fade(color, SpacingFade(x - lastMeasureLineX));
                        batcher.AddVerticalLineAa(x, rc.TopMargin, width,
                            (float)rc.Height - rc.BottomMargin - rc.TopMargin, color);
                        lastMeasureLineX = x;
                        lastBeatLineX = x; // a plain beat right on top of a measure line adds nothing
                    }
                }
                else if (x - lastBeatLineX >= minLineSpacing && x - lastMeasureLineX >= minLineSpacing)
                {
                    if (polish)
                    {
                        color = Fade(color,
                            SpacingFade(Math.Min(x - lastBeatLineX, x - lastMeasureLineX)));
                    }

                    DrawDashedVerticalLine(batcher, x, rc.TopMargin, (float)rc.Height - rc.BottomMargin,
                        ChartEditorConstants.RenderMetrics.DashLength, ChartEditorConstants.RenderMetrics.GapLength,
                        width, color);
                    lastBeatLineX = x;
                }

                if (beat.IsAnchor)
                {
                    batcher.AddTriangle(x - 5f, rc.TopMargin, x + 5f, rc.TopMargin, x, rc.TopMargin + 6f,
                        ChartTheme.AnchorColor);
                }
            }
            else if (beat.IsMeasure && x < 0)
            {
                // Off-screen left but inside the culling buffer — still a
                // candidate for sticky-label rendering.
                stickyMeasureIdx = i;
            }

            // Measure metadata pill: collected here, planned and drawn after the
            // walk (DrawMeasureRow) so the thinning can see every measure in view.
            if (beat.IsMeasure && rc.Ribbon.ShowMeasureMetadata && x >= 0 && x <= rc.Width)
            {
                _measureSlots.Add(new MeasurePillPolicy.MeasureSlot(
                    beat.MeasureNumber, x, BpmChanged(rc, i), TimeSigChanged(rc, i)));
                _measureSlotBeats.Add(i);
            }

            DrawSubdivisions(batcher, rc, beat, i);
        }

        // Trailing stripe: if the last visible measure is odd, fill from
        // its x to the right edge of the viewport.
        if (!float.IsNaN(prevMeasureX) && (prevMeasureNumber & 1) == 1 && prevMeasureX < rc.Width)
        {
            var sx = Math.Max(0f, prevMeasureX);
            batcher.AddRect(sx, stripeTop, (float)rc.Width - sx, stripeBottom - stripeTop, AlternateMeasureTint);
        }

        if (rc.Ribbon.ShowMeasureMetadata) DrawMeasureRow(batcher, rc, stickyMeasureIdx);
    }

    // Reused per frame: the measures in view, and the beat index of each.
    private readonly List<MeasurePillPolicy.MeasureSlot> _measureSlots = [];
    private readonly List<int> _measureSlotBeats = [];
    private readonly List<MeasurePillPolicy.PlannedPill> _plannedPills = [];

    // The planner's width callback, cached so a frame allocates no closure; it
    // reads this frame's batcher and beats from the two fields beside it.
    private MeasurePillPolicy.PillWidth? _slotPillWidth;
    private TimelineBatcher? _frameBatcher;
    private RenderBeat[]? _frameBeats;

    private double SlotPillWidth(int slot, bool full, bool showSig)
    {
        var b = _frameBeats![_measureSlotBeats[slot]];
        return MeasurePillWidth(_frameBatcher!, b.MeasureNumber, b.Bpm, b.BeatsInMeasure, b.NoteDenominator,
            showSig, full);
    }

    /// <summary>
    ///     The measure row: every pill <see cref="MeasurePillPolicy.Plan" />
    ///     picks — the first in view, every tempo / meter change, one per stride —
    ///     then the sticky pill at the left edge, pushed out by the first of them.
    ///     Every pill states its measure number and its tempo, so the row reads at
    ///     any zoom.
    /// </summary>
    private void DrawMeasureRow(TimelineBatcher batcher, ChartRenderContext rc, int stickyMeasureIdx)
    {
        var beats = rc.Snapshot.Beats;
        _frameBatcher = batcher;
        _frameBeats = beats;
        MeasurePillPolicy.Plan(_measureSlots, _slotPillWidth ??= SlotPillWidth,
            firstCarriesContext: stickyMeasureIdx < 0, _plannedPills);
        _frameBatcher = null;
        _frameBeats = null;

        foreach (var pill in _plannedPills)
        {
            var idx = _measureSlotBeats[pill.Slot];
            var beat = beats[idx];
            DrawMeasurePill(batcher, (float)pill.Left, rc.Ribbon, beat.MeasureNumber, beat.Bpm,
                beat.BeatsInMeasure, beat.NoteDenominator, hasTimeSigChange: TimeSigChanged(rc, idx),
                hasBpmChange: BpmChanged(rc, idx), showSig: pill.ShowSig, full: pill.Full);
        }

        // Pin the latest off-screen-left measure at the viewport edge. It is the
        // left-edge context: number, tempo AND meter, always.
        if (stickyMeasureIdx < 0) return;
        var sticky = beats[stickyMeasureIdx];
        var nextLeft = _plannedPills.Count > 0 ? (float)_plannedPills[0].Left : float.PositiveInfinity;
        var full = true;
        var stickyWidth = MeasurePillWidth(batcher, sticky.MeasureNumber, sticky.Bpm,
            sticky.BeatsInMeasure, sticky.NoteDenominator, showSig: true, full: true);
        var fixedX = ChartEditorConstants.RenderMetrics.ActiveLabelFixedX;
        if (fixedX + stickyWidth + (float)MeasurePillPolicy.MinPillGapPx > nextLeft)
        {
            // Not room for the full form before the first in-flow pill: the compact one.
            full = false;
            stickyWidth = MeasurePillWidth(batcher, sticky.MeasureNumber, sticky.Bpm,
                sticky.BeatsInMeasure, sticky.NoteDenominator, showSig: true, full: false);
        }

        // It never overlaps the first in-flow pill: that pill pushes it out,
        // sticky-header style (see StickyLabel).
        var stickyX = StickyLabel.PinnedX(fixedX, stickyWidth, nextLeft - (float)MeasurePillPolicy.MinPillGapPx);
        if (StickyLabel.IsVisible(stickyX, stickyWidth))
        {
            DrawMeasurePill(batcher, stickyX, rc.Ribbon, sticky.MeasureNumber, sticky.Bpm,
                sticky.BeatsInMeasure, sticky.NoteDenominator,
                hasTimeSigChange: TimeSigChanged(rc, stickyMeasureIdx),
                hasBpmChange: BpmChanged(rc, stickyMeasureIdx), showSig: true, full: full);
        }
    }

    /// <summary>
    /// Returns true when this measure's BPM differs from the previous
    /// measure's BPM (or it's the first measure). This highlights tempo
    /// changes in the metadata pill so they stand out at a glance.
    /// </summary>
    private static bool BpmChanged(ChartRenderContext rc, int beatIdx)
    {
        var cur = rc.Snapshot.Beats[beatIdx];
        for (var k = beatIdx - 1; k >= 0; k--)
        {
            var b = rc.Snapshot.Beats[k];
            if (!b.IsMeasure) continue;
            return MeasurePillPolicy.BpmDiffers(b.Bpm, cur.Bpm);
        }

        return false;
    }

    private static bool TimeSigChanged(ChartRenderContext rc, int beatIdx)
    {
        var cur = rc.Snapshot.Beats[beatIdx];
        for (var k = beatIdx - 1; k >= 0; k--)
        {
            var b = rc.Snapshot.Beats[k];
            if (!b.IsMeasure) continue;
            return b.BeatsInMeasure != cur.BeatsInMeasure || b.NoteDenominator != cur.NoteDenominator;
        }

        return false;
    }

    /// <summary>
    /// Measure metadata as a single flat chip — matches the event-ribbon
    /// chip language (radius=4, hairline border, no shadow, no skeu
    /// highlight). The measure number is always shown; the BPM and time-sig
    /// segments (each behind a 1-px hairline divider): the BPM always, the
    /// time-sig when <paramref name="showSig"/> says it carries
    /// new information (a change, or the first visible measure — see
    /// <see cref="MeasurePillPolicy"/>), so the row isn't a wall of identical
    /// "200 BPM · 4/4". Tempo / time-sig changes recolour their segment.
    /// </summary>
    /// <summary>
    ///     The pill sits in <paramref name="ribbon" />'s metadata BAND, centred
    ///     by <see cref="RibbonLayout.PillTop" /> in that layout's density.
    ///     It must not be drawn one pixel ABOVE the band it belongs to and 6 px
    ///     shorter than it, which would leave a third of the band's height doing
    ///     nothing at all.
    /// </summary>
    private static void DrawMeasurePill(TimelineBatcher batcher, float x, RibbonLayout ribbon, int measureNumber, double bpm,
        int beatsInMeasure, int noteDenominator, bool hasTimeSigChange, bool hasBpmChange,
        bool showSig, bool full)
    {
        var measureTxt = full
            ? RenderStringCache.MeasureNumberLabel(measureNumber)
            : RenderStringCache.MeasureNumberCompactLabel(measureNumber);
        var measureW = batcher.MeasureTextWidth(measureTxt);

        // Cached labels are keyed independently (M#, BPM, sig) so both forms
        // are zero-alloc. Every pill states its tempo: "120 BPM" in the
        // full form, "120" in the compact one.
        var bpmTxt = full ? RenderStringCache.BpmLabel(bpm) : RenderStringCache.BpmCompactLabel(bpm);
        var sigTxt = showSig ? RenderStringCache.TimeSigLabel(beatsInMeasure, noteDenominator) : null;
        var bpmW = batcher.MeasureTextWidth(bpmTxt);
        var sigW = sigTxt != null ? batcher.MeasureTextWidth(sigTxt) : 0f;

        const float radius = 4f;
        var h = ribbon.Pill;
        var y = ribbon.PillTop(ribbon.MetadataY);

        var pad = full ? HorizontalPad : CompactPad;
        var gap = full ? SegmentGap : CompactGap;
        var totalW = PillWidth(measureW, bpmW, sigW, sigTxt != null, full);

        // A 1-px ring under an inset body, both with anti-aliased corners (a ring of four
        // straight strips would notch the corners).
        batcher.AddRoundRectAa(x, y, totalW, h, radius, MeasureBorder);
        batcher.AddRoundRectAa(x + 1f, y + 1f, totalW - 2f, h - 2f, radius - 1f, MeasurePillBg);

        var cx = x + pad;
        var midY = y + h * 0.5f;

        batcher.AddText(measureTxt, cx, midY, MeasureMeasureText, centerY: true);
        cx += measureW;

        cx += gap;
        Divider(batcher, cx, y, h, full);
        cx += DividerWidth + gap;
        var bpmCol = hasBpmChange ? MeasureChangeAccent : MeasureTextStrong;
        batcher.AddText(bpmTxt, cx, midY, bpmCol, centerY: true);
        cx += bpmW;

        if (sigTxt != null)
        {
            cx += gap;
            Divider(batcher, cx, y, h, full);
            cx += DividerWidth + gap;
            var sigCol = hasTimeSigChange ? MeasureTimeChangeAccent : MeasureTextStrong;
            batcher.AddText(sigTxt, cx, midY, sigCol, centerY: true);
        }
    }

    // Pill inset / segment rhythm. Shared by the draw above and the width
    // probe below, which is what lets the sticky pill know whether it CLEARS
    // the first in-flow pill before it draws over it.
    private const float HorizontalPad = 8f;
    private const float SegmentGap = 7f;
    private const float DividerWidth = 1f;

    // The compact form ("17 · 120 · 3/4"): tighter inset and gaps, a dot for a divider.
    private const float CompactPad = 5f;
    private const float CompactGap = 3f;

    private static float PillWidth(float measureW, float bpmW, float sigW, bool hasSig, bool full)
    {
        var pad = full ? HorizontalPad : CompactPad;
        var gap = full ? SegmentGap : CompactGap;
        var total = pad + measureW + gap + DividerWidth + gap + bpmW;
        if (hasSig) total += gap + DividerWidth + gap + sigW;
        return total + pad;
    }

    /// <summary>The full form's hairline divider, or the compact form's middle dot.</summary>
    private static void Divider(TimelineBatcher batcher, float x, float y, float h, bool full)
    {
        if (full) batcher.AddRect(x, y + 4f, DividerWidth, h - 8f, MeasureDivider);
        else batcher.AddRect(x - 0.5f, y + h * 0.5f - 1f, 2f, 2f, MeasureDot);
    }

    /// <summary>How wide the pill for this measure WOULD be. Same cached
    /// labels and the same measurement the draw uses, so the two cannot
    /// disagree about where the pill ends.</summary>
    private static float MeasurePillWidth(TimelineBatcher batcher, int measureNumber, double bpm,
        int beatsInMeasure, int noteDenominator, bool showSig, bool full)
        => PillWidth(
            batcher.MeasureTextWidth(full
                ? RenderStringCache.MeasureNumberLabel(measureNumber)
                : RenderStringCache.MeasureNumberCompactLabel(measureNumber)),
            batcher.MeasureTextWidth(full ? RenderStringCache.BpmLabel(bpm) : RenderStringCache.BpmCompactLabel(bpm)),
            showSig ? batcher.MeasureTextWidth(RenderStringCache.TimeSigLabel(beatsInMeasure, noteDenominator)) : 0f,
            showSig, full);

    // Opaque: the anti-aliased corners overlap the body (TimelineBatcher.AddRoundRectAa). These are
    // the old translucent slate (215 / 30,35,48) and its border (120 / 60,70,90) as they fell on the lane.
    private static readonly Color MeasurePillBg = Color.FromRgb(29, 33, 45);
    private static readonly Color MeasureBorder = Color.FromRgb(42, 48, 62);
    private static readonly Color MeasureDivider = Color.FromArgb(50, 200, 210, 230);
    private static readonly Color MeasureDot = Color.FromArgb(150, 200, 210, 230);
    private static readonly Color MeasureMeasureText = Color.FromArgb(235, 125, 180, 220);
    private static readonly Color MeasureTextStrong = Color.FromArgb(225, 220, 226, 236);
    private static readonly Color MeasureChangeAccent = Color.FromArgb(245, 56, 189, 248);
    private static readonly Color MeasureTimeChangeAccent = Color.FromArgb(245, 245, 158, 11);

    private void DrawSubdivisions(TimelineBatcher batcher, ChartRenderContext rc, RenderBeat beat, int currentIndex)
    {
        if (rc.CurrentZoom <= ChartEditorConstants.RenderMetrics.ZoomThresholdDetailed || rc.Transport.GridDivisor <=
            ChartEditorConstants.RenderMetrics.MinimumGridDivisorForSubdivisions)
        {
            return;
        }

        var nextTime = currentIndex + 1 < rc.Snapshot.BeatCount
            ? rc.Snapshot.Beats[currentIndex + 1].Time
            : beat.Time + 500;

        var subDivisions = rc.Transport.GridDivisor;
        var msPerSub = (nextTime - beat.Time) / subDivisions;
        if (msPerSub <= 0) return;

        var pixelSpacing = msPerSub * rc.CurrentZoom;
        // Below ~1px the dashed sub-grid produces only moiré noise and silently
        // overflows the VBO at high GridDivisor; the human eye sees nothing
        // distinguishable. Identical visual result, ~100x fewer quads.
        if (pixelSpacing < 1.0)
            return;

        // Pre-cull the j range to the view, so we never iterate (or branch on)
        // subdivisions that lie off-screen. Equivalent to the prior per-j
        // "if (subX >= 0 && subX <= rc.Width)" check, but O(visible) instead
        // of O(subDivisions).
        var jStart = (int)Math.Ceiling((rc.ViewStartMs - beat.Time) / msPerSub);
        if (jStart < 1) jStart = 1;
        var jEnd = (int)Math.Floor((rc.ViewEndMs - beat.Time) / msPerSub);
        if (jEnd > subDivisions - 1) jEnd = subDivisions - 1;
        if (jEnd < jStart) return;

        var top = rc.TopMargin;
        var height = (float)rc.Height - rc.BottomMargin - top;
        var color = ChartTheme.BorderColor;
        // 0.14.3 B: subdivision rank — thinner line, wider alpha step, and a
        // spacing fade so a dense sub-grid melts away instead of popping at
        // the 1px cutoff. All folded into the hoisted alpha — free at draw
        // time (the ramp input, pixelSpacing, is constant across the loop).
        var polish = Timeline2DVisuals.PolishEnabled;
        var subOpacity = polish
            ? SubdivisionOpacityPolish * SpacingFade((float)pixelSpacing + 1f)
            : SubdivisionOpacity;
        // Fully faded-out sub-grid: identical pixels to not drawing at all,
        // so skip the quads entirely.
        if (subOpacity <= 0.004f) return;
        // Hoist Color → float once. With BeatCount × GridDivisor inner
        // iterations (potentially 10k+/frame) the per-vertex .R/.G/.B/.A
        // getter cost dominates. Profiler showed ~20 ms/frame total here.
        // SubdivisionOpacity folds the D3 hierarchy step into the hoisted
        // alpha — free at draw time.
        float cr = color.R / 255f, cg = color.G / 255f, cb = color.B / 255f,
            ca = color.A / 255f * subOpacity;
        const float dashLen = 2f;
        const float gapLen = 4f;
        // When subdivision spacing is denser than one full dash+gap cycle the
        // dash pattern visually fuses into a continuous line. Switch to a
        // single solid AA quad — visually identical at the eye, but ~75x
        // fewer quads vs the per-dash rectangles.
        var useSolid = pixelSpacing < dashLen + gapLen;
        var lineW = polish
            ? ChartEditorConstants.RenderMetrics.SubdivisionLineWidth
            : ChartEditorConstants.RenderMetrics.StandardLineWidth;

        // Pre-grow the general buffer once so the per-call EnsureCapacity
        // inside each AddVerticalLineAaRaw becomes a branch-predictable
        // fast-path compare.
        var iterations = jEnd - jStart + 1;
        var quadsPerIter = useSolid
            ? 1
            : Math.Max(1, (int)Math.Ceiling(height / (dashLen + gapLen)));
        batcher.EnsureQuadsCapacity(0, iterations * quadsPerIter);

        for (var j = jStart; j <= jEnd; j++)
        {
            var subX = (float)((beat.Time + j * msPerSub - rc.ViewStartMs) * rc.CurrentZoom);
            if (useSolid)
            {
                batcher.AddVerticalLineAaRaw(subX, top, lineW, height, cr, cg, cb, ca);
            }
            else
            {
                DrawDashedVerticalLineRaw(batcher, subX, top, top + height, dashLen, gapLen,
                    lineW, cr, cg, cb, ca);
            }
        }
    }

    /// <summary>
    ///     The TIME ruler: a tick and an <c>m:ss</c> label every
    ///     <see cref="TimeRulerPolicy.StepMs" /> — the smallest nice step (1 s, 2 s, 5 s …)
    ///     that holds the widest label in view, so the labels never run into each other
    ///     at any zoom, on multiples of the step so they do not jump while scrolling.
    /// </summary>
    private static void DrawRuler(TimelineBatcher batcher, ChartRenderContext rc)
    {
        var widest = batcher.MeasureTextWidth(RenderStringCache.RulerTime(Math.Max(0, rc.ViewEndMs)));
        var step = TimeRulerPolicy.StepMs(rc.CurrentZoom, widest + TimeRulerPolicy.LabelOffsetPx);
        for (var t = TimeRulerPolicy.FirstTickMs(rc.ViewStartMs, step); t <= rc.ViewEndMs; t += step)
        {
            var x = (float)((t - rc.ViewStartMs) * rc.CurrentZoom);
            if (x < 0 || x > rc.Width) continue;
            batcher.AddVerticalLineAa(x, (float)rc.Height - 20f, 1f, 5f, ChartTheme.MeasureColor);
            batcher.AddText(RenderStringCache.RulerTime(t), x + (float)TimeRulerPolicy.LabelOffsetPx,
                (float)rc.Height - 18f, ChartTheme.GrayColor);
        }
    }

    private void DrawDashedVerticalLine(TimelineBatcher batcher, float x, float startY, float endY, float dashLen,
        float gapLen, float thickness, Color color)
    {
        DrawDashedVerticalLineRaw(batcher, x, startY, endY, dashLen, gapLen, thickness,
            color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
    }

    private void DrawDashedVerticalLineRaw(TimelineBatcher batcher, float x, float startY, float endY, float dashLen,
        float gapLen, float thickness, float r, float g, float b, float a)
    {
        var y = startY;
        while (y < endY)
        {
            var h = Math.Min(dashLen, endY - y);
            batcher.AddVerticalLineAaRaw(x, y, thickness, h, r, g, b, a);
            y += dashLen + gapLen;
        }
    }
}
