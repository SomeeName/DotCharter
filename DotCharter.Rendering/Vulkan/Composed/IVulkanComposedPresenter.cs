namespace DotCharter.Rendering.Vulkan.Composed;

/// <summary>
///     The pump-facing half of a composed-output renderer: exactly what a
///     UI-side present pump needs to import exported frames and complete them
///     back to the pool. Implemented by <see cref="VulkanRenderer" /> (via
///     <see cref="IVulkanComposedPresenter" />, the 3D highway) and by
///     <c>Vulkan2D.Timeline2DComposedRenderer</c> (the 2D timeline). Contract:
///
///     1. Every submitted frame raises <see cref="ComposedFrameReady" />
///        outside any renderer lock. The UI marshals to the UI thread, imports
///        the image once per (index, generation) via
///        <see cref="CreateComposedExportHandle" /> and calls
///        <c>CompositionDrawingSurface.UpdateWithKeyedMutexAsync(img, 1, 0)</c>
///        (Vulkan released key 1 on the submit; the compositor releases back
///        to key 0).
///     2. When that task completes — success or fault, ALWAYS —
///        <see cref="CompleteComposedPresent" /> returns the image to the
///        pool. Dropping a completion wedges one pool slot forever.
/// </summary>
public interface IComposedFrameSource
{
    /// <summary>(imageIndex, generation) of a freshly submitted frame.
    /// Raised outside any renderer lock.</summary>
    event Action<int, long>? ComposedFrameReady;

    /// <summary>Fresh DXGI shared NT handle for the compositor import
    /// (ImportImage takes ownership). 0 when the pool moved on — skip the
    /// import and complete the present as failed.</summary>
    nint CreateComposedExportHandle(int index, long generation, out ulong memorySize);

    /// <summary>Pixel size of the current pool images (what the import
    /// properties must declare).</summary>
    (uint Width, uint Height) ComposedImageSize { get; }

    void CompleteComposedPresent(int index, long generation, bool success);
}

/// <summary>
///     The renderer's composed-output surface (0.16): what the UI-side
///     composition pump needs beyond <see cref="IVulkanRenderService" />.
///     Implemented by <see cref="VulkanRenderer" />; the composed canvas
///     obtains it by casting the DI singleton. The pump-facing members live on
///     <see cref="IComposedFrameSource" /> (shared with the 2D timeline's
///     composed renderer); this interface adds the 3D singleton's mode-locked
///     device initialization.
/// </summary>
public interface IVulkanComposedPresenter : IComposedFrameSource
{
    /// <summary>True once the renderer was initialized for composed output.
    /// The presentation mode is fixed for the renderer's lifetime — the two
    /// init calls are mutually exclusive.</summary>
    bool IsComposedMode { get; }

    /// <summary>Current exportable-image generation (bumped on every pool
    /// rebuild — resize, broken-pool recovery).</summary>
    long ComposedGeneration { get; }

    void InitializeDeviceComposed(byte[]? adapterLuid);
}
