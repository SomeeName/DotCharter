using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using DotCharter.Rendering.Vulkan.Composed;
using Silk.NET.Vulkan;
using Buffer = System.Buffer;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using DotCharter.Core.Graphics;

namespace DotCharter.Rendering.Vulkan;

[StructLayout(LayoutKind.Sequential)]
public struct HighwayPushConstants
{
    public Matrix4x4 MVP;
    public float CamX;
    public float CamY;
    public float CamZ;
    public float FadeStart;
    public float FadeEnd;
    public float Time;
    public uint Effects; // HighwayEffects bit set — mirrored by the GLSL block
}

[StructLayout(LayoutKind.Sequential)]
public struct TextPushConstants
{
    public Matrix4x4 MVP;
}

/// <summary>Push constants for the post-process pipelines (0.15.6 / plan 5.5).
/// Layout mirrors the GLSL PostPC block in post_extract/post_blur/post_blit.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PostPushConstants
{
    public Vector2 Texel; // 1 / source size
    public Vector2 Dir; // blur axis in texels; (0,0) = plain copy/downsample
    public Vector4 Params; // extract: x=threshold, y=knee | blit: x=intensity, y=tonemap flag
}

public class VulkanRenderer : IVulkanRenderService, IVulkanComposedPresenter, IVulkanHeadlessRenderer
{
    public const int MaxFramesInFlight = 2;
    private const int HighwayVertexFloatCount = 13;
    private const ulong MaxVertexBufferSize = 250000 * 13 * 4;

    // Sized PER FRAME through TextVertexBudget, not for the whole buffer.
    // It must not be `50000 * 9 * 4` total, which BeginFrame then splits across
    // MaxFramesInFlight — so a frame's chunk would hold 25,000 vertices against a
    // TextBatcher staging 50,000, and the batcher's own auto-flush would be
    // guaranteed to be dropped by the chunk guard below.
    private const ulong MaxTextVertexBufferSize = TextVertexBudget.TotalBufferSizeBytes;

    /// <summary>Vertices one frame's text chunk holds — the number
    /// <c>Batching/TextBatcher</c> must not stage past. Test seam.</summary>
    internal static int MaxTextVerticesPerFrame =>
        (int)(MaxTextVertexBufferSize / MaxFramesInFlight / TextVertexBudget.VertexSizeBytes);

    /// <summary>Text batches this renderer dropped for exceeding the per-frame
    /// chunk. Nonzero means text went missing on screen.</summary>
    public int DroppedTextBatchCount { get; private set; }

    private bool _textDropWarned;

    private readonly CommandBuffer[] _commandBuffers = new CommandBuffer[MaxFramesInFlight];

    private readonly VulkanContext _context = new();

    // No semaphores exist: the composed path syncs across APIs with the
    // images' keyed mutex and the headless path blocks on the fence; the
    // frame fence alone paces the CPU in both.
    private readonly Fence[] _inFlightFences = new Fence[MaxFramesInFlight];

    private readonly object _renderLock = new();
    private int _activeFrameIndex;

    /// <summary>
    ///     Index of the command buffer currently between
    ///     <c>BeginCommandBuffer</c> and <c>EndCommandBuffer</c>, or -1 when no
    ///     frame is being recorded.
    /// </summary>
    /// <remarks>
    ///     <see cref="SetViewportRect" /> reads it to no-op outside a begun frame.
    /// </remarks>
    private int _recordingFrameIndex = -1;
    private CommandPool _commandPool;

    private int _currentDrawCallCount;
    private ulong _currentTextVertexChunkEnd;
    private ulong _currentTextVertexOffset;
    private ulong _currentVertexChunkEnd;
    private int _currentVertexCount;
    private ulong _currentVertexOffset;
    private uint _imageIndex;
    private bool _isDisposed;
    private unsafe void* _mappedTextVertexBuffer;
    private unsafe void* _mappedVertexBuffer;
    private VulkanPipelineManager _pipelineManager = null!;
    private VulkanResourceManager _resourceManager = null!;
    private VulkanSwapchainManager _swapchainManager = null!;
    private bool _surfaceInitialized;

    private VkBuffer _textVertexBuffer;
    private DeviceMemory _textVertexBufferMemory;

    private VkBuffer _vertexBuffer;
    private DeviceMemory _vertexBufferMemory;

    // Shared static quad index buffer (0.15.6 / plan 5.9): both batchers emit
    // 4 unique vertices per quad and the GPU expands them via this immutable
    // 0,1,2 / 0,2,3 pattern (QuadIndexMath). Written once at pipeline init;
    // per-draw addressing uses vkCmdDrawIndexed's vertexOffset. Sized for the
    // largest batch a per-frame highway vertex chunk can hold.
    private VkBuffer _quadIndexBuffer;
    private DeviceMemory _quadIndexBufferMemory;
    private const int MaxIndexedQuads =
        (int)(MaxVertexBufferSize / MaxFramesInFlight / (HighwayVertexFloatCount * QuadIndexMath.VerticesPerQuad * 4));

    // Per-frame redundant-bind elision (plan 5.9): QuadBatcher flushes many
    // batches per pass with the same pipeline/texture — rebinding identical
    // state per draw is pure command-buffer overhead. Reset in BeginFrame.
    private ulong _lastBoundPipeline;
    private ulong _lastBoundPipelineLayout;
    private ulong _lastBoundDescriptorSet;
    private ulong _lastBoundVertexBuffer;

    private VulkanTexture? _whiteTexture;

    // Present mode the user asked for (Settings → Graphics, 0.14.2). Recorded
    // by SetPresentMode (which still flags SwapchainNeedsRecreation), but it has
    // NO effect on presentation: it only ever applied to the native swapchain,
    // which was removed — the composed path is paced by the compositor.
    private PresentModeKHR _requestedPresentMode = PresentModeKHR.FifoKhr;

    // MSAA level the user asked for (Settings → Graphics, 0.14.2). Applied on
    // the same deferred path as the present mode; a level change additionally
    // rebuilds the render pass + all pipelines (they bake the sample count).
    private int _requestedMsaaSamples = MsaaPolicy.DefaultSampleCount;

    // Render scale (Settings → Graphics, 0.14.2). 100 = the exact direct
    // render-into-swapchain path (no intermediate image, no blit, zero cost);
    // anything else renders into a scaled intermediate target that EndFrame
    // blits (linear) onto the swapchain image.
    private int _requestedRenderScalePercent = GraphicsSettingsPolicy.DefaultRenderScalePercent;

    // Post effects (0.15.6 / plan 5.5): bloom and the filmic tonemap both run
    // on the Sampled composite path. Flags flip per frame freely; only a
    // change of the required OUTPUT MODE (post on/off) rides the deferred
    // swapchain recreation.
    private bool _requestedBloom;
    private bool _requestedFilmicTonemap;

    // ---- Composed output (0.16) ------------------------------------------
    // Presentation without a swapchain: frames render into exportable
    // D3D11-backed images the Avalonia compositor imports; cross-API sync is
    // the images' keyed mutex (Vulkan acquires key 0 / releases key 1 on the
    // frame submit; the compositor acquires 1 / releases 0). No present
    // semaphores exist in this mode — the frame fence paces the CPU, the
    // keyed mutex orders the GPUs' access.
    private VulkanComposedImagePool? _composedPool;
    private bool _composedMode;

    public bool IsComposedMode => _composedMode;
    public long ComposedGeneration => _composedPool?.Generation ?? 0;
    public event Action<int, long>? ComposedFrameReady;

    // ---- Headless output (testing) ---------------------------------------
    // Offscreen rendering without a surface/swapchain: frames render into the
    // swapchain manager's single device-local BGRA8 image, then EndFrame copies
    // it into this host-visible readback buffer (vkCmdCopyImageToBuffer, fence-
    // synchronized) so a test can pull the pixels. No semaphores, no present.
    private bool _headlessMode;
    private VkBuffer _readbackBuffer;
    private DeviceMemory _readbackBufferMemory;
    private ulong _readbackBufferSize;
    private unsafe void* _mappedReadbackBuffer;

    public bool IsHeadlessMode => _headlessMode;
    public (uint Width, uint Height) HeadlessImageSize => (CurrentWidth, CurrentHeight);

    public uint GraphicsQueueFamilyIndex => _context.GraphicsQueueFamilyIndex;

