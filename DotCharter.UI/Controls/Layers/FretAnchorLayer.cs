// ReSharper disable InconsistentNaming -- private static color channel fields use _camelCase to pair with same-named public PascalCase properties.
using Avalonia.Media;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Timeline2D;
using DotCharter.UI.Constants;
using DotCharter.UI.Controls.Layers.Helpers;
using DotCharter.UI.Controls.Themes;

namespace DotCharter.UI.Controls.Layers;

public class FretAnchorLayer : ITimelineRenderLayer
{
    public void Render(TimelineBatcher batcher, ChartRenderContext rc)
    {
        RefreshAmber();

        for (var i = 0; i < rc.Snapshot.AnchorCount; i++)
            DrawAnchor(batcher, rc, rc.Snapshot.Anchors[i], false);

        for (var i = 0; i < rc.Snapshot.PreviewAnchorCount; i++)
            DrawAnchor(batcher, rc, rc.Snapshot.PreviewAnchors[i], true);

        // A pending change's NEW anchors (the AI chart pass review), in the
        // EditTarget colour and LAST, so where one lands on a live anchor it reads as
        // "this becomes that" over the live chip, which is drawn faded. The colour
        // fields are re-read from the theme at the top of every frame.
        if (rc.Snapshot.ProposedAnchorCount == 0) return;
        var tint = ChartTheme.EditTargetColor;
        _amberR = _amberBrightR = tint.R;
        _amberG = _amberBrightG = tint.G;
        _amberB = _amberBrightB = tint.B;
        for (var i = 0; i < rc.Snapshot.ProposedAnchorCount; i++)
            DrawAnchor(batcher, rc, rc.Snapshot.ProposedAnchors[i], false, proposal: true);
    }

    /// <summary>A live anchor a pending change removes is drawn at this alpha, so the
    /// proposal over it reads first.</summary>
    internal const float ReplacedAlpha = 0.35f;

    private void DrawAnchor(TimelineBatcher batcher, ChartRenderContext rc, RenderAnchor a, bool isPreview,
        bool proposal = false)
    {
        var x = (float)((a.Time - rc.ViewStartMs) * rc.CurrentZoom);
        var endX = (float)((a.EndTime - rc.ViewStartMs) * rc.CurrentZoom);
        var w = Math.Max(endX - x, 18f);

        var rowH = ChartEditorConstants.RenderMetrics.AnchorTailHeight;
        var y = rc.FhpY;
        var midY = y + rowH * 0.5f;

        var alphaMul = isPreview ? 0.55f : a.IsEditTarget && !proposal ? ReplacedAlpha : 1f;
        var selected = a.IsSelected;
        var hovered = a.IsHovered;

        // THE SPAN: a slim track through the row's middle, stopping short of
        // the next anchor so two spans never merge. It is not a full-height
        // amber-tinted box with a hairline top and bottom, which would run the whole
        // width of the song as a yellow bar between two lines. Brighter when
        // hovered or selected.
        var trackAlpha = selected ? 230 : hovered ? 150 : 90;
        var track = Color.FromArgb((byte)(trackAlpha * alphaMul), _amberR, _amberG, _amberB);
        batcher.AddRect(x, midY - TrackHeight * 0.5f, Math.Max(2f, w - SpanGap), TrackHeight, track);

        // THE START: a short upright tick, so where the hand moves still reads
        // when the view is zoomed out too far for the fret chips.
        var tick = Color.FromArgb((byte)(235 * alphaMul), _amberR, _amberG, _amberB);
        batcher.AddRect(x, midY - TickHeight * 0.5f, 2f, TickHeight, tick);

        if (!rc.DrawLabels) return;

        var txt = RenderStringCache.Fret(a.Fret);
        var tw = batcher.MeasureTextWidth(txt);

        // Sticky label: keep the fret number visible at the left edge of the
        // viewport while the anchor extends off-screen, and slide it out with
        // the anchor's END rather than hanging it over the next anchor's chip
        // (StickyLabel).
        var chipW = Math.Max(tw + 10f, ChipHeight);
        var chipX = Math.Max(x,
            StickyLabel.PinnedX(ChartEditorConstants.RenderMetrics.ActiveLabelFixedX - 1f, chipW,
                x + w + StickyLabel.PushGapPx - 1f));
        if (!StickyLabel.IsVisible(chipX, chipW)) return;

        // THE FRET: an opaque amber chip with anti-aliased corners and dark
        // text — the section / phrase chips' language, one size smaller. A
        // selected anchor wears the same outside ring they do.
        var chipY = midY - ChipHeight * 0.5f;
        var chipText = Color.FromArgb((byte)(255 * alphaMul), 24, 18, 6);
        if (isPreview)
        {
            batcher.AddRoundRect(chipX, chipY, chipW, ChipHeight, ChipRadius,
                Color.FromArgb((byte)(255 * alphaMul), _amberR, _amberG, _amberB));
        }
        else
        {
            if (selected)
            {
                batcher.AddRoundRectAa(chipX - 1.5f, chipY - 1.5f, chipW + 3f, ChipHeight + 3f, ChipRadius + 1.5f,
                    Color.FromRgb((byte)Math.Min(255, _amberR + 70), (byte)Math.Min(255, _amberG + 70),
                        (byte)Math.Min(255, _amberB + 70)));
            }

            var fill = hovered
                ? Color.FromRgb(_amberBrightR, _amberBrightG, _amberBrightB)
                : Color.FromRgb(_amberR, _amberG, _amberB);
            batcher.AddRoundRectAa(chipX, chipY, chipW, ChipHeight, ChipRadius, fill);
        }

        batcher.AddText(txt, chipX + (chipW - tw) * 0.5f, midY, chipText, centerY: true);
    }

    /// <summary>The span's track: thin enough to read as a range, not a bar.</summary>
    internal const float TrackHeight = 3f;

    /// <summary>The gap a span leaves before the next anchor starts.</summary>
    internal const float SpanGap = 3f;

    internal const float TickHeight = 10f;
    internal const float ChipHeight = 14f;
    internal const float ChipRadius = 4f;

    // Pre-cached amber components so we never allocate Color structs in the
    // hot per-anchor render path. Sourced from ChartTheme.AnchorColor, which is
    // the theme's Phrase token — so it MOVES, and these have to move with it.
    // They were `static readonly`, captured at type init: the FHP overlay went
    // on drawing the launch palette's amber through every theme switch,
    // silently, while every other chart element followed. Re-read once per
    // FRAME rather than through a ChartTheme.ThemeChanged subscription — a
    // static event roots its subscriber for the process (see
    // ViewSubscriptionLeakGuardTests), and one struct read per frame is not a
    // cost worth a subscription.
    private byte _amberR;
    private byte _amberG;
    private byte _amberB;
    private byte _amberBrightR;
    private byte _amberBrightG;
    private byte _amberBrightB;

    private void RefreshAmber()
    {
        var amber = ChartTheme.AnchorColor;
        _amberR = amber.R;
        _amberG = amber.G;
        _amberB = amber.B;
        _amberBrightR = (byte)Math.Min(255, amber.R + 20);
        _amberBrightG = (byte)Math.Min(255, amber.G + 20);
        _amberBrightB = (byte)Math.Min(255, amber.B + 20);
    }
}
