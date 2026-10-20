using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace DotCharter.Rendering.Vulkan;

public unsafe class VulkanPipelineManager : IDisposable
{
    private readonly VulkanContext _context;
    private bool _isDisposed;
    private PipelineCache _pipelineCache;

    // SPIR-V retained so RebuildFor can recreate the scene/text pipelines
    // when the MSAA level changes (0.14.2) — they bake the sample count
    // into PipelineMultisampleStateCreateInfo, so a level change is a full
    // render-pass + pipeline rebuild, not just a swapchain recreate. The
    // 0.15.6 post/composite pipelines are static single-sample and survive.
    private byte[]? _highwayVertCode;
    private byte[]? _highwayFragCode;
    private byte[]? _textVertCode;
    private byte[]? _textFragCode;

    /// <summary>The sample count the live render pass + pipelines were built
    /// for. 1 = no MSAA (direct render into the swapchain image, no resolve
    /// attachment). Callers must clamp via <see cref="MsaaPolicy" /> first.</summary>
    public int CurrentSampleCount { get; private set; } = MsaaPolicy.DefaultSampleCount;

    /// <summary>Where the scene pass leaves its single-sample color output
    /// (0.14.2 blit / 0.15.6 sampled composite). Load/store ops and layouts
    /// do not affect render-pass compatibility, so flipping the mode rebuilds
    /// ONLY the render pass — the pipelines stay valid.</summary>
    public SceneOutputMode CurrentOutputMode { get; private set; } = SceneOutputMode.Direct;

    public VulkanPipelineManager(VulkanContext context) => _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <summary>
    ///     The folder the pipeline cache blob is kept in, set once by the host at
    ///     startup (<c>DotCharter.UI.Program</c>: <c>%LOCALAPPDATA%\DotCharter</c>
    ///     through <c>UserDataFolders</c>, so a data-root override moves it).
    ///     Null — every host that does not set it, the tests included — keeps the
    ///     cache in memory only: this assembly never picks a user folder itself.
    /// </summary>
    public static string? PipelineCacheDirectory { get; set; }

    private static string? PipelineCachePath =>
        PipelineCacheDirectory is { Length: > 0 } dir ? Path.Combine(dir, PipelineCacheFileName) : null;

    internal const string PipelineCacheFileName = "vulkan-pipeline.cache";

    public RenderPass RenderPass { get; private set; }
    public DescriptorSetLayout DescriptorSetLayout { get; private set; }
    public DescriptorPool DescriptorPool { get; private set; }

    public PipelineLayout PipelineLayout { get; private set; }
    public Pipeline PipelineOpaque { get; private set; }
    public Pipeline PipelineTransparent { get; private set; }

    public PipelineLayout PipelineLayoutText { get; private set; }
    public Pipeline PipelineText { get; private set; }

    // ---- Post-process chain (0.15.6 / plan 5.5) -----------------------------
    // Two STATIC single-sample render passes (never rebuilt — independent of
    // MSAA level and output mode) + four fullscreen-triangle pipelines and a
    // shared linear-clamp sampler for the post targets.
    /// <summary>Offscreen pass for the bloom mip targets: one B8G8R8A8 color
    /// attachment, LoadOp DontCare (fully overwritten), final ShaderReadOnly.</summary>
    public RenderPass PostRenderPass { get; private set; }

    /// <summary>Composite pass writing the output image, final ColorAttachmentOptimal.</summary>
    public RenderPass CompositeRenderPass { get; private set; }

