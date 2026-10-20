using DotCharter.Rendering.Vulkan;
using Silk.NET.Vulkan;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The persisted present-mode token's parse. No Vulkan device involved.
///     (The swapchain image-count / extent / present-mode clamps went with the
///     native swapchain path.)
/// </summary>
public class SwapchainPolicyTests
{
    [Theory]
    [InlineData("Fifo", PresentModeKHR.FifoKhr)]
    [InlineData("fifo", PresentModeKHR.FifoKhr)] // case-insensitive
    [InlineData("Mailbox", PresentModeKHR.MailboxKhr)]
    [InlineData("MAILBOX", PresentModeKHR.MailboxKhr)]
    [InlineData("Immediate", PresentModeKHR.ImmediateKhr)]
    [InlineData("", PresentModeKHR.FifoKhr)] // unknown/empty → FIFO
    [InlineData(null, PresentModeKHR.FifoKhr)]
    [InlineData("FifoRelaxed", PresentModeKHR.FifoKhr)] // not offered → FIFO
    [InlineData("garbage", PresentModeKHR.FifoKhr)]
    public void ParsePresentMode_MapsConfigTokens_FifoFallback(string? token, PresentModeKHR expected) => Assert.Equal(expected, SwapchainPolicy.ParsePresentMode(token));
}
