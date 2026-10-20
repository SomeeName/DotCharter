using DotCharter.Rendering.Vulkan.Composed;
using DotCharter.Services.Infrastructure.Graphics;

namespace DotCharter.UI.Platform;

/// <summary>
///     <see cref="IGpuRenderProbe" /> over the renderer's own device rule
///     (<see cref="ComposedDeviceProbe" />): an instance and the device pick, never a
///     device. Lives here for the reason <see cref="VulkanGraphicsDeviceSettings" />
///     does — this is the one project that sees both Rendering and Services.
/// </summary>
public sealed class VulkanGpuRenderProbe : IGpuRenderProbe
{
    public GpuRenderCheck Probe() => Map(ComposedDeviceProbe.Run());

    public static GpuRenderCheck Map(ComposedDeviceProbeResult result) => new(result.Support switch
    {
        ComposedDeviceSupport.Supported => GpuRenderState.Ready,
        ComposedDeviceSupport.NoDevice => GpuRenderState.NoDevice,
        ComposedDeviceSupport.NoInteropDevice => GpuRenderState.NoInteropDevice,
        _ => GpuRenderState.NoVulkan
    }, result.DeviceName, result.Detail);
}
