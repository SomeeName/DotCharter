using DotCharter.Rendering.Vulkan;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The 3D highway's textures get a full mip chain and anisotropic filtering, so a note
///     or inlay far down the highway neither shimmers nor smears.
/// </summary>
public sealed class TextureFilterPolicyTests
{
    [Theory]
    [InlineData(1u, 1u, 1u)]
    [InlineData(2u, 2u, 2u)]
    [InlineData(256u, 256u, 9u)]
    [InlineData(512u, 128u, 10u)]
    [InlineData(300u, 7u, 9u)]
    public void MipLevels_IsAFullChainDownToOnePixel(uint width, uint height, uint expected)
        => Assert.Equal(expected, TextureFilterPolicy.MipLevels(width, height));

    [Theory]
    [InlineData(true, 16f, 16f)]
    [InlineData(true, 8f, 8f)]
    [InlineData(true, 64f, 16f)]
    [InlineData(false, 16f, 0f)]
    [InlineData(true, 0f, 0f)]
    public void Anisotropy_IsSixteenClampedToTheDevice_AndOffWithoutTheFeature(bool supported, float limit,
        float expected)
        => Assert.Equal(expected, TextureFilterPolicy.Anisotropy(supported, limit));

    [Fact]
    public void Sampler_SamplesTheWholeChainAnisotropically()
    {
        var info = TextureFilterPolicy.Sampler(9, 16f);

        Assert.True(info.AnisotropyEnable);
        Assert.Equal(16f, info.MaxAnisotropy);
        Assert.Equal(8f, info.MaxLod);
        Assert.Equal(Silk.NET.Vulkan.SamplerMipmapMode.Linear, info.MipmapMode);
    }

    [Fact]
    public void Sampler_WithoutTheFeature_KeepsAnisotropyOff()
    {
        var info = TextureFilterPolicy.Sampler(1, 0f);

        Assert.False(info.AnisotropyEnable);
        Assert.Equal(1f, info.MaxAnisotropy);
        Assert.Equal(0f, info.MaxLod);
    }
}
