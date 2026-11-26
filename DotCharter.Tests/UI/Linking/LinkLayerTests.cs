using Avalonia.Media;
using DotCharter.Rendering.Timeline2D;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Editing.Linking;
using DotCharter.UI.Editing.Tracks;
using static DotCharter.Tests.Core.Linking.LinkFixture;

namespace DotCharter.Tests.UI.Linking;

/// <summary>
///     The link layer into a CPU <see cref="TimelineBatcher" /> (no GPU, no font atlas, so only
///     geometry): chips for the drawn arrangement's placements only, and the outline over the
///     measures of the emphasised group only.
/// </summary>
public sealed class LinkLayerTests
{
    private const float TopMargin = 120f;
    private const float LaneAreaHeight = 300f;

    [Fact]
    public void Chips_are_drawn_for_the_drawn_arrangement_and_nothing_for_one_without_placements()
    {
        var h = LinkedMeasuresHarness.WithRhythm();
        var group = h.Links.Link((h.Links.Lead, 1), (h.Links.Lead, 4));
        var color = LinkLaneModel.ColorOf(h.Links.Project, group);

        var lead = Render(h, h.Links.Lead, out var rc);
        var chipX = X(rc, At(1, 0)) + 4f;
        var chipY = rc.Ribbon.BarTop(rc.Ribbon.LinkY) + rc.Ribbon.Bar * 0.5f;
        Assert.Equal(Rgb(color), Rgb(TopmostAt(lead, chipX, chipY)));

        var rhythm = Render(h, h.Links.Rhythm, out _);
        Assert.Equal(0, rhythm.SolidVertexCount);
    }

    [Fact]
    public void Only_the_hovered_group_gets_the_outline_over_its_measures()
    {
        var h = LinkedMeasuresHarness.WithRhythm();
        var a = h.Links.Link((h.Links.Lead, 1), (h.Links.Lead, 4));
        var b = h.Links.Link((h.Links.Lead, 7), (h.Links.Rhythm, 7));
        var midLane = TopMargin + LaneAreaHeight * 0.5f;

        var plain = Render(h, h.Links.Lead, out var rc);
        Assert.Equal(Colors.Transparent, TopmostAt(plain, X(rc, At(4, 0)) + 0.5f, midLane));

        h.Editor.LinkHighlight.SetHovered(a.Id);
        var hovered = Render(h, h.Links.Lead, out rc);
        var colorA = LinkLaneModel.ColorOf(h.Links.Project, a);
        Assert.Equal(Rgb(colorA), Rgb(TopmostAt(hovered, X(rc, At(4, 0)) + 0.5f, midLane)));
        Assert.Equal(Rgb(colorA), Rgb(TopmostAt(hovered, X(rc, At(1, 0)) + 0.5f, midLane)));
        Assert.NotEqual(Rgb(LinkLaneModel.ColorOf(h.Links.Project, b)), Rgb(TopmostAt(hovered, X(rc, At(7, 0)) + 0.5f, midLane)));
        Assert.Equal(Colors.Transparent, TopmostAt(hovered, X(rc, At(7, 0)) + 0.5f, midLane));
    }

    [Fact]
    public void Groups_at_the_same_and_nested_starts_each_keep_a_visible_chip_and_their_own_line()
    {
        var h = LinkedMeasuresHarness.WithRhythm();
        var (a, b, c) = Crowd(h);
        var project = h.Links.Project;

        var frame = Render(h, h.Links.Lead, out var rc);
        var top = rc.Ribbon.BarTop(rc.Ribbon.LinkY);
        var bar = rc.Ribbon.Bar;
        var (from, to) = (X(rc, At(1, 0)), X(rc, At(3, 0)));

        foreach (var group in new[] { a, b, c })
        {
            var color = Rgb(LinkLaneModel.ColorOf(project, group));
            var seen = false;
            for (var x = from; x < to && !seen; x += 1f) seen = Rgb(TopmostAt(frame, x, top + 2f)) == color;
            Assert.True(seen, $"group {group.Letter}'s chip is hidden under another label");
        }

        // Measure 3 is inside A and C only: each draws its own sub-line there.
        var probeX = X(rc, At(3, 2));
        var ys = new[] { a, c }.Select(g => RowsOf(frame, probeX, top, bar, Rgb(LinkLaneModel.ColorOf(project, g)))).ToArray();
        Assert.NotEmpty(ys[0]);
        Assert.NotEmpty(ys[1]);
        Assert.Empty(ys[0].Intersect(ys[1]));
    }

