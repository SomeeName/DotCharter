using DotCharter.Rendering.Timeline2D;
using DotCharter.Rendering.Vulkan;
using DotCharter.Rendering.Vulkan.Composed;
using Silk.NET.Vulkan;

namespace DotCharter.Rendering.Vulkan2D;

/// <summary>
///     Per-viewport composed-Vulkan renderer for the 2D chart timeline
///     (v0.33): renders an <see cref="TimelineBatcher" /> frame into a
///     <see cref="VulkanComposedImagePool" /> image (D3D11 keyed-mutex shared
///     texture) and raises <see cref="ComposedFrameReady" /> for the UI-side
///     present pump — the exact contract composed 3D uses
///     (<see cref="IComposedFrameSource" />): every ready frame must get
///     exactly one <see cref="CompleteComposedPresent" /> or a pool slot
///     wedges.
///     <para>
///         Runs entirely on the UI thread (the 2D frame is built from live
///         view-model state, so there is no render thread by design);
///         submissions on the shared device are therefore naturally
///         serialized and no renderer lock exists. Sync per frame:
///         keyed mutex acquire key 0 / release key 1 on the submit, NO
///         semaphores; the per-slot frame fence paces the CPU — waited with a
///         short timeout so a slow GPU skips a frame instead of stalling the
///         UI thread.
///     </para>
///     <para>
///         Lifetime: create on viewport attach, <see cref="Dispose" /> on
///         detach (pop-out / redock) — pool, framebuffers, vertex buffers and
///         atlas die; the shared device (<see cref="Timeline2DVulkanDevice" />)
///         survives. Old views/framebuffers are destroyed BEFORE the pool
///         recreates the images under them; a faulted present marks the image
///         Broken and the next <see cref="TryBeginFrame" /> rebuilds the pool.
///     </para>
/// </summary>
public sealed unsafe class Timeline2DComposedRenderer : IComposedFrameSource, IDisposable
{
    /// <summary>Fence wait budget per <see cref="TryBeginFrame" /> — on the
    /// UI thread a stall is worse than a skipped frame, so an unsignaled
    /// fence after 2 ms skips (the next animation tick retries).</summary>
    private const ulong FenceWaitNanos = 2_000_000;

    private readonly VulkanContext _context;
    private readonly Timeline2DVulkanCore _core;
    private readonly VulkanComposedImagePool _pool;
    private readonly Timeline2DFrameResources _frames;

    private ImageView[] _views = [];
    private Framebuffer[] _framebuffers = [];

    private uint _pendingWidth;
    private uint _pendingHeight;
    private bool _targetsDirty;
    private uint _imageIndex;
    private int _frameSlot;
    private bool _disposed;

    /// <summary>This viewport's glyph atlas (per-viewport — pop-out/redock
    /// cycles it with the rest of the per-viewport resources).</summary>
    public VulkanTimelineFontAtlas FontAtlas { get; }

    /// <summary>Frames skipped because the frame-slot fence was still
    /// unsignaled after <see cref="FenceWaitNanos" /> (GPU behind).
    /// Diagnostic counter for the env-gated [2DStats] log line — monotonic,
    /// read-only consumers diff it across log intervals.</summary>
    public long FenceTimeoutSkips { get; private set; }

    /// <summary>Frames skipped because the compositor owned every pool image
    /// (composed back-pressure). Same diagnostic contract as
    /// <see cref="FenceTimeoutSkips" />.</summary>
    public long PoolExhaustedSkips { get; private set; }

    /// <summary>Frames whose submit did not reach the GPU — a keyed-mutex
    /// acquire that timed out, or a lost device. Each one poisons its pool slot
    /// and forces a pool rebuild; nonzero means the compositor stopped handing
    /// images back.</summary>
    public long SubmitFailures { get; private set; }

    /// <summary>The shared 2D device's GPU name (diagnostics).</summary>
    public string DeviceName => _context.DeviceName;

    private Timeline2DComposedRenderer(VulkanContext context, Timeline2DVulkanCore core)
    {
        _context = context;
        _core = core;
        _pool = new VulkanComposedImagePool(context);
        _frames = new Timeline2DFrameResources(context);
        FontAtlas = new VulkanTimelineFontAtlas(context, core);
    }

