using Silk.NET.Vulkan;

namespace DotCharter.Rendering.Vulkan;

/// <summary>
///     The persisted present-mode token's parse (see <c>SwapchainPolicyTests</c>).
///     There are no image-count / extent / present-mode clamps here: they would only
///     serve a native child-HWND swapchain, which does not exist: the composed
///     and headless paths have no surface to clamp against, so the parsed mode
///     is recorded by <see cref="VulkanRenderer.SetPresentMode" /> and has no
///     effect on presentation (pacing is the compositor's).
/// </summary>
public static class SwapchainPolicy
{
    /// <summary>
    ///     Maps the persisted AppConfig token to a Vulkan present mode.
    ///     Unknown/empty tokens fall back to FIFO — the only mode the spec
    ///     guarantees (and the pre-0.14.2 hardcoded behaviour).
    /// </summary>
    public static PresentModeKHR ParsePresentMode(string? configToken)
    {
        if (string.Equals(configToken, "Mailbox", StringComparison.OrdinalIgnoreCase))
            return PresentModeKHR.MailboxKhr;
        if (string.Equals(configToken, "Immediate", StringComparison.OrdinalIgnoreCase))
            return PresentModeKHR.ImmediateKhr;
        return PresentModeKHR.FifoKhr;
    }
}
