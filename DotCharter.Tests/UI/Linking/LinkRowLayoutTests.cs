using Avalonia.Media;
using DotCharter.UI.Editing.Linking;

namespace DotCharter.Tests.UI.Linking;

/// <summary>
///     The LINKS row layout (#1011): labels of groups at the same or nearby starts never
///     intersect, a moved label that no longer fits drops its count, and spans that overlap in
///     time get different sub-lines while a lone span keeps the mid-line.
/// </summary>
public sealed class LinkRowLayoutTests
{
    private const float Chip = 19f;
    private const float Count = 22f;
    private const float BarTop = 100f;
    private const float Bar = 19f;

    [Fact]
    public void Three_groups_at_the_same_start_get_labels_side_by_side()
    {
        var spans = Spans((0, 4000), (0, 2000), (0, 8000));

        var (x, w) = Labels(spans, 0.25);

        AssertNoIntersection(x, w);
        Assert.Equal(0f, x[0]);
    }

    [Fact]
    public void Adjacent_starts_closer_than_a_label_move_the_later_label_right()
    {
        var spans = Spans((0, 4000), (100, 4000), (200, 4000));

        var (x, w) = Labels(spans, 0.25);

        AssertNoIntersection(x, w);
        Assert.True(x[1] > 100 * 0.25, "the second label was not moved");
    }

    [Fact]
    public void Starts_far_apart_keep_their_labels_at_their_starts()
    {
        var spans = Spans((0, 2000), (2000, 4000));
        var offsets = new float[2];
        var show = new bool[2];

        LinkRowLayout.PlaceLabels(spans, 0.25, [Chip, Chip], [Count, Count], offsets, show);

        Assert.Equal([0f, 0f], offsets);
        Assert.Equal([true, true], show);
    }

    [Fact]
    public void A_moved_label_with_no_room_before_its_end_shows_the_chip_only()
    {
        var spans = Spans((0, 4000), (0, 120));
        var offsets = new float[2];
        var show = new bool[2];

        LinkRowLayout.PlaceLabels(spans, 0.25, [Chip, Chip], [Count, Count], offsets, show);

        Assert.True(show[0]);
        Assert.False(show[1]);
    }

    [Fact]
    public void Nested_spans_get_different_sub_lines_and_a_lone_span_keeps_the_mid_line()
    {
        var spans = Spans((0, 8000), (0, 2000), (2500, 6000), (10000, 12000));
        var lines = new int[4];
        var counts = new int[4];

        LinkRowLayout.AssignSubLines(spans, lines, counts);
        var y = Enumerable.Range(0, 4).Select(i => LinkRowLayout.SubLineY(lines[i], counts[i], BarTop, Bar)).ToArray();

        Assert.NotEqual(y[0], y[1]);
        Assert.NotEqual(y[0], y[2]);
        Assert.Equal(BarTop + Bar * 0.5f, y[3]);
        Assert.All(y, v => Assert.InRange(v, BarTop + 1f, BarTop + Bar - 1f));
    }

    [Fact]
    public void A_sub_line_is_reused_once_its_span_has_ended()
    {
        var spans = Spans((0, 8000), (0, 2000), (2000, 4000));
        var lines = new int[3];
        var counts = new int[3];

        LinkRowLayout.AssignSubLines(spans, lines, counts);

        Assert.Equal([0, 1, 1], lines);
        Assert.Equal([2, 2, 2], counts);
    }

    private static (float[] X, float[] W) Labels(List<LinkSpan> spans, double zoom)
    {
        var n = spans.Count;
        var chips = Enumerable.Repeat(Chip, n).ToArray();
        var counts = Enumerable.Repeat(Count, n).ToArray();
        var offsets = new float[n];
        var show = new bool[n];
        LinkRowLayout.PlaceLabels(spans, zoom, chips, counts, offsets, show);
        var x = Enumerable.Range(0, n).Select(i => (float)(spans[i].StartMs * zoom) + offsets[i]).ToArray();
        var w = Enumerable.Range(0, n).Select(i => LinkRowLayout.LabelWidth(chips[i], counts[i], show[i])).ToArray();
        return (x, w);
    }

    private static void AssertNoIntersection(float[] x, float[] w)
    {
        for (var i = 0; i < x.Length; i++)
        {
            for (var j = i + 1; j < x.Length; j++)
                Assert.False(x[i] < x[j] + w[j] && x[j] < x[i] + w[i], $"label {i} intersects label {j}");
        }
    }

    private static List<LinkSpan> Spans(params (double Start, double End)[] ranges) =>
    [
        .. ranges.Select((r, i) => new LinkSpan(new LinkGroup { Letter = ((char)('A' + i)).ToString() }, new LinkPlacement(),
            r.Start, r.End, ((char)('A' + i)).ToString(), 2, Colors.White))
    ];
}