    /// <summary>Creates a renderer on the process-wide shared 2D device
    /// (created on first use against the compositor's adapter LUID). Throws
    /// when composed Vulkan is unavailable — the UI surfaces the timeline
    /// error state.</summary>
    public static Timeline2DComposedRenderer Create(byte[]? compositorAdapterLuid)
    {
        var (context, core) = Timeline2DVulkanDevice.Acquire(compositorAdapterLuid);
        return new Timeline2DComposedRenderer(context, core);
    }

    public event Action<int, long>? ComposedFrameReady;

    public long Generation => _pool.Generation;

    public (uint Width, uint Height) ComposedImageSize => (_pool.Width, _pool.Height);

    public nint CreateComposedExportHandle(int index, long generation, out ulong memorySize)
        => _pool.CreateExportHandle(index, generation, out memorySize);

    public void CompleteComposedPresent(int index, long generation, bool success)
        => _pool.CompletePresent((uint)index, generation, success);

    /// <summary>Requests a new pixel size. Cheap when unchanged; the rebuild
    /// itself is deferred to the next <see cref="TryBeginFrame" />.</summary>
    public void Resize(uint pixelWidth, uint pixelHeight)
    {
        pixelWidth = Math.Min(pixelWidth, _context.MaxRenderTargetWidth);
        pixelHeight = Math.Min(pixelHeight, _context.MaxRenderTargetHeight);
        if (pixelWidth == _pool.Width && pixelHeight == _pool.Height && !_targetsDirty) return;
        _pendingWidth = pixelWidth;
        _pendingHeight = pixelHeight;
        _targetsDirty = true;
    }

    /// <summary>Applies any deferred pool rebuild, paces on the frame-slot
    /// fence and acquires a free pool image. False = skip this frame (no free
    /// image / GPU behind / zero-sized) — retry on the next tick.</summary>
    public bool TryBeginFrame()
    {
        if (_disposed) return false;

        if (_targetsDirty || _pool.HasBroken) RecreateTargets();
        if (_pool.Count == 0) return false;

        var api = _context.Api;
        var wait = api.WaitForFences(_context.Device, 1, in _frames.Fences[_frameSlot], true, FenceWaitNanos);
        if (wait == Result.Timeout)
        {
            FenceTimeoutSkips++;
            return false;
        }

        // Nothing free = the compositor owns every image — natural
        // back-pressure to its rate (uncapped since Avalonia 12.1).
        if (!_pool.TryAcquire(out _imageIndex))
        {
            PoolExhaustedSkips++;
            return false;
        }

        api.ResetFences(_context.Device, 1, in _frames.Fences[_frameSlot]);
        return true;
    }

