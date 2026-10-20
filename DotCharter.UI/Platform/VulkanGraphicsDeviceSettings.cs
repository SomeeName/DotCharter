using DotCharter.Rendering.Vulkan;
using DotCharter.Services.Infrastructure.Graphics;
using DotCharter.UI.Controls;

namespace DotCharter.UI.Platform;

/// <summary>
///     <see cref="IGraphicsDeviceSettings" /> over the Vulkan renderer.
///     <para>
///         The adapter lives here rather than on <c>VulkanRenderer</c> because
///         <c>DotCharter.Rendering</c> may never reference <c>DotCharter.Services</c>
///         — the dependency runs the other way. <c>DotCharter.UI</c> is the one
///         project that sees both, which is what makes it the adapter's home,
///         the same way the OS adapters beside it are.
///     </para>
/// </summary>
public sealed class VulkanGraphicsDeviceSettings(IVulkanRenderService renderer) : IGraphicsDeviceSettings
{
    public IReadOnlyList<string> SupportedPresentModes => renderer.SupportedPresentModes;

    public IReadOnlyList<int> SupportedMsaaSamples => renderer.SupportedMsaaSamples;

    public void SetPresentMode(string presentMode) => renderer.SetPresentMode(presentMode);

    public void SetMsaaSamples(int samples) => renderer.SetMsaaSamples(samples);

    public void SetRenderScalePercent(int percent) => renderer.SetRenderScalePercent(percent);

    public int DefaultMsaaSamples => MsaaPolicy.DefaultSampleCount;

    public double MaxVisualLeadMs => VisualLeadTiming.MaxLeadMs;

    // The pacing, timeline and lead channels are process-wide statics the render
    // loops read per frame. They are not the renderer's, which is why they were
    // not on this seam until a Presentation screen had to reach them: the old
    // settings page wrote them directly from DotCharter.UI.
    public void SetMaxFps(int fps) => Preview3DPacing.MaxFps = fps;

    public void SetIdleFps(int fps) => Preview3DPacing.IdleFps = fps;

    public void SetTimelineVisuals(bool polish, bool laneTint)
    {
        Timeline2DVisuals.PolishEnabled = polish;
        Timeline2DVisuals.LaneTintEnabled = laneTint;
    }

    public void SetVisualLeadTrimMs(double ms) =>
        VisualLeadTiming.LeadTrimMs = Math.Clamp(ms, 0, VisualLeadTiming.MaxLeadMs);

    public void SetFingeringOverlay(bool enabled) =>
        DotCharter.Rendering.Components.NoteRendering.FingeringRenderer.Enabled = enabled;
}
