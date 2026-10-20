using DotCharter.Rendering.Timeline2D;
using DotCharter.Rendering.Vulkan;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkImage = Silk.NET.Vulkan.Image;

namespace DotCharter.Rendering.Vulkan2D;

/// <summary>
///     Offscreen, windowless Vulkan renderer for the 2D timeline (testing /
///     golden frames) — the composed path's sibling, mirroring
///     <c>HeadlessHighwayRenderer</c>'s structure: an own
///     <see cref="VulkanContext.InitializeDeviceHeadless" /> device (no
///     surface/interop extensions), the same <see cref="Timeline2DVulkanCore" />
///     pass + pipelines the app runs, ONE plain device-local BGRA8 image, and
///     a fence-paced copy into a persistently-mapped readback buffer. Honors
///     <c>DOTCHARTER_VK_VALIDATION=1</c> via the shared context init.
///     <para>
///         Consumes a populated <see cref="TimelineBatcher" /> — the UI-side
///         facade (<c>HeadlessVulkanTimelineRenderer</c>) runs the real layer
///         stack into one. Output is tightly-packed BGRA8, top row first.
///     </para>
/// </summary>
public sealed unsafe class Timeline2DHeadlessRenderer : IDisposable
{
    private readonly VulkanContext _context;
    private readonly Timeline2DVulkanCore _core;
    private readonly Timeline2DFrameResources _frames;