    [Fact]
    public void A_pointer_on_a_shifted_chip_inside_another_span_picks_the_chip_s_own_group()
    {
        // At 0.02 px/ms a beat is ~10 px: B starts one beat after A, so its chip moves right past
        // A's label, beyond B's own end and into A's span.
        var h = LinkedMeasuresHarness.WithRhythm();
        var lead = h.Links.Lead;
        var a = Group("A", (lead, At(1, 0), At(5, 0)), (lead, At(7, 0), At(8, 0)));
        var b = Group("B", (lead, At(1, 1), At(1, 2)), (lead, At(8, 0), At(9, 0)));
        h.Links.Project.LinkGroups.Add(a);
        h.Links.Project.LinkGroups.Add(b);
        var rc = Context(h, lead);
        rc.CurrentZoom = 0.02;
        var frame = new TimelineBatcher();
        new LinkLayer().Render(frame, rc);

        var top = rc.Ribbon.BarTop(rc.Ribbon.LinkY);
        var colorB = Rgb(LinkLaneModel.ColorOf(h.Links.Project, b));
        var chipX = Enumerable.Range(0, 400).Select(x => x + 0.5f).First(x => Rgb(TopmostAt(frame, x, top + 2f)) == colorB);
        var chipMs = (chipX + 2f) / rc.CurrentZoom;
        var spans = LinkLaneModel.Spans(h.Links.Project, lead);
        Assert.Same(a, LinkLaneModel.HitTest(spans, chipMs)?.Group);

        Assert.Same(b, h.Editor.LinkHighlight.RowSpanAt(lead, spans, chipMs)?.Group);
        // The line area still picks by time: measure 4 is inside A only.
        Assert.Same(a, h.Editor.LinkHighlight.RowSpanAt(lead, spans, At(4, 0).GetPositionMs(h.Links.Project.Beats))?.Group);
    }

    /// <summary>
    ///     Three groups crowding the start of Lead (the #1011 report): A spans measures 1-4 and has
    ///     twelve placements (the rest in Rhythm), B starts with A at measure 1 and C starts one
    ///     beat into measure 2, nested inside A.
    /// </summary>
    internal static (LinkGroup A, LinkGroup B, LinkGroup C) Crowd(LinkedMeasuresHarness h)
    {
        var lead = h.Links.Lead;
        var rhythm = h.Links.Rhythm;
        var a = Group("A", (lead, At(1, 0), At(5, 0)), (lead, At(7, 0), At(8, 0)));
        for (var m = 1; m <= 10; m++) a.Placements.Add(Place(rhythm, At(m, 0), At(m + 1, 0)));
        var b = Group("B", (lead, At(1, 0), At(2, 0)), (lead, At(8, 0), At(9, 0)), (rhythm, At(4, 0), At(5, 0)));
        var c = Group("C", (lead, At(2, 1), At(4, 0)), (rhythm, At(6, 0), At(7, 0)));
        foreach (var group in new[] { a, b, c }) h.Links.Project.LinkGroups.Add(group);
        return (a, b, c);
    }

    private static LinkGroup Group(string letter, params (Arrangement Arrangement, FractionalPosition Start, FractionalPosition End)[] runs)
    {
        var group = new LinkGroup { Letter = letter, Name = letter };
        foreach (var (arrangement, start, end) in runs) group.Placements.Add(Place(arrangement, start, end));
        return group;
    }