    public PipelineLayout PipelineLayoutPost { get; private set; }
    public Pipeline PipelinePostExtract { get; private set; }
    public Pipeline PipelinePostBlur { get; private set; }
    public Pipeline PipelineCompositeScene { get; private set; }
    public Pipeline PipelineCompositeAdd { get; private set; }
    public Sampler PostSampler { get; private set; }
    public bool IsPostPipelinesInitialized { get; private set; }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (DescriptorPool.Handle != 0) _context.Api.DestroyDescriptorPool(_context.Device, DescriptorPool, null);
        if (DescriptorSetLayout.Handle != 0)
            _context.Api.DestroyDescriptorSetLayout(_context.Device, DescriptorSetLayout, null);
        if (PipelineOpaque.Handle != 0) _context.Api.DestroyPipeline(_context.Device, PipelineOpaque, null);
        if (PipelineTransparent.Handle != 0) _context.Api.DestroyPipeline(_context.Device, PipelineTransparent, null);
        if (PipelineLayout.Handle != 0) _context.Api.DestroyPipelineLayout(_context.Device, PipelineLayout, null);
        if (PipelineText.Handle != 0) _context.Api.DestroyPipeline(_context.Device, PipelineText, null);
        if (PipelineLayoutText.Handle != 0)
            _context.Api.DestroyPipelineLayout(_context.Device, PipelineLayoutText, null);
        if (PipelinePostExtract.Handle != 0) _context.Api.DestroyPipeline(_context.Device, PipelinePostExtract, null);
        if (PipelinePostBlur.Handle != 0) _context.Api.DestroyPipeline(_context.Device, PipelinePostBlur, null);
        if (PipelineCompositeScene.Handle != 0)
            _context.Api.DestroyPipeline(_context.Device, PipelineCompositeScene, null);
        if (PipelineCompositeAdd.Handle != 0)
            _context.Api.DestroyPipeline(_context.Device, PipelineCompositeAdd, null);
        if (PipelineLayoutPost.Handle != 0)
            _context.Api.DestroyPipelineLayout(_context.Device, PipelineLayoutPost, null);
        if (PostSampler.Handle != 0) _context.Api.DestroySampler(_context.Device, PostSampler, null);
        if (PostRenderPass.Handle != 0) _context.Api.DestroyRenderPass(_context.Device, PostRenderPass, null);
        if (CompositeRenderPass.Handle != 0)
            _context.Api.DestroyRenderPass(_context.Device, CompositeRenderPass, null);
        if (RenderPass.Handle != 0) _context.Api.DestroyRenderPass(_context.Device, RenderPass, null);
        if (_pipelineCache.Handle != 0) _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
    }

    public void Initialize(byte[] vertCode, byte[] fragCode, int sampleCount = MsaaPolicy.DefaultSampleCount,
        SceneOutputMode outputMode = SceneOutputMode.Direct)
    {
        _highwayVertCode = vertCode;
        _highwayFragCode = fragCode;
        CurrentSampleCount = sampleCount;
        CurrentOutputMode = outputMode;
        CreatePipelineCache();
        CreateRenderPass();
        CreateDescriptorSetLayout();
        CreateGraphicsPipelines(vertCode, fragCode);
    }

    public void InitializeTextPipeline(byte[] textVertCode, byte[] textFragCode)
    {
        _textVertCode = textVertCode;
        _textFragCode = textFragCode;
        CreateTextGraphicsPipelines(textVertCode, textFragCode);
        // All four pipelines exist now — persist immediately. Waiting for
        // Dispose would never save: the app exits via TerminateProcess by
        // design (VST teardown access-violates), skipping managed teardown.
        SavePipelineCache();
    }

    /// <summary>
    ///     Rebuilds the scene render pass (and, when the sample count changed,
    ///     the scene/text pipelines and their layouts) for the requested MSAA
    ///     level and output mode from the retained SPIR-V (0.14.2). An
    ///     output-mode-only flip keeps the pipelines: two render passes whose
    ///     attachments differ only in load/store ops and layouts are
    ///     COMPATIBLE, and pipeline ↔ render-pass validity is defined by
    ///     compatibility, not identity. The post/composite passes + pipelines
    ///     (0.15.6) are always single-sample against their own static passes
    ///     and never rebuild here. The descriptor set layout/pool and the
    ///     pipeline cache always survive — textures keep their descriptor
    ///     sets. Caller contract: device idle, no frame in flight (the
    ///     deferred Resize path), and the swapchain framebuffers must be
    ///     rebuilt against the NEW render pass right after.
    /// </summary>
    public void RebuildFor(int sampleCount, SceneOutputMode outputMode)
    {
        if (sampleCount == CurrentSampleCount && outputMode == CurrentOutputMode) return;
        if (_highwayVertCode == null || _highwayFragCode == null) return;

        var pipelinesNeedRebuild = sampleCount != CurrentSampleCount;

        if (RenderPass.Handle != 0) _context.Api.DestroyRenderPass(_context.Device, RenderPass, null);
        RenderPass = default;
        CurrentSampleCount = sampleCount;
        CurrentOutputMode = outputMode;
        CreateRenderPass();

        if (!pipelinesNeedRebuild) return;

        if (PipelineOpaque.Handle != 0) _context.Api.DestroyPipeline(_context.Device, PipelineOpaque, null);
        if (PipelineTransparent.Handle != 0) _context.Api.DestroyPipeline(_context.Device, PipelineTransparent, null);
        if (PipelineLayout.Handle != 0) _context.Api.DestroyPipelineLayout(_context.Device, PipelineLayout, null);
        if (PipelineText.Handle != 0) _context.Api.DestroyPipeline(_context.Device, PipelineText, null);
        if (PipelineLayoutText.Handle != 0)
            _context.Api.DestroyPipelineLayout(_context.Device, PipelineLayoutText, null);
        PipelineOpaque = default;
        PipelineTransparent = default;
        PipelineLayout = default;
        PipelineText = default;
        PipelineLayoutText = default;

        CreateGraphicsPipelines(_highwayVertCode, _highwayFragCode);
        if (_textVertCode != null && _textFragCode != null)
            CreateTextGraphicsPipelines(_textVertCode, _textFragCode);
        // New sample-count variants are worth caching for the next launch.
        SavePipelineCache();
    }

    /// <summary>
    ///     Seeds the driver's pipeline cache from disk so shader compilation on
    ///     later launches is a lookup. Every failure degrades to an empty (or
    ///     no) cache — the cache is never a source of truth. Drivers validate
    ///     the blob's header (vendor/device id) themselves; a stale blob from
    ///     another GPU is ignored, and a create failure retries uncached.
    /// </summary>
    private void CreatePipelineCache()
    {
        byte[]? seed = null;
        try
        {
            if (PipelineCachePath is { } path && File.Exists(path)) seed = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            // Unreadable cache file — start empty.
        }

        fixed (byte* pSeed = seed)
        {
            var info = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo,
                InitialDataSize = (nuint)(seed?.Length ?? 0),
                PInitialData = pSeed
            };
            if (_context.Api.CreatePipelineCache(_context.Device, in info, null, out _pipelineCache) ==
                Result.Success)
            {
                return;
            }
        }

        var empty = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
        if (_context.Api.CreatePipelineCache(_context.Device, in empty, null, out _pipelineCache) != Result.Success)
            _pipelineCache = default; // pipelines still build, just uncached
    }

    private void SavePipelineCache()
    {
        if (_pipelineCache.Handle == 0 || PipelineCachePath is not { } path) return;
        try
        {
            nuint size = 0;
            _context.Api.GetPipelineCacheData(_context.Device, _pipelineCache, ref size, null);
            if (size == 0) return;
            var data = new byte[size];
            fixed (byte* pData = data)
            {
                _context.Api.GetPipelineCacheData(_context.Device, _pipelineCache, ref size, pData);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }
        catch (Exception)
        {
            // Cache-only concern; never let persistence break rendering init.
        }
    }

    private void CreateRenderPass()
    {
        // Two structures, keyed on CurrentSampleCount (0.14.2 MSAA setting):
        //   >1 sample — [0] transient MSAA color (resolved → [2]),
        //               [1] transient depth, [2] single-sample resolve target.
        //   1 sample  — [0] single-sample color rendered DIRECTLY (Store, no
        //               resolve attachment), [1] transient depth.
        // With a non-Direct output mode the single-sample color output is the
        // INTERMEDIATE image instead of the swapchain image:
        //   Blit (render scale, 0.14.2)  — final TRANSFER_SRC_OPTIMAL for the
        //     CmdBlitImage in EndFrame; second (subpass → EXTERNAL) dependency
        //     orders color/resolve writes against the transfer read.
        //   Sampled (post chain, 0.15.6) — final SHADER_READ_ONLY_OPTIMAL for
        //     the bloom-extract / composite fragment sampling; the second
        //     dependency targets the fragment-shader read instead.
        // VulkanSwapchainManager mirrors the same attachment layout when it
        // builds framebuffers, and VulkanRenderer's clear-value array indexes
        // color=[0] / depth=[1] in both shapes.
        var samples = MsaaPolicy.ToFlag(CurrentSampleCount);
        var msaaOn = CurrentSampleCount > 1;
        var outputLayout = CurrentOutputMode switch
        {
            SceneOutputMode.Blit => ImageLayout.TransferSrcOptimal,
            SceneOutputMode.Sampled => ImageLayout.ShaderReadOnlyOptimal,
            // Direct: the color target is never a swapchain image — an
            // exported D3D11-backed image (composed, 0.16: the keyed-mutex
            // release orders the writes against the compositor's read) or the
            // headless offscreen image (the copy barrier + fence order the
            // readback). PresentSrc is only legal for swapchain images, and
            // nothing presents from Vulkan.
            _ => ImageLayout.ColorAttachmentOptimal
        };

        // StoreOp=DontCare on the MSAA color attachment (0.14 P6): it is
        // resolved into attachment 2 within this pass and never sampled
        // afterwards, so storing it only burned resolve-store bandwidth —
        // and it would force actual backing for the shared TRANSIENT /
        // lazily-allocated image. LoadOp stays Clear. Without MSAA the color
        // attachment IS the output image: StoreOp must be Store and the final
        // layout the output layout above.
        var colorAttachment = new AttachmentDescription
        {
            Format = Format.B8G8R8A8Unorm, Samples = samples, LoadOp = AttachmentLoadOp.Clear,
            StoreOp = msaaOn ? AttachmentStoreOp.DontCare : AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare, InitialLayout = ImageLayout.Undefined,
            FinalLayout = msaaOn ? ImageLayout.ColorAttachmentOptimal : outputLayout
        };

        var colorAttachmentRef = new AttachmentReference
        { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal };

        // Depth matches the color sample count (a subpass renders all its
        // attachments at one rate); stays transient/DontCare at every level.
        var depthAttachment = new AttachmentDescription
        {
            Format = Format.D32Sfloat, Samples = samples, LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare, StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare, InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal
        };

        var depthAttachmentRef = new AttachmentReference
        { Attachment = 1, Layout = ImageLayout.DepthStencilAttachmentOptimal };

        var colorAttachmentResolve = new AttachmentDescription
        {
            Format = Format.B8G8R8A8Unorm, Samples = SampleCountFlags.Count1Bit, LoadOp = AttachmentLoadOp.DontCare,
            StoreOp = AttachmentStoreOp.Store, StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare, InitialLayout = ImageLayout.Undefined,
            FinalLayout = outputLayout
        };

        var colorAttachmentRefResolve = new AttachmentReference
        { Attachment = 2, Layout = ImageLayout.ColorAttachmentOptimal };

        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics, ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachmentRef, PDepthStencilAttachment = &depthAttachmentRef,
            PResolveAttachments = msaaOn ? &colorAttachmentRefResolve : null
        };

        // The external dependency must cover the DEPTH attachment too: its
        // LoadOp.Clear is a depth write, and with 2 frames in flight the
        // previous frame's fragment tests on the same per-image depth buffer
        // may still be executing when this pass's layout transition + clear
        // start (write-after-write hazard the color-only dependency left
        // unsynchronized — the sync-validation gap from the 0.19 research).
        var dependencies = stackalloc SubpassDependency[2];
        dependencies[0] = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal, DstSubpass = 0,
            // In blit/sampled mode the src scope additionally covers the
            // consuming stage (TRANSFER for the blit, FRAGMENT_SHADER for the
            // post-chain sampling): the intermediate color target is ONE
            // shared image across frames in flight (unlike per-index
            // swapchain images, which the acquire semaphore serializes), so
            // frame N+1's layout transition + color/resolve write must wait
            // for frame N's READ of the same image. Write-after-read needs
            // only the execution dependency — no extra src access mask.
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit |
                           PipelineStageFlags.LateFragmentTestsBit |
                           (CurrentOutputMode == SceneOutputMode.Blit ? PipelineStageFlags.TransferBit : 0) |
                           (CurrentOutputMode == SceneOutputMode.Sampled ? PipelineStageFlags.FragmentShaderBit : 0),
            SrcAccessMask = AccessFlags.DepthStencilAttachmentWriteBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit |
                           PipelineStageFlags.LateFragmentTestsBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit
        };
        var dependencyCount = 1u;
        if (CurrentOutputMode == SceneOutputMode.Blit)
        {
            // Blit mode: EndFrame reads the color output with CmdBlitImage in
            // the same command buffer. Color attachment writes AND multisample
            // resolve writes both happen in the COLOR_ATTACHMENT_OUTPUT stage
            // with COLOR_ATTACHMENT_WRITE access, so one outgoing dependency
            // covers either shape; it also orders the finalLayout transition
            // to TRANSFER_SRC_OPTIMAL before the transfer read.
            dependencies[dependencyCount++] = new SubpassDependency
            {
                SrcSubpass = 0, DstSubpass = Vk.SubpassExternal,
                SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
                DstStageMask = PipelineStageFlags.TransferBit,
                DstAccessMask = AccessFlags.TransferReadBit
            };
        }
        else if (CurrentOutputMode == SceneOutputMode.Sampled)
        {
            // Sampled mode (0.15.6): the bloom extract / composite passes
            // sample the color output in the same command buffer — order the
            // color/resolve writes (and the ShaderReadOnly finalLayout
            // transition) before the fragment-shader read.
            dependencies[dependencyCount++] = new SubpassDependency
            {
                SrcSubpass = 0, DstSubpass = Vk.SubpassExternal,
                SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
                DstStageMask = PipelineStageFlags.FragmentShaderBit,
                DstAccessMask = AccessFlags.ShaderReadBit
            };
        }

        var attachments = stackalloc AttachmentDescription[3];
        attachments[0] = colorAttachment;
        attachments[1] = depthAttachment;
        var attachmentCount = 2u;
        if (msaaOn) attachments[attachmentCount++] = colorAttachmentResolve;

        var renderPassInfo = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo, AttachmentCount = attachmentCount,
            PAttachments = attachments,
            SubpassCount = 1, PSubpasses = &subpass, DependencyCount = dependencyCount,
            PDependencies = dependencies
        };

        VulkanCheck.Ok(_context.Api.CreateRenderPass(_context.Device, in renderPassInfo, null, out var renderPass), "VulkanPipelineManager.CreateRenderPass");
        RenderPass = renderPass;
    }

    private void CreateDescriptorSetLayout()
    {
        var samplerLayoutBinding = new DescriptorSetLayoutBinding
        {
            Binding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler,
            StageFlags = ShaderStageFlags.FragmentBit
        };

        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 1, PBindings = &samplerLayoutBinding
        };

        VulkanCheck.Ok(_context.Api.CreateDescriptorSetLayout(_context.Device, in layoutInfo, null, out var descriptorSetLayout), "VulkanPipelineManager.CreateDescriptorSetLayout");
        DescriptorSetLayout = descriptorSetLayout;

        var poolSize = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 1000 };

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo, PoolSizeCount = 1, PPoolSizes = &poolSize, MaxSets = 1000,
            // Required so VulkanTexture.Dispose can return its descriptor
            // set to the pool. Without this flag, freeing individual sets
            // is not permitted and the pool eventually runs out.
            Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit
        };

        VulkanCheck.Ok(_context.Api.CreateDescriptorPool(_context.Device, in poolInfo, null, out var descriptorPool), "VulkanPipelineManager.CreateDescriptorPool");
        DescriptorPool = descriptorPool;
    }

    private Pipeline CreateGraphicsPipelineInternal(
        ShaderModule vertModule, ShaderModule fragModule,
        VertexInputBindingDescription bindingDescription,
        VertexInputAttributeDescription* attributeDescriptions, uint attributeCount,
        PipelineLayout layout, bool depthWriteEnable, bool depthTestEnable = true)
    {
        var vertStageInfo = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit,
            Module = vertModule, PName = (byte*)Marshal.StringToHGlobalAnsi("main")
        };

        var fragStageInfo = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit,
            Module = fragModule, PName = (byte*)Marshal.StringToHGlobalAnsi("main")
        };

        var shaderStages = stackalloc PipelineShaderStageCreateInfo[] { vertStageInfo, fragStageInfo };

        var vertexInputInfo = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo, VertexBindingDescriptionCount = 1,
            PVertexBindingDescriptions = &bindingDescription, VertexAttributeDescriptionCount = attributeCount,
            PVertexAttributeDescriptions = attributeDescriptions
        };

        var inputAssembly = new PipelineInputAssemblyStateCreateInfo
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList,
            PrimitiveRestartEnable = false
        };

        var viewportState = new PipelineViewportStateCreateInfo
        { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };

        var rasterizer = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo, DepthClampEnable = false,
            RasterizerDiscardEnable = false, PolygonMode = PolygonMode.Fill, LineWidth = 1.0f,
            CullMode = CullModeFlags.None, FrontFace = FrontFace.CounterClockwise
        };

        var multisampling = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo, SampleShadingEnable = false,
            RasterizationSamples = MsaaPolicy.ToFlag(CurrentSampleCount)
        };

        // Shared by the opaque, transparent AND text pipelines. The alpha half
        // is NOT free-form: see FrameAlphaPolicy — (One, Zero) on alpha lets
        // every translucent draw overwrite the frame's alpha with its own, which
        // is what put a hard-edged rectangle at the vignette's inner boundary
        // and a translucent halo around every note head.
        var colorBlendAttachment = FrameAlphaPolicy.SceneAttachment();

        var colorBlending = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo, LogicOpEnable = false, AttachmentCount = 1,
            PAttachments = &colorBlendAttachment
        };

        var dynamicStates = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
        var dynamicStateInfo = new PipelineDynamicStateCreateInfo
        {
            SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynamicStates
        };

        var depthStencil = new PipelineDepthStencilStateCreateInfo
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo, DepthTestEnable = depthTestEnable,
            DepthWriteEnable = depthWriteEnable,
            DepthCompareOp = CompareOp.LessOrEqual
        };

        var pipelineInfo = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo, StageCount = 2, PStages = shaderStages,
            PVertexInputState = &vertexInputInfo, PInputAssemblyState = &inputAssembly, PViewportState = &viewportState,
            PRasterizationState = &rasterizer, PMultisampleState = &multisampling,
            PDepthStencilState = &depthStencil, PColorBlendState = &colorBlending,
            PDynamicState = &dynamicStateInfo, Layout = layout, RenderPass = RenderPass, Subpass = 0
        };

        VulkanCheck.Ok(_context.Api.CreateGraphicsPipelines(_context.Device, _pipelineCache, 1, in pipelineInfo, null,
            out var pipeline), "VulkanPipelineManager.CreateGraphicsPipelines");

        Marshal.FreeHGlobal((nint)vertStageInfo.PName);
        Marshal.FreeHGlobal((nint)fragStageInfo.PName);

        return pipeline;
    }

    private void CreateGraphicsPipelines(byte[] vertCode, byte[] fragCode)
    {
        var vertModule = CreateShaderModule(vertCode);
        var fragModule = CreateShaderModule(fragCode);

        var bindingDescription = new VertexInputBindingDescription
        { Binding = 0, Stride = 13 * sizeof(float), InputRate = VertexInputRate.Vertex };

        var attributeDescriptions = stackalloc VertexInputAttributeDescription[5];
        attributeDescriptions[0] = new VertexInputAttributeDescription
        { Binding = 0, Location = 0, Format = Format.R32G32B32Sfloat, Offset = 0 };
        attributeDescriptions[1] = new VertexInputAttributeDescription
        { Binding = 0, Location = 1, Format = Format.R32G32B32A32Sfloat, Offset = 12 };
        attributeDescriptions[2] = new VertexInputAttributeDescription
        { Binding = 0, Location = 2, Format = Format.R32G32B32Sfloat, Offset = 28 };
        attributeDescriptions[3] = new VertexInputAttributeDescription
        { Binding = 0, Location = 3, Format = Format.R32G32Sfloat, Offset = 40 };
        attributeDescriptions[4] = new VertexInputAttributeDescription
        { Binding = 0, Location = 4, Format = Format.R32Sfloat, Offset = 48 };

        var pushConstantRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, Offset = 0,
            Size = (uint)sizeof(HighwayPushConstants)
        };

        var setLayout = DescriptorSetLayout;
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &setLayout,
            PushConstantRangeCount = 1, PPushConstantRanges = &pushConstantRange
        };

        VulkanCheck.Ok(_context.Api.CreatePipelineLayout(_context.Device, in pipelineLayoutInfo, null, out var pipelineLayout), "VulkanPipelineManager.CreatePipelineLayout");
        PipelineLayout = pipelineLayout;

        PipelineOpaque = CreateGraphicsPipelineInternal(vertModule, fragModule, bindingDescription,
            attributeDescriptions, 5, PipelineLayout, true);
        PipelineTransparent = CreateGraphicsPipelineInternal(vertModule, fragModule, bindingDescription,
            attributeDescriptions, 5, PipelineLayout, false);

        _context.Api.DestroyShaderModule(_context.Device, vertModule, null);
        _context.Api.DestroyShaderModule(_context.Device, fragModule, null);
    }

    private void CreateTextGraphicsPipelines(byte[] vertCode, byte[] fragCode)
    {
        var vertModule = CreateShaderModule(vertCode);
        var fragModule = CreateShaderModule(fragCode);

        var bindingDescription = new VertexInputBindingDescription
        { Binding = 0, Stride = 9 * sizeof(float), InputRate = VertexInputRate.Vertex };

        var attributeDescriptions = stackalloc VertexInputAttributeDescription[3];
        attributeDescriptions[0] = new VertexInputAttributeDescription
        { Binding = 0, Location = 0, Format = Format.R32G32B32Sfloat, Offset = 0 };
        attributeDescriptions[1] = new VertexInputAttributeDescription
        { Binding = 0, Location = 1, Format = Format.R32G32B32A32Sfloat, Offset = 12 };
        attributeDescriptions[2] = new VertexInputAttributeDescription
        { Binding = 0, Location = 2, Format = Format.R32G32Sfloat, Offset = 28 };

        var pushConstantRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, Offset = 0,
            Size = (uint)sizeof(TextPushConstants)
        };

        var setLayout = DescriptorSetLayout;
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &setLayout,
            PushConstantRangeCount = 1, PPushConstantRanges = &pushConstantRange
        };

        VulkanCheck.Ok(_context.Api.CreatePipelineLayout(_context.Device, in pipelineLayoutInfo, null, out var pipelineLayoutText), "VulkanPipelineManager.CreatePipelineLayout");
        PipelineLayoutText = pipelineLayoutText;

        // Overlay text is depth-INDEPENDENT (no test, no write). Everything drawn
        // through this pipeline is an overlay recorded AFTER the opaque and
        // transparent scene passes: the UI pass's on-board fret numbers
        // (TextOverlayComponent, sitting a hair above the board's top face, always
        // camera-facing — nothing draws text below/behind the board plane) and the
        // HUD pass's screen-space section/phrase/tone/lyric/timing labels + render
        // stats. None of it should ever be occluded by scene geometry, yet with
        // DepthTestEnable the HUD labels (ortho z=0 → depth 0.5) lost the depth
        // fight against nearer highway/fog geometry and had their glyph bottoms
        // clipped ("text renders behind the graphics"). Disabling the test — the
        // write was already off — makes the overlay win unconditionally. RebuildFor
        // recreates this pipeline via CreateTextGraphicsPipelines, so MSAA/mode
        // changes keep the same state.
        PipelineText = CreateGraphicsPipelineInternal(vertModule, fragModule, bindingDescription, attributeDescriptions,
            3, PipelineLayoutText, depthWriteEnable: false, depthTestEnable: false);

        _context.Api.DestroyShaderModule(_context.Device, vertModule, null);
        _context.Api.DestroyShaderModule(_context.Device, fragModule, null);
    }

    /// <summary>
    ///     Builds the 0.15.6 post-process chain: the two static single-sample
    ///     render passes, the shared post pipeline layout (existing sampler
    ///     descriptor set layout + <see cref="PostPushConstants" />), the
    ///     linear-clamp sampler and the four fullscreen-triangle pipelines
    ///     (threshold extract, separable blur, opaque scene composite,
    ///     additive bloom composite). Independent of MSAA level and output
    ///     mode — never rebuilt after creation.
    /// </summary>
    public void InitializePostPipelines(byte[] fullscreenVertCode, byte[] extractFragCode, byte[] blurFragCode,
        byte[] blitFragCode)
    {
        if (IsPostPipelinesInitialized) return;

        PostRenderPass = CreateSingleColorRenderPass(ImageLayout.ShaderReadOnlyOptimal);
        // The composite targets an exported/offscreen image, never a presentable one.
        CompositeRenderPass = CreateSingleColorRenderPass(ImageLayout.ColorAttachmentOptimal);
        CreatePostSampler();
        CreatePostPipelineLayout();

        var vertModule = CreateShaderModule(fullscreenVertCode);
        var extractModule = CreateShaderModule(extractFragCode);
        var blurModule = CreateShaderModule(blurFragCode);
        var blitModule = CreateShaderModule(blitFragCode);

        PipelinePostExtract = CreatePostPipelineInternal(vertModule, extractModule, PostRenderPass, false);
        PipelinePostBlur = CreatePostPipelineInternal(vertModule, blurModule, PostRenderPass, false);
        PipelineCompositeScene = CreatePostPipelineInternal(vertModule, blitModule, CompositeRenderPass, false);
        PipelineCompositeAdd = CreatePostPipelineInternal(vertModule, blitModule, CompositeRenderPass, true);

        _context.Api.DestroyShaderModule(_context.Device, vertModule, null);
        _context.Api.DestroyShaderModule(_context.Device, extractModule, null);
        _context.Api.DestroyShaderModule(_context.Device, blurModule, null);
        _context.Api.DestroyShaderModule(_context.Device, blitModule, null);

        IsPostPipelinesInitialized = true;
        SavePipelineCache();
    }

    /// <summary>
    ///     One single-sample B8G8R8A8 color attachment, LoadOp DontCare (every
    ///     pass overwrites the full target), no depth. Dependencies serialize
    ///     the chained fullscreen passes: EXTERNAL→0 orders this pass's write
    ///     after any previous fragment-shader read of the same image (WAR —
    ///     post targets are shared across frames in flight; for the composite
    ///     pass the src scope also covers the acquire-semaphore wait stage),
    ///     0→EXTERNAL orders the write (and the finalLayout transition) before
    ///     the next pass's fragment read / the present.
    /// </summary>
    private RenderPass CreateSingleColorRenderPass(ImageLayout finalLayout)
    {
        var colorAttachment = new AttachmentDescription
        {
            Format = Format.B8G8R8A8Unorm, Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.DontCare, StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare, StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined, FinalLayout = finalLayout
        };

        var colorRef = new AttachmentReference { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal };
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics, ColorAttachmentCount = 1, PColorAttachments = &colorRef
        };

        var dependencies = stackalloc SubpassDependency[2];
        dependencies[0] = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal, DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = 0,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit
        };
        dependencies[1] = new SubpassDependency
        {
            SrcSubpass = 0, DstSubpass = Vk.SubpassExternal,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstStageMask = PipelineStageFlags.FragmentShaderBit,
            DstAccessMask = AccessFlags.ShaderReadBit
        };

        var info = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo, AttachmentCount = 1, PAttachments = &colorAttachment,
            SubpassCount = 1, PSubpasses = &subpass, DependencyCount = 2, PDependencies = dependencies
        };

        VulkanCheck.Ok(_context.Api.CreateRenderPass(_context.Device, in info, null, out var pass), "VulkanPipelineManager.CreateRenderPass");
        return pass;
    }

    private void CreatePostSampler()
    {
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear, MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = 0f
        };
        VulkanCheck.Ok(_context.Api.CreateSampler(_context.Device, in samplerInfo, null, out var sampler), "VulkanPipelineManager.CreateSampler");
        PostSampler = sampler;
    }

    private void CreatePostPipelineLayout()
    {
        var pushConstantRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.FragmentBit, Offset = 0, Size = (uint)sizeof(PostPushConstants)
        };

        var setLayout = DescriptorSetLayout;
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &setLayout,
            PushConstantRangeCount = 1, PPushConstantRanges = &pushConstantRange
        };
        VulkanCheck.Ok(_context.Api.CreatePipelineLayout(_context.Device, in layoutInfo, null, out var layout), "VulkanPipelineManager.CreatePipelineLayout");
        PipelineLayoutPost = layout;
    }

    /// <summary>Fullscreen-triangle pipeline: no vertex input, no depth,
    /// single sample. <paramref name="additiveBlend" /> selects ONE/ONE
    /// additive blending (bloom composite) vs opaque overwrite.</summary>
    private Pipeline CreatePostPipelineInternal(ShaderModule vertModule, ShaderModule fragModule,
        RenderPass renderPass, bool additiveBlend)
    {
        var vertStageInfo = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit,
            Module = vertModule, PName = (byte*)Marshal.StringToHGlobalAnsi("main")
        };
        var fragStageInfo = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit,
            Module = fragModule, PName = (byte*)Marshal.StringToHGlobalAnsi("main")
        };
        var shaderStages = stackalloc PipelineShaderStageCreateInfo[] { vertStageInfo, fragStageInfo };

        var vertexInputInfo = new PipelineVertexInputStateCreateInfo
        { SType = StructureType.PipelineVertexInputStateCreateInfo };

        var inputAssembly = new PipelineInputAssemblyStateCreateInfo
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList,
            PrimitiveRestartEnable = false
        };

        var viewportState = new PipelineViewportStateCreateInfo
        { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };

        var rasterizer = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo, DepthClampEnable = false,
            RasterizerDiscardEnable = false, PolygonMode = PolygonMode.Fill, LineWidth = 1.0f,
            CullMode = CullModeFlags.None, FrontFace = FrontFace.CounterClockwise
        };

        var multisampling = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo, SampleShadingEnable = false,
            RasterizationSamples = SampleCountFlags.Count1Bit
        };

        // The additive bloom accumulation must not be able to lower the
        // composited frame's alpha either — same rule, same policy.
        var colorBlendAttachment = FrameAlphaPolicy.PostAttachment(additiveBlend);

        var colorBlending = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo, LogicOpEnable = false, AttachmentCount = 1,
            PAttachments = &colorBlendAttachment
        };

        var dynamicStates = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
        var dynamicStateInfo = new PipelineDynamicStateCreateInfo
        {
            SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynamicStates
        };

        var pipelineInfo = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo, StageCount = 2, PStages = shaderStages,
            PVertexInputState = &vertexInputInfo, PInputAssemblyState = &inputAssembly,
            PViewportState = &viewportState, PRasterizationState = &rasterizer, PMultisampleState = &multisampling,
            PColorBlendState = &colorBlending, PDynamicState = &dynamicStateInfo,
            Layout = PipelineLayoutPost, RenderPass = renderPass, Subpass = 0
        };

        VulkanCheck.Ok(_context.Api.CreateGraphicsPipelines(_context.Device, _pipelineCache, 1, in pipelineInfo, null,
            out var pipeline), "VulkanPipelineManager.CreateGraphicsPipelines");

        Marshal.FreeHGlobal((nint)vertStageInfo.PName);
        Marshal.FreeHGlobal((nint)fragStageInfo.PName);

        return pipeline;
    }

    private ShaderModule CreateShaderModule(byte[] code)
    {
        fixed (byte* pCode = code)
        {
            var createInfo = new ShaderModuleCreateInfo
            { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)code.Length, PCode = (uint*)pCode };
            VulkanCheck.Ok(_context.Api.CreateShaderModule(_context.Device, in createInfo, null, out var shaderModule), "VulkanPipelineManager.CreateShaderModule");
            return shaderModule;
        }
    }
}
