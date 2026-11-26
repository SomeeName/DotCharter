using Avalonia.Media;
using DotCharter.Rendering.Timeline2D;
using DotCharter.UI.Constants;
using DotCharter.UI.Editing.Linking;

namespace DotCharter.UI.Controls.Layers;

/// <summary>
///     Linked measures on the timeline: the LINKS row (a letter chip and "×N" at each
///     placement's start, a thin line to its end; <see cref="LinkRowLayout" /> moves a label
///     that would overlap another and gives overlapping spans their own sub-lines; the row's hit test
///     uses the same label rectangles through <see cref="LinkHighlight.RowSpanAt" />) and, for the
///     hovered or selected group only, an outline over each of its placements' measures, with
///     every other group's chips dimmed. A placement an edit was just copied into flashes.
///     Nothing is tinted permanently.
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

    // The row layout: sub-lines once per span list, labels once per zoom. The arrays are
    // reused, so only a longer span list allocates.
    private int[] _lines = [];
    private int[] _lineCounts = [];
    private float[] _chipWidths = [];
    private float[] _countWidths = [];
    private float[] _labelOffsets = [];
    private bool[] _showCount = [];
    private double _labelZoom = double.NaN;
    private float _labelBar = float.NaN;
    private object? _labelAtlas;
    private readonly LinkRowLabels _rowLabels = new();

    public void Render(TimelineBatcher batcher, ChartRenderContext rc)
    {
        var highlight = rc.Editor?.LinkHighlight;
        var spans = SpansFor(rc);
        if (spans.Count == 0) return;
        var row = rc.Ribbon.ShowLinks;
        if (row) EnsureLabels(batcher, rc, spans);

        var emphasis = highlight?.EmphasisGroupId;
        var flash = highlight?.FlashAlpha ?? 0f;

        // Lines first, then every label over them, so no line crosses another group's chip.
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < spans.Count; i++)
            {
                var span = spans[i];
                var x1 = (float)((span.StartMs - rc.ViewStartMs) * rc.CurrentZoom);
                var x2 = (float)((span.EndMs - rc.ViewStartMs) * rc.CurrentZoom);
                var right = row ? Math.Max(x2, x1 + _labelOffsets[i] + LabelWidth(i)) : x2;
                if (right < -ChartEditorConstants.RenderMetrics.EventCullBufferPx || x1 > rc.Width) continue;

                var emph = emphasis != null && span.Group.Id == emphasis;
                var dimmed = emphasis != null && !emph;
                if (pass == 1)
                {
                    if (row) DrawLabel(batcher, rc, span, i, x1, emph, dimmed);
                    continue;
                }

                DrawLanes(batcher, rc, span, x1, x2, emph, flash);
                if (row) DrawLine(batcher, rc, span, i, x1, x2, emph, dimmed);
            }
        }
    }

    private static void DrawLanes(TimelineBatcher batcher, ChartRenderContext rc, LinkSpan span, float x1, float x2, bool emph,
        float flash)
    {
        var laneTop = rc.TopMargin;
        var laneHeight = rc.LaneAreaHeight;
        if (emph)
        {
            batcher.AddRect(x1, laneTop, x2 - x1, laneHeight, WithAlpha(span.Color, 26));
            Outline(batcher, x1, laneTop, x2 - x1, laneHeight, 1.5f, WithAlpha(span.Color, 230));
        }

        var highlight = rc.Editor?.LinkHighlight;
        if (flash > 0f && highlight!.FlashGroupId == span.Group.Id && IsFlashTarget(highlight, span.Placement))
            batcher.AddRect(x1, laneTop, x2 - x1, laneHeight, WithAlpha(span.Color, (byte)(110 * flash)));
    }

    private void DrawLine(TimelineBatcher batcher, ChartRenderContext rc, LinkSpan span, int i, float x1, float x2, bool emph,
        bool dimmed)
    {
        var ribbon = rc.Ribbon;
        var bar = ribbon.Bar;
        var count = _lineCounts[i];
        var y = LinkRowLayout.SubLineY(_lines[i], count, ribbon.BarTop(ribbon.LinkY), bar);
        var tick = count <= 1 ? bar * 0.6f : Math.Max(3f, bar / (count + 1));
        var alpha = dimmed ? (byte)90 : (byte)255;
        var lineColor = WithAlpha(span.Color, (byte)(alpha * (emph ? 0.9f : 0.6f)));

        var labelX = x1 + _labelOffsets[i];
        var lineStart = labelX + LabelWidth(i) + LinkRowLayout.Gap;
        var lineEnd = x2 - 2f;
        if (lineEnd > lineStart) batcher.AddRect(lineStart, y - 0.75f, lineEnd - lineStart, 1.5f, lineColor);
        if (lineEnd > labelX + _chipWidths[i]) batcher.AddRect(lineEnd, y - tick * 0.5f, 1.5f, tick, lineColor);
        // A label moved off its start leaves a tick where the span begins.
        if (_labelOffsets[i] > 0f) batcher.AddRect(x1, y - tick * 0.5f, 1.5f, tick, lineColor);
    }

    private void DrawLabel(TimelineBatcher batcher, ChartRenderContext rc, LinkSpan span, int i, float x1, bool emph,
        bool dimmed)
    {
        var ribbon = rc.Ribbon;
        var top = ribbon.BarTop(ribbon.LinkY);
        var bar = ribbon.Bar;
        var mid = top + bar * 0.5f;
        var alpha = dimmed ? (byte)90 : (byte)255;
        var x = x1 + _labelOffsets[i];

        var chipW = _chipWidths[i];
        if (emph) batcher.AddRoundRectAa(x - 1.5f, top - 1.5f, chipW + 3f, bar + 3f, 4.5f, WithAlpha(Lift(span.Color), 255));
        // A dimmed chip is mixed toward the row background rather than drawn translucent: the
        // rounded rect is several overlapping quads, and alpha would darken their overlaps.
        var chip = dimmed ? Mix(span.Color, DimBackground, 0.62f) : span.Color;
        batcher.AddRoundRectAa(x, top, chipW, bar, 3f, chip);
        batcher.AddText(span.Letter, x + chipW * 0.5f, mid, WithAlpha(LetterText, alpha), centerX: true, centerY: true);

        if (!_showCount[i]) return;
        batcher.AddText(CountLabel(span), x + chipW + LinkRowLayout.Gap, mid, WithAlpha(span.Color, (byte)(alpha * 0.85f)),
            centerY: true);
    }

    /// <summary>Places the labels again when the zoom, the bar height or the font changed.</summary>
    private void EnsureLabels(TimelineBatcher batcher, ChartRenderContext rc, List<LinkSpan> spans)
    {
        var bar = rc.Ribbon.Bar;
        if (rc.CurrentZoom == _labelZoom && bar == _labelBar && ReferenceEquals(batcher.FontAtlas, _labelAtlas)) return;
        for (var i = 0; i < spans.Count; i++)
        {
            _chipWidths[i] = Math.Max(bar, batcher.MeasureTextWidth(spans[i].Letter) + 8f);
            _countWidths[i] = batcher.MeasureTextWidth(CountLabel(spans[i]));
        }

        LinkRowLayout.PlaceLabels(spans, rc.CurrentZoom, _chipWidths, _countWidths, _labelOffsets, _showCount);
        // The row's hover and click pick by these same rectangles (LinkHighlight.RowSpanAt).
        _rowLabels.Set(rc.Arrangement, spans, rc.CurrentZoom, _labelOffsets, _chipWidths, _countWidths, _showCount);
        rc.Editor?.LinkHighlight.PublishRowLabels(_rowLabels);
        _labelZoom = rc.CurrentZoom;
        _labelBar = bar;
        _labelAtlas = batcher.FontAtlas;
    }

    private float LabelWidth(int i) => LinkRowLayout.LabelWidth(_chipWidths[i], _countWidths[i], _showCount[i]);

    private static string CountLabel(LinkSpan span) => CountLabels[Math.Clamp(span.GroupSize, 0, CountLabels.Length - 1)];

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
            Layout(_spans);
        }

        return _spans;
    }

    private void Layout(List<LinkSpan> spans)
    {
        if (_lines.Length < spans.Count)
        {
            var n = Math.Max(spans.Count, _lines.Length * 2);
            _lines = new int[n];
            _lineCounts = new int[n];
            _chipWidths = new float[n];
            _countWidths = new float[n];
            _labelOffsets = new float[n];
            _showCount = new bool[n];
        }

        LinkRowLayout.AssignSubLines(spans, _lines, _lineCounts);
        _labelZoom = double.NaN;
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