    private static LinkPlacement Place(Arrangement arrangement, FractionalPosition start, FractionalPosition end) =>
        new() { Arrangement = arrangement, Position = start, EndPosition = end };

    private static List<int> RowsOf(TimelineBatcher frame, float x, float top, float bar, (byte R, byte G, byte B) color)
    {
        var rows = new List<int>();
        for (var y = (int)top; y < top + bar; y++)
        {
            if (Rgb(TopmostAt(frame, x, y + 0.5f)) == color) rows.Add(y);
        }

        return rows;
    }

    [Fact]
    public void A_warm_crowded_frame_allocates_nothing()
    {
        var h = LinkedMeasuresHarness.WithRhythm();
        Crowd(h);
        var rc = Context(h, h.Links.Lead);
        var layer = new LinkLayer();
        var batcher = new TimelineBatcher();
        layer.Render(batcher, rc);

        AllocationProbe.AssertAllocationFree("warm crowded LINKS frame", () =>
        {
            batcher.Reset();
            layer.Render(batcher, rc);
        });
    }

    private static TimelineBatcher Render(LinkedMeasuresHarness h, Arrangement arrangement, out ChartRenderContext rc)
    {
        rc = Context(h, arrangement);
        var batcher = new TimelineBatcher();
        new LinkLayer().Render(batcher, rc);
        return batcher;
    }

    private static ChartRenderContext Context(LinkedMeasuresHarness h, Arrangement arrangement)
    {
        var project = h.Links.Project;
        return new ChartRenderContext
        {
            Width = 4000,
            Height = 600,
            CurrentZoom = 0.25,
            ViewStartMs = 0,
            ViewEndMs = 16000,
            TopMargin = TopMargin,
            BottomMargin = 40f,
            LaneAreaHeight = LaneAreaHeight,
            LaneHeight = LaneAreaHeight / 6f,
            StringCount = 6,
            DrawLabels = false,
            Editor = h.Editor,
            Project = project,
            Arrangement = arrangement,
            Ribbon = RibbonLaneRules.Compose(new DotCharter.Services.Editing.Tracks.RibbonVisibility(),
                new RibbonContent(2, true, true, true, LinkLaneModel.HasLinks(project, arrangement)))
        };
    }

    private static float X(ChartRenderContext rc, FractionalPosition at) =>
        (float)((at.GetPositionMs(rc.Project!.Beats) - rc.ViewStartMs) * rc.CurrentZoom);

    private static (byte R, byte G, byte B) Rgb(Color c) => (c.R, c.G, c.B);

    /// <summary>The colour of the LAST solid triangle covering the point.</summary>
    private static Color TopmostAt(TimelineBatcher batcher, float x, float y)
    {
        var buf = batcher.SolidBuffer;
        var found = Colors.Transparent;
        for (var v = 0; v + 2 < batcher.SolidVertexCount; v += 3)
        {
            var i = v * 6;
            if (!InTriangle(x, y, buf[i], buf[i + 1], buf[i + 6], buf[i + 7], buf[i + 12], buf[i + 13])) continue;
            found = Color.FromArgb(ToByte(buf[i + 5]), ToByte(buf[i + 2]), ToByte(buf[i + 3]), ToByte(buf[i + 4]));
        }

        return found;
    }

    private static byte ToByte(float channel) => (byte)Math.Round(channel * 255f);

    private static bool InTriangle(float px, float py, float ax, float ay, float bx, float by, float cx, float cy)
    {
        var d1 = (px - bx) * (ay - by) - (ax - bx) * (py - by);
        var d2 = (px - cx) * (by - cy) - (bx - cx) * (py - cy);
        var d3 = (px - ax) * (cy - ay) - (cx - ax) * (py - ay);
        var hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
        var hasPos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(hasNeg && hasPos);
    }
}
