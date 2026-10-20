using DotCharter.Rendering.Vulkan.Composed;
using Silk.NET.Vulkan;
using VkImage = Silk.NET.Vulkan.Image;

namespace DotCharter.Rendering.Vulkan;

/// <summary>
///     Manages the output images' downstream resources — views, the shared MSAA
///     color + depth pair, the render-scale/post intermediate, the bloom targets
///     and the framebuffers that tie them to the RenderPass. The output images
///     themselves come from the composed pool (<see cref="RecreateComposed" />) or
///     the headless offscreen image (<see cref="RecreateHeadless" />). The name is
///     historical: there is no <c>VkSwapchainKHR</c> any more — the native
///     child-HWND swapchain path was removed.
/// </summary>
public unsafe class VulkanSwapchainManager : IDisposable
{
    private readonly VulkanContext _context;

    // ONE shared MSAA color + depth pair for ALL framebuffers (0.14 P6):
    // their contents never outlive a render-pass instance (color is resolved
    // into the swapchain image, depth is StoreOp=DontCare) and the render
    // pass's external subpass dependency orders the next frame's layout
    // transitions/clears against the previous frame's attachment writes, so
    // per-swapchain-image copies were pure VRAM waste (~3x at 4x MSAA).
    private VkImage _colorImageMsaa;
    private DeviceMemory _colorImageMsaaMemory;
    private ImageView _colorImageViewMsaa;

    private VkImage _depthImageMsaa;
    private DeviceMemory _depthImageMsaaMemory;
    private ImageView _depthImageViewMsaa;

    // Intermediate color target (0.14.2 blit / 0.15.6 sampled composite): the
    // scene renders into this single-sample image (directly, or as the MSAA
    // resolve target); EndFrame either blits it onto the swapchain image
    // (Blit mode) or samples it in the post/composite chain (Sampled mode).
    // NOT transient — it is stored by the pass and read afterwards. Exists
    // only while the output mode is not Direct.
    private VkImage _intermediateColor;
    private DeviceMemory _intermediateColorMemory;
    private ImageView _intermediateColorView;

    // ---- Post-process resources (0.15.6 / plan 5.5, Sampled mode only) ----
    // Bloom ping-pong mip targets ([0]=half A, [1]=half B, [2]=quarter A,
    // [3]=quarter B) + a descriptor set for sampling the intermediate + one
    // composite framebuffer per swapchain image. All (re)created with the
    // output images (they reference the pipeline manager's static post passes
    // and the output image views), destroyed in CleanupImageResources.
    private PostTargetResources[] _bloomTargets = [];
    private DescriptorSet _intermediateDescriptorSet;
    private Framebuffer[] _compositeFramebuffers = [];
    private VulkanPipelineManager? _postPipelines; // for descriptor-set cleanup
    private bool _isDisposed;

    private struct PostTargetResources
    {
        public VkImage Image;
        public DeviceMemory Memory;
        public ImageView View;
        public Framebuffer Framebuffer;
        public DescriptorSet DescriptorSet;
        public Extent2D Extent;
    }

    /// <summary>One bloom target's draw/sample handles for EndFrame.</summary>
    public readonly record struct PostTargetInfo(Framebuffer Framebuffer, DescriptorSet DescriptorSet,
        Extent2D Extent);

    private VkImage[] _swapchainImages = [];
    private ImageView[] _swapchainImageViews = [];

    // Headless (testing): the single plain device-local BGRA8 image the scene
    // renders into, standing in for a swapchain image. Owned here (unlike the
    // composed path, where the pool owns the images), so it must be destroyed
    // explicitly.
    private VkImage _headlessColorImage;
    private DeviceMemory _headlessColorMemory;

    public VulkanSwapchainManager(VulkanContext context) => _context = context ?? throw new ArgumentNullException(nameof(context));

    public Extent2D Extent { get; private set; }
    public Format ImageFormat { get; private set; }
    public Framebuffer[] Framebuffers { get; private set; } = [];

    /// <summary>The MSAA sample count the live framebuffers were built for
    /// (already clamped by the caller). 1 = no MSAA: no shared transient
    /// color image exists and the color target is rendered directly.</summary>
    public int SampleCount { get; private set; } = MsaaPolicy.DefaultSampleCount;

