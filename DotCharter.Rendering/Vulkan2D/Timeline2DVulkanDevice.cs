using DotCharter.Rendering.Vulkan;

namespace DotCharter.Rendering.Vulkan2D;

/// <summary>
///     The process-lifetime shared Vulkan device for the composed 2D timeline
///     (v0.33). The 2D path cannot borrow the 3D <c>VulkanRenderer</c>'s
///     device: that singleton is mode-locked to whichever presentation mode
///     it was initialized for (composed in the app, headless in tests) and
///     owns its own queue/lock discipline, while the 2D timeline is ALWAYS
///     composed on the UI thread — so it owns a second, independent
///     <see cref="VulkanContext" /> initialized via
///     <see cref="VulkanContext.InitializeComposed" /> (LUID-matched to the
///     compositor's adapter, hard-fail on mismatch — never cross-GPU).
///     <para>
///         Lifetime story: created lazily on the first composed viewport,
///         then kept alive for the rest of the process — exactly like the 3D
///         singleton's device. Per-viewport resources (image pool,
///         framebuffers, vertex buffers, font atlas) are created on attach
///         and destroyed on detach (pop-out / redock); only they cycle. A
///         failed creation is cached so every later caller fails fast into
///         the OpenGL fallback instead of re-probing per viewport.
///     </para>
/// </summary>
public static class Timeline2DVulkanDevice
{
    private static readonly object Gate = new();
    private static VulkanContext? _context;
    private static Timeline2DVulkanCore? _core;
    private static Exception? _failure;

    /// <summary>Acquires the shared device + pipeline core, creating them on
    /// first use against the compositor's adapter LUID. Throws (and caches
    /// the failure) when composed Vulkan is unavailable on this machine.</summary>
    public static (VulkanContext Context, Timeline2DVulkanCore Core) Acquire(byte[]? compositorAdapterLuid)
    {
        lock (Gate)
        {
            if (_failure != null)
            {
                throw new NotSupportedException(
                    "Composed-Vulkan 2D device creation already failed this session.", _failure);
            }

            if (_context == null)
            {
                var context = new VulkanContext();
                Timeline2DVulkanCore core;
                try
                {
                    context.InitializeComposed(compositorAdapterLuid);
                    core = new Timeline2DVulkanCore(context);
                }
                catch (Exception ex)
                {
                    _failure = ex;
                    context.Dispose();
                    throw;
                }

                _context = context;
                _core = core;
            }

            return (_context, _core!);
        }
    }
}
