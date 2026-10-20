using DotCharter.UI.Controls;

namespace DotCharter.Tests.UI.Controls;

/// <summary>
///     The song map's view-window rectangle must be measured in the TIMELINE's
///     width, not in the map's own. In <c>MainWindow.axaml</c> the map strip
///     spends its first 118 px on the SONG MAP caption cell, so the two are
///     never equal and the box was drawn <c>118 / PixelsPerMs</c> ms too narrow
///     at every zoom.
/// </summary>
public sealed class MinimapViewportMathTests
{
    private const double PlayheadX = 200;

    [Fact]
    public void TheBoxIsMeasuredInTheTimelineWidth_NotTheMapWidth()
    {
        // 1 px/ms: the timeline shows 1,600 ms, the 482 px-wide map does not.
        const double scale = 0.01; // map px per song ms
        var (_, width) = MinimapViewportMath.ViewportBox(
            viewAnchorMs: 5_000, PlayheadX, pixelsPerMs: 1.0,
            timelineWidthPx: 1600, scale);

        Assert.Equal(1600 * scale, width, 6);
        // …and that is NOT what the map's own width would have produced.
        var (_, wrong) = MinimapViewportMath.ViewportBox(
            viewAnchorMs: 5_000, PlayheadX, pixelsPerMs: 1.0,
            timelineWidthPx: 1600 - 118, scale);
        Assert.NotEqual(wrong, width, 6);
    }

    [Fact]
    public void TheBoxStartsOneScreenAnchorBeforeTheViewAnchor()
    {
        const double scale = 0.01;
        var (x, _) = MinimapViewportMath.ViewportBox(
            viewAnchorMs: 5_000, PlayheadX, pixelsPerMs: 2.0,
            timelineWidthPx: 1600, scale);

        // 200 px at 2 px/ms = 100 ms of lead-in before the anchor.
        Assert.Equal((5_000 - 100) * scale, x, 6);
    }

    [Fact]
    public void AZeroZoomFallsBackToTheDefaultRatherThanDividingByZero()
    {
        var (x, width) = MinimapViewportMath.ViewportBox(
            viewAnchorMs: 1_000, PlayheadX, pixelsPerMs: 0, timelineWidthPx: 1600, scale: 0.01);

        Assert.True(double.IsFinite(x));
        Assert.True(double.IsFinite(width));
        Assert.True(width > 0);
    }

    [Fact]
    public void TheBoxIsNeverThinnerThanFourPixels()
    {
        var (_, width) = MinimapViewportMath.ViewportBox(
            viewAnchorMs: 0, PlayheadX, pixelsPerMs: 1.0, timelineWidthPx: 1600,
            scale: 0.000001);

        Assert.Equal(4, width, 6);
    }

    /// <summary>
    ///     …and the control must actually ASK. The math being right is worth
    ///     nothing if <c>DrawViewport</c> goes on passing its own
    ///     <c>Bounds.Width</c>, which is exactly what it did — and no pure test
    ///     of the helper can see that, because a <c>DrawingContext</c> is not
    ///     constructible in the default gate.
    /// </summary>
    [Fact]
    public void TheControlResolvesTheTimelineWidthInsteadOfUsingItsOwn()
    {
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Controls", "MinimapOverviewControl.cs")));
        var body = source[source.IndexOf("private void DrawViewport(", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("PlayheadPen", StringComparison.Ordinal)];

        Assert.Contains("MinimapViewportMath.ResolveTimelineWidth(", body, StringComparison.Ordinal);
        Assert.Contains("MinimapViewportMath.ViewportBox(", body, StringComparison.Ordinal);
        // The old shape, spelled out so a revert reads as this failure.
        Assert.DoesNotContain("width / pixelsPerMs", body, StringComparison.Ordinal);
    }

    [Theory]
    // explicit wins outright…
    [InlineData(1600d, 900d, 482d, 1600d)]
    // …then whatever ChartRenderer last published…
    [InlineData(0d, 900d, 482d, 900d)]
    // …and only a control with neither falls back to its own width.
    [InlineData(0d, 0d, 482d, 482d)]
    public void TheWidthSourcesArePreferredInOrder(
        double explicitWidth, double published, double map, double expected) => Assert.Equal(expected, MinimapViewportMath.ResolveTimelineWidth(explicitWidth, published, map));
}