    /// <summary>True while the BLIT-mode intermediate target is in use: the
    /// pass renders at <see cref="RenderExtent" /> and EndFrame must blit
    /// <see cref="IntermediateColorImage" /> onto the swapchain image.</summary>
    public bool RenderScaleActive { get; private set; }

    /// <summary>True while the SAMPLED-mode post chain is in use (0.15.6):
    /// the scene renders into the intermediate as a sampled texture and
    /// EndFrame must run the bloom/composite passes into the swapchain image.</summary>
    public bool PostProcessActive { get; private set; }

    /// <summary>The extent the render pass actually renders at — equals
    /// <see cref="Extent" /> whenever the output mode is Direct (or the
    /// render scale is 100%). Render area, viewport and scissor must use THIS.</summary>
    public Extent2D RenderExtent { get; private set; }

    /// <summary>The blit source while <see cref="RenderScaleActive" />.</summary>
    public VkImage IntermediateColorImage => _intermediateColor;

    /// <summary>Sampling handle for the scene color while
    /// <see cref="PostProcessActive" />.</summary>
    public DescriptorSet IntermediateColorDescriptorSet => _intermediateDescriptorSet;

    /// <summary>Bloom mip targets while <see cref="PostProcessActive" />:
    /// [0]=half A, [1]=half B (ping-pong), [2]=quarter A, [3]=quarter B.</summary>
    public PostTargetInfo GetBloomTarget(int index)
    {
        var t = _bloomTargets[index];
        return new PostTargetInfo(t.Framebuffer, t.DescriptorSet, t.Extent);
    }

    /// <summary>Composite framebuffer (swapchain image target) while
    /// <see cref="PostProcessActive" />.</summary>
    public Framebuffer GetCompositeFramebuffer(uint imageIndex) => _compositeFramebuffers[imageIndex];

