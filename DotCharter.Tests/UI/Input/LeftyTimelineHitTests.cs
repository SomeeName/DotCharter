using Avalonia;
using DotCharter.UI.Controls;
using DotCharter.UI.Editing.Tracks;
using DotCharter.UI.Input;

namespace DotCharter.Tests.UI.Input;

public sealed class LeftyTimelineHitTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void PointerAndMarquee_UseOnlyInvertStrings(bool lefty, bool invert)
    {
        IChartViewport viewport = new Viewport(lefty, invert);
        const double lowY = 65;
        const double highY = 315;
        var expectedY = invert ? highY : lowY;
        Assert.Equal(expectedY, ChartHitTester.GetLaneY(380, 40, 40, 6, 0, viewport.FlipStrings));
        Assert.Equal(0, ChartHitTester.GetLaneIndexAtY(expectedY, 380, 40, 40, 6, viewport.FlipStrings));

        var note = new GuitarNote { Position = new FractionalPosition(1), StringIndex = 0 };
        var results = new List<GuitarNote>();
        MarqueeHitTester.CollectNotes(new Rect(480, expectedY - 10, 40, 20), [note],
            450, 1, 1000, 380, 40, 40, 6,
            [new BeatMarker { Time = 0, Bpm = 120 }, new BeatMarker { Time = 500, Bpm = 120 }],
            results, viewport.FlipStrings);
        Assert.Same(note, Assert.Single(results));
    }

    private sealed class Viewport(bool lefty, bool invert) : IChartViewport
    {
        public double ViewportWidth => 1000;
        public double ViewportHeight => 380;
        public double TopMargin => 40;
        public double BottomMargin => 40;
        public bool LeftyFlip => lefty;
        public bool InvertStrings => invert;
        public RibbonLayout TopRibbon => default;
        public void RequestNextFrameRendering() { }
        public void SetMarquee(Rect? marquee) { }
        public void ShowContextMenu(IReadOnlyList<TimelineMenuItem> items) { }
    }
}
