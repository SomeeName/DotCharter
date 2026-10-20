namespace DotCharter.Rendering.Vulkan;

/// <summary>
///     Offscreen, windowless output contract (testing / golden frames). Like
///     <see cref="Composed.IVulkanComposedPresenter" /> this is a surfaceless
///     output path: the device is created WITHOUT a surface or
///     swapchain, the scene renders into a plain device-local BGRA8 image, and
///     each finished frame is copied back to CPU memory (vkCmdCopyImageToBuffer +
///     a fence wait). Nothing opens a window, an audio device or a compositor
///     surface. Consumed by <c>HeadlessHighwayRenderer</c> via
///     <c>Fretboard3DEngine.InitializeHeadless</c>.
///     <para>
///         The presentation mode is fixed for the singleton renderer's lifetime:
///         <see cref="InitializeDeviceHeadless" /> and
///         <c>InitializeDeviceComposed</c> are mutually exclusive.
///     </para>
/// </summary>
public interface IVulkanHeadlessRenderer
{
    /// <summary>Initializes the device for offscreen output (no surface, no
    /// swapchain). Idempotent.</summary>
    void InitializeDeviceHeadless();

    /// <summary>True once <see cref="InitializeDeviceHeadless" /> has run.</summary>
    bool IsHeadlessMode { get; }

    /// <summary>The size of the offscreen color image the last
    /// <c>Resize</c> produced (0×0 before the first resize).</summary>
    (uint Width, uint Height) HeadlessImageSize { get; }

    /// <summary>
    ///     Copies the most recently rendered frame's pixels into
    ///     <paramref name="destination" />, tightly packed BGRA8 top-to-bottom
    ///     (row 0 = top of the image), <c>Width*Height*4</c> bytes. The GPU copy
    ///     already completed inside <c>EndFrame</c> (fence-synchronized), so this
    ///     is a plain memory read. Returns false when no frame has been rendered
    ///     or the buffer is too small.
    /// </summary>
    bool TryReadbackBgra(byte[] destination);
}
