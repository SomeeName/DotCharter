using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     0.14.2 B — pure upcoming-anchor lookup for the dimmed FHP preview.
///     A "change" is an anchor whose fret/width differs from the position
///     playing right before it; restated positions are skipped.
/// </summary>
public class NextAnchorWindowTests
{
    private static RenderAnchor A(double timeMs, byte fret, byte width = 4) => new() { Time = timeMs, EndTime = timeMs, Fret = fret, Width = width };

    [Fact]
    public void NoAnchors_NoPreview()
    {
        var result = NextAnchorWindow.Find([], 0, 1000, 1000);
        Assert.False(result.HasUpcoming);
    }

    [Fact]
    public void NextDifferingAnchorInsideTheWindow_IsReported()
    {
        var anchors = new[] { A(0, 3), A(1500, 7, 5) };
        var result = NextAnchorWindow.Find(anchors, anchors.Length, 1000, 1000);

        Assert.True(result.HasUpcoming);
        Assert.Equal(1500, result.AnchorTimeMs);
        Assert.Equal(7, result.Fret);
        Assert.Equal(5, result.Width);
    }

    [Fact]
    public void AnchorBeyondTheLeadWindow_IsNotPreviewedYet()
    {
        var anchors = new[] { A(0, 3), A(2500, 7) };
        Assert.False(NextAnchorWindow.Find(anchors, anchors.Length, 1000, 1000).HasUpcoming);
        // …but is once the playhead moves close enough.
        Assert.True(NextAnchorWindow.Find(anchors, anchors.Length, 1600, 1000).HasUpcoming);
    }

    [Theory]
    [InlineData(500, 0.5f)] // halfway through the window
    [InlineData(100, 0.1f)] // change just entered the window (900 ms away)
    [InlineData(900, 0.9f)] // change almost here (100 ms away)
    public void FadeFraction_RampsLinearly_TowardTheChange(double now, float expected)
    {
        var anchors = new[] { A(0, 3), A(1000, 7) };
        var result = NextAnchorWindow.Find(anchors, anchors.Length, now, 1000);

        Assert.True(result.HasUpcoming);
        Assert.Equal(expected, result.FadeFraction, 3);
    }

    [Fact]
    public void RestatedPosition_IsNotAShift_LaterRealShiftIsFound()
    {
        // +300 restates 3/4 (no visible change), +800 actually moves.
        var anchors = new[] { A(0, 3), A(1300, 3), A(1800, 9) };
        var result = NextAnchorWindow.Find(anchors, anchors.Length, 1000, 1000);

        Assert.True(result.HasUpcoming);
        Assert.Equal(9, result.Fret);
        Assert.Equal(1800, result.AnchorTimeMs);
    }

    [Fact]
    public void WidthOnlyChange_CountsAsAShift()
    {
        var anchors = new[] { A(0, 3, 4), A(1500, 3, 6) };
        var result = NextAnchorWindow.Find(anchors, anchors.Length, 1000, 1000);

        Assert.True(result.HasUpcoming);
        Assert.Equal(6, result.Width);
    }

    [Fact]
    public void NoCurrentAnchor_UsesTheDefaultPositionAsBaseline()
    {
        // Default baseline is fret 1 / width 4 (camera + fret-number default):
        // an upcoming 1/4 anchor is a restatement, an upcoming 5/4 is a shift.
        var restated = new[] { A(500, 1, 4) };
        Assert.False(NextAnchorWindow.Find(restated, restated.Length, 0, 1000).HasUpcoming);

        var shifted = new[] { A(500, 5, 4) };
        Assert.True(NextAnchorWindow.Find(shifted, shifted.Length, 0, 1000).HasUpcoming);
    }

    [Fact]
    public void NonPositiveLead_DisablesThePreview()
    {
        var anchors = new[] { A(0, 3), A(100, 7) };
        Assert.False(NextAnchorWindow.Find(anchors, anchors.Length, 50, 0).HasUpcoming);
        Assert.False(NextAnchorWindow.Find(anchors, anchors.Length, 50, -100).HasUpcoming);
    }
}
