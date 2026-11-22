using Silk.NET.Vulkan;

namespace DotCharter.Rendering.Vulkan.Composed;

/// <summary>
///     What to do with a frame-slot fence after its frame's <c>QueueSubmit</c>.
///     <para>
///         The composed renderers reset the slot fence BEFORE recording. A failed
///         submit (keyed-mutex acquire timeout, device loss) never signals it, so
///         the next <c>WaitForFences</c> on that slot times out forever and the
///         view goes blank. A failed submit has no work in flight, so the fence is
///         destroyed and recreated signaled; an empty re-signalling submit would
///         fail the same way on a lost device.
///     </para>
/// </summary>
public static class FrameFencePolicy
{
    /// <summary>True when the slot fence must be recreated signaled after this submit result.</summary>
    public static bool NeedsRecreateSignaled(Result submitResult) => submitResult != Result.Success;

    /// <summary>Same decision for a result already classified by <c>VulkanRenderer.Check</c>.</summary>
    public static bool NeedsRecreateSignaled(FrameResultAction submitAction) => submitAction != FrameResultAction.Continue;

    /// <summary>Replaces <paramref name="fence" /> with a fresh SIGNALED fence. Only valid when no submit using it is pending.</summary>
    public static unsafe void RecreateSignaled(Vk api, Device device, ref Fence fence)
    {
        if (fence.Handle != 0) api.DestroyFence(device, fence, null);
        var info = new FenceCreateInfo
        { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
        if (api.CreateFence(device, in info, null, out fence) != Result.Success) fence = default;
    }
}