    // ---- Device loss -----------------------------------------------------
    // Every frame-loop result goes through Check(); VK_ERROR_DEVICE_LOST
    // latches here and the loop stops for good. Before this existed, a TDR
    // read as a successful WaitForFences (only Result.Timeout was compared),
    // the acquire then failed, and Resize re-entered DeviceWaitIdle →
    // CleanupImageResources → CreateSwapchain on a dead device forever.
    private string? _pendingDeviceLostNotice;
    private readonly HashSet<string> _reportedFaultSites = new(StringComparer.Ordinal);

    public bool IsDeviceLost { get; private set; }
    public string? DeviceLostReason { get; private set; }
    public event Action<string>? DeviceLost;

    /// <summary>
    ///     Classifies ONE frame-loop Vulkan result and records the consequence.
    ///     Every <c>WaitForFences</c> / <c>QueueSubmit</c> call in this file is an
    ///     ARGUMENT to this method — <c>Rendering/DeviceLostHandlingTests</c>
    ///     lints exactly that shape, because the original bug was a call site
    ///     whose result was compared only against <see cref="Result.Timeout" />.
    ///     Call under <c>_renderLock</c>; the notification is raised outside it.
    /// </summary>
    private FrameResultAction Check(Result result, string site)
    {
        var action = FrameResultPolicy.Classify(result);
        switch (action)
        {
            case FrameResultAction.DeviceLost:
                LatchDeviceLost(site, result);
                break;
            case FrameResultAction.Fatal:
                // Not latched — the device may still be usable — but never
                // silent either. Once per call site, so a wedged frame loop
                // cannot spam the log at 144 Hz.
                if (_reportedFaultSites.Add(site))
                    Console.Error.WriteLine($"[VULKAN] {site} returned {result}; frame abandoned.");
                break;
        }

        return action;
    }

    private void LatchDeviceLost(string site, Result result)
    {
        if (IsDeviceLost) return;
        IsDeviceLost = true;
        DeviceLostReason = $"Vulkan device lost at {site} ({result}). The GPU driver reset; " +
                           "rendering has stopped and cannot recover without a new device.";
        Console.Error.WriteLine($"[VULKAN] {DeviceLostReason}");
        _pendingDeviceLostNotice = DeviceLostReason;
    }

    /// <summary>Raises <see cref="DeviceLost" /> OUTSIDE the render lock — the
    /// handler marshals to the UI thread, which may itself be inside the
    /// renderer (same rule as <c>ComposedFrameReady</c>).</summary>
    private void RaisePendingDeviceLost()
    {
        string? notice;
        lock (_renderLock)
        {
            notice = _pendingDeviceLostNotice;
            _pendingDeviceLostNotice = null;
        }

        if (notice != null) DeviceLost?.Invoke(notice);
    }

    public bool SwapchainNeedsRecreation { get; private set; }
    public uint CurrentWidth { get; private set; }
    public uint CurrentHeight { get; private set; }
    public bool IsDeviceInitialized => _context.IsInitialized;
    public bool IsPipelinesInitialized { get; private set; }
    public string DeviceName => _context.DeviceName;
    public int LastDrawCallCount { get; private set; }
    public int LastVertexCount { get; private set; }

