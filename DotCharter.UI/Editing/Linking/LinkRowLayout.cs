namespace DotCharter.UI.Editing.Linking;

/// <summary>
///     Where the LINKS row puts each span's label (letter chip, then "×N") and line, so that
///     groups starting at the same or a nearby measure, or overlapping, never draw on top of
///     each other. Pure and allocation-free: the layer owns the arrays and fills them once per
///     span list (sub-lines) and once per zoom (labels), never per frame.
/// </summary>
public static class LinkRowLayout
{
    /// <summary>The gap between a chip and its count, after a label, and before a span's end.</summary>
    public const float Gap = 4f;

    /// <summary>
    ///     Gives every span a sub-line: spans that overlap in time get different ones, the lowest
    ///     free one first. <paramref name="lineCounts" /> receives how many sub-lines the span's
    ///     overlap cluster uses, so a span that overlaps nothing keeps the plain mid-line.
    ///     <paramref name="spans" /> is in start order (<see cref="LinkLaneModel.Spans" />).
    /// </summary>
    public static void AssignSubLines(IReadOnlyList<LinkSpan> spans, int[] lines, int[] lineCounts)
    {
        var clusterStart = 0;
        var clusterEnd = double.NegativeInfinity;
        var clusterLines = 0;
        for (var i = 0; i < spans.Count; i++)
        {
            var span = spans[i];
            if (span.StartMs >= clusterEnd)
            {
                Fill(lineCounts, clusterStart, i, clusterLines);
                clusterStart = i;
                clusterLines = 0;
            }

            var line = 0;
            while (Held(spans, lines, clusterStart, i, line, span.StartMs)) line++;
            lines[i] = line;
            clusterLines = Math.Max(clusterLines, line + 1);
            clusterEnd = Math.Max(clusterEnd, span.EndMs);
        }

        Fill(lineCounts, clusterStart, spans.Count, clusterLines);
    }

    /// <summary>
    ///     Places the labels left to right at <paramref name="zoom" /> (px per ms). A label sits
    ///     at its span's start unless that would overlap the previous label; then it moves right
    ///     past it, and drops its count when the full label does not fit before its span ends.
    ///     Writes each label's x offset from its span's start x and whether its count shows.
    /// </summary>
    public static void PlaceLabels(IReadOnlyList<LinkSpan> spans, double zoom, float[] chipWidths, float[] countWidths,
        float[] offsets, bool[] showCount)
    {
        var cursor = double.NegativeInfinity;
        for (var i = 0; i < spans.Count; i++)
        {
            var start = spans[i].StartMs * zoom;
            var x = Math.Max(start, cursor);
            var full = chipWidths[i] + Gap + countWidths[i];
            showCount[i] = x <= start || x + full <= spans[i].EndMs * zoom - Gap;
            offsets[i] = (float)(x - start);
            cursor = x + LabelWidth(chipWidths[i], countWidths[i], showCount[i]) + Gap;
        }
    }

    /// <summary>The label's width: the chip, plus the gap and the count when it shows.</summary>
    public static float LabelWidth(float chipWidth, float countWidth, bool showCount) =>
        showCount ? chipWidth + Gap + countWidth : chipWidth;

    /// <summary>The y of sub-line <paramref name="line" /> of <paramref name="lineCount" /> inside the bar.</summary>
    public static float SubLineY(int line, int lineCount, float barTop, float bar) =>
        lineCount <= 1 ? barTop + bar * 0.5f : barTop + bar * (line + 1) / (lineCount + 1);

    private static bool Held(IReadOnlyList<LinkSpan> spans, int[] lines, int from, int to, int line, double startMs)
    {
        for (var j = from; j < to; j++)
        {
            if (lines[j] == line && spans[j].EndMs > startMs) return true;
        }

        return false;
    }

    private static void Fill(int[] lineCounts, int from, int to, int count)
    {
        for (var j = from; j < to; j++) lineCounts[j] = count;
    }
}
