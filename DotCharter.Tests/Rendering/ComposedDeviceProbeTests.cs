using DotCharter.Rendering.Vulkan.Composed;
using DotCharter.Services.Infrastructure.Graphics;
using DotCharter.UI.Platform;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The pure half of the onboarding GPU probe (#288): the verdict over the enumerated
///     devices is the composed path's own pick (<see cref="ComposedDevicePolicy" />), so
///     the first page says "unsupported" exactly when <c>InitializeComposed</c> would fail
///     for want of a device — and never on <c>devices[0]</c>. No Vulkan call is made here.
/// </summary>
public sealed class ComposedDeviceProbeTests
{
    private static (ComposedDeviceCandidate, string) Device(bool usable, string name) =>
        (new ComposedDeviceCandidate(usable, false, new byte[8]), name);

    [Fact]
    public void NoDevices_IsNoDevice()
    {
        var result = ComposedDeviceProbe.Classify([]);

        Assert.Equal(ComposedDeviceSupport.NoDevice, result.Support);
        Assert.Equal(string.Empty, result.DeviceName);
    }

    [Fact]
    public void DevicesWithoutTheInterop_AreNoInteropDevice_AndTheLogNamesThem()
    {
        var result = ComposedDeviceProbe.Classify([Device(false, "Microsoft Basic Render"), Device(false, "Old GPU")]);

        Assert.Equal(ComposedDeviceSupport.NoInteropDevice, result.Support);
        Assert.Contains("Old GPU", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFirstUsableDevice_IsTheOneReported_NotTheFirstEnumerated()
    {
        var result = ComposedDeviceProbe.Classify([Device(false, "llvmpipe"), Device(true, "GeForce RTX 3080")]);

        Assert.Equal(ComposedDeviceSupport.Supported, result.Support);
        Assert.Equal("GeForce RTX 3080", result.DeviceName);
    }

    [Theory]
    [InlineData(ComposedDeviceSupport.Supported, GpuRenderState.Ready)]
    [InlineData(ComposedDeviceSupport.NoVulkan, GpuRenderState.NoVulkan)]
    [InlineData(ComposedDeviceSupport.NoDevice, GpuRenderState.NoDevice)]
    [InlineData(ComposedDeviceSupport.NoInteropDevice, GpuRenderState.NoInteropDevice)]
    public void TheUiAdapter_MapsEveryVerdict(ComposedDeviceSupport support, GpuRenderState expected)
    {
        var check = VulkanGpuRenderProbe.Map(new ComposedDeviceProbeResult(support, "GPU", "detail"));

        Assert.Equal(expected, check.State);
        Assert.Equal("GPU", check.DeviceName);
        Assert.Equal("detail", check.Detail);
    }
}
