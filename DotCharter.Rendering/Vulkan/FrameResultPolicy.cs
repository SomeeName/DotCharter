using Silk.NET.Vulkan;

namespace DotCharter.Rendering.Vulkan;

/// <summary>What the frame loop must do with a Vulkan <see cref="Result" />.</summary>
public enum FrameResultAction
{
    /// <summary>Success — carry on with the frame.</summary>
    Continue,

    /// <summary>
    ///     <c>VK_SUBOPTIMAL_KHR</c>: a SUCCESSFUL acquire/present that also asks
    ///     for a swapchain rebuild. The frame MUST be finished — an acquire that
    ///     returned Suboptimal handed out an image and WILL signal its semaphore,
    ///     so bailing leaks a signaled binary semaphore into the next acquire and
    ///     freezes rendering on several drivers.
    /// </summary>
    ContinueAndRecreate,

    /// <summary>
    ///     <c>VK_TIMEOUT</c> / <c>VK_NOT_READY</c>: nothing happened and nothing
    ///     was signaled. Skip this frame and retry on the next tick.
    /// </summary>
    Skip,

    /// <summary>
    ///     <c>VK_ERROR_OUT_OF_DATE_KHR</c> / <c>VK_ERROR_SURFACE_LOST_KHR</c> /
    ///     <c>VK_ERROR_FULL_SCREEN_EXCLUSIVE_MODE_LOST_EXT</c>: nothing was
    ///     acquired, the semaphore stays unsignaled — bail and rebuild the
    ///     swapchain on the deferred path.
    /// </summary>
    Recreate,

    /// <summary>
    ///     <c>VK_ERROR_DEVICE_LOST</c>: the device is DEAD. Every later call on
    ///     it fails the same way, so the frame loop must STOP — recreating a
    ///     swapchain here is an infinite silent retry, which is exactly what a
    ///     TDR would produce (a permanently black view, no log line).
    /// </summary>
    DeviceLost,

    /// <summary>
    ///     Anything else (out of memory, an unexpected error code). The frame is
    ///     abandoned and the fault is reported once per call site; the device may
    ///     still be usable, so this is not latched.
    /// </summary>
    Fatal
}

/// <summary>
///     The ONE place a frame-loop Vulkan result is classified.
///     <para>
///         Before this existed, every <c>WaitForFences</c> result was compared
///         only against <see cref="Result.Timeout" />, so a
///         <c>VK_ERROR_DEVICE_LOST</c> after a driver reset (TDR) read as a
///         SUCCESSFUL wait: the frame proceeded, the acquire then failed, the
///         renderer set <c>SwapchainNeedsRecreation</c>, and <c>Resize</c>
///         re-entered <c>DeviceWaitIdle</c> → <c>CleanupImageResources</c> →
///         <c>CreateSwapchain</c> on a dead device forever, with no log line and
///         no error surface. <c>grep ErrorDeviceLost</c> over this assembly
///         returned zero hits.
///     </para>
///     <para>Pure and total, so the state machine is testable without a GPU.</para>
/// </summary>
public static class FrameResultPolicy
{
    public static FrameResultAction Classify(Result result) => result switch
    {
        Result.Success => FrameResultAction.Continue,
        // A successful acquire/present that wants a rebuild — never a bail.
        Result.SuboptimalKhr => FrameResultAction.ContinueAndRecreate,
        Result.Timeout or Result.NotReady => FrameResultAction.Skip,
        Result.ErrorOutOfDateKhr or Result.ErrorSurfaceLostKhr
            or Result.ErrorFullScreenExclusiveModeLostExt => FrameResultAction.Recreate,
        Result.ErrorDeviceLost => FrameResultAction.DeviceLost,
        _ => FrameResultAction.Fatal
    };

    /// <summary>True when the frame loop must stop for good on this result.</summary>
    public static bool IsTerminal(Result result) => Classify(result) == FrameResultAction.DeviceLost;
}