    private VkImage _image;
    private DeviceMemory _imageMemory;
    private ImageView _view;
    private Framebuffer _framebuffer;
    private VkBuffer _readbackBuffer;
    private DeviceMemory _readbackMemory;
    private byte* _readbackMapped;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }

    /// <summary>This renderer's glyph atlas — assign it to the batcher (and
    /// rebuild before batching text) exactly like the GL engine's atlas.</summary>
    public VulkanTimelineFontAtlas FontAtlas { get; }

    public Timeline2DHeadlessRenderer(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Offscreen Vulkan size must be positive.");
        Width = width;
        Height = height;

        _context = new VulkanContext();
        try
        {
            _context.InitializeDeviceHeadless();
            _core = new Timeline2DVulkanCore(_context);
            _frames = new Timeline2DFrameResources(_context);
            FontAtlas = new VulkanTimelineFontAtlas(_context, _core);

            _context.CreateImage((uint)width, (uint)height, Timeline2DVulkanCore.ColorFormat, ImageTiling.Optimal,
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
                MemoryPropertyFlags.DeviceLocalBit, out _image, out _imageMemory);
            _view = _context.CreateImageView(_image, Timeline2DVulkanCore.ColorFormat, ImageAspectFlags.ColorBit);

            var attachment = _view;
            var fbInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _core.RenderPass,
                AttachmentCount = 1,
                PAttachments = &attachment,
                Width = (uint)width,
                Height = (uint)height,
                Layers = 1
            };
            if (_context.Api.CreateFramebuffer(_context.Device, in fbInfo, null, out _framebuffer) != Result.Success)
                throw new Exception("Failed to create the headless 2D framebuffer.");

            // Readback memory prefers HOST_CACHED: the CPU READS this buffer
            // every frame, and reading write-combined (uncached) host memory
            // runs at ~100–500 MB/s — several ms per 900×400 frame, which
            // drowned the GL-vs-Vulkan perf A/B. Cached+coherent exists on
            // every desktop GPU; plain coherent is the correctness fallback.
            var byteCount = (ulong)(width * height * 4);
            try
            {
                _context.CreateBuffer(byteCount, BufferUsageFlags.TransferDstBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit |
                    MemoryPropertyFlags.HostCachedBit,
                    out _readbackBuffer, out _readbackMemory);
            }
            catch (Exception)
            {
                _context.CreateBuffer(byteCount, BufferUsageFlags.TransferDstBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                    out _readbackBuffer, out _readbackMemory);
            }
            void* mapped;
            _context.Api.MapMemory(_context.Device, _readbackMemory, 0, byteCount, 0, &mapped);
            _readbackMapped = (byte*)mapped;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    ///     Renders one batched frame and returns tightly-packed BGRA8, top row
    ///     first (<c>Width*Height*4</c> bytes). <paramref name="reusePixels" />
    ///     of exactly that size makes repeated renders readback-allocation-free
    ///     (perf harness); null allocates fresh (golden tests).
    /// </summary>
    public byte[] Render(TimelineBatcher batcher, float logicalWidth, float logicalHeight,
        float clearR, float clearG, float clearB, byte[]? reusePixels = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var api = _context.Api;

        FontAtlas.RebuildIfNeeded();
        _frames.EnsureCapacity(batcher);
        var (solidBase, generalBase) = _frames.Upload(0, batcher);

        api.WaitForFences(_context.Device, 1, in _frames.Fences[0], true, 5_000_000_000);
        api.ResetFences(_context.Device, 1, in _frames.Fences[0]);

        var cmd = _frames.CommandBuffers[0];
        var begin = new CommandBufferBeginInfo
        { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        api.BeginCommandBuffer(cmd, in begin);

        var clear = new ClearValue
        {
            Color = new ClearColorValue
            { Float32_0 = clearR, Float32_1 = clearG, Float32_2 = clearB, Float32_3 = 1.0f }
        };
        var extent = new Extent2D((uint)Width, (uint)Height);
        var passBegin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _core.RenderPass,
            Framebuffer = _framebuffer,
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

        // The pass left the image in COLOR_ATTACHMENT_OPTIMAL — transition to
        // TRANSFER_SRC and copy into the mapped readback buffer, all in the
        // same submission; the fence proves the pixels are host-visible.
        var toTransfer = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstAccessMask = AccessFlags.TransferReadBit,
            OldLayout = ImageLayout.ColorAttachmentOptimal,
            NewLayout = ImageLayout.TransferSrcOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _image,
            SubresourceRange = new ImageSubresourceRange
            { AspectMask = ImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 }
        };
        api.CmdPipelineBarrier(cmd, PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.TransferBit,
            0, 0, null, 0, null, 1, &toTransfer);

        var region = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, LayerCount = 1 },
            ImageExtent = new Extent3D((uint)Width, (uint)Height, 1)
        };
        api.CmdCopyImageToBuffer(cmd, _image, ImageLayout.TransferSrcOptimal, _readbackBuffer, 1, &region);

        api.EndCommandBuffer(cmd);

        var submit = new SubmitInfo
        { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmd };
        api.QueueSubmit(_context.GraphicsQueue, 1, in submit, _frames.Fences[0]);
        api.WaitForFences(_context.Device, 1, in _frames.Fences[0], true, 5_000_000_000);

        var byteCount = Width * Height * 4;
        var pixels = reusePixels is { } r && r.Length == byteCount ? r : new byte[byteCount];
        fixed (byte* dst = pixels)
        {
            System.Buffer.MemoryCopy(_readbackMapped, dst, byteCount, byteCount);
        }

        return pixels;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_context.IsInitialized)
        {
            var api = _context.Api;
            api.DeviceWaitIdle(_context.Device);
            if (_readbackMapped != null)
            {
                api.UnmapMemory(_context.Device, _readbackMemory);
                _readbackMapped = null;
            }

            if (_readbackBuffer.Handle != 0) api.DestroyBuffer(_context.Device, _readbackBuffer, null);
            if (_readbackMemory.Handle != 0) api.FreeMemory(_context.Device, _readbackMemory, null);
            if (_framebuffer.Handle != 0) api.DestroyFramebuffer(_context.Device, _framebuffer, null);
            if (_view.Handle != 0) api.DestroyImageView(_context.Device, _view, null);
            if (_image.Handle != 0) api.DestroyImage(_context.Device, _image, null);
            if (_imageMemory.Handle != 0) api.FreeMemory(_context.Device, _imageMemory, null);
            FontAtlas?.Dispose();
            _frames?.Dispose();
            _core?.Dispose();
        }

        _context.Dispose();
    }
}
