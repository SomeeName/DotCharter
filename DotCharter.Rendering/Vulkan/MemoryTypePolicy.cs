using Silk.NET.Vulkan;

namespace DotCharter.Rendering.Vulkan;

/// <summary>
///     Pure memory-type selection (no Vk handles — unit-tested in
///     <c>MemoryTypePolicyTests</c>, the same pattern as
///     <see cref="SwapchainPolicy" />). Used by
///     <see cref="VulkanContext.CreateImage" /> so the transient MSAA/depth
///     attachments can PREFER a lazily-allocated memory type (free on
///     tiler/UMA hardware) and fall back to plain device-local on desktop
///     heaps that expose none — always honouring the resource's
///     <c>memoryTypeBits</c>.
/// </summary>
public static class MemoryTypePolicy
{
    /// <summary>
    ///     Picks the first memory type allowed by <paramref name="typeFilter" />
    ///     (the resource's <c>memoryTypeBits</c>) whose property flags contain
    ///     <paramref name="preferred" />; when none qualifies (or
    ///     <paramref name="preferred" /> is 0), falls back to the first type
    ///     containing <paramref name="required" />. Returns -1 when nothing
    ///     matches at all.
    /// </summary>
    public static int Pick(uint typeFilter, ReadOnlySpan<MemoryPropertyFlags> typeFlags,
        MemoryPropertyFlags preferred, MemoryPropertyFlags required)
    {
        if (preferred != 0)
        {
            var i = IndexOf(typeFilter, typeFlags, preferred);
            if (i >= 0) return i;
        }

        return IndexOf(typeFilter, typeFlags, required);
    }

    private static int IndexOf(uint typeFilter, ReadOnlySpan<MemoryPropertyFlags> typeFlags,
        MemoryPropertyFlags wanted)
    {
        for (var i = 0; i < typeFlags.Length; i++)
        {
            if ((typeFilter & (1u << i)) != 0 && (typeFlags[i] & wanted) == wanted)
                return i;
        }

        return -1;
    }
}