    /// <summary>
    ///     Records + submits the frame: uploads the batcher's two vertex
    ///     streams into this slot's mapped chunks, replays the draw-command
    ///     list (insertion order — alpha-blend order matches GL), clears to
    ///     the timeline background color and submits with the keyed-mutex
    ///     acquire/release chained (key 0 → key 1). Raises
    ///     <see cref="ComposedFrameReady" /> after the submit.
    /// </summary>
    public void EndFrame(TimelineBatcher batcher, float logicalWidth, float logicalHeight,
        float clearR, float clearG, float clearB)
    {
        if (_disposed) return;
        var api = _context.Api;

        _frames.EnsureCapacity(batcher);
        var (solidBase, generalBase) = _frames.Upload(_frameSlot, batcher);

        var cmd = _frames.CommandBuffers[_frameSlot];
        var begin = new CommandBufferBeginInfo
        { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        api.BeginCommandBuffer(cmd, in begin);

        var clear = new ClearValue
        {
            Color = new ClearColorValue
            { Float32_0 = clearR, Float32_1 = clearG, Float32_2 = clearB, Float32_3 = 1.0f }
        };
        var extent = new Extent2D(_pool.Width, _pool.Height);
        var passBegin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _core.RenderPass,
            Framebuffer = _framebuffers[_imageIndex],
            RenderArea = new Rect2D(new Offset2D(0, 0), extent),
            ClearValueCount = 1,
            PClearValues = &clear
        };
        api.CmdBeginRenderPass(cmd, in passBegin, SubpassContents.Inline);

        var viewport = new Viewport
        { X = 0f, Y = 0f, Width = extent.Width, Height = extent.Height, MinDepth = 0f, MaxDepth = 1f };
        api.CmdSetViewport(cmd, 0, 1, &viewport);
        var scissor = new Rect2D { Offset = new Offset2D(0, 0), Extent = extent };
        api.CmdSetScissor(cmd, 0, 1, &scissor);

        _frames.RecordDraws(cmd, _core, batcher, solidBase, generalBase, logicalWidth, logicalHeight,
            FontAtlas.DescriptorSet);

        api.CmdEndRenderPass(cmd);
        api.EndCommandBuffer(cmd);

        // Keyed mutex is the only cross-API sync: acquire key 0 before any
        // command runs, release key 1 when the submit completes. NO
        // semaphores in composed mode; the frame fence paces the CPU.
        ulong acquireKey = 0, releaseKey = 1;
        var syncMemory = _pool.GetDeviceMemory(_imageIndex);
        // FINITE, from ComposedSubmitPolicy — see the 3D twin. uint.MaxValue is
        // INFINITE: a compositor that never released key 0 hung the queue until
        // the driver reset the GPU, with no escape hatch.
        var acquireTimeout = ComposedSubmitPolicy.AcquireTimeoutMs;
        var keyedMutex = new Win32KeyedMutexAcquireReleaseInfoKHR
        {
            SType = StructureType.Win32KeyedMutexAcquireReleaseInfoKhr,
            AcquireCount = 1,
            PAcquireKeys = &acquireKey,
            PAcquireSyncs = &syncMemory,
            PAcquireTimeouts = &acquireTimeout,
            ReleaseCount = 1,
            PReleaseKeys = &releaseKey,
            PReleaseSyncs = &syncMemory
        };
        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            PNext = &keyedMutex,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd
        };
        var submitResult = api.QueueSubmit(_context.GraphicsQueue, 1, in submit, _frames.Fences[_frameSlot]);
        _frameSlot = (_frameSlot + 1) % Timeline2DFrameResources.MaxFramesInFlight;

        if (submitResult != Result.Success)
        {
            // The frame never reached the GPU (a keyed-mutex acquire timeout, a
            // lost device), so no ComposedFrameReady may be raised — and the
            // slot must not be left in Rendering, where it is never Free again
            // and never gets its matching CompleteComposedPresent. Poison it;
            // TryBeginFrame's HasBroken path rebuilds the pool.
            SubmitFailures++;
            _pool.MarkBroken(_imageIndex);
            return;
        }

        _pool.MarkSubmitted(_imageIndex);
        ComposedFrameReady?.Invoke((int)_imageIndex, _pool.Generation);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _context.Api.DeviceWaitIdle(_context.Device);
        DestroyTargets();
        // Pending compositor reads of the dead pool survive via COM refcounts
        // (the compositor holds its own texture reference); late present
        // completions are ignored by generation/state checks.
        _pool.Dispose();
        _frames.Dispose();
        FontAtlas.Dispose();
    }

    private void RecreateTargets()
    {
        var api = _context.Api;
        api.DeviceWaitIdle(_context.Device);
        // Invariant: old views/framebuffers die BEFORE the pool recreates the
        // images under them.
        DestroyTargets();
        _pool.Recreate(_pendingWidth, _pendingHeight);

        var images = _pool.Images;
        _views = new ImageView[images.Length];
        _framebuffers = new Framebuffer[images.Length];
        for (var i = 0; i < images.Length; i++)
        {
            _views[i] = _context.CreateImageView(images[i], Timeline2DVulkanCore.ColorFormat,
                ImageAspectFlags.ColorBit);
            var attachment = _views[i];
            var fbInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _core.RenderPass,
                AttachmentCount = 1,
                PAttachments = &attachment,
                Width = _pool.Width,
                Height = _pool.Height,
                Layers = 1
            };
            if (api.CreateFramebuffer(_context.Device, in fbInfo, null, out _framebuffers[i]) != Result.Success)
                throw new Exception("Failed to create a 2D timeline framebuffer.");
        }

        _targetsDirty = false;
    }

    private void DestroyTargets()
    {
        var api = _context.Api;
        foreach (var fb in _framebuffers)
        {
            if (fb.Handle != 0)
                api.DestroyFramebuffer(_context.Device, fb, null);
        }

        foreach (var view in _views)
        {
            if (view.Handle != 0)
                api.DestroyImageView(_context.Device, view, null);
        }

        _framebuffers = [];
        _views = [];
    }
}
