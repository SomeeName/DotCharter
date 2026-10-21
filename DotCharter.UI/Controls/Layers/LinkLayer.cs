using Avalonia.Media;
using DotCharter.Rendering.Timeline2D;
using DotCharter.UI.Constants;
using DotCharter.UI.Editing.Linking;

namespace DotCharter.UI.Controls.Layers;

/// <summary>
///     Linked measures on the timeline: the LINKS row (a letter chip and "×N" at each
///     placement's start, a thin line to its end) and, for the hovered or selected group only,
///     an outline over each of its placements' measures, with every other group's chips dimmed.
///     A placement an edit was just copied into flashes. Nothing is tinted permanently.
/// </summary>
public sealed class LinkLayer : ITimelineRenderLayer
{
    /// <summary>The glyphs this layer draws beyond the atlas's ASCII seed.</summary>
    public const string Glyphs = "×";

    private static readonly Color LetterText = Color.FromRgb(20, 22, 30);

    /// <summary>The ribbon row's background, which a dimmed chip is mixed toward.</summary>
    private static readonly Color DimBackground = Color.FromRgb(26, 28, 36);

    private static readonly string[] CountLabels = [.. Enumerable.Range(0, 100).Select(n => $"×{n}")];

    private List<LinkSpan> _spans = [];
    private object? _cachedArrangement;
    private object? _cachedProject;
    private long _cachedRevision = -1;
    private int _cachedGroupCount = -1;

    public void Render(TimelineBatcher batcher, ChartRenderContext rc)
    {
        var highlight = rc.Editor?.LinkHighlight;
        var spans = SpansFor(rc);
        if (spans.Count == 0) return;

        var emphasis = highlight?.EmphasisGroupId;
        var laneTop = rc.TopMargin;
        var laneHeight = rc.LaneAreaHeight;
        var flash = highlight?.FlashAlpha ?? 0f;

        foreach (var span in spans)
        {
            var x1 = (float)((span.StartMs - rc.ViewStartMs) * rc.CurrentZoom);
            var x2 = (float)((span.EndMs - rc.ViewStartMs) * rc.CurrentZoom);
            if (x2 < -ChartEditorConstants.RenderMetrics.EventCullBufferPx || x1 > rc.Width) continue;

            var emph = emphasis != null && span.Group.Id == emphasis;
            var dimmed = emphasis != null && !emph;

            if (emph)
            {
                batcher.AddRect(x1, laneTop, x2 - x1, laneHeight, WithAlpha(span.Color, 26));
                Outline(batcher, x1, laneTop, x2 - x1, laneHeight, 1.5f, WithAlpha(span.Color, 230));
            }

            if (flash > 0f && highlight!.FlashGroupId == span.Group.Id && IsFlashTarget(highlight, span.Placement))
                batcher.AddRect(x1, laneTop, x2 - x1, laneHeight, WithAlpha(span.Color, (byte)(110 * flash)));

            if (rc.Ribbon.ShowLinks) DrawRow(batcher, rc, span, x1, x2, emph, dimmed);
        }
    }

    private void DrawRow(TimelineBatcher batcher, ChartRenderContext rc, LinkSpan span, float x1, float x2, bool emph,
        bool dimmed)
    {
        var ribbon = rc.Ribbon;
        var top = ribbon.BarTop(ribbon.LinkY);
        var bar = ribbon.Bar;
        var mid = top + bar * 0.5f;
        var alpha = dimmed ? (byte)90 : (byte)255;

        var chipW = Math.Max(bar, batcher.MeasureTextWidth(span.Letter) + 8f);
        if (emph) batcher.AddRoundRectAa(x1 - 1.5f, top - 1.5f, chipW + 3f, bar + 3f, 4.5f, WithAlpha(Lift(span.Color), 255));
        // A dimmed chip is mixed toward the row background rather than drawn translucent: the
        // rounded rect is several overlapping quads, and alpha would darken their overlaps.
        var chip = dimmed ? Mix(span.Color, DimBackground, 0.62f) : span.Color;
        batcher.AddRoundRectAa(x1, top, chipW, bar, 3f, chip);
        batcher.AddText(span.Letter, x1 + chipW * 0.5f, mid, WithAlpha(LetterText, alpha), centerX: true, centerY: true);

        var count = CountLabels[Math.Clamp(span.GroupSize, 0, CountLabels.Length - 1)];
        var countX = x1 + chipW + 4f;
        batcher.AddText(count, countX, mid, WithAlpha(span.Color, (byte)(alpha * 0.85f)), centerY: true);

        var lineStart = countX + batcher.MeasureTextWidth(count) + 4f;
        var lineEnd = x2 - 2f;
        var lineColor = WithAlpha(span.Color, (byte)(alpha * (emph ? 0.9f : 0.6f)));
        if (lineEnd > lineStart) batcher.AddRect(lineStart, mid - 0.75f, lineEnd - lineStart, 1.5f, lineColor);
        if (lineEnd > x1 + chipW) batcher.AddRect(lineEnd, mid - bar * 0.3f, 1.5f, bar * 0.6f, lineColor);
    }

    private List<LinkSpan> SpansFor(ChartRenderContext rc)
    {
        var revision = rc.Editor?.HistoryRevision ?? 0;
        if (!ReferenceEquals(rc.Arrangement, _cachedArrangement) || !ReferenceEquals(rc.Project, _cachedProject) ||
            revision != _cachedRevision || (rc.Project?.LinkGroups.Count ?? 0) != _cachedGroupCount)
        {
            _spans = LinkLaneModel.Spans(rc.Project, rc.Arrangement);
            _cachedArrangement = rc.Arrangement;
            _cachedProject = rc.Project;
            _cachedRevision = revision;
            _cachedGroupCount = rc.Project?.LinkGroups.Count ?? 0;
        }

        return _spans;
    }

    private static bool IsFlashTarget(LinkHighlight highlight, LinkPlacement placement)
    {
        foreach (var target in highlight.FlashTargets)
        {
            if (ReferenceEquals(target.Arrangement, placement.Arrangement) && target.Position == placement.Position) return true;
        }

        return false;
    }

    private static void Outline(TimelineBatcher batcher, float x, float y, float w, float h, float t, Color c)
    {
        batcher.AddRect(x, y, w, t, c);
        batcher.AddRect(x, y + h - t, w, t, c);
        batcher.AddRect(x, y, t, h, c);
        batcher.AddRect(x + w - t, y, t, h, c);
    }

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static Color Mix(Color a, Color b, float t) => Color.FromRgb((byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static Color Lift(Color c) => Color.FromRgb((byte)Math.Min(255, c.R + 60), (byte)Math.Min(255, c.G + 60),
        (byte)Math.Min(255, c.B + 60));
}
