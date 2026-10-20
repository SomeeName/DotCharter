using Avalonia.Media;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Timeline2D;
using DotCharter.UI.Constants;
using DotCharter.UI.Controls.Layers.Helpers;
using DotCharter.UI.Controls.Themes;

namespace DotCharter.UI.Controls.Layers;

/// <summary>
///     The hand-shape bars under the lanes. A bar says what it is
///     without a click into the inspector:
///     <list type="bullet">
///         <item>its <see cref="RenderHandShape.Label" /> inside it — a committed
///         bar's template name (not at Marks density), a bar being drawn's grip
/// — shown only when it fits whole (a cut-off "Cmaj7" reads as a
///         different chord);</item>
///         <item>an ARPEGGIO bar is purple and carries bracket posts, from
///         <see cref="RenderHandShape.IsArpeggio" /> (the snapshot's
///         <c>ChordTemplate.IsArpeggio</c> — both flags);</item>
///         <item>a bar the inspector reports E05/D09 against
///         (<see cref="RenderHandShape.HasFinding" />, data — never a rule run
///         here) gets a warning outline, a corner flag and a warning wash;</item>
/// <item>a SUGGESTED bar (<see cref="RenderHandShape.IsSuggested" />)
///         is a faint bar with a DASHED outline and its grip's name — a proposal
///         a click accepts, never mistaken for a shape the chart has;</item>
///         <item>a bar a pending bulk chord edit would change
/// (<see cref="RenderHandShape.IsEditTarget" />) gets the chord's
///         EditTarget corner brackets.</item>
///     </list>
///     Each state has a SHAPE cue as well as a colour (posts, flag), so neither
///     depends on telling two hues apart.
/// </summary>
public class HandShapeLayer : ITimelineRenderLayer
{
    /// <summary>Gap between the bar's edge (or the flag) and its name.</summary>
    internal const float NamePad = 4f;

    /// <summary>Leg of the warning flag in the bar's top-left corner.</summary>
    internal const float FlagSize = 7f;

    /// <summary>How far an arpeggio's bracket posts rise above the bar.</summary>
    internal const float BracketPostHeight = 4f;

    internal const float BracketPostWidth = 2f;

    /// <summary>Dash and gap of a suggested bar's outline.</summary>
    internal const float SuggestedDash = 6f;

    internal const float SuggestedGap = 4f;

    /// <summary>Alpha of a suggested bar's fill and of its dashes.</summary>
    internal const byte SuggestedFillAlpha = 48;

    internal const byte SuggestedDashAlpha = 200;

    public void Render(TimelineBatcher batcher, ChartRenderContext rc)
    {
        var drawNames = rc.DrawLabels && rc.NoteLod != NoteLevelOfDetail.Marks;
        for (var i = 0; i < rc.Snapshot.SuggestedHandShapeCount; i++)
            DrawSuggested(batcher, rc, rc.Snapshot.SuggestedHandShapes[i], drawNames);

        for (var i = 0; i < rc.Snapshot.HandShapeCount; i++)
            DrawHandShape(batcher, rc, rc.Snapshot.HandShapes[i], false, drawNames);

        for (var i = 0; i < rc.Snapshot.PreviewHandShapeCount; i++)
            DrawHandShape(batcher, rc, rc.Snapshot.PreviewHandShapes[i], true, false);
    }

