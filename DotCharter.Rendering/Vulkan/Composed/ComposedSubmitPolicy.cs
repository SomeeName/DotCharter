namespace DotCharter.Rendering.Vulkan.Composed;

/// <summary>
///     The composed path's submit-side constants. Both composed renderers (the
///     3D <c>VulkanRenderer</c> and the 2D <c>Timeline2DComposedRenderer</c>)
///     chain a <c>Win32KeyedMutexAcquireReleaseInfoKHR</c> onto their frame
///     submit; this owns the acquire timeout so the two cannot drift.
///     <para>
///         Both used <c>uint.MaxValue</c> — INFINITE. If the compositor ever
///         fails to release key 0 (a lost D3D11 device, a wedged
///         <c>UpdateWithKeyedMutexAsync</c>), the submitted work never begins
///         and the queue hangs until the driver resets the GPU. The pool state
///         machine makes that unlikely, but there was no escape hatch at all —
///         and, before the device-lost work, nothing behind it either.
///     </para>
/// </summary>
public static class ComposedSubmitPolicy
{
    /// <summary>
    ///     How long a frame submit waits for the compositor to hand the image's
    ///     keyed mutex back. Generous next to a frame (a compositor that is
    ///     merely slow finishes in single-digit milliseconds) and finite, so a
    ///     wedged compositor costs one frame instead of the GPU.
    /// </summary>
    public const uint AcquireTimeoutMs = 1_000;
}
