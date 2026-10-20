using DotCharter.Rendering.Text;
using DotCharter.Rendering.Vulkan;
using Silk.NET.Vulkan;
using SkiaSharp;
using VkImage = Silk.NET.Vulkan.Image;

namespace DotCharter.Rendering.Vulkan2D;

/// <summary>
///     Vulkan upload backend for the shared 2D chart font atlas
///     (<see cref="ChartFontAtlas" /> owns rasterization and every metric —
///     the upload subclass only moves pixels to the GPU). Rebuilds are rare
///     (font change, new glyph registered, render-scale change) and replace
///     the whole sheet: the upload device-idles, replaces the RGBA8 image via
///     a staging copy and points the (single, per-atlas) descriptor set at
///     the new view — legal exactly because nothing is in flight after the
///     idle.
/// </summary>
public sealed unsafe class VulkanTimelineFontAtlas : ChartFontAtlas
{
    private readonly VulkanContext _context;
    private readonly Timeline2DVulkanCore _core;

    private CommandPool _uploadPool;
    private DescriptorPool _descriptorPool;
    private VkImage _image;
    private DeviceMemory _memory;
    private ImageView _view;
    private bool _disposed;

    /// <summary>The combined-image-sampler set the general pipeline binds.
    /// Valid after the first <see cref="ChartFontAtlas.RebuildIfNeeded" />
    /// (the atlas starts dirty, so any frame that draws has one).</summary>
    public DescriptorSet DescriptorSet { get; }

    public VulkanTimelineFontAtlas(VulkanContext context, Timeline2DVulkanCore core)
    {
        _context = context;
        _core = core;
        var api = context.Api;

        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.TransientBit,
            QueueFamilyIndex = context.GraphicsQueueFamilyIndex
        };
        if (api.CreateCommandPool(context.Device, in poolInfo, null, out _uploadPool) != Result.Success)
            throw new Exception("Failed to create the 2D atlas upload command pool.");

        var poolSize = new DescriptorPoolSize
        { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 1 };
        var descPoolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize
        };
        if (api.CreateDescriptorPool(context.Device, in descPoolInfo, null, out _descriptorPool) != Result.Success)
            throw new Exception("Failed to create the 2D atlas descriptor pool.");

        var setLayout = core.AtlasSetLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout
        };
        DescriptorSet set;
        if (api.AllocateDescriptorSets(context.Device, in allocInfo, &set) != Result.Success)
            throw new Exception("Failed to allocate the 2D atlas descriptor set.");
        DescriptorSet = set;
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var api = _context.Api;
        api.DeviceWaitIdle(_context.Device);
        DestroyImage();
        if (_descriptorPool.Handle != 0) api.DestroyDescriptorPool(_context.Device, _descriptorPool, null);
        _descriptorPool = default;
        if (_uploadPool.Handle != 0) api.DestroyCommandPool(_context.Device, _uploadPool, null);
        _uploadPool = default;
    }

    protected override void UploadAtlas(SKBitmap bitmap)
    {
        var api = _context.Api;

        // Frames in flight may still sample the old sheet — idle first, then
        // swap the image under the (now unreferenced) descriptor set.
        api.DeviceWaitIdle(_context.Device);
        DestroyImage();

        _context.CreateImage(AtlasSize, AtlasSize, Format.R8G8B8A8Unorm, ImageTiling.Optimal,
            ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
            MemoryPropertyFlags.DeviceLocalBit, out _image, out _memory);

        var byteCount = (ulong)(AtlasSize * AtlasSize * 4);
        _context.CreateBuffer(byteCount, BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out var staging, out var stagingMemory);
        try
        {
            void* mapped;
            api.MapMemory(_context.Device, stagingMemory, 0, byteCount, 0, &mapped);
            System.Buffer.MemoryCopy((void*)bitmap.GetPixels(), mapped, byteCount, byteCount);
            api.UnmapMemory(_context.Device, stagingMemory);

            var cmdAlloc = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _uploadPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1
            };
            CommandBuffer cmd;
            if (api.AllocateCommandBuffers(_context.Device, in cmdAlloc, &cmd) != Result.Success)
                throw new Exception("Failed to allocate the 2D atlas upload command buffer.");
            try
            {
                var begin = new CommandBufferBeginInfo
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit
                };
                api.BeginCommandBuffer(cmd, in begin);

                var subresource = new ImageSubresourceRange
                { AspectMask = ImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 };

                var toTransfer = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = 0,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    OldLayout = ImageLayout.Undefined,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = _image,
                    SubresourceRange = subresource
                };
                api.CmdPipelineBarrier(cmd, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit,
                    0, 0, null, 0, null, 1, &toTransfer);

                var region = new BufferImageCopy
                {
                    ImageSubresource = new ImageSubresourceLayers
                    { AspectMask = ImageAspectFlags.ColorBit, LayerCount = 1 },
                    ImageExtent = new Extent3D(AtlasSize, AtlasSize, 1)
                };
                api.CmdCopyBufferToImage(cmd, staging, _image, ImageLayout.TransferDstOptimal, 1, &region);

                var toSampled = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.ShaderReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = _image,
                    SubresourceRange = subresource
                };
                api.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit,
                    0, 0, null, 0, null, 1, &toSampled);

                api.EndCommandBuffer(cmd);

                var submit = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers = &cmd
                };
                api.QueueSubmit(_context.GraphicsQueue, 1, in submit, default);
                api.QueueWaitIdle(_context.GraphicsQueue);
            }
            finally
            {
                api.FreeCommandBuffers(_context.Device, _uploadPool, 1, &cmd);
            }
        }
        finally
        {
            api.DestroyBuffer(_context.Device, staging, null);
            api.FreeMemory(_context.Device, stagingMemory, null);
        }

        _view = _context.CreateImageView(_image, Format.R8G8B8A8Unorm, ImageAspectFlags.ColorBit);

        var imageInfo = new DescriptorImageInfo
        {
            Sampler = _core.AtlasSampler,
            ImageView = _view,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal
        };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = DescriptorSet,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &imageInfo
        };
        api.UpdateDescriptorSets(_context.Device, 1, &write, 0, null);
    }

    private void DestroyImage()
    {
        var api = _context.Api;
        if (_view.Handle != 0) api.DestroyImageView(_context.Device, _view, null);
        if (_image.Handle != 0) api.DestroyImage(_context.Device, _image, null);
        if (_memory.Handle != 0) api.FreeMemory(_context.Device, _memory, null);
        _view = default;
        _image = default;
        _memory = default;
    }
}