    private static void DrawHandShape(TimelineBatcher batcher, ChartRenderContext rc, RenderHandShape hs,
        bool isPreview, bool drawName)
    {
        var x = (float)((hs.Time - rc.ViewStartMs) * rc.CurrentZoom);
        // The bar is drawn to its exact data end. A trailing render inset (0.34.1
        // M19 stopgap, since REMOVED) pulled the right edge back a gem radius to
        // stop it touching the next note — but that job now belongs to the DATA:
        // ChartSpacingEnforcer.EnforceHandShapeSpacing leaves the min-note-distance
        // gap before the next sound. The inset then only did harm: a bend chord's
        // sustain (and its bend) end exactly at the hand shape's data end, so
        // shrinking the rendered bar left the sustain poking past it (owner report
        // M30: "the bend and sustain are outside of the handshape"). The bar and
        // the sustain it covers must share the same right edge.
        var w = Math.Max((float)((hs.EndTime - hs.Time) * rc.CurrentZoom), 4f);

        var isArpeggio = hs.IsArpeggio;
        var hasFinding = hs.HasFinding && !isPreview;

        var alpha = isPreview ? (byte)16 : (byte)32;
        var barAlpha = isPreview ? (byte)128 : (byte)255;
        var barColor = isArpeggio ? ChartTheme.HandShapeArpeggioColor : ChartTheme.HandShapeBarColor;
        var washColor = hasFinding ? ChartTheme.HandShapeWarningColor
            : isArpeggio ? ChartTheme.HandShapeArpeggioColor
            : ChartTheme.HandShapeBgColor;

        batcher.AddRect(x, rc.TopMargin, w, rc.LaneAreaHeight,
            Color.FromArgb(alpha, washColor.R, washColor.G, washColor.B));

        var barY = rc.TopMargin + rc.LaneAreaHeight + ChartEditorConstants.RenderMetrics.HandShapeBarYOffset;
        var barH = ChartEditorConstants.RenderMetrics.HandShapeActiveBarHeight;
        batcher.AddRect(x, barY, w, barH, Color.FromArgb(barAlpha, barColor.R, barColor.G, barColor.B));

        // Bracket posts: the bar and its posts read as a bracket around the notes
        // above it, the way Rocksmith marks an arpeggio.
        if (isArpeggio)
        {
            var postColor = Color.FromArgb(barAlpha, barColor.R, barColor.G, barColor.B);
            batcher.AddRect(x, barY - BracketPostHeight, BracketPostWidth, BracketPostHeight, postColor);
            batcher.AddRect(x + w - BracketPostWidth, barY - BracketPostHeight, BracketPostWidth, BracketPostHeight,
                postColor);
        }

        if (!isPreview)
        {
            var th = hs.IsSelected || hasFinding
                ? ChartEditorConstants.RenderMetrics.HandShapeSelectedThickness
                : ChartEditorConstants.RenderMetrics.HandShapeNormalThickness;
            var borderCol = hs.IsSelected || hs.IsHovered ? ChartTheme.WhiteSmokeColor
                : hasFinding ? ChartTheme.HandShapeWarningColor
                : barColor;

            // Axis-aligned border: 4 solid quads (4 vertices*6 = 24 floats each)
            // is dramatically cheaper than 4 AddThickLine calls (each emits 3
            // quads incl. 2 AA-gradient wings + a sqrt). Visual is identical
            // for screen-aligned strokes since the AA wings collapse to 1px.
            batcher.AddRect(x, barY, w, th, borderCol);                       // top
            batcher.AddRect(x, barY + barH - th, w, th, borderCol);           // bottom
            batcher.AddRect(x, barY, th, barH, borderCol);                    // left
            batcher.AddRect(x + w - th, barY, th, barH, borderCol);           // right

            // The flag stays even when the bar is selected and its outline is white.
            if (hasFinding)
            {
                var flag = Math.Min(FlagSize, w);
                batcher.AddTriangle(x, barY, x + flag, barY, x, barY + flag, ChartTheme.HandShapeWarningColor);
            }
        }

        // A bar a pending bulk chord edit would change: corner brackets
        // just outside it, the same mark NoteLayer gives such a chord.
        if (hs.IsEditTarget && !isPreview)
        {
            const float pad = NoteLayer.EditTargetPad;
            NoteLayer.DrawCornerBrackets(batcher, x - pad, barY - pad, w + pad * 2f, barH + pad * 2f,
                ChartTheme.EditTargetColor);
        }

        // ONE text path for both kinds of bar: a committed bar's template name and
        // a bar being DRAWN's grip, which shows at every density so the
        // charter sees the shape before releasing.
        if (drawName || isPreview)
            DrawName(batcher, rc, hs.Label, x, w, barY + barH * 0.5f, hasFinding);

        if (!isPreview && hs.GhostMask != 0) DrawGhostGems(batcher, rc, hs);
    }

    /// <summary>How far left of the bar's start a ghost gem goes when a played gem sits on its
    /// string at that instant (a tapped harmonic over its own held fret).</summary>
    internal static float GhostNudgePx => ChartEditorConstants.RenderMetrics.BaseNoteRadius * 2f + 2f;

    private static readonly List<DeferredLabel> GhostLabels = [];

