using DotCharter.Rendering.Vulkan;
using Silk.NET.Vulkan;

namespace DotCharter.Tests.Rendering;

public class PostProcessPolicyTests
{
    [Theory]
    [InlineData(false, false, SceneOutputMode.Direct)]
    [InlineData(false, true, SceneOutputMode.Blit)]
    [InlineData(true, false, SceneOutputMode.Sampled)]
    // Post effects absorb the render scaling in the composite draw — Sampled
    // wins over Blit whenever the post chain is active.
    [InlineData(true, true, SceneOutputMode.Sampled)]
    public void ChooseOutputMode_PostEffectsWinOverBlit(bool postEffects, bool renderScale, SceneOutputMode expected) => Assert.Equal(expected, PostProcessPolicy.ChooseOutputMode(postEffects, renderScale));

    [Theory]
    [InlineData(1920u, 1080u, 1, 960u, 540u)]
    [InlineData(1920u, 1080u, 2, 480u, 270u)]
    [InlineData(1919u, 1079u, 1, 959u, 539u)] // floor division
    [InlineData(3u, 3u, 2, 1u, 1u)]
    public void BloomMipExtent_HalvesPerLevel(uint w, uint h, int level, uint expectedW, uint expectedH)
    {
        var extent = PostProcessPolicy.BloomMipExtent(new Extent2D(w, h), level);
        Assert.Equal(expectedW, extent.Width);
        Assert.Equal(expectedH, extent.Height);
    }

    [Fact]
    public void BloomMipExtent_NeverReachesZero()
    {
        var extent = PostProcessPolicy.BloomMipExtent(new Extent2D(1, 1), 2);
        Assert.Equal(1u, extent.Width);
        Assert.Equal(1u, extent.Height);
    }

    [Fact]
    public void BloomMipExtent_RejectsNonPositiveLevels()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PostProcessPolicy.BloomMipExtent(new Extent2D(100, 100), 0));
    }

    [Fact]
    public void BloomConstants_StaySubtle()
    {
        // Guard rails: the bloom must stay an accent, not a bleach — a total
        // additive weight above ~1 white-clips every bright gem.
        Assert.InRange(PostProcessPolicy.BloomThreshold, 0.4f, 0.95f);
        Assert.True(PostProcessPolicy.BloomKnee > 0f);
        Assert.InRange(PostProcessPolicy.BloomIntensityHalf + PostProcessPolicy.BloomIntensityQuarter, 0.1f, 1.0f);
    }
}
