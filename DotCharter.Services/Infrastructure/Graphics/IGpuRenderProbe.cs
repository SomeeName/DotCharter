namespace DotCharter.Services.Infrastructure.Graphics;

/// <summary>Whether the GPU can run the Vulkan renderer the highway and the chart views draw with.</summary>
public enum GpuRenderState
{
    Ready,

    /// <summary>No Vulkan driver: the loader is missing or refuses an instance.</summary>
    NoVulkan,

    /// <summary>Vulkan loads but finds no graphics card.</summary>
    NoDevice,

    /// <summary>A card exists, but none can hand its frames to the window (no D3D11 shared-texture interop).</summary>
    NoInteropDevice
}

/// <summary>The answer, with the card the renderer would use when <see cref="State" /> is Ready and a line for the log.</summary>
public sealed record GpuRenderCheck(GpuRenderState State, string DeviceName = "", string Detail = "");

/// <summary>
///     Asks the renderer's own device rule whether this machine can draw, without
///     creating a device. Implemented in <c>DotCharter.UI/Platform</c> over
///     <c>DotCharter.Rendering</c>, which a Presentation screen cannot name.
/// </summary>
public interface IGpuRenderProbe
{
    /// <summary>Blocking — a driver call. Callers run it off the UI thread.</summary>
    GpuRenderCheck Probe();
}
