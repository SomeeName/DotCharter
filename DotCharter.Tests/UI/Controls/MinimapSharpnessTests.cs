using DotCharter.UI.Controls;

namespace DotCharter.Tests.UI.Controls;

/// <summary>
///     The song map's cached layer (section names, note ticks, waveform) was a 96-DPI bitmap at the
///     control's LOGICAL size, drawn stretched to the display's scaling — so on a scaled screen the whole
///     map was blurred (owner report, 2026-09-23: "almost like it's not rendered sharp"). It is built in
///     device pixels now.
/// </summary>
public sealed class MinimapSharpnessTests
{
    [Theory]
    [InlineData(1.0, 1200, 40)]
    [InlineData(1.25, 1500, 50)]
    [InlineData(1.5, 1800, 60)]
    [InlineData(2.0, 2400, 80)]
    public void TheLayer_HasOneBitmapPixelPerDevicePixel(double scaling, int expectedWidth, int expectedHeight)
    {
        var (w, h) = MinimapViewportMath.StaticLayerPixels(1200, 40, scaling);
        Assert.Equal(expectedWidth, w);
        Assert.Equal(expectedHeight, h);
    }

    [Fact]
    public void AnUnknownScaling_FallsBackToOne()
    {
        Assert.Equal((1200, 40), MinimapViewportMath.StaticLayerPixels(1200, 40, 0));
        Assert.Equal((1200, 40), MinimapViewportMath.StaticLayerPixels(1200, 40, double.NaN));
    }

    [Fact]
    public void TheControl_SizesAndScalesItsLayerByTheWindowsScaling()
    {
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Controls", "MinimapOverviewControl.cs")));

        // Sized for the pixels the map really covers: the window's scaling times the interface
        // scale's layout transform (DeviceScale). Sized from the window's scaling alone, the layer
        // was stretched 1.1× at a 110 % interface scale and every tenth pixel column doubled.
        Assert.Contains("MinimapViewportMath.StaticLayerPixels(width, height, renderScaling)", source);
        Assert.Contains("var renderScaling = DeviceScale();", source);
        Assert.Contains("private double DeviceScale() => VisualPixelScale.For(this);", source);
        // Drawn into at 96 DPI under an explicit device-scale transform, and drawn back 1:1: every
        // bitmap pixel to one screen pixel, never squeezed into the control's fractional width.
        Assert.Contains("PushTransform(Matrix.CreateScale(_layerScaling, _layerScaling))", source);
        var flat = string.Join(" ", source.Split(['\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("new Rect(0, 0, _staticLayerBitmap.PixelSize.Width, _staticLayerBitmap.PixelSize.Height), "
                        + "new Rect(0, 0, _staticLayerBitmap.PixelSize.Width / _layerScaling, "
                        + "_staticLayerBitmap.PixelSize.Height / _layerScaling));", flat);
        Assert.Contains("RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);", source);
    }
}