    /// <summary>The presentable image for <paramref name="imageIndex" /> —
    /// the blit destination while <see cref="RenderScaleActive" />.</summary>
    public VkImage GetSwapchainImage(uint imageIndex) => _swapchainImages[imageIndex];

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Cleanup();
    }

    /// <summary>
    ///     Composed-output (re)creation (0.16): no
    ///     surface, no swapchain — the presentable images come from the
    ///     exportable D3D11-backed <paramref name="pool" /> (recreated here at
    ///     the requested pixel size), and everything downstream (MSAA/depth
    ///     pair, intermediate, bloom targets, composite framebuffers, the
    ///     scene framebuffers) is built over them by the same
    ///     <see cref="CreateImageResources" /> the headless path uses. Present mode does not apply — pacing is the
    ///     compositor's (uncapped since Avalonia 12.1 on Windows) plus the
    ///     pool's own back-pressure.
    /// </summary>
    public void RecreateComposed(uint width, uint height, VulkanPipelineManager pipelines,
        int sampleCount, int renderScalePercent, SceneOutputMode outputMode,
        VulkanComposedImagePool pool)
    {
        _postPipelines = pipelines;
        _context.Api.DeviceWaitIdle(_context.Device);
        // Views/framebuffers over the OLD pool images must die before the
        // pool destroys the images themselves.
        CleanupImageResources();

        // Clamp to what the device can actually render to (no surface caps
        // exist to do it for us).
        width = Math.Min(width, _context.MaxRenderTargetWidth);
        height = Math.Min(height, _context.MaxRenderTargetHeight);

        Extent = new Extent2D(width, height);
        ImageFormat = Format.B8G8R8A8Unorm;

        if (width == 0 || height == 0)
        {
            // Zero-size panel (collapsed/minimized): keep no images; the
            // renderer stays in "needs recreation" until a real size arrives.
            pool.Recreate(0, 0);
            return;
        }

        RenderScaleActive = outputMode == SceneOutputMode.Blit;
        PostProcessActive = outputMode == SceneOutputMode.Sampled;
        var scaled = outputMode != SceneOutputMode.Direct && RenderScalePolicy.IsScaled(renderScalePercent);
        RenderExtent = scaled
            ? RenderScalePolicy.ComputeRenderExtent(Extent, renderScalePercent,
                _context.MaxRenderTargetWidth, _context.MaxRenderTargetHeight)
            : Extent;

        pool.Recreate(width, height);
        _swapchainImages = pool.Images;

        CreateImageResources(pipelines, sampleCount);
    }

    /// <summary>
    ///     Headless-output (re)creation (testing): no
    ///     surface, no swapchain, no compositor — the render target is ONE plain
    ///     device-local BGRA8 image created here (usage ColorAttachment |
    ///     TransferDst[blit dest] | TransferSrc[copy-back source]). Everything
    ///     downstream (MSAA/depth pair, intermediate, bloom targets, framebuffers)
    ///     is built over it by the exact same <see cref="CreateImageResources" />
    ///     the composed path uses. <see cref="VulkanRenderer" /> copies this image
    ///     back to a host-visible buffer in EndFrame.
    /// </summary>
    public void RecreateHeadless(uint width, uint height, VulkanPipelineManager pipelines,
        int sampleCount, int renderScalePercent, SceneOutputMode outputMode)
    {
        _postPipelines = pipelines;
        _context.Api.DeviceWaitIdle(_context.Device);
        // Views/framebuffers over the OLD image must die before the image does.
        CleanupImageResources();
        DestroyHeadlessColorImage();

        width = Math.Min(width, _context.MaxRenderTargetWidth);
        height = Math.Min(height, _context.MaxRenderTargetHeight);

        Extent = new Extent2D(width, height);
        ImageFormat = Format.B8G8R8A8Unorm;

        if (width == 0 || height == 0)
            // Zero-size request: keep no image; the renderer stays in "needs
            // recreation" until a real size arrives (mirror of the collapsed-pool
            // path above).
            return;

        RenderScaleActive = outputMode == SceneOutputMode.Blit;
        PostProcessActive = outputMode == SceneOutputMode.Sampled;
        var scaled = outputMode != SceneOutputMode.Direct && RenderScalePolicy.IsScaled(renderScalePercent);
        RenderExtent = scaled
            ? RenderScalePolicy.ComputeRenderExtent(Extent, renderScalePercent,
                _context.MaxRenderTargetWidth, _context.MaxRenderTargetHeight)
            : Extent;

        // The single offscreen color image at the FULL output extent — the
        // stand-in for a swapchain image. All three usages so every output mode
        // works: ColorAttachment (Direct/Sampled composite target), TransferDst
        // (Blit destination), TransferSrc (the copy-to-buffer readback).
        _context.CreateImage(width, height, Format.B8G8R8A8Unorm, ImageTiling.Optimal,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.DeviceLocalBit, out _headlessColorImage, out _headlessColorMemory);
        _swapchainImages = [_headlessColorImage];

        CreateImageResources(pipelines, sampleCount);
    }

    /// <summary>
    ///     Shared second half of <see cref="RecreateComposed" />/<see cref="RecreateHeadless" />:
    ///     builds views + MSAA/depth pair + intermediate + post resources +
    ///     framebuffers over whatever is in <see cref="_swapchainImages" />,
    ///     honoring the Extent/RenderExtent/mode flags the caller set.
    /// </summary>
    private void CreateImageResources(VulkanPipelineManager pipelines, int sampleCount)
    {
        var renderPass = pipelines.RenderPass;
        var usesIntermediate = RenderScaleActive || PostProcessActive;
        var imageCount = _swapchainImages.Length;

        // Attachments live at the RENDER extent (== the output extent
        // whenever render scale is off).
        var targetWidth = RenderExtent.Width;
        var targetHeight = RenderExtent.Height;

        _swapchainImageViews = new ImageView[imageCount];
        Framebuffers = new Framebuffer[imageCount];

        SampleCount = sampleCount;
        var msaaOn = sampleCount > 1;
        var sampleFlag = MsaaPolicy.ToFlag(sampleCount);

        // The shared transient pair (see the field comment): TRANSIENT usage
        // + prefer LAZILY_ALLOCATED memory (free on tiler/UMA hardware);
        // desktop heaps that expose no lazily-allocated type fall back to
        // plain device-local via MemoryTypePolicy, constrained by the
        // image's memoryTypeBits. Without MSAA the color half of the pair
        // does not exist at all (the swapchain image is rendered directly);
        // the depth attachment stays transient at every sample count.
        if (msaaOn)
        {
            _context.CreateImage(targetWidth, targetHeight, Format.B8G8R8A8Unorm, ImageTiling.Optimal,
                ImageUsageFlags.TransientAttachmentBit | ImageUsageFlags.ColorAttachmentBit,
                MemoryPropertyFlags.DeviceLocalBit, out _colorImageMsaa, out _colorImageMsaaMemory,
                sampleFlag, MemoryPropertyFlags.LazilyAllocatedBit);

            _colorImageViewMsaa =
                _context.CreateImageView(_colorImageMsaa, Format.B8G8R8A8Unorm, ImageAspectFlags.ColorBit);
        }

        _context.CreateImage(targetWidth, targetHeight, Format.D32Sfloat, ImageTiling.Optimal,
            ImageUsageFlags.TransientAttachmentBit | ImageUsageFlags.DepthStencilAttachmentBit,
            MemoryPropertyFlags.DeviceLocalBit, out _depthImageMsaa, out _depthImageMsaaMemory,
            sampleFlag, MemoryPropertyFlags.LazilyAllocatedBit);

        _depthImageViewMsaa =
            _context.CreateImageView(_depthImageMsaa, Format.D32Sfloat, ImageAspectFlags.DepthBit);

        // Intermediate: single-sample color at the render extent, rendered/
        // resolved into by the pass (StoreOp Store) and read afterwards —
        // by EndFrame's blit (ColorAttachment|TransferSrc) in Blit mode, or
        // sampled by the post/composite passes (ColorAttachment|Sampled) in
        // Sampled mode. Plain device-local memory (deliberately NOT
        // transient/lazy: its contents outlive the render-pass instance).
        if (usesIntermediate)
        {
            var intermediateUsage = ImageUsageFlags.ColorAttachmentBit |
                                    (PostProcessActive ? ImageUsageFlags.SampledBit : ImageUsageFlags.TransferSrcBit);
            _context.CreateImage(targetWidth, targetHeight, Format.B8G8R8A8Unorm, ImageTiling.Optimal,
                intermediateUsage,
                MemoryPropertyFlags.DeviceLocalBit, out _intermediateColor, out _intermediateColorMemory);

            _intermediateColorView =
                _context.CreateImageView(_intermediateColor, Format.B8G8R8A8Unorm, ImageAspectFlags.ColorBit);
        }

        // Post-process resources (0.15.6, Sampled mode): bloom mip ping-pong
        // targets + sampling sets + per-image composite framebuffers. Bloom
        // targets exist whenever the mode is Sampled (the bloom toggle itself
        // can then flip per frame without a swapchain rebuild).
        if (PostProcessActive && pipelines.IsPostPipelinesInitialized)
        {
            _intermediateDescriptorSet = AllocatePostDescriptorSet(pipelines, _intermediateColorView);

            _bloomTargets = new PostTargetResources[PostProcessPolicy.BloomMipCount * 2];
            for (var mip = 1; mip <= PostProcessPolicy.BloomMipCount; mip++)
            {
                var mipExtent = PostProcessPolicy.BloomMipExtent(RenderExtent, mip);
                for (var pingPong = 0; pingPong < 2; pingPong++)
                {
                    ref var target = ref _bloomTargets[(mip - 1) * 2 + pingPong];
                    target.Extent = mipExtent;
                    _context.CreateImage(mipExtent.Width, mipExtent.Height, Format.B8G8R8A8Unorm,
                        ImageTiling.Optimal, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit,
                        MemoryPropertyFlags.DeviceLocalBit, out target.Image, out target.Memory);
                    target.View = _context.CreateImageView(target.Image, Format.B8G8R8A8Unorm,
                        ImageAspectFlags.ColorBit);
                    target.DescriptorSet = AllocatePostDescriptorSet(pipelines, target.View);

                    var view = target.View;
                    var fbInfo = new FramebufferCreateInfo
                    {
                        SType = StructureType.FramebufferCreateInfo,
                        RenderPass = pipelines.PostRenderPass,
                        AttachmentCount = 1,
                        PAttachments = &view,
                        Width = mipExtent.Width,
                        Height = mipExtent.Height,
                        Layers = 1
                    };
                    VulkanCheck.Ok(_context.Api.CreateFramebuffer(_context.Device, in fbInfo, null, out target.Framebuffer), "VulkanSwapchainManager.CreateFramebuffer");
                }
            }
        }

        // FIX: Moved stackalloc outside the loop to prevent StackOverflowExceptions
        // Attachment layout mirrors VulkanPipelineManager.CreateRenderPass:
        //   MSAA on:  [0] shared MSAA color, [1] shared depth, [2] single-sample target
        //   MSAA off: [0] single-sample target, [1] shared depth
        // where the single-sample target is the per-image swapchain view, or
        // the ONE shared intermediate view while render scale is active (the
        // framebuffers are then identical per image — kept per-image anyway
        // so the BeginFrame indexing stays uniform).
        var attachments = stackalloc ImageView[3];
        uint attachmentCount;
        if (msaaOn)
        {
            attachments[0] = _colorImageViewMsaa;
            attachments[1] = _depthImageViewMsaa;
            attachmentCount = 3;
        }
        else
        {
            attachments[1] = _depthImageViewMsaa;
            attachmentCount = 2;
        }

        if (PostProcessActive) _compositeFramebuffers = new Framebuffer[imageCount];

        for (var i = 0; i < imageCount; i++)
        {
            _swapchainImageViews[i] =
                _context.CreateImageView(_swapchainImages[i], ImageFormat, ImageAspectFlags.ColorBit);

            var singleSampleTarget = usesIntermediate ? _intermediateColorView : _swapchainImageViews[i];
            if (msaaOn) attachments[2] = singleSampleTarget;
            else attachments[0] = singleSampleTarget;

            var framebufferInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = renderPass,
                AttachmentCount = attachmentCount,
                PAttachments = attachments,
                Width = targetWidth,
                Height = targetHeight,
                Layers = 1
            };

            VulkanCheck.Ok(_context.Api.CreateFramebuffer(_context.Device, in framebufferInfo, null, out Framebuffers[i]), "VulkanSwapchainManager.CreateFramebuffer");

            // Composite framebuffer: the fullscreen post draw targets the
            // swapchain image at the FULL surface extent (the composite draw
            // is what upsamples a scaled render).
            if (PostProcessActive)
            {
                var swapchainView = _swapchainImageViews[i];
                var compositeInfo = new FramebufferCreateInfo
                {
                    SType = StructureType.FramebufferCreateInfo,
                    RenderPass = pipelines.CompositeRenderPass,
                    AttachmentCount = 1,
                    PAttachments = &swapchainView,
                    Width = Extent.Width,
                    Height = Extent.Height,
                    Layers = 1
                };
                VulkanCheck.Ok(_context.Api.CreateFramebuffer(_context.Device, in compositeInfo, null,
                    out _compositeFramebuffers[i]), "VulkanSwapchainManager.CreateFramebuffer");
            }
        }
    }

    /// <summary>Allocates + writes one combined-image-sampler set (the shared
    /// post sampler + the given view) from the pipeline manager's pool.</summary>
    private DescriptorSet AllocatePostDescriptorSet(VulkanPipelineManager pipelines, ImageView view)
    {
        var layout = pipelines.DescriptorSetLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = pipelines.DescriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout
        };
        VulkanCheck.Ok(_context.Api.AllocateDescriptorSets(_context.Device, in allocInfo, out var set), "VulkanSwapchainManager.AllocateDescriptorSets");

        var imageInfo = new DescriptorImageInfo
        {
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            ImageView = view,
            Sampler = pipelines.PostSampler
        };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 0,
            DstArrayElement = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = &imageInfo
        };
        _context.Api.UpdateDescriptorSets(_context.Device, 1, &write, 0, null);
        return set;
    }

    private void Cleanup()
    {
        CleanupImageResources();
        DestroyHeadlessColorImage();
    }

    /// <summary>Destroys the headless offscreen color image (no-op on the
    /// composed path, where <see cref="_headlessColorImage" /> is
    /// null). Call only with the device idle and after the framebuffers/views
    /// over it are gone (<see cref="CleanupImageResources" />).</summary>
    private void DestroyHeadlessColorImage()
    {
        if (_headlessColorImage.Handle != 0) _context.Api.DestroyImage(_context.Device, _headlessColorImage, null);
        if (_headlessColorMemory.Handle != 0) _context.Api.FreeMemory(_context.Device, _headlessColorMemory, null);
        _headlessColorImage = default;
        _headlessColorMemory = default;
    }

    /// <summary>
    ///     Destroys the per-image resources (framebuffers, views, MSAA
    ///     color/depth, intermediate, post targets) — never the output images
    ///     themselves (the pool or <see cref="DestroyHeadlessColorImage" /> owns
    ///     those). Requires an idle device.
    /// </summary>
    private void CleanupImageResources()
    {
        for (var i = 0; i < Framebuffers.Length; i++)
        {
            if (Framebuffers[i].Handle != 0) _context.Api.DestroyFramebuffer(_context.Device, Framebuffers[i], null);
            if (_swapchainImageViews[i].Handle != 0)
                _context.Api.DestroyImageView(_context.Device, _swapchainImageViews[i], null);
        }

        // The shared transient MSAA color/depth pair (one instance for all
        // framebuffers — destroy AFTER the framebuffers that reference it).
        if (_colorImageViewMsaa.Handle != 0)
            _context.Api.DestroyImageView(_context.Device, _colorImageViewMsaa, null);
        if (_colorImageMsaa.Handle != 0) _context.Api.DestroyImage(_context.Device, _colorImageMsaa, null);
        if (_colorImageMsaaMemory.Handle != 0)
            _context.Api.FreeMemory(_context.Device, _colorImageMsaaMemory, null);
        if (_depthImageViewMsaa.Handle != 0)
            _context.Api.DestroyImageView(_context.Device, _depthImageViewMsaa, null);
        if (_depthImageMsaa.Handle != 0) _context.Api.DestroyImage(_context.Device, _depthImageMsaa, null);
        if (_depthImageMsaaMemory.Handle != 0)
            _context.Api.FreeMemory(_context.Device, _depthImageMsaaMemory, null);
        // Post-process resources (0.15.6) — composite framebuffers reference
        // the swapchain views and MUST die with them; bloom targets + their
        // descriptor sets follow (the pool has FreeDescriptorSetBit).
        for (var i = 0; i < _compositeFramebuffers.Length; i++)
        {
            if (_compositeFramebuffers[i].Handle != 0)
                _context.Api.DestroyFramebuffer(_context.Device, _compositeFramebuffers[i], null);
        }

        _compositeFramebuffers = [];

        for (var i = 0; i < _bloomTargets.Length; i++)
        {
            ref var target = ref _bloomTargets[i];
            if (target.Framebuffer.Handle != 0)
                _context.Api.DestroyFramebuffer(_context.Device, target.Framebuffer, null);
            if (target.DescriptorSet.Handle != 0 && _postPipelines != null)
            {
                _context.Api.FreeDescriptorSets(_context.Device, _postPipelines.DescriptorPool, 1,
                    in target.DescriptorSet);
            }

            if (target.View.Handle != 0) _context.Api.DestroyImageView(_context.Device, target.View, null);
            if (target.Image.Handle != 0) _context.Api.DestroyImage(_context.Device, target.Image, null);
            if (target.Memory.Handle != 0) _context.Api.FreeMemory(_context.Device, target.Memory, null);
        }

        _bloomTargets = [];

        if (_intermediateDescriptorSet.Handle != 0 && _postPipelines != null)
        {
            _context.Api.FreeDescriptorSets(_context.Device, _postPipelines.DescriptorPool, 1,
                in _intermediateDescriptorSet);
        }

        _intermediateDescriptorSet = default;
        PostProcessActive = false;

        // Intermediate (0.14.2/0.15.6) — same after-the-framebuffers rule.
        if (_intermediateColorView.Handle != 0)
            _context.Api.DestroyImageView(_context.Device, _intermediateColorView, null);
        if (_intermediateColor.Handle != 0) _context.Api.DestroyImage(_context.Device, _intermediateColor, null);
        if (_intermediateColorMemory.Handle != 0)
            _context.Api.FreeMemory(_context.Device, _intermediateColorMemory, null);
        _intermediateColorView = default;
        _intermediateColor = default;
        _intermediateColorMemory = default;
        RenderScaleActive = false;
        _colorImageViewMsaa = default;
        _colorImageMsaa = default;
        _colorImageMsaaMemory = default;
        _depthImageViewMsaa = default;
        _depthImageMsaa = default;
        _depthImageMsaaMemory = default;

        Framebuffers = [];
        _swapchainImageViews = [];
        _swapchainImages = [];
    }
}
