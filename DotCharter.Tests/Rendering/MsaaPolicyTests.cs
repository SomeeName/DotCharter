using DotCharter.Rendering.Vulkan;
using Silk.NET.Vulkan;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     0.14.2 — pure MSAA clamping against the device's framebuffer sample
///     mask (color ∩ depth). No Vulkan device involved.
/// </summary>
public class MsaaPolicyTests
{
    private const SampleCountFlags UpTo8 =
        SampleCountFlags.Count1Bit | SampleCountFlags.Count2Bit |
        SampleCountFlags.Count4Bit | SampleCountFlags.Count8Bit;

    private const SampleCountFlags UpTo4 =
        SampleCountFlags.Count1Bit | SampleCountFlags.Count2Bit | SampleCountFlags.Count4Bit;

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 4)]
    [InlineData(8, 8)]
    public void Clamp_PassesThrough_WhenDeviceSupportsIt(int requested, int expected) => Assert.Equal(expected, MsaaPolicy.Clamp(requested, UpTo8));

    [Theory]
    [InlineData(8, 4)] // above device max → highest supported
    [InlineData(16, 4)] // above the offered set entirely
    public void Clamp_WalksDown_ToTheDeviceMax(int requested, int expected) => Assert.Equal(expected, MsaaPolicy.Clamp(requested, UpTo4));

    [Theory]
    [InlineData(0, 1)] // degenerate config values floor at "off"
    [InlineData(-4, 1)]
    [InlineData(3, 2)] // non-offered values round down to the offered set
    [InlineData(5, 4)]
    [InlineData(7, 4)]
    public void Clamp_NormalizesToTheOfferedSet(int requested, int expected) => Assert.Equal(expected, MsaaPolicy.Clamp(requested, UpTo8));

    [Fact]
    public void Clamp_NeverFails_EvenOnAnEmptyMask()
    {
        // 1 sample needs no mask bit — rendering single-sampled is always valid.
        Assert.Equal(1, MsaaPolicy.Clamp(8, 0));
        Assert.Equal(1, MsaaPolicy.Clamp(1, 0));
    }

    [Fact]
    public void Clamp_SkipsUnsupportedMiddleLevels()
    {
        // Pathological mask: 8x supported but 4x not (mask gaps do occur in
        // the wild for depth formats) — an 8x request stays 8x, a 4x request
        // walks down past the gap to 2x.
        const SampleCountFlags gappy =
            SampleCountFlags.Count1Bit | SampleCountFlags.Count2Bit | SampleCountFlags.Count8Bit;
        Assert.Equal(8, MsaaPolicy.Clamp(8, gappy));
        Assert.Equal(2, MsaaPolicy.Clamp(4, gappy));
    }

    [Theory]
    [InlineData(1, SampleCountFlags.Count1Bit)]
    [InlineData(2, SampleCountFlags.Count2Bit)]
    [InlineData(4, SampleCountFlags.Count4Bit)]
    [InlineData(8, SampleCountFlags.Count8Bit)]
    [InlineData(0, SampleCountFlags.Count1Bit)] // anything else = 1x
    [InlineData(3, SampleCountFlags.Count1Bit)]
    public void ToFlag_MapsTheOfferedSet(int samples, SampleCountFlags expected) => Assert.Equal(expected, MsaaPolicy.ToFlag(samples));

    [Fact]
    public void SupportedOffered_AlwaysIncludesOff_AndFiltersByMask()
    {
        Assert.Equal(new[] { 1, 2, 4, 8 }, MsaaPolicy.SupportedOffered(UpTo8));
        Assert.Equal(new[] { 1, 2, 4 }, MsaaPolicy.SupportedOffered(UpTo4));
        Assert.Equal(new[] { 1 }, MsaaPolicy.SupportedOffered(0));
    }

    [Fact]
    public void Default_IsTheOldHardcodedLevel() => Assert.Equal(4, MsaaPolicy.DefaultSampleCount);
}
