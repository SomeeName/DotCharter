using DotCharter.Rendering.Vulkan;
using Silk.NET.Vulkan;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     0.14.2 — pure sizing rules for the render-scale intermediate target.
///     No Vulkan device involved.
/// </summary>
public class RenderScalePolicyTests
{
    private const uint MaxDim = 16384;

    [Theory]
    [InlineData(100, false)] // 100% = the exact pre-0.14.2 direct path
    [InlineData(50, true)]
    [InlineData(200, true)]
    [InlineData(99, true)]
    [InlineData(0, true)] // clamps to 50 → scaled
    [InlineData(10_000, true)] // clamps to 200 → scaled
    public void IsScaled_KeyedOnTheClampedPercent(int percent, bool expected) => Assert.Equal(expected, RenderScalePolicy.IsScaled(percent));

    [Theory]
    [InlineData(1920u, 1080u, 100, 1920u, 1080u)]
    [InlineData(1920u, 1080u, 50, 960u, 540u)]
    [InlineData(1920u, 1080u, 200, 3840u, 2160u)]
    [InlineData(1280u, 720u, 75, 960u, 540u)]
    public void ComputeRenderExtent_ScalesBothDimensions(uint w, uint h, int percent,
        uint expectedW, uint expectedH)
    {
        var result = RenderScalePolicy.ComputeRenderExtent(new Extent2D(w, h), percent, MaxDim, MaxDim);
        Assert.Equal(expectedW, result.Width);
        Assert.Equal(expectedH, result.Height);
    }

    [Fact]
    public void ComputeRenderExtent_RoundsHalfPixels()
    {
        // 1001 × 75% = 750.75 → 751 (round, not truncate — truncation drifts
        // the aspect ratio on odd sizes).
        var result = RenderScalePolicy.ComputeRenderExtent(new Extent2D(1001, 999), 75, MaxDim, MaxDim);
        Assert.Equal(751u, result.Width);
        Assert.Equal(749u, result.Height);
    }

    [Fact]
    public void ComputeRenderExtent_NeverGoesBelowOnePixel()
    {
        var result = RenderScalePolicy.ComputeRenderExtent(new Extent2D(1, 1), 50, MaxDim, MaxDim);
        Assert.Equal(1u, result.Width);
        Assert.Equal(1u, result.Height);
    }

    [Fact]
    public void ComputeRenderExtent_CapsAtTheDeviceLimits()
    {
        // 200% of an already-huge surface must not exceed what the device can
        // create images/framebuffers for.
        var result = RenderScalePolicy.ComputeRenderExtent(new Extent2D(10_000, 9000), 200, 16384, 12000);
        Assert.Equal(16384u, result.Width);
        Assert.Equal(12000u, result.Height);
    }

    [Fact]
    public void ComputeRenderExtent_ClampsDegeneratePercents()
    {
        var low = RenderScalePolicy.ComputeRenderExtent(new Extent2D(1000, 1000), -20, MaxDim, MaxDim);
        Assert.Equal(500u, low.Width); // clamped to 50%

        var high = RenderScalePolicy.ComputeRenderExtent(new Extent2D(1000, 1000), 9999, MaxDim, MaxDim);
        Assert.Equal(2000u, high.Width); // clamped to 200%
    }
}
