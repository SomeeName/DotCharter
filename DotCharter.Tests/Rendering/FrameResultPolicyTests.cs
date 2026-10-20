using DotCharter.Rendering.Vulkan;
using Silk.NET.Vulkan;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The frame loop's result classification, driven by a fake
///     <see cref="Result" /> sequence — no GPU anywhere.
///     <para>
///         Before <see cref="FrameResultPolicy" /> existed, every
///         <c>WaitForFences</c> result in <c>VulkanRenderer</c> was compared only
///         against <see cref="Result.Timeout" />. A <c>VK_ERROR_DEVICE_LOST</c>
///         after a driver reset therefore read as a SUCCESSFUL wait: the frame
///         proceeded, the acquire failed, <c>SwapchainNeedsRecreation</c> was set,
///         and <c>Resize</c> re-entered <c>DeviceWaitIdle</c> →
///         <c>CleanupImageResources</c> → <c>CreateSwapchain</c> on a dead device
///         forever — with no log line and no error surface.
///     </para>
/// </summary>
public sealed class FrameResultPolicyTests
{
    [Theory]
    [InlineData(Result.Success, FrameResultAction.Continue)]
    [InlineData(Result.SuboptimalKhr, FrameResultAction.ContinueAndRecreate)]
    [InlineData(Result.Timeout, FrameResultAction.Skip)]
    [InlineData(Result.NotReady, FrameResultAction.Skip)]
    [InlineData(Result.ErrorOutOfDateKhr, FrameResultAction.Recreate)]
    [InlineData(Result.ErrorSurfaceLostKhr, FrameResultAction.Recreate)]
    [InlineData(Result.ErrorFullScreenExclusiveModeLostExt, FrameResultAction.Recreate)]
    [InlineData(Result.ErrorDeviceLost, FrameResultAction.DeviceLost)]
    [InlineData(Result.ErrorOutOfDeviceMemory, FrameResultAction.Fatal)]
    [InlineData(Result.ErrorOutOfHostMemory, FrameResultAction.Fatal)]
    [InlineData(Result.ErrorInitializationFailed, FrameResultAction.Fatal)]
    public void ClassifyMapsEveryFrameLoopResult(Result result, FrameResultAction expected)
        => Assert.Equal(expected, FrameResultPolicy.Classify(result));

    /// <summary>
    ///     The heart of the bug: a device loss must never look like a timeout,
    ///     and must never look like a successful wait either.
    /// </summary>
    [Fact]
    public void ADeviceLossIsNeitherSkippableNorSuccessful()
    {
        var action = FrameResultPolicy.Classify(Result.ErrorDeviceLost);

        Assert.NotEqual(FrameResultAction.Continue, action);
        Assert.NotEqual(FrameResultAction.ContinueAndRecreate, action);
        Assert.NotEqual(FrameResultAction.Skip, action);
        // …and above all not Recreate: rebuilding a swapchain on a dead device
        // is the infinite silent retry.
        Assert.NotEqual(FrameResultAction.Recreate, action);
        Assert.True(FrameResultPolicy.IsTerminal(Result.ErrorDeviceLost));
    }

    /// <summary>
    ///     Suboptimal is the one error-shaped result that must NOT bail: the
    ///     acquire handed out an image and WILL signal its semaphore, so bailing
    ///     leaks a signaled binary semaphore into the next acquire (freezes
    ///     rendering on several drivers). It is a documented hard rule in
    ///     <c>docs/agents/rendering/vulkan-device-lifetime.md</c>.
    /// </summary>
    [Fact]
    public void SuboptimalFinishesTheFrame()
    {
        var action = FrameResultPolicy.Classify(Result.SuboptimalKhr);

        Assert.Equal(FrameResultAction.ContinueAndRecreate, action);
        Assert.False(FrameResultPolicy.IsTerminal(Result.SuboptimalKhr));
    }

    /// <summary>Only a device loss is terminal — a transient skip or a
    /// swapchain rebuild must not stop the loop.</summary>
    [Theory]
    [InlineData(Result.Success)]
    [InlineData(Result.Timeout)]
    [InlineData(Result.NotReady)]
    [InlineData(Result.ErrorOutOfDateKhr)]
    [InlineData(Result.ErrorSurfaceLostKhr)]
    [InlineData(Result.ErrorOutOfDeviceMemory)]
    public void NothingElseIsTerminal(Result result) => Assert.False(FrameResultPolicy.IsTerminal(result));

    /// <summary>
    ///     The state machine as the renderer drives it: a run of ordinary
    ///     results keeps the loop alive; the first device loss latches and every
    ///     later result is refused without touching the device again.
    /// </summary>
    [Fact]
    public void AFakeResultSequenceStopsTheLoopAtTheDeviceLoss()
    {
        Result[] sequence =
        [
            Result.Success, Result.Timeout, Result.SuboptimalKhr, Result.Success,
            Result.ErrorOutOfDateKhr, Result.Success,
            Result.ErrorDeviceLost,
            // A dead device keeps answering with the same error; the loop must
            // already be stopped by the time these arrive.
            Result.ErrorDeviceLost, Result.ErrorDeviceLost
        ];

        var lost = false;
        var recreations = 0;
        var framesDrawn = 0;
        var framesAfterLoss = 0;

        foreach (var result in sequence)
        {
            if (lost)
            {
                framesAfterLoss++;
                continue; // the loop is stopped: nothing is submitted, nothing rebuilt
            }

            switch (FrameResultPolicy.Classify(result))
            {
                case FrameResultAction.Continue:
                    framesDrawn++;
                    break;
                case FrameResultAction.ContinueAndRecreate:
                    framesDrawn++;
                    recreations++;
                    break;
                case FrameResultAction.Skip:
                    break;
                case FrameResultAction.Recreate:
                    recreations++;
                    break;
                case FrameResultAction.DeviceLost:
                    lost = true;
                    break;
                case FrameResultAction.Fatal:
                    break;
            }
        }

        Assert.True(lost);
        Assert.Equal(4, framesDrawn);
        // Two rebuild requests — and crucially NOT one per dead-device result.
        Assert.Equal(2, recreations);
        Assert.Equal(2, framesAfterLoss);
    }
}
