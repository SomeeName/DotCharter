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

    private static TimelineBatcher Render(LinkedMeasuresHarness h, Arrangement arrangement, out ChartRenderContext rc)
    {
        var project = h.Links.Project;
        rc = new ChartRenderContext
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
        var batcher = new TimelineBatcher();
        new LinkLayer().Render(batcher, rc);
        return batcher;
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
