using Avalonia;
using DotCharter.UI.Constants;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Input;

namespace DotCharter.Tests.UI.Input;

/// <summary>The drawn fret badge edits; its spanning line remains available for selection.</summary>
public sealed class ChartHitTesterAnchorHeadTests
{
    [Fact]
    public void OneAndAHalfBeatsAfterTheAnchor_HitsItsTailButNotItsHead()
    {
        var beats = Beats();
        var anchor = new FretAnchor(new FractionalPosition(1), 12, 5);
        var pointer = new Point(1250, 109);

        Assert.Same(anchor, ChartHitTester.HitTestAnchor(pointer, [anchor], 450, 1, 1600, 100, beats));
        Assert.Null(ChartHitTester.HitTestAnchorHead(pointer, [anchor], 450, 1, 1600, 100, beats, _ => 16f));
        Assert.Same(anchor, ChartHitTester.HitTestAnchorHead(new Point(510, 109), [anchor], 450, 1, 1600, 100, beats, _ => 16f));
        Assert.Null(ChartHitTester.HitTestAnchor(new Point(490, 109), [anchor], 450, 1, 1600, 100, beats));
        Assert.Null(ChartHitTester.HitTestAnchorHead(new Point(490, 109), [anchor], 450, 1, 1600, 100, beats, _ => 16f));
    }

    [Theory]
    [InlineData(0.2, 450, 0)]
    [InlineData(0.2, 450, 8)]
    [InlineData(1, 900, 24)]
    public void HeadBoundsFollowTheRenderedBadgeAtEachZoomAndPan(double zoom, double viewAnchor, float textWidth)
    {
        var beats = Beats();
        var anchor = new FretAnchor(new FractionalPosition(2), 12, 4);
        var bounds = ChartHitTester.GetViewportBounds(viewAnchor, zoom, 1600);
        var x = (float)((1000 - bounds.StartMs) * zoom);
        var endX = (float)((Math.Max(bounds.EndMs, 6000) - bounds.StartMs) * zoom);
        var badge = FretAnchorLayer.GetHeadBounds(x, endX, 100, textWidth);

        Assert.Same(anchor, ChartHitTester.HitTestAnchorHead(new Point(badge.Right - 0.1, badge.Center.Y),
            [anchor], viewAnchor, zoom, 1600, 100, beats, _ => textWidth));
        Assert.Null(ChartHitTester.HitTestAnchorHead(new Point(badge.Right + 0.1, badge.Center.Y),
            [anchor], viewAnchor, zoom, 1600, 100, beats, _ => textWidth));
        Assert.Null(ChartHitTester.HitTestAnchorHead(new Point(badge.Center.X, badge.Top - 0.1),
            [anchor], viewAnchor, zoom, 1600, 100, beats, _ => textWidth));
    }

    [Fact]
    public void ThePinnedBadgeIsAHead_WhileAnUnlabelledRowDoesNotPinItsMarker()
    {
        var beats = Beats();
        var anchor = new FretAnchor(new FractionalPosition(0), 7, 4);
        var pointer = new Point(ChartEditorConstants.RenderMetrics.ActiveLabelFixedX, 109);

        Assert.Same(anchor, ChartHitTester.HitTestAnchorHead(pointer, [anchor], 1000, 1, 1600, 100, beats, _ => 8f));
        Assert.Null(ChartHitTester.HitTestAnchorHead(pointer, [anchor], 1000, 1, 1600, 100, beats, _ => null));
        Assert.Same(anchor, ChartHitTester.HitTestAnchorHead(pointer, [anchor], 450, 1, 1600, 100, beats, _ => null));
    }

    [Fact]
    public void AnUnlabelledRowEditsItsMarkerWithTheTailTestsMinimumHitWidth()
    {
        var beats = Beats();
        var anchor = new FretAnchor(new FractionalPosition(1), 7, 4);
        Assert.Same(anchor, ChartHitTester.HitTestAnchorHead(new Point(514.9, 109), [anchor], 450, 1,
            1600, 100, beats, _ => null));
        Assert.Null(ChartHitTester.HitTestAnchorHead(new Point(515.1, 109), [anchor], 450, 1,
            1600, 100, beats, _ => null));
    }

    private static List<BeatMarker> Beats() =>
        Enumerable.Range(0, 16).Select(i => new BeatMarker { Time = i * 500, Bpm = 120 }).ToList();
}
