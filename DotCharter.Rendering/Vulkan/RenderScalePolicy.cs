using Silk.NET.Vulkan;
using DotCharter.Core.Graphics;

namespace DotCharter.Rendering.Vulkan;

/// <summary>
///     Pure render-scale decisions (0.14.2 Settings → Graphics): the 3D scene
///     renders into an intermediate color target at a percentage of the
///     swapchain extent and is blitted (linear) onto the swapchain image.
///     Extracted so the sizing rules are unit-testable without a Vulkan device
///     (<c>RenderScalePolicyTests</c>). 100% deliberately means "no
///     intermediate target at all" — the unscaled path must stay byte-for-byte
///     the pre-0.14.2 render-into-swapchain path.
/// </summary>
public static class RenderScalePolicy
{
    /// <summary>True when the percentage (after clamping) actually requests a
    /// scaled render target. Keyed on the percentage, not the computed
    /// extent, so the render-pass structure is deterministic per setting.</summary>
    public static bool IsScaled(int scalePercent) => GraphicsSettingsPolicy.ClampRenderScalePercent(scalePercent) != 100;

    /// <summary>
    ///     Computes the intermediate target extent: presentation extent scaled
    ///     by the (clamped) percentage, rounded, floored at 1×1 and capped at
    ///     the device's 2D image/framebuffer limits.
    /// </summary>
    public static Extent2D ComputeRenderExtent(Extent2D presentExtent, int scalePercent,
        uint maxWidth, uint maxHeight)
    {
        var percent = GraphicsSettingsPolicy.ClampRenderScalePercent(scalePercent);
        return new Extent2D(
            ScaleDimension(presentExtent.Width, percent, maxWidth),
            ScaleDimension(presentExtent.Height, percent, maxHeight));
    }

    private static uint ScaleDimension(uint value, int percent, uint max)
    {
        var scaled = (long)Math.Round(value * (double)percent / 100.0);
        if (max == 0) max = 1;
        return (uint)Math.Clamp(scaled, 1, max);
    }
}
