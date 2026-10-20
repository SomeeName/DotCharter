using Silk.NET.Vulkan;

namespace DotCharter.Rendering.Vulkan;

/// <summary>
///     Pure texture-filtering decisions for the 3D highway's textures (note atlas, inlays,
///     glyphs), unit-testable without a Vulkan device (<c>TextureFilterPolicyTests</c>).
///     The highway is seen at a grazing angle, so a far texture covers few pixels along the
///     time axis: without mipmaps it shimmers, and with mipmaps alone it blurs. A full mip
///     chain plus anisotropic filtering keeps it sharp and stable.
/// </summary>
public static class TextureFilterPolicy
{
    /// <summary>The anisotropy we ask for; the device limit clamps it.</summary>
    public const float PreferredAnisotropy = 16f;

    /// <summary>A full mip chain down to 1x1: floor(log2(max side)) + 1 levels.</summary>
    public static uint MipLevels(uint width, uint height)
    {
        var side = Math.Max(1u, Math.Max(width, height));
        var levels = 1u;
        while (side > 1)
        {
            side >>= 1;
            levels++;
        }

        return levels;
    }

    /// <summary>
    ///     The anisotropy to sample with: <see cref="PreferredAnisotropy" /> clamped to the
    ///     device limit, or 0 (off) when the device lacks the samplerAnisotropy feature.
    /// </summary>
    public static float Anisotropy(bool featureSupported, float deviceLimit)
        => featureSupported && deviceLimit >= 1f ? Math.Min(PreferredAnisotropy, deviceLimit) : 0f;

    /// <summary>
    ///     The sampler for a texture with <paramref name="mipLevels" /> levels: trilinear
    ///     across the whole chain, anisotropic when <paramref name="anisotropy" /> is at least 1.
    /// </summary>
    public static SamplerCreateInfo Sampler(uint mipLevels, float anisotropy) => new()
    {
        SType = StructureType.SamplerCreateInfo,
        MagFilter = Filter.Linear,
        MinFilter = Filter.Linear,
        MipmapMode = SamplerMipmapMode.Linear,
        AddressModeU = SamplerAddressMode.ClampToEdge,
        AddressModeV = SamplerAddressMode.ClampToEdge,
        AddressModeW = SamplerAddressMode.ClampToEdge,
        AnisotropyEnable = anisotropy >= 1f,
        MaxAnisotropy = anisotropy >= 1f ? anisotropy : 1f,
        MinLod = 0f,
        MaxLod = Math.Max(0, mipLevels - 1)
    };
}
