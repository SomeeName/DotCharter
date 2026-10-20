using Silk.NET.Vulkan;

namespace DotCharter.Rendering.Vulkan;

/// <summary>How the scene render pass hands its color output onward.</summary>
public enum SceneOutputMode
{
    /// <summary>Render directly into the swapchain image (100% scale, no
    /// post effects) — the classic zero-cost path, final layout PresentSrc.</summary>
    Direct,

    /// <summary>Render into the scaled intermediate, EndFrame blits it onto
    /// the swapchain image (render scale ≠ 100%, no post effects) — final
    /// layout TransferSrc.</summary>
    Blit,

    /// <summary>Render into the intermediate as a SAMPLED texture; EndFrame
    /// runs the post-process chain (bloom mips) and a fullscreen composite
    /// pass into the swapchain image (bloom and/or filmic tonemap on; also
    /// absorbs render scaling — the composite draw upsamples) — final layout
    /// ShaderReadOnly.</summary>
    Sampled
}

/// <summary>
///     Pure decisions for the 0.15.6 post-process chain (plan 5.5 bloom +
///     composite). No Vulkan calls — unit-tested in
///     <c>PostProcessPolicyTests</c>.
/// </summary>
public static class PostProcessPolicy
{
    /// <summary>Bloom mip levels (half + quarter resolution).</summary>
    public const int BloomMipCount = 2;

    /// <summary>Threshold extract: luminance where bloom starts contributing
    /// fully…</summary>
    public const float BloomThreshold = 0.68f;

    /// <summary>…with a soft knee this wide below it (smoothstep).</summary>
    public const float BloomKnee = 0.22f;

    /// <summary>Additive composite weight of the half-resolution mip.</summary>
    public const float BloomIntensityHalf = 0.42f;

    /// <summary>Additive composite weight of the quarter-resolution mip.</summary>
    public const float BloomIntensityQuarter = 0.30f;

    /// <summary>The output mode the scene pass must be built for.</summary>
    public static SceneOutputMode ChooseOutputMode(bool postEffectsActive, bool renderScaleActive)
    {
        if (postEffectsActive) return SceneOutputMode.Sampled;
        return renderScaleActive ? SceneOutputMode.Blit : SceneOutputMode.Direct;
    }

    /// <summary>Extent of bloom mip <paramref name="level" /> (1-based: 1 =
    /// half res, 2 = quarter res), clamped so a tiny render target never
    /// produces a zero-sized image.</summary>
    public static Extent2D BloomMipExtent(Extent2D renderExtent, int level)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 1);
        var shift = level;
        return new Extent2D(
            Math.Max(1, renderExtent.Width >> shift),
            Math.Max(1, renderExtent.Height >> shift));
    }
}
