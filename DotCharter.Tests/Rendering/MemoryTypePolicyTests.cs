using DotCharter.Rendering.Vulkan;
using Silk.NET.Vulkan;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     0.14 P6 — pure memory-type selection for the shared transient MSAA/
///     depth attachments: prefer LAZILY_ALLOCATED, fall back to DEVICE_LOCAL,
///     always constrained by the resource's <c>memoryTypeBits</c>.
/// </summary>
public class MemoryTypePolicyTests
{
    private const MemoryPropertyFlags DeviceLocal = MemoryPropertyFlags.DeviceLocalBit;
    private const MemoryPropertyFlags Lazy = MemoryPropertyFlags.LazilyAllocatedBit;
    private const MemoryPropertyFlags HostVisible = MemoryPropertyFlags.HostVisibleBit;

    [Fact]
    public void PrefersTheLazilyAllocatedType_WhenTheHeapHasOne()
    {
        // Tiler/UMA-style layout: type 0 device-local, type 1 device-local+lazy.
        ReadOnlySpan<MemoryPropertyFlags> flags = [DeviceLocal, DeviceLocal | Lazy];
        Assert.Equal(1, MemoryTypePolicy.Pick(0b11, flags, Lazy, DeviceLocal));
    }

    [Fact]
    public void FallsBackToDeviceLocal_WhenNoLazilyAllocatedTypeExists()
    {
        // Desktop-style layout: no lazily-allocated type at all.
        ReadOnlySpan<MemoryPropertyFlags> flags = [HostVisible, DeviceLocal];
        Assert.Equal(1, MemoryTypePolicy.Pick(0b11, flags, Lazy, DeviceLocal));
    }

    [Fact]
    public void RespectsTheTypeFilter_ForThePreferredFlags()
    {
        // A lazily-allocated type exists but the image's memoryTypeBits
        // exclude it — the policy must not pick a disallowed index.
        ReadOnlySpan<MemoryPropertyFlags> flags = [DeviceLocal | Lazy, DeviceLocal];
        Assert.Equal(1, MemoryTypePolicy.Pick(0b10, flags, Lazy, DeviceLocal));
    }

    [Fact]
    public void RespectsTheTypeFilter_ForTheFallbackFlags()
    {
        ReadOnlySpan<MemoryPropertyFlags> flags = [DeviceLocal, DeviceLocal, HostVisible];
        Assert.Equal(1, MemoryTypePolicy.Pick(0b110, flags, Lazy, DeviceLocal));
    }

    [Fact]
    public void ZeroPreferredFlags_GoStraightToTheRequiredLookup()
    {
        ReadOnlySpan<MemoryPropertyFlags> flags = [HostVisible, DeviceLocal];
        Assert.Equal(1, MemoryTypePolicy.Pick(0b11, flags, 0, DeviceLocal));
    }

    [Fact]
    public void NoMatchAtAll_ReturnsMinusOne()
    {
        ReadOnlySpan<MemoryPropertyFlags> flags = [HostVisible];
        Assert.Equal(-1, MemoryTypePolicy.Pick(0b1, flags, Lazy, DeviceLocal));
    }

    [Fact]
    public void PreferredMatchMustCarryAllPreferredFlags_NotASubset()
    {
        // Preferred = DeviceLocal|Lazy: a type with only DeviceLocal is not a
        // preferred match, but IS the fallback.
        ReadOnlySpan<MemoryPropertyFlags> flags = [DeviceLocal, HostVisible];
        Assert.Equal(0, MemoryTypePolicy.Pick(0b11, flags, DeviceLocal | Lazy, DeviceLocal));
    }
}