    public unsafe void Dispose()
    {
        lock (_renderLock)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            if (!IsDeviceInitialized) return;

            _context.Api.DeviceWaitIdle(_context.Device);

            _swapchainManager.Dispose();
            _whiteTexture?.Dispose();

            if (_vertexBuffer.Handle != 0)
            {
                _context.Api.UnmapMemory(_context.Device, _vertexBufferMemory);
                _context.Api.DestroyBuffer(_context.Device, _vertexBuffer, null);
                _context.Api.FreeMemory(_context.Device, _vertexBufferMemory, null);
            }

            if (_textVertexBuffer.Handle != 0)
            {
                _context.Api.UnmapMemory(_context.Device, _textVertexBufferMemory);
                _context.Api.DestroyBuffer(_context.Device, _textVertexBuffer, null);
                _context.Api.FreeMemory(_context.Device, _textVertexBufferMemory, null);
            }

            if (_quadIndexBuffer.Handle != 0)
            {
                _context.Api.DestroyBuffer(_context.Device, _quadIndexBuffer, null);
                _context.Api.FreeMemory(_context.Device, _quadIndexBufferMemory, null);
            }

            DestroyReadbackBuffer();

            if (IsPipelinesInitialized)
            {
                for (var i = 0; i < MaxFramesInFlight; i++)
                {
                    _context.Api.DestroyFence(_context.Device, _inFlightFences[i], null);
                }

                if (_commandPool.Handle != 0)
                    _context.Api.DestroyCommandPool(_context.Device, _commandPool, null);
            }

            _resourceManager.Dispose();
            _pipelineManager.Dispose();
            _composedPool?.Dispose();
            _context.Dispose();

            IsPipelinesInitialized = false;
        }
    }

    /// <summary>
    ///     Composed-output initialization (0.16): device picked by the
    ///     compositor's adapter LUID, no surface/swapchain; presentation via
    ///     the exportable image pool. Mutually exclusive with
    ///     <see cref="InitializeDeviceHeadless" /> — the mode is fixed for the
    ///     renderer's lifetime. Idempotent across canvas re-attach cycles.
    /// </summary>
    public void InitializeDeviceComposed(byte[]? adapterLuid)
    {
        if (_headlessMode)
        {
            throw new InvalidOperationException(
                "Renderer already initialized for headless output; composed mode needs a fresh renderer.");
        }

        _context.InitializeComposed(adapterLuid);
        if (_surfaceInitialized) return; // singleton re-attach — everything survives

        _composedMode = true;
        _composedPool = new VulkanComposedImagePool(_context);
        _swapchainManager = new VulkanSwapchainManager(_context);
        _pipelineManager = new VulkanPipelineManager(_context);
        _resourceManager = new VulkanResourceManager(_context, _pipelineManager);
        _surfaceInitialized = true;
    }

    /// <summary>
    ///     Headless-output initialization (testing): device created WITHOUT a
    ///     surface or swapchain; frames render into an offscreen device-local
    ///     image copied back to the CPU. Mutually exclusive with
    ///     <see cref="InitializeDeviceComposed" />. Idempotent.
    /// </summary>
    public void InitializeDeviceHeadless()
    {
        if (_surfaceInitialized && !_headlessMode)
        {
            throw new InvalidOperationException(
                "Renderer already initialized for on-screen output; headless mode needs a fresh renderer.");
        }

        _context.InitializeDeviceHeadless();
        if (_surfaceInitialized) return; // idempotent re-init — everything survives

        _headlessMode = true;
        _swapchainManager = new VulkanSwapchainManager(_context);
        // The pipeline manager's output layout is non-presentable (the color
        // image ends in COLOR_ATTACHMENT_OPTIMAL, never PRESENT_SRC).
        _pipelineManager = new VulkanPipelineManager(_context);
        _resourceManager = new VulkanResourceManager(_context, _pipelineManager);
        _surfaceInitialized = true;
    }

    public unsafe void InitializePipelines(byte[] vertSpirv, byte[] fragSpirv)
    {
        if (!IsDeviceInitialized || IsPipelinesInitialized) return;

        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            var fenInfo = new FenceCreateInfo
            { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
            VulkanCheck.Ok(_context.Api.CreateFence(_context.Device, in fenInfo, null, out _inFlightFences[i]), "VulkanRenderer.CreateFence");
        }

        CreateFrameCommandPool();

        // Build the first render pass + pipelines at the persisted MSAA level
        // and output mode (clamped to this device/surface) so startup never
        // renders one frame at the wrong config and immediately rebuilds.
        var postWanted = _requestedBloom || _requestedFilmicTonemap;
        _pipelineManager.Initialize(vertSpirv, fragSpirv,
            MsaaPolicy.Clamp(_requestedMsaaSamples, _context.FramebufferSampleCountMask),
            PostProcessPolicy.ChooseOutputMode(postWanted,
                RenderScalePolicy.IsScaled(EffectiveRenderScalePercent(postWanted))));

        _context.CreateBuffer(MaxVertexBufferSize, BufferUsageFlags.VertexBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out _vertexBuffer,
            out _vertexBufferMemory);
        _context.CreateBuffer(MaxTextVertexBufferSize, BufferUsageFlags.VertexBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out _textVertexBuffer,
            out _textVertexBufferMemory);

        void* vData;
        VulkanCheck.Ok(_context.Api.MapMemory(_context.Device, _vertexBufferMemory, 0, MaxVertexBufferSize, 0, &vData), "VulkanRenderer.MapMemory");
        _mappedVertexBuffer = vData;

        void* tData;
        VulkanCheck.Ok(_context.Api.MapMemory(_context.Device, _textVertexBufferMemory, 0, MaxTextVertexBufferSize, 0, &tData), "VulkanRenderer.MapMemory");
        _mappedTextVertexBuffer = tData;

        CreateQuadIndexBuffer();

        byte[] whitePixel = [255, 255, 255, 255];
        _whiteTexture = LoadTexture(1, 1, whitePixel);

        IsPipelinesInitialized = true;
    }

    /// <summary>
    ///     Writes the immutable quad expansion pattern once. Host-visible is
    ///     fine here: the buffer is written a single time and only ever read by
    ///     the GPU afterwards (index fetch bandwidth is trivial next to the
    ///     vertex stream it replaces — 6 indices vs 2 duplicated 52-byte verts).
    /// </summary>
    private unsafe void CreateQuadIndexBuffer()
    {
        var pattern = QuadIndexMath.BuildPattern(MaxIndexedQuads);
        var byteSize = (ulong)pattern.Length * sizeof(uint);
        _context.CreateBuffer(byteSize, BufferUsageFlags.IndexBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out _quadIndexBuffer,
            out _quadIndexBufferMemory);

        void* data;
        VulkanCheck.Ok(_context.Api.MapMemory(_context.Device, _quadIndexBufferMemory, 0, byteSize, 0, &data), "VulkanRenderer.MapMemory");
        pattern.AsSpan().CopyTo(new Span<uint>(data, pattern.Length));
        _context.Api.UnmapMemory(_context.Device, _quadIndexBufferMemory);
    }

    public void InitializeTextPipeline(byte[] textVertSpirv, byte[] textFragSpirv)
    {
        if (!IsDeviceInitialized || !IsPipelinesInitialized) return;
        _pipelineManager.InitializeTextPipeline(textVertSpirv, textFragSpirv);
    }

    public void InitializePostPipelines(byte[] fullscreenVertSpirv, byte[] extractFragSpirv, byte[] blurFragSpirv,
        byte[] blitFragSpirv)
    {
        if (!IsDeviceInitialized || !IsPipelinesInitialized) return;
        _pipelineManager.InitializePostPipelines(fullscreenVertSpirv, extractFragSpirv, blurFragSpirv, blitFragSpirv);
    }

    /// <summary>
    ///     Requests the per-frame post effects (0.15.6). Device-free like the
    ///     other setters: flag flips are free while the Sampled composite path
    ///     is already active; only turning the whole post chain on/off flags
    ///     the deferred swapchain recreation (the scene pass output mode
    ///     changes). Safe to call before initialization and from any thread.
    /// </summary>
    public void SetPostEffects(bool bloom, bool filmicTonemap)
    {
        lock (_renderLock)
        {
            if (bloom == _requestedBloom && filmicTonemap == _requestedFilmicTonemap) return;
            var wasActive = _requestedBloom || _requestedFilmicTonemap;
            _requestedBloom = bloom;
            _requestedFilmicTonemap = filmicTonemap;
            if ((bloom || filmicTonemap) != wasActive) SwapchainNeedsRecreation = true;
        }
    }

    public VulkanTexture LoadTexture(uint width, uint height, byte[] pixels, bool mipmapped = true)
        => _resourceManager.LoadTexture(width, height, pixels, mipmapped);

    /// <summary>
    ///     Present-mode tokens for the settings UI: always just FIFO. There is
    ///     no surface to query — the composed path is paced by the compositor
    ///     (the native swapchain that honoured Mailbox/Immediate was removed).
    /// </summary>
    public IReadOnlyList<string> SupportedPresentModes => new[] { "Fifo" };

    /// <summary>Token of the live presentation mode (diagnostics — the
    /// [3DStats] log line): "Composed" / "Headless", "Fifo" before init.
    /// Safe from any thread at 1 Hz.</summary>
    public string CurrentPresentMode
    {
        get
        {
            if (_headlessMode) return "Headless";
            if (_composedMode) return "Composed";
            return "Fifo";
        }
    }

    /// <summary>
    ///     Requests a present mode by config token. Cheap and device-free:
    ///     records the wish and flags the deferred Resize-before-BeginFrame
    ///     rebuild (never mid-frame). The mode itself has no effect on the
    ///     composed/headless paths — see <c>_requestedPresentMode</c>.
    /// </summary>
    public void SetPresentMode(string presentMode)
    {
        lock (_renderLock)
        {
            var requested = SwapchainPolicy.ParsePresentMode(presentMode);
            if (requested == _requestedPresentMode) return;
            _requestedPresentMode = requested;
            SwapchainNeedsRecreation = true;
        }
    }

    /// <summary>MSAA levels the device's framebuffer limits support, from
    /// the offered 1/2/4/8 set, for the settings UI. The full set before the
    /// device exists — requests are clamped again at build time either way.</summary>
    public IReadOnlyList<int> SupportedMsaaSamples =>
        IsDeviceInitialized
            ? MsaaPolicy.SupportedOffered(_context.FramebufferSampleCountMask)
            : MsaaPolicy.OfferedSampleCounts;

    /// <summary>
    ///     Requests an MSAA level (1 = off). Device-free like
    ///     <see cref="SetPresentMode" />: records the wish and flags the
    ///     deferred recreation; the Resize path clamps via
    ///     <see cref="MsaaPolicy" /> and rebuilds render pass + pipelines +
    ///     swapchain resources when the effective level actually changes.
    /// </summary>
    public void SetMsaaSamples(int samples)
    {
        lock (_renderLock)
        {
            if (samples == _requestedMsaaSamples) return;
            _requestedMsaaSamples = samples;
            SwapchainNeedsRecreation = true;
        }
    }

    /// <summary>
    ///     Requests a render scale (percent of the panel size, clamped
    ///     50–200). Device-free like the other setters; slider drags coalesce
    ///     — the flag is consumed once per rendered frame by the deferred
    ///     Resize path, so at most one rebuild happens per frame.
    /// </summary>
    public void SetRenderScalePercent(int percent)
    {
        lock (_renderLock)
        {
            var clamped = GraphicsSettingsPolicy.ClampRenderScalePercent(percent);
            if (clamped == _requestedRenderScalePercent) return;
            _requestedRenderScalePercent = clamped;
            SwapchainNeedsRecreation = true;
        }
    }

    /// <summary>
    ///     The render scale that will actually be used: the clamped request,
    ///     forced to 100 when the surface cannot be a blit destination
    ///     (TRANSFER_DST missing from its supported usage — effectively
    ///     unheard-of on desktop, but scaling without it would be invalid).
    ///     The Sampled composite path (0.15.6) upsamples with a fullscreen
    ///     draw instead of a blit, so it never needs TRANSFER_DST.
    /// </summary>
    private int EffectiveRenderScalePercent(bool sampledOutput)
    {
        var percent = GraphicsSettingsPolicy.ClampRenderScalePercent(_requestedRenderScalePercent);
        if (percent != 100 && !sampledOutput &&
            (_context.SurfaceSupportedUsage & ImageUsageFlags.TransferDstBit) == 0)
        {
            return 100;
        }

        return percent;
    }

    /// <summary>
    ///     Rebuilds every size-dependent resource. Runs ENTIRELY under
    ///     <c>_renderLock</c>: it is the heaviest frame op there is (device
    ///     idle, pipeline rebuild, swapchain/pool recreate, sync-object
    ///     replacement), and <see cref="Dispose" /> takes the same lock to
    ///     destroy the swapchain manager, pipelines and context. A Dispose from
    ///     the UI thread landing inside a render-thread Resize was a
    ///     use-after-free; the <c>_isDisposed</c> guard closes the other half.
    ///     The four request setters only touch flags under the same lock, so no
    ///     deadlock is possible.
    /// </summary>
    public void Resize(uint width, uint height)
    {
        lock (_renderLock)
        {
            if (_isDisposed || !IsPipelinesInitialized) return;
            // A lost device is DEAD: DeviceWaitIdle → CleanupImageResources →
            // CreateSwapchain against it fails identically every time, and the
            // caller re-enters here every frame because SwapchainNeedsRecreation
            // stays set. That silent infinite rebuild loop is the TDR symptom.
            if (IsDeviceLost) return;

            // CONSUME the recreation request HERE, before the work — and from here
            // on only ever SET the flag, never clear it.
            //
            // Resize must not end with `SwapchainNeedsRecreation = <degenerate?>`,
            // which is an unconditional write of `false` on the success path. The
            // work in between idles the device and rebuilds every image
            // resource — milliseconds — and SetPresentMode / SetMsaaSamples /
            // SetRenderScalePercent / SetPostEffects all run on the UI THREAD and
            // set the flag under _renderLock. A request arriving inside that window
            // would be read back out by the trailing write and LOST. None of those four
            // settings changes the surface SIZE, and the engine's consume point
            // (Fretboard3DEngine.RenderFrame) only re-enters Resize on a size
            // change or the flag — so the setting would silently never be applied,
            // with no log line. Most visible as a render-scale slider whose final
            // tick lands inside the last rebuild and never takes effect.
            //
            // Clearing first means such a request re-arms the flag and is picked up
            // by the next frame instead.
            SwapchainNeedsRecreation = false;

            // MSAA level change: pipelines bake the sample count and the
            // render pass structure differs (resolve vs direct), so rebuild both
            // BEFORE the swapchain resources — the framebuffers below must be
            // created against the new render pass. A render-scale flip between
            // 100% and scaled changes only the pass's color-output layout/
            // dependency (compatible with the existing pipelines — RebuildFor
            // keeps them). Both no-op when nothing effective changed. The device
            // must be idle while old objects die: the previous frame may still be
            // executing (2 frames in flight) even though no render-pass instance
            // is open on this thread.
            var targetSamples = MsaaPolicy.Clamp(_requestedMsaaSamples, _context.FramebufferSampleCountMask);
            // Post effects require the Sampled composite path — but only when the
            // post pipelines exist (the engine initializes them right after the
            // text pipeline; guarding keeps a half-initialized renderer valid).
            var postEffects = (_requestedBloom || _requestedFilmicTonemap) &&
                              _pipelineManager.IsPostPipelinesInitialized;
            var scalePercent = EffectiveRenderScalePercent(postEffects);
            var outputMode = PostProcessPolicy.ChooseOutputMode(postEffects, RenderScalePolicy.IsScaled(scalePercent));
            if (targetSamples != _pipelineManager.CurrentSampleCount ||
                outputMode != _pipelineManager.CurrentOutputMode)
            {
                _context.Api.DeviceWaitIdle(_context.Device);
                _pipelineManager.RebuildFor(targetSamples, outputMode);
            }

            if (_headlessMode)
            {
                // Headless (testing): rebuild the single offscreen image + all
                // downstream resources, plus the host-visible readback buffer the
                // copy-to-buffer targets. No semaphores exist — the frame fence +
                // copy barrier order everything.
                _swapchainManager.RecreateHeadless(width, height, _pipelineManager,
                    targetSamples, scalePercent, outputMode);
                EnsureReadbackBuffer(width, height);
                CurrentWidth = width;
                CurrentHeight = height;
                // Only ever SET (see the consume at the top of Resize).
                if (_swapchainManager.Framebuffers.Length == 0) SwapchainNeedsRecreation = true;
                return;
            }

            if (_composedMode)
            {
                // Composed (0.16): rebuild the exportable image pool + all
                // downstream resources. No present semaphores exist to recreate —
                // the keyed mutex replaces them.
                _swapchainManager.RecreateComposed(width, height, _pipelineManager,
                    targetSamples, scalePercent, outputMode, _composedPool!);
                CurrentWidth = width;
                CurrentHeight = height;
                if (_composedPool!.Count == 0) SwapchainNeedsRecreation = true;
                return;
            }
        }
    }

    public bool BeginFrame()
    {
        bool began;
        lock (_renderLock)
        {
            began = BeginFrameLocked();
        }

        // The device-lost notice is raised OUTSIDE the lock (see
        // RaisePendingDeviceLost) — a handler that marshals to the UI thread
        // must never be able to dead-embrace a UI thread already in here.
        RaisePendingDeviceLost();
        return began;
    }

    private unsafe bool BeginFrameLocked()
    {
        {
            if (!IsPipelinesInitialized || _isDisposed || CurrentWidth == 0 || CurrentHeight == 0) return false;
            // A lost device never comes back: refuse the frame rather than
            // re-arming the swapchain-recreation loop on a dead device.
            if (IsDeviceLost) return false;

            if (_headlessMode)
            {
                // Headless (testing): no acquire, no semaphore — there is one
                // fixed offscreen image at index 0. Just wait the frame fence
                // so the previous frame's copy-back completed before we reuse
                // the command buffer / vertex chunk.
                if (_swapchainManager.Framebuffers.Length == 0) return false;
                var headlessFence = Check(_context.Api.WaitForFences(_context.Device, 1,
                    in _inFlightFences[_activeFrameIndex], true, 1_000_000_000), "BeginFrame/headless fence");
                if (headlessFence != FrameResultAction.Continue) return false;
                _imageIndex = 0;
            }
            else if (_composedMode)
            {
                // Composed (0.16): no swapchain acquire — pick a free pool
                // image. A faulted present poisons the pool; rebuild it on
                // the deferred path before rendering anything else.
                if (_composedPool == null || _composedPool.Count == 0) return false;
                if (_composedPool.HasBroken)
                {
                    SwapchainNeedsRecreation = true;
                    return false;
                }

                var composedFence = Check(_context.Api.WaitForFences(_context.Device, 1,
                    in _inFlightFences[_activeFrameIndex], true, 100_000_000), "BeginFrame/composed fence");
                if (composedFence != FrameResultAction.Continue) return false;

                // Nothing free = the compositor owns every image — natural
                // back-pressure to its (uncapped since Avalonia 12.1) rate.
                if (!_composedPool.TryAcquire(out _imageIndex)) return false;
            }
            else
            {
                // Neither mode initialized — unreachable once pipelines exist
                // (both init paths set a mode). Never record a frame.
                return false;
            }

            _currentDrawCallCount = 0;
            _currentVertexCount = 0;
            _context.Api.ResetFences(_context.Device, 1, in _inFlightFences[_activeFrameIndex]);

            var chunkSize = MaxVertexBufferSize / MaxFramesInFlight;
            _currentVertexOffset = (ulong)_activeFrameIndex * chunkSize;
            _currentVertexChunkEnd = _currentVertexOffset + chunkSize;

            var textChunkSize = MaxTextVertexBufferSize / MaxFramesInFlight;
            _currentTextVertexOffset = (ulong)_activeFrameIndex * textChunkSize;
            _currentTextVertexChunkEnd = _currentTextVertexOffset + textChunkSize;

            var cmd = _commandBuffers[_activeFrameIndex];
            var beginInfo = new CommandBufferBeginInfo
            { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
            _context.Api.BeginCommandBuffer(cmd, in beginInfo);
            _recordingFrameIndex = _activeFrameIndex;

            // Clear-value indices mirror the render pass attachment layout in
            // both MSAA shapes: [0] color (MSAA target or the swapchain image
            // itself), [1] depth. The resolve attachment [2] (MSAA only) uses
            // LoadOp=DontCare, so its entry is never read — but the count must
            // still cover the highest attachment index.
            var clearValues = stackalloc ClearValue[3];
            clearValues[0] = new ClearValue
            {
                Color = new ClearColorValue { Float32_0 = 0.0f, Float32_1 = 0.0f, Float32_2 = 0.0f, Float32_3 = 1.0f }
            };
            clearValues[1] = new ClearValue { DepthStencil = new ClearDepthStencilValue(1.0f, 0) };
            clearValues[2] = new ClearValue
            {
                Color = new ClearColorValue { Float32_0 = 0.0f, Float32_1 = 0.0f, Float32_2 = 0.0f, Float32_3 = 0.0f }
            };

            var renderPassBeginInfo = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = _pipelineManager.RenderPass,
                Framebuffer = _swapchainManager.Framebuffers[_imageIndex],
                RenderArea = new Rect2D(new Offset2D(0, 0), _swapchainManager.RenderExtent),
                ClearValueCount = _swapchainManager.SampleCount > 1 ? 3u : 2u, PClearValues = clearValues
            };

            _context.Api.CmdBeginRenderPass(cmd, in renderPassBeginInfo, SubpassContents.Inline);

            // One index buffer serves every quad draw this frame; vertex
            // buffers rebind only when the stream (highway/text) switches.
            _context.Api.CmdBindIndexBuffer(cmd, _quadIndexBuffer, 0, IndexType.Uint32);
            _lastBoundPipeline = 0;
            _lastBoundPipelineLayout = 0;
            _lastBoundDescriptorSet = 0;
            _lastBoundVertexBuffer = 0;

            // Viewport/scissor follow the ACTUAL render extent — the (surface-
            // clamped) swapchain extent, scaled by the render-scale setting
            // when active — never the requested size: they must never exceed
            // the framebuffer.
            var extent = _swapchainManager.RenderExtent;
            var viewport = new Viewport
            { X = 0.0f, Y = 0.0f, Width = extent.Width, Height = extent.Height, MinDepth = 0.0f, MaxDepth = 1.0f };
            _context.Api.CmdSetViewport(cmd, 0, 1, &viewport);
            var scissor = new Rect2D
            { Offset = new Offset2D(0, 0), Extent = extent };
            _context.Api.CmdSetScissor(cmd, 0, 1, &scissor);

            return true;
        }
    }

    public unsafe void DrawBatch(float[] vertices, int count, Matrix4x4 mvp, Vector3 camPos, float fadeStart,
        float fadeEnd, float timeMs, bool depthWrite, HighwayEffects effects = HighwayEffects.None,
        VulkanTexture? texture = null)
    {
        lock (_renderLock)
        {
            if (!IsPipelinesInitialized || count == 0 || _mappedVertexBuffer == null) return;
            if (count < 0 || count > vertices.Length)
            {
                Debug.WriteLine($"[VULKAN] Dropped invalid geometry batch. Count={count}, buffer={vertices.Length}.");
                return;
            }

            // Quad-aligned batches only (4 verts per quad, indexed expansion):
            // a partial quad can never render, so trim it away up front.
            const int floatsPerQuad = HighwayVertexFloatCount * QuadIndexMath.VerticesPerQuad;
            var quadCount = QuadIndexMath.QuadCount(count, HighwayVertexFloatCount);
            var alignedCount = quadCount * floatsPerQuad;
            if (alignedCount <= 0) return;
            if (alignedCount != count)
                Debug.WriteLine($"[VULKAN] Trimmed quad-misaligned geometry batch from {count} to {alignedCount} floats.");
            if (quadCount > MaxIndexedQuads)
            {
                Debug.WriteLine(
                    $"[VULKAN] Dropped geometry batch that exceeds the static index buffer. Quads={quadCount}, max={MaxIndexedQuads}.");
                return;
            }

            var dataSize = (ulong)alignedCount * sizeof(float);
            if (_currentVertexOffset + dataSize > _currentVertexChunkEnd)
            {
                Debug.WriteLine(
                    $"[VULKAN] Dropped geometry batch that exceeds the per-frame vertex chunk. RequestedBytes={dataSize}, remainingBytes={_currentVertexChunkEnd - _currentVertexOffset}.");
                return;
            }

            Marshal.Copy(vertices, 0, (nint)((byte*)_mappedVertexBuffer + _currentVertexOffset), alignedCount);
            var cmd = _commandBuffers[_activeFrameIndex];

            var pipeline = depthWrite ? _pipelineManager.PipelineOpaque : _pipelineManager.PipelineTransparent;
            if (pipeline.Handle != _lastBoundPipeline)
            {
                _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
                _lastBoundPipeline = pipeline.Handle;
            }

            // The highway and text pipeline LAYOUTS differ in their push
            // constant ranges, so their set-0 bindings are NOT compatible: a
            // layout switch invalidates the cached descriptor binding even if
            // the set handle matches.
            if (_pipelineManager.PipelineLayout.Handle != _lastBoundPipelineLayout)
            {
                _lastBoundDescriptorSet = 0;
                _lastBoundPipelineLayout = _pipelineManager.PipelineLayout.Handle;
            }

            var texToBind = texture ?? _whiteTexture;
            if (texToBind != null && texToBind.DescriptorSet.Handle != _lastBoundDescriptorSet)
            {
                var ds = texToBind.DescriptorSet;
                _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipelineManager.PipelineLayout, 0,
                    1, &ds, 0, null);
                _lastBoundDescriptorSet = ds.Handle;
            }

            var pc = new HighwayPushConstants
            {
                MVP = mvp, CamX = camPos.X, CamY = camPos.Y, CamZ = camPos.Z, FadeStart = fadeStart, FadeEnd = fadeEnd,
                Time = timeMs, Effects = (uint)effects
            };
            _context.Api.CmdPushConstants(cmd, _pipelineManager.PipelineLayout,
                ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(HighwayPushConstants), &pc);

            if (_vertexBuffer.Handle != _lastBoundVertexBuffer)
            {
                var pBuffersStack = _vertexBuffer;
                ulong pOffsetsStack = 0;
                _context.Api.CmdBindVertexBuffers(cmd, 0, 1, &pBuffersStack, &pOffsetsStack);
                _lastBoundVertexBuffer = _vertexBuffer.Handle;
            }

            // The chunk base and every batch size are whole quads, so the byte
            // offset is always vertex-stride aligned — address via vertexOffset
            // instead of rebinding the vertex buffer per draw.
            var vertexOffset = (int)(_currentVertexOffset / (HighwayVertexFloatCount * sizeof(float)));
            _context.Api.CmdDrawIndexed(cmd, (uint)(quadCount * QuadIndexMath.IndicesPerQuad), 1, 0, vertexOffset, 0);

            _currentDrawCallCount++;
            _currentVertexCount += quadCount * QuadIndexMath.VerticesPerQuad;
            _currentVertexOffset += dataSize;
        }
    }

    public unsafe void DrawTextBatch(TextVertex[] vertices, int count, Matrix4x4 mvp, VulkanTexture texture)
    {
        lock (_renderLock)
        {
            if (!IsPipelinesInitialized || count == 0 || _mappedTextVertexBuffer == null) return;
            if (count < 0 || count > vertices.Length)
            {
                Debug.WriteLine($"[VULKAN] Dropped invalid text batch. Count={count}, buffer={vertices.Length}.");
                return;
            }

            // Text glyphs are quads too (4 verts each) and share
            // the same static index buffer as the highway stream.
            var quadCount = count / QuadIndexMath.VerticesPerQuad;
            var alignedCount = quadCount * QuadIndexMath.VerticesPerQuad;
            if (alignedCount <= 0) return;
            if (alignedCount != count)
                Debug.WriteLine($"[VULKAN] Trimmed quad-misaligned text batch from {count} to {alignedCount} vertices.");
            if (quadCount > MaxIndexedQuads)
            {
                Debug.WriteLine(
                    $"[VULKAN] Dropped text batch that exceeds the static index buffer. Quads={quadCount}, max={MaxIndexedQuads}.");
                return;
            }

            var dataSize = (ulong)alignedCount * (ulong)sizeof(TextVertex);
            if (_currentTextVertexOffset + dataSize > _currentTextVertexChunkEnd)
            {
                // A dropped batch is text MISSING on screen — chord names, a
                // lyric line, a HUD. Debug.WriteLine is compiled OUT of Release,
                // so for as long as it was the only channel the shipped app lost
                // text in total silence. Console.Error survives, and says it once
                // per session so a busy frame cannot spam at 144 Hz. Same
                // treatment as Text.ChartFontAtlas.OverflowWarning.
                DroppedTextBatchCount++;
                if (!_textDropWarned)
                {
                    _textDropWarned = true;
                    Console.Error.WriteLine(
                        $"[VULKAN] Dropped a text batch that exceeds the per-frame vertex chunk " +
                        $"({MaxTextVerticesPerFrame} vertices): requested {dataSize} B, " +
                        $"{_currentTextVertexChunkEnd - _currentTextVertexOffset} B left. " +
                        "Text is missing on screen; further drops this session are counted in " +
                        "DroppedTextBatchCount but not repeated here.");
                }

                Debug.WriteLine(
                    $"[VULKAN] Dropped text batch that exceeds the per-frame vertex chunk. RequestedBytes={dataSize}, remainingBytes={_currentTextVertexChunkEnd - _currentTextVertexOffset}.");
                return;
            }

            fixed (void* src = vertices)
            {
                Buffer.MemoryCopy(src, (byte*)_mappedTextVertexBuffer + _currentTextVertexOffset, dataSize, dataSize);
            }

            var cmd = _commandBuffers[_activeFrameIndex];

            if (_pipelineManager.PipelineText.Handle != _lastBoundPipeline)
            {
                _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipelineManager.PipelineText);
                _lastBoundPipeline = _pipelineManager.PipelineText.Handle;
            }

            // See DrawBatch: a pipeline-layout switch invalidates the cached
            // descriptor binding (incompatible push constant ranges).
            if (_pipelineManager.PipelineLayoutText.Handle != _lastBoundPipelineLayout)
            {
                _lastBoundDescriptorSet = 0;
                _lastBoundPipelineLayout = _pipelineManager.PipelineLayoutText.Handle;
            }

            if (texture.DescriptorSet.Handle != _lastBoundDescriptorSet)
            {
                var ds = texture.DescriptorSet;
                _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipelineManager.PipelineLayoutText,
                    0, 1, &ds, 0, null);
                _lastBoundDescriptorSet = ds.Handle;
            }

            var pc = new TextPushConstants { MVP = mvp };
            _context.Api.CmdPushConstants(cmd, _pipelineManager.PipelineLayoutText,
                ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(TextPushConstants), &pc);

            if (_textVertexBuffer.Handle != _lastBoundVertexBuffer)
            {
                var pBuffersStack = _textVertexBuffer;
                ulong pOffsetsStack = 0;
                _context.Api.CmdBindVertexBuffers(cmd, 0, 1, &pBuffersStack, &pOffsetsStack);
                _lastBoundVertexBuffer = _textVertexBuffer.Handle;
            }

            var vertexOffset = (int)(_currentTextVertexOffset / (ulong)sizeof(TextVertex));
            _context.Api.CmdDrawIndexed(cmd, (uint)(quadCount * QuadIndexMath.IndicesPerQuad), 1, 0, vertexOffset, 0);

            _currentDrawCallCount++;
            _currentVertexCount += alignedCount;
            _currentTextVertexOffset += dataSize;
        }
    }

    public unsafe void EndFrame()
    {
        // Raised OUTSIDE the render lock: the handler marshals to the UI
        // thread and must never be able to dead-embrace a UI thread that is
        // itself calling into the renderer.
        var notifyIndex = -1;
        long notifyGeneration = 0;

        lock (_renderLock)
        {
            if (!IsPipelinesInitialized || _isDisposed || CurrentWidth == 0 || CurrentHeight == 0) return;
            if (IsDeviceLost) return;

            LastDrawCallCount = _currentDrawCallCount;
            LastVertexCount = _currentVertexCount;
            var cmd = _commandBuffers[_activeFrameIndex];

            _context.Api.CmdEndRenderPass(cmd);
            // Post chain (0.15.6): the pass ended with the intermediate in
            // SHADER_READ_ONLY (its second subpass dependency synchronizes
            // the color/resolve writes against the fragment sampling) — run
            // the bloom mips + fullscreen composite into the swapchain image.
            if (_swapchainManager.PostProcessActive) RecordPostProcess(cmd);
            // Render scale (0.14.2): the pass ended with the intermediate in
            // TRANSFER_SRC_OPTIMAL (its second subpass dependency synchronizes
            // the color/resolve writes against this blit) — copy it onto the
            // acquired swapchain image and hand that to present.
            else if (_swapchainManager.RenderScaleActive) RecordRenderScaleBlit(cmd);
            // Headless (testing): copy the finished offscreen image into the
            // host-visible readback buffer, in the SAME command buffer, before
            // it is ended and submitted.
            if (_headlessMode) RecordHeadlessReadback(cmd);
            _context.Api.EndCommandBuffer(cmd);
            _recordingFrameIndex = -1;

            if (_headlessMode)
            {
                // Headless (testing): no semaphores, no present — submit with
                // the frame fence only and BLOCK until the GPU finishes so the
                // copy-back buffer holds the rendered pixels when EndFrame
                // returns. A test then reads them via TryReadbackBgra.
                var headlessSubmit = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers = &cmd
                };
                var headlessSubmitResult = Check(_context.Api.QueueSubmit(_context.GraphicsQueue, 1,
                    in headlessSubmit, _inFlightFences[_activeFrameIndex]), "EndFrame/headless submit");
                if (headlessSubmitResult == FrameResultAction.Continue)
                {
                    Check(_context.Api.WaitForFences(_context.Device, 1, in _inFlightFences[_activeFrameIndex],
                        true, 5_000_000_000), "EndFrame/headless readback wait");
                }

                _activeFrameIndex = (_activeFrameIndex + 1) % MaxFramesInFlight;
            }
            else if (_composedMode)
            {
                // Composed (0.16): no semaphores at all — the keyed mutex is
                // the cross-API sync (acquire key 0 before any command runs,
                // release key 1 when the submit completes), the frame fence
                // paces the CPU. There is no present; the compositor pulls
                // the image after the UI-side UpdateWithKeyedMutexAsync.
                ulong acquireKey = 0, releaseKey = 1;
                var syncMemory = _composedPool!.GetDeviceMemory(_imageIndex);
                // FINITE, from ComposedSubmitPolicy: uint.MaxValue is INFINITE,
                // so a compositor that never released key 0 hung the queue until
                // the driver reset the GPU. On a timeout the submit fails, the
                // slot is poisoned below and BeginFrame rebuilds the pool.
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

                var composedSubmit = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    PNext = &keyedMutex,
                    CommandBufferCount = 1,
                    PCommandBuffers = &cmd
                };
                var composedSubmitResult = Check(_context.Api.QueueSubmit(_context.GraphicsQueue, 1,
                    in composedSubmit, _inFlightFences[_activeFrameIndex]), "EndFrame/composed submit");

                if (composedSubmitResult == FrameResultAction.Continue)
                {
                    _composedPool.MarkSubmitted(_imageIndex);
                    notifyIndex = (int)_imageIndex;
                    notifyGeneration = _composedPool.Generation;
                }
                else
                {
                    // The frame never reached the GPU, so no ComposedFrameReady
                    // may be raised — and the slot must not be left in Rendering
                    // (it would never be Free again and never get its matching
                    // CompleteComposedPresent). Poison it; BeginFrame's HasBroken
                    // path rebuilds the pool on the deferred path.
                    _composedPool.MarkBroken(_imageIndex);
                }

                _activeFrameIndex = (_activeFrameIndex + 1) % MaxFramesInFlight;
            }
        }

        if (notifyIndex >= 0) ComposedFrameReady?.Invoke(notifyIndex, notifyGeneration);
        RaisePendingDeviceLost();
    }

    // ---- IVulkanComposedPresenter pass-throughs (0.16) --------------------

    public nint CreateComposedExportHandle(int index, long generation, out ulong memorySize)
    {
        memorySize = 0;
        return _composedPool?.CreateExportHandle(index, generation, out memorySize) ?? 0;
    }

    public (uint Width, uint Height) ComposedImageSize =>
        _composedPool is { } pool ? (pool.Width, pool.Height) : (0u, 0u);

    // A faulted present poisons the pool — BeginFrame will flag the
    // deferred rebuild when it sees the broken image.
    public void CompleteComposedPresent(int index, long generation, bool success) => _composedPool?.CompletePresent((uint)index, generation, success);

    public void WaitDeviceIdle()
    {
        if (IsDeviceInitialized) _context.Api.DeviceWaitIdle(_context.Device);
    }

    // ---- Headless output (testing) ---------------------------------------

    /// <summary>
    ///     Records the offscreen-image → host-buffer copy inside the frame's
    ///     command buffer. Every output mode leaves the offscreen image in
    ///     COLOR_ATTACHMENT_OPTIMAL at pass/composite end EXCEPT the blit
    ///     (render-scale) path, which already transitioned it to
    ///     TRANSFER_SRC_OPTIMAL with a transfer-read barrier — so transition
    ///     here only when it is not already a transfer source.
    /// </summary>
    private unsafe void RecordHeadlessReadback(CommandBuffer cmd)
    {
        var image = _swapchainManager.GetSwapchainImage(_imageIndex);
        var extent = _swapchainManager.Extent;
        var colorRange = new ImageSubresourceRange
        {
            AspectMask = ImageAspectFlags.ColorBit, BaseMipLevel = 0, LevelCount = 1,
            BaseArrayLayer = 0, LayerCount = 1
        };

        if (!_swapchainManager.RenderScaleActive)
        {
            var toSrc = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
                DstAccessMask = AccessFlags.TransferReadBit,
                OldLayout = ImageLayout.ColorAttachmentOptimal,
                NewLayout = ImageLayout.TransferSrcOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = colorRange
            };
            _context.Api.CmdPipelineBarrier(cmd,
                PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.TransferBit,
                0, 0, null, 0, null, 1, &toSrc);
        }

        var region = new BufferImageCopy
        {
            BufferOffset = 0, BufferRowLength = 0, BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers
            { AspectMask = ImageAspectFlags.ColorBit, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1 },
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D(extent.Width, extent.Height, 1)
        };
        _context.Api.CmdCopyImageToBuffer(cmd, image, ImageLayout.TransferSrcOptimal, _readbackBuffer, 1, &region);
    }

    /// <summary>(Re)creates the persistently-mapped host-visible readback buffer
    /// when the offscreen size grows. Called from the headless Resize path, where
    /// the device is already idle and the previous frame's copy has completed.</summary>
    private unsafe void EnsureReadbackBuffer(uint width, uint height)
    {
        var needed = (ulong)width * height * 4;
        if (needed == 0) return;
        if (_readbackBuffer.Handle != 0 && needed <= _readbackBufferSize) return;

        DestroyReadbackBuffer();

        _context.CreateBuffer(needed, BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out _readbackBuffer, out _readbackBufferMemory);
        void* data;
        VulkanCheck.Ok(_context.Api.MapMemory(_context.Device, _readbackBufferMemory, 0, needed, 0, &data), "VulkanRenderer.MapMemory");
        _mappedReadbackBuffer = data;
        _readbackBufferSize = needed;
    }

    public unsafe bool TryReadbackBgra(byte[] destination)
    {
        lock (_renderLock)
        {
            if (!_headlessMode || _mappedReadbackBuffer == null) return false;
            var needed = (long)CurrentWidth * CurrentHeight * 4;
            if (needed == 0 || destination.Length < needed) return false;
            // Host-coherent memory + the EndFrame fence wait already made the
            // copy visible on the CPU — a plain read, no invalidate.
            new Span<byte>(_mappedReadbackBuffer, (int)needed).CopyTo(destination.AsSpan(0, (int)needed));
            return true;
        }
    }

    private unsafe void DestroyReadbackBuffer()
    {
        if (_readbackBuffer.Handle == 0) return;
        if (_mappedReadbackBuffer != null) _context.Api.UnmapMemory(_context.Device, _readbackBufferMemory);
        _context.Api.DestroyBuffer(_context.Device, _readbackBuffer, null);
        _context.Api.FreeMemory(_context.Device, _readbackBufferMemory, null);
        _readbackBuffer = default;
        _readbackBufferMemory = default;
        _mappedReadbackBuffer = null;
        _readbackBufferSize = 0;
    }

    /// <summary>
    ///     Records the render-scale upscale/downscale: transitions the output
    ///     image UNDEFINED → TRANSFER_DST (whole image is overwritten, so
    ///     discarding the old contents is correct and free), blits the
    ///     intermediate color target onto it with linear filtering
    ///     (B8G8R8A8Unorm has mandatory blit-src/dst + linear-filter support),
    ///     then transitions it to COLOR_ATTACHMENT_OPTIMAL (composed) or
    ///     TRANSFER_SRC_OPTIMAL (headless, for the readback copy). Both barriers
    ///     chain off the TRANSFER stage.
    /// </summary>
    private unsafe void RecordRenderScaleBlit(CommandBuffer cmd)
    {
        var swapchainImage = _swapchainManager.GetSwapchainImage(_imageIndex);
        var srcExtent = _swapchainManager.RenderExtent;
        var dstExtent = _swapchainManager.Extent;

        var colorRange = new ImageSubresourceRange
        {
            AspectMask = ImageAspectFlags.ColorBit, BaseMipLevel = 0, LevelCount = 1,
            BaseArrayLayer = 0, LayerCount = 1
        };

        var toTransferDst = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = 0,
            DstAccessMask = AccessFlags.TransferWriteBit,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = swapchainImage,
            SubresourceRange = colorRange
        };
        _context.Api.CmdPipelineBarrier(cmd,
            PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit,
            0, 0, null, 0, null, 1, &toTransferDst);

        var blit = new ImageBlit
        {
            SrcSubresource = new ImageSubresourceLayers
            { AspectMask = ImageAspectFlags.ColorBit, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1 },
            DstSubresource = new ImageSubresourceLayers
            { AspectMask = ImageAspectFlags.ColorBit, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1 }
        };
        blit.SrcOffsets[0] = new Offset3D(0, 0, 0);
        blit.SrcOffsets[1] = new Offset3D((int)srcExtent.Width, (int)srcExtent.Height, 1);
        blit.DstOffsets[0] = new Offset3D(0, 0, 0);
        blit.DstOffsets[1] = new Offset3D((int)dstExtent.Width, (int)dstExtent.Height, 1);

        _context.Api.CmdBlitImage(cmd,
            _swapchainManager.IntermediateColorImage, ImageLayout.TransferSrcOptimal,
            swapchainImage, ImageLayout.TransferDstOptimal,
            1, &blit, Filter.Linear);

        // Headless: the blit dest is about to be copied to a buffer, so leave
        // it in TRANSFER_SRC and order the copy read against the blit write in
        // the same command buffer (no extra barrier needed in EndFrame).
        // Composed: the target is an exported (non-presentable) image —
        // PresentSrc is not a legal layout for it; the keyed-mutex release
        // at submit completion orders the writes for the compositor.
        var blitFinalLayout = _headlessMode ? ImageLayout.TransferSrcOptimal
            : ImageLayout.ColorAttachmentOptimal;
        var toPresent = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = _headlessMode ? AccessFlags.TransferReadBit : 0,
            OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = blitFinalLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = swapchainImage,
            SubresourceRange = colorRange
        };
        _context.Api.CmdPipelineBarrier(cmd,
            PipelineStageFlags.TransferBit,
            _headlessMode ? PipelineStageFlags.TransferBit : PipelineStageFlags.BottomOfPipeBit,
            0, 0, null, 0, null, 1, &toPresent);
    }

    /// <summary>
    ///     Records the 0.15.6 post chain (plan 5.5): when bloom is on, a
    ///     threshold extract of the scene into the half-res mip, separable
    ///     gaussian blurs at half and quarter res (ping-pong; the blur
    ///     pipeline with direction (0,0) doubles as the downsample copy), then
    ///     one composite pass into the acquired swapchain image — an opaque
    ///     fullscreen draw of the scene (which also upsamples a scaled
    ///     render) plus additive draws of the bloom mips. All inter-pass
    ///     hazards ride the static post/composite passes' EXTERNAL subpass
    ///     dependencies (fragment-read ↔ color-write both directions).
    /// </summary>
    private unsafe void RecordPostProcess(CommandBuffer cmd)
    {
        var renderExtent = _swapchainManager.RenderExtent;
        var bloomOn = _requestedBloom && _pipelineManager.IsPostPipelinesInitialized;

        if (bloomOn)
        {
            var halfA = _swapchainManager.GetBloomTarget(0);
            var halfB = _swapchainManager.GetBloomTarget(1);
            var quarterA = _swapchainManager.GetBloomTarget(2);
            var quarterB = _swapchainManager.GetBloomTarget(3);

            var sceneTexel = new Vector2(1f / renderExtent.Width, 1f / renderExtent.Height);
            var halfTexel = new Vector2(1f / halfA.Extent.Width, 1f / halfA.Extent.Height);
            var quarterTexel = new Vector2(1f / quarterA.Extent.Width, 1f / quarterA.Extent.Height);

            // Threshold extract: scene → half A.
            RecordPostPass(cmd, _pipelineManager.PipelinePostExtract, halfA.Framebuffer, halfA.Extent,
                _swapchainManager.IntermediateColorDescriptorSet, new PostPushConstants
                {
                    Texel = sceneTexel,
                    Params = new Vector4(PostProcessPolicy.BloomThreshold, PostProcessPolicy.BloomKnee, 0f, 0f)
                });
            // Gaussian H/V at half res (A → B → A).
            RecordPostPass(cmd, _pipelineManager.PipelinePostBlur, halfB.Framebuffer, halfB.Extent,
                halfA.DescriptorSet, new PostPushConstants { Texel = halfTexel, Dir = new Vector2(1f, 0f) });
            RecordPostPass(cmd, _pipelineManager.PipelinePostBlur, halfA.Framebuffer, halfA.Extent,
                halfB.DescriptorSet, new PostPushConstants { Texel = halfTexel, Dir = new Vector2(0f, 1f) });
            // Downsample half A → quarter A (blur with zero direction = copy).
            RecordPostPass(cmd, _pipelineManager.PipelinePostBlur, quarterA.Framebuffer, quarterA.Extent,
                halfA.DescriptorSet, new PostPushConstants { Texel = halfTexel, Dir = Vector2.Zero });
            // Gaussian H/V at quarter res (A → B → A).
            RecordPostPass(cmd, _pipelineManager.PipelinePostBlur, quarterB.Framebuffer, quarterB.Extent,
                quarterA.DescriptorSet, new PostPushConstants { Texel = quarterTexel, Dir = new Vector2(1f, 0f) });
            RecordPostPass(cmd, _pipelineManager.PipelinePostBlur, quarterA.Framebuffer, quarterA.Extent,
                quarterB.DescriptorSet, new PostPushConstants { Texel = quarterTexel, Dir = new Vector2(0f, 1f) });
        }

        // Composite into the acquired swapchain image at FULL surface extent.
        var extent = _swapchainManager.Extent;
        var beginInfo = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _pipelineManager.CompositeRenderPass,
            Framebuffer = _swapchainManager.GetCompositeFramebuffer(_imageIndex),
            RenderArea = new Rect2D(new Offset2D(0, 0), extent)
        };
        _context.Api.CmdBeginRenderPass(cmd, in beginInfo, SubpassContents.Inline);
        SetViewportScissor(cmd, extent);

        var tonemapFlag = _requestedFilmicTonemap ? 1f : 0f;
        RecordCompositeDraw(cmd, _pipelineManager.PipelineCompositeScene,
            _swapchainManager.IntermediateColorDescriptorSet,
            new PostPushConstants { Params = new Vector4(1f, tonemapFlag, 0f, 0f) });

        if (bloomOn)
        {
            RecordCompositeDraw(cmd, _pipelineManager.PipelineCompositeAdd,
                _swapchainManager.GetBloomTarget(0).DescriptorSet,
                new PostPushConstants { Params = new Vector4(PostProcessPolicy.BloomIntensityHalf, 0f, 0f, 0f) });
            RecordCompositeDraw(cmd, _pipelineManager.PipelineCompositeAdd,
                _swapchainManager.GetBloomTarget(2).DescriptorSet,
                new PostPushConstants { Params = new Vector4(PostProcessPolicy.BloomIntensityQuarter, 0f, 0f, 0f) });
        }

        _context.Api.CmdEndRenderPass(cmd);
    }

    /// <summary>One offscreen fullscreen-triangle pass into a bloom target.</summary>
    private unsafe void RecordPostPass(CommandBuffer cmd, Pipeline pipeline, Framebuffer framebuffer,
        Extent2D extent, DescriptorSet source, PostPushConstants pc)
    {
        var beginInfo = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _pipelineManager.PostRenderPass,
            Framebuffer = framebuffer,
            RenderArea = new Rect2D(new Offset2D(0, 0), extent)
        };
        _context.Api.CmdBeginRenderPass(cmd, in beginInfo, SubpassContents.Inline);
        SetViewportScissor(cmd, extent);
        RecordCompositeDraw(cmd, pipeline, source, pc);
        _context.Api.CmdEndRenderPass(cmd);
    }

    /// <summary>Binds a post pipeline + source set + push constants and draws
    /// the fullscreen triangle (3 vertices, no vertex buffer).</summary>
    private unsafe void RecordCompositeDraw(CommandBuffer cmd, Pipeline pipeline, DescriptorSet source,
        PostPushConstants pc)
    {
        _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
        _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipelineManager.PipelineLayoutPost, 0,
            1, &source, 0, null);
        _context.Api.CmdPushConstants(cmd, _pipelineManager.PipelineLayoutPost, ShaderStageFlags.FragmentBit, 0,
            (uint)sizeof(PostPushConstants), &pc);
        _context.Api.CmdDraw(cmd, 3, 1, 0, 0);
        _currentDrawCallCount++;
    }

    private unsafe void SetViewportScissor(CommandBuffer cmd, Extent2D extent)
    {
        var viewport = new Viewport
        { X = 0.0f, Y = 0.0f, Width = extent.Width, Height = extent.Height, MinDepth = 0.0f, MaxDepth = 1.0f };
        _context.Api.CmdSetViewport(cmd, 0, 1, &viewport);
        var scissor = new Rect2D { Offset = new Offset2D(0, 0), Extent = extent };
        _context.Api.CmdSetScissor(cmd, 0, 1, &scissor);
    }

    /// <summary>
    ///     Restricts subsequent draws in the CURRENT frame to a sub-rectangle of
    ///     the render target (split highway / couch co-op). The rect is given in
    ///     the REQUESTED panel space — the same pixels the engine is handed —
    ///     and is rescaled here onto <c>RenderExtent</c>, so it is correct under
    ///     the render-scale setting and under a surface-clamped swapchain
    ///     without the caller knowing either exists.
    ///     <para>
    ///         Both viewport and scissor are set: the viewport alone maps NDC
    ///         into the sub-rect but does not CLIP, so anything a shader emits
    ///         outside the frustum (the ortho passes deliberately span the whole
    ///         NDC square) would bleed into the neighbouring player's half.
    ///     </para>
    ///     <para>
    ///         Both are dynamic pipeline state in every scene/text pipeline, so
    ///         this is a plain command with no pipeline rebuild. The frame's
    ///         post-process and blit passes reset the full-frame rect themselves
    ///         (<see cref="SetViewportScissor" />), so a split frame still
    ///         composites and presents whole.
    ///     </para>
    ///     Silently no-ops outside a begun frame or on a degenerate rect — a
    ///     zero/negative extent is a validation error, never worth throwing a
    ///     render thread over.
    /// </summary>
    public unsafe void SetViewportRect(int x, int y, int width, int height)
    {
        lock (_renderLock)
        {
            if (!IsPipelinesInitialized || _recordingFrameIndex < 0) return;
            if (CurrentWidth == 0 || CurrentHeight == 0) return;

            var extent = _swapchainManager.RenderExtent;
            if (extent.Width == 0 || extent.Height == 0) return;

            var scaleX = extent.Width / (double)CurrentWidth;
            var scaleY = extent.Height / (double)CurrentHeight;

            var px = (int)Math.Round(x * scaleX);
            var py = (int)Math.Round(y * scaleY);
            var pw = (int)Math.Round(width * scaleX);
            var ph = (int)Math.Round(height * scaleY);

            // Clamp INTO the attachment: a viewport that exceeds the framebuffer
            // is VUID-vkCmdSetViewport-x-04821 territory on some drivers and a
            // scissor that does is always invalid.
            px = Math.Clamp(px, 0, (int)extent.Width);
            py = Math.Clamp(py, 0, (int)extent.Height);
            pw = Math.Clamp(pw, 0, (int)extent.Width - px);
            ph = Math.Clamp(ph, 0, (int)extent.Height - py);
            if (pw <= 0 || ph <= 0) return;

            var cmd = _commandBuffers[_activeFrameIndex];
            var viewport = new Viewport
            { X = px, Y = py, Width = pw, Height = ph, MinDepth = 0.0f, MaxDepth = 1.0f };
            _context.Api.CmdSetViewport(cmd, 0, 1, &viewport);
            var scissor = new Rect2D
            { Offset = new Offset2D(px, py), Extent = new Extent2D((uint)pw, (uint)ph) };
            _context.Api.CmdSetScissor(cmd, 0, 1, &scissor);
        }
    }

    private unsafe void CreateFrameCommandPool()
    {
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = _context.GraphicsQueueFamilyIndex,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit
        };
        VulkanCheck.Ok(_context.Api.CreateCommandPool(_context.Device, in poolInfo, null, out _commandPool), "VulkanRenderer.CreateCommandPool");

        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = MaxFramesInFlight
        };

        fixed (CommandBuffer* pCmdBuffers = _commandBuffers)
        {
            VulkanCheck.Ok(_context.Api.AllocateCommandBuffers(_context.Device, in allocInfo, pCmdBuffers), "VulkanRenderer.AllocateCommandBuffers");
        }
    }
}
