using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using VkImage = Silk.NET.Vulkan.Image;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace DotCharter.Rendering.Vulkan;

/// <summary>
///     Manages loading resources like textures into device memory. Uses a transient command pool
///     to orchestrate staging buffer copies.
/// </summary>
public unsafe class VulkanResourceManager : IDisposable
{
    private readonly VulkanContext _context;
    private readonly VulkanPipelineManager _pipelineManager;
    private bool _isDisposed;
    private CommandPool _transferCommandPool;

    public VulkanResourceManager(VulkanContext context, VulkanPipelineManager pipelineManager)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _pipelineManager = pipelineManager ?? throw new ArgumentNullException(nameof(pipelineManager));
        CreateTransferCommandPool();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_transferCommandPool.Handle != 0)
            _context.Api.DestroyCommandPool(_context.Device, _transferCommandPool, null);
    }

    private void CreateTransferCommandPool()
    {
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = _context.GraphicsQueueFamilyIndex,
            Flags = CommandPoolCreateFlags.TransientBit
        };
        VulkanCheck.Ok(_context.Api.CreateCommandPool(_context.Device, in poolInfo, null, out _transferCommandPool), "VulkanResourceManager.CreateCommandPool");
    }

    public VulkanTexture LoadTexture(uint width, uint height, byte[] pixels, bool mipmapped = true)
    {
        var tex = new VulkanTexture(_context.Api, _context.Device);
        ulong imageSize = width * height * 4;

        _context.CreateBuffer(imageSize, BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out var stagingBuffer,
            out var stagingBufferMemory);
        void* data;
        VulkanCheck.Ok(_context.Api.MapMemory(_context.Device, stagingBufferMemory, 0, imageSize, 0, &data), "VulkanResourceManager.MapMemory");
        Marshal.Copy(pixels, 0, (nint)data, pixels.Length);
        _context.Api.UnmapMemory(_context.Device, stagingBufferMemory);

        // A full mip chain when the device can blit-filter the format linearly; one level otherwise.
        var mipLevels = mipmapped && CanBlitLinear(Format.R8G8B8A8Unorm)
            ? TextureFilterPolicy.MipLevels(width, height)
            : 1u;
        _context.CreateImage(width, height, Format.R8G8B8A8Unorm, ImageTiling.Optimal,
            ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            MemoryPropertyFlags.DeviceLocalBit, out tex.Image, out tex.Memory, mipLevels: mipLevels);
        TransitionImageLayout(tex.Image, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, mipLevels);
        CopyBufferToImage(stagingBuffer, tex.Image, width, height);
        GenerateMipmaps(tex.Image, width, height, mipLevels);

        _context.Api.DestroyBuffer(_context.Device, stagingBuffer, null);
        _context.Api.FreeMemory(_context.Device, stagingBufferMemory, null);

        tex.View = _context.CreateImageView(tex.Image, Format.R8G8B8A8Unorm, ImageAspectFlags.ColorBit, mipLevels);

        var samplerInfo = TextureFilterPolicy.Sampler(mipLevels, _context.SamplerAnisotropy);
        VulkanCheck.Ok(_context.Api.CreateSampler(_context.Device, in samplerInfo, null, out tex.Sampler), "VulkanResourceManager.CreateSampler");

        var layout = _pipelineManager.DescriptorSetLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pipelineManager.DescriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout
        };
        VulkanCheck.Ok(_context.Api.AllocateDescriptorSets(_context.Device, in allocInfo, out tex.DescriptorSet), "VulkanResourceManager.AllocateDescriptorSets");
        tex.AttachDescriptorPool(_pipelineManager.DescriptorPool);

        var imageInfo = new DescriptorImageInfo
        { ImageLayout = ImageLayout.ShaderReadOnlyOptimal, ImageView = tex.View, Sampler = tex.Sampler };
        var descriptorWrite = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstSet = tex.DescriptorSet, DstBinding = 0, DstArrayElement = 0,
            DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, PImageInfo = &imageInfo
        };
        _context.Api.UpdateDescriptorSets(_context.Device, 1, in descriptorWrite, 0, null);

        return tex;
    }

    private bool CanBlitLinear(Format format)
    {
        _context.Api.GetPhysicalDeviceFormatProperties(_context.PhysicalDevice, format, out var props);
        const FormatFeatureFlags needed = FormatFeatureFlags.BlitSrcBit | FormatFeatureFlags.BlitDstBit |
                                          FormatFeatureFlags.SampledImageFilterLinearBit;
        return (props.OptimalTilingFeatures & needed) == needed;
    }

    /// <summary>
    ///     Fills levels 1.. of <paramref name="image" /> (level 0 holds the upload, every level
    ///     in TRANSFER_DST) by halving each level into the next with a linear blit, and leaves
    ///     every level SHADER_READ_ONLY.
    /// </summary>
    private void GenerateMipmaps(VkImage image, uint width, uint height, uint mipLevels)
    {
        var commandBuffer = BeginOneShot();
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier, Image = image,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            SubresourceRange = new ImageSubresourceRange
            { AspectMask = ImageAspectFlags.ColorBit, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1 }
        };

        var w = (int)width;
        var h = (int)height;
        for (var level = 1u; level < mipLevels; level++)
        {
            barrier.SubresourceRange.BaseMipLevel = level - 1;
            barrier.OldLayout = ImageLayout.TransferDstOptimal;
            barrier.NewLayout = ImageLayout.TransferSrcOptimal;
            barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
            barrier.DstAccessMask = AccessFlags.TransferReadBit;
            _context.Api.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.TransferBit,
                PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, in barrier);

            var nw = Math.Max(1, w / 2);
            var nh = Math.Max(1, h / 2);
            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, level - 1, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, level, 0, 1)
            };
            blit.SrcOffsets[1] = new Offset3D(w, h, 1);
            blit.DstOffsets[1] = new Offset3D(nw, nh, 1);
            _context.Api.CmdBlitImage(commandBuffer, image, ImageLayout.TransferSrcOptimal, image,
                ImageLayout.TransferDstOptimal, 1, in blit, Filter.Linear);

            barrier.OldLayout = ImageLayout.TransferSrcOptimal;
            barrier.NewLayout = ImageLayout.ShaderReadOnlyOptimal;
            barrier.SrcAccessMask = AccessFlags.TransferReadBit;
            barrier.DstAccessMask = AccessFlags.ShaderReadBit;
            _context.Api.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.TransferBit,
                PipelineStageFlags.FragmentShaderBit, 0, 0, null, 0, null, 1, in barrier);
            w = nw;
            h = nh;
        }

        barrier.SubresourceRange.BaseMipLevel = mipLevels - 1;
        barrier.OldLayout = ImageLayout.TransferDstOptimal;
        barrier.NewLayout = ImageLayout.ShaderReadOnlyOptimal;
        barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
        barrier.DstAccessMask = AccessFlags.ShaderReadBit;
        _context.Api.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.TransferBit,
            PipelineStageFlags.FragmentShaderBit, 0, 0, null, 0, null, 1, in barrier);
        SubmitOneShot(commandBuffer);
    }

    private CommandBuffer BeginOneShot()
    {
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo, Level = CommandBufferLevel.Primary,
            CommandPool = _transferCommandPool, CommandBufferCount = 1
        };
        VulkanCheck.Ok(_context.Api.AllocateCommandBuffers(_context.Device, in allocInfo, out var commandBuffer), "VulkanResourceManager.AllocateCommandBuffers");
        var beginInfo = new CommandBufferBeginInfo
        { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        _context.Api.BeginCommandBuffer(commandBuffer, in beginInfo);
        return commandBuffer;
    }

    private void SubmitOneShot(CommandBuffer commandBuffer)
    {
        _context.Api.EndCommandBuffer(commandBuffer);
        var submitInfo = new SubmitInfo
        { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &commandBuffer };
        _context.Api.QueueSubmit(_context.GraphicsQueue, 1, in submitInfo, default);
        _context.Api.QueueWaitIdle(_context.GraphicsQueue);
        _context.Api.FreeCommandBuffers(_context.Device, _transferCommandPool, 1, in commandBuffer);
    }

    private void TransitionImageLayout(VkImage image, ImageLayout oldLayout, ImageLayout newLayout,
        uint levelCount = 1)
    {
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo, Level = CommandBufferLevel.Primary,
            CommandPool = _transferCommandPool, CommandBufferCount = 1
        };
        VulkanCheck.Ok(_context.Api.AllocateCommandBuffers(_context.Device, in allocInfo, out var commandBuffer), "VulkanResourceManager.AllocateCommandBuffers");
        var beginInfo = new CommandBufferBeginInfo
        { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };

        _context.Api.BeginCommandBuffer(commandBuffer, in beginInfo);

        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier, OldLayout = oldLayout, NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored, Image = image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit, BaseMipLevel = 0, LevelCount = levelCount, BaseArrayLayer = 0,
                LayerCount = 1
            }
        };

        PipelineStageFlags sourceStage, destinationStage;
        if (oldLayout == ImageLayout.Undefined && newLayout == ImageLayout.TransferDstOptimal)
        {
            barrier.SrcAccessMask = 0;
            barrier.DstAccessMask = AccessFlags.TransferWriteBit;
            sourceStage = PipelineStageFlags.TopOfPipeBit;
            destinationStage = PipelineStageFlags.TransferBit;
        }
        else if (oldLayout == ImageLayout.TransferDstOptimal && newLayout == ImageLayout.ShaderReadOnlyOptimal)
        {
            barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
            barrier.DstAccessMask = AccessFlags.ShaderReadBit;
            sourceStage = PipelineStageFlags.TransferBit;
            destinationStage = PipelineStageFlags.FragmentShaderBit;
        }
        else
        {
            throw new Exception("Unsupported layout transition.");
        }

        _context.Api.CmdPipelineBarrier(commandBuffer, sourceStage, destinationStage, 0, 0, null, 0, null, 1,
            in barrier);
        _context.Api.EndCommandBuffer(commandBuffer);

        var submitInfo = new SubmitInfo
        { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &commandBuffer };
        _context.Api.QueueSubmit(_context.GraphicsQueue, 1, in submitInfo, default);
        _context.Api.QueueWaitIdle(_context.GraphicsQueue);
        _context.Api.FreeCommandBuffers(_context.Device, _transferCommandPool, 1, in commandBuffer);
    }

    private void CopyBufferToImage(VkBuffer buffer, VkImage image, uint width, uint height)
    {
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo, Level = CommandBufferLevel.Primary,
            CommandPool = _transferCommandPool, CommandBufferCount = 1
        };
        VulkanCheck.Ok(_context.Api.AllocateCommandBuffers(_context.Device, in allocInfo, out var commandBuffer), "VulkanResourceManager.AllocateCommandBuffers");
        var beginInfo = new CommandBufferBeginInfo
        { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };

        _context.Api.BeginCommandBuffer(commandBuffer, in beginInfo);
        var region = new BufferImageCopy
        {
            BufferOffset = 0, BufferRowLength = 0, BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers
            { AspectMask = ImageAspectFlags.ColorBit, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1 },
            ImageOffset = new Offset3D(0, 0, 0), ImageExtent = new Extent3D(width, height, 1)
        };
        _context.Api.CmdCopyBufferToImage(commandBuffer, buffer, image, ImageLayout.TransferDstOptimal, 1, in region);
        _context.Api.EndCommandBuffer(commandBuffer);

        var submitInfo = new SubmitInfo
        { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &commandBuffer };
        _context.Api.QueueSubmit(_context.GraphicsQueue, 1, in submitInfo, default);
        _context.Api.QueueWaitIdle(_context.GraphicsQueue);
        _context.Api.FreeCommandBuffers(_context.Device, _transferCommandPool, 1, in commandBuffer);
    }
}
