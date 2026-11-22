using DotCharter.UI.Constants;
using DotCharter.UI.Editing.Tracks;
using DotCharter.UI.Input;

namespace DotCharter.Tests.UI;

/// <summary>Where the in-place editors sit: through the one view anchor, so a panned timeline moves them with their time.</summary>
public sealed class InlineEditPlacementTests
{
    private const double Width = 1200;
    private const double Zoom = 0.5;

    [Fact]
    public void An_editor_sits_where_the_chart_draws_its_time()
    {
        // The time under the view anchor is drawn at the anchor column.
        var x = InlineEditPlacement.Left(10_000, 10_000, Zoom, Width, 200);

        Assert.Equal(ChartEditorConstants.PlayheadX, x, 6);
    }

    [Fact]
    public void Panning_the_view_moves_the_editor_by_the_pan_distance()
    {
        var before = InlineEditPlacement.Left(10_000, 10_000, Zoom, Width, 200);
        var after = InlineEditPlacement.Left(10_000, 10_400, Zoom, Width, 200);

        Assert.Equal(-400 * Zoom, after - before, 6);
    }

    [Fact]
    public void An_editor_stays_inside_the_timeline_whatever_the_pan()
    {
        Assert.Equal(0, InlineEditPlacement.Left(10_000, 500_000, Zoom, Width, 200));
        Assert.Equal(Width - 200, InlineEditPlacement.Left(900_000, 10_000, Zoom, Width, 200));
    }

    [Fact]
    public void The_rows_are_the_ones_the_renderer_draws()
    {
        Assert.Equal(300 + (800 - 300 - 100) + ChartEditorConstants.RenderMetrics.FhpYOffset,
            InlineEditPlacement.FhpTop(800, 300, 100));
        var ribbon = new RibbonLayout(RibbonLanes.Tones | RibbonLanes.Sections);
        Assert.Equal(ribbon.SectionY + ribbon.RowPitch, InlineEditPlacement.SectionPopoverTop(ribbon));
    }
}