    /// <summary>
    /// The bar's GHOST gems: each held-not-played fret of its grip as a DIMMED gem
    ///     (<see cref="NoteFlags.IsGhost" />, the note head's ghost alpha) with its fret number,
    ///     on its string's lane at the bar's start — nudged just before the start when a played
    ///     gem is there too, so neither hides the other.
    /// </summary>
    private static void DrawGhostGems(TimelineBatcher batcher, ChartRenderContext rc, RenderHandShape hs)
    {
        var nudgeMs = rc.CurrentZoom > 0 ? GhostNudgePx / rc.CurrentZoom : 0;
        for (var s = 0; s < RenderHandShape.MaxGhostStrings && s < Math.Max(rc.StringCount, 1); s++)
        {
            if ((hs.GhostMask & (1 << s)) == 0) continue;
            var time = (hs.GhostNudgeMask & (1 << s)) != 0 ? hs.Time - nudgeMs : hs.Time;
            var data = new NoteDrawData(time, 0, s, hs.GhostFret(s), 0, 0, 0, 0, NoteFlags.IsGhost, false, 0, 0);
            HighwayNoteHeadRenderer.Draw(batcher, rc, data, GhostLabels);
            GhostLabels.Clear();
        }
    }

    /// <summary>
    ///     A suggested bar: a faint fill, a dashed top and bottom edge, solid ends — no lane wash,
    ///     so a chart full of proposals does not read as a chart full of shapes.
    /// </summary>
    private static void DrawSuggested(TimelineBatcher batcher, ChartRenderContext rc, RenderHandShape hs, bool drawName)
    {
        var x = (float)((hs.Time - rc.ViewStartMs) * rc.CurrentZoom);
        var w = Math.Max((float)((hs.EndTime - hs.Time) * rc.CurrentZoom), 4f);
        var barY = rc.TopMargin + rc.LaneAreaHeight + ChartEditorConstants.RenderMetrics.HandShapeBarYOffset;
        var barH = ChartEditorConstants.RenderMetrics.HandShapeActiveBarHeight;
        var c = ChartTheme.HandShapeBarColor;
        var th = ChartEditorConstants.RenderMetrics.HandShapeNormalThickness;
        var dash = Color.FromArgb(SuggestedDashAlpha, c.R, c.G, c.B);

        batcher.AddRect(x, barY, w, barH, Color.FromArgb(SuggestedFillAlpha, c.R, c.G, c.B));

        // Only the on-screen part is dashed: a bar that starts far off the left edge would
        // otherwise loop over thousands of invisible dashes.
        var from = Math.Max(x, -SuggestedDash);
        var to = Math.Min(x + w, (float)rc.Width + SuggestedDash);
        for (var d = x + MathF.Floor((from - x) / (SuggestedDash + SuggestedGap)) * (SuggestedDash + SuggestedGap);
             d < to; d += SuggestedDash + SuggestedGap)
        {
            var len = Math.Min(SuggestedDash, x + w - d);
            if (len <= 0f) break;
            batcher.AddRect(d, barY, len, th, dash);
            batcher.AddRect(d, barY + barH - th, len, th, dash);
        }

        batcher.AddRect(x, barY, th, barH, dash);
        batcher.AddRect(x + w - th, barY, th, barH, dash);

        if (drawName) DrawName(batcher, rc, hs.Label, x, w, barY + barH * 0.5f, false);
    }

    /// <summary>
    ///     The template name, left-aligned in the part of the bar that is on
    ///     screen (a bar that began off the left edge keeps its name on the part
    ///     showing), and left out whole when it does not fit — never truncated.
    /// </summary>
    private static void DrawName(TimelineBatcher batcher, ChartRenderContext rc, string? name, float x, float w,
        float centreY, bool hasFinding)
    {
        if (string.IsNullOrEmpty(name)) return;
        var flagShowing = hasFinding && x + FlagSize > 0f;
        var left = Math.Max(x, 0f) + NamePad + (flagShowing ? FlagSize : 0f);
        var right = Math.Min(x + w, (float)rc.Width) - NamePad;
        if (right <= left) return;
        const float scale = ChartEditorConstants.RenderMetrics.HandShapeNameTextScale;
        if (TextRenderHelper.MeasureScaledTextWidth(batcher, name, scale) > right - left) return;
        TextRenderHelper.DrawScaledText(batcher, name, left, centreY, scale, ChartTheme.WhiteSmokeColor, centerY: true);
    }
}
