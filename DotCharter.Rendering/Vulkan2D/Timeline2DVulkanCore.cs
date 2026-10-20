using System.Runtime.InteropServices;
using DotCharter.Rendering.Vulkan;
using Silk.NET.Vulkan;

namespace DotCharter.Rendering.Vulkan2D;

/// <summary>
///     Device-level machinery for the composed-Vulkan 2D timeline (v0.33):
///     the single-attachment BGRA8 render pass and the two pipelines split by
///     shader shape — a compact solid pipeline (24-byte vertex: x, y, r, g,
///     b, a) for the flat-shaded 90 % of frame geometry, and the general
///     pipeline (36-byte vertex: + u, v, type) for circles, ring outlines,
///     AA lines and font glyphs. Blend state comes from
///     <see cref="FrameAlphaPolicy.SceneAttachment" />, the same owner as the
///     3D pipelines': <c>SrcAlpha / OneMinusSrcAlpha</c> on COLOUR — exactly
///     the removed OpenGL path's semantics, which is how output parity was
///     proven at the port (commit 43866e8d) — and the "over" operator on
///     ALPHA. Colour parity is unaffected by the alpha factors; only the alpha
///     half moves, and only in the direction of keeping the frame opaque.
///     <para>
///         One instance per <see cref="VulkanContext" /> — shared by every
///         composed viewport on the process-lifetime 2D device
///         (<see cref="Timeline2DVulkanDevice" />) and owned outright by the
///         headless renderer. Both pipelines end the pass in
///         COLOR_ATTACHMENT_OPTIMAL (never PRESENT_SRC — these images are
///         never presented by Vulkan; the compositor or a copy-out consumes
///         them).
///     </para>
/// </summary>
public sealed unsafe class Timeline2DVulkanCore : IDisposable
{
    public const Format ColorFormat = Format.B8G8R8A8Unorm;

    /// <summary>
    ///     The colour-blend attachment BOTH 2D pipelines are built from — the
    ///     3D pipelines' own, so there is exactly one owner of the alpha rule
    ///     in this assembly and a fix to it cannot cover only half the
    ///     renderers again. Exposed so the rule is pinned in the DEFAULT gate
    ///     (`FrameAlphaPolicyTests`) rather than only on a GPU.
    /// </summary>
    public static PipelineColorBlendAttachmentState BlendAttachment => FrameAlphaPolicy.SceneAttachment();

    /// <summary>Push-constant block shared by both pipelines: the LOGICAL
    /// viewport size the vertex shaders divide pixel positions by (the GL
    /// engine's <c>u_resolution</c>).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PushConstants
    {
        public float ResolutionX;
        public float ResolutionY;
    }

    private readonly VulkanContext _context;
    private bool _disposed;

    public VulkanContext Context => _context;
    public RenderPass RenderPass { get; }
    public Pipeline SolidPipeline { get; private set; }
    public PipelineLayout SolidPipelineLayout { get; private set; }
    public Pipeline GeneralPipeline { get; private set; }
    public PipelineLayout GeneralPipelineLayout { get; private set; }
    public DescriptorSetLayout AtlasSetLayout { get; }
    public Sampler AtlasSampler { get; }

    public Timeline2DVulkanCore(VulkanContext context)
    {
        _context = context;
        RenderPass = CreateRenderPass();
        AtlasSetLayout = CreateAtlasSetLayout();
        AtlasSampler = CreateAtlasSampler();
        CreatePipelines();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var api = _context.Api;
        api.DestroyPipeline(_context.Device, SolidPipeline, null);
        api.DestroyPipeline(_context.Device, GeneralPipeline, null);
        api.DestroyPipelineLayout(_context.Device, SolidPipelineLayout, null);
        api.DestroyPipelineLayout(_context.Device, GeneralPipelineLayout, null);
        api.DestroyDescriptorSetLayout(_context.Device, AtlasSetLayout, null);
        api.DestroySampler(_context.Device, AtlasSampler, null);
        api.DestroyRenderPass(_context.Device, RenderPass, null);
    }

    private RenderPass CreateRenderPass()
    {
        var colorAttachment = new AttachmentDescription
        {
            Format = ColorFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            // Undefined: the frame fully clears, so prior contents (a previous
            // frame, or whatever the pool image held) are never read.
            InitialLayout = ImageLayout.Undefined,
            // COLOR_ATTACHMENT_OPTIMAL — PRESENT_SRC is illegal off-swapchain.
            // Composed images are consumed by the compositor via the keyed
            // mutex; the headless image is barriered to TRANSFER_SRC for the
            // readback copy.
            FinalLayout = ImageLayout.ColorAttachmentOptimal
        };

        var colorRef = new AttachmentReference { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal };
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef
        };

        // EXTERNAL→0: order this frame's layout transition + clear against the
        // previous frame's attachment writes (WAW) and — headless only — the
        // previous readback copy's transfer READ (WAR, execution-only). The
        // composed path's cross-API ordering is the keyed mutex, not this.
        var dependency = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.ColorAttachmentReadBit
        };

        var info = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &colorAttachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency
        };

        if (_context.Api.CreateRenderPass(_context.Device, in info, null, out var pass) != Result.Success)
            throw new Exception("Failed to create the 2D timeline render pass.");
        return pass;
    }

    private DescriptorSetLayout CreateAtlasSetLayout()
    {
        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit
        };
        var info = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding
        };
        if (_context.Api.CreateDescriptorSetLayout(_context.Device, in info, null, out var layout) != Result.Success)
            throw new Exception("Failed to create the 2D atlas descriptor set layout.");
        return layout;
    }

    private Sampler CreateAtlasSampler()
    {
        // Bilinear + clamp-to-edge, single mip level (the atlas is sampled at
        // ~1:1 texel:pixel — the sheet is rasterized at the surface's render
        // scale, so anything beyond bilinear level 0 buys nothing). Clamp is
        // the backstop against out-of-range UVs wrapping onto unrelated glyph
        // rows.
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MinLod = 0f,
            MaxLod = 0f
        };
        if (_context.Api.CreateSampler(_context.Device, in info, null, out var sampler) != Result.Success)
            throw new Exception("Failed to create the 2D atlas sampler.");
        return sampler;
    }

    private void CreatePipelines()
    {
        var solidVert = CreateShaderModule(LoadSpirv("timeline2d_solid.vert.spv"));
        var solidFrag = CreateShaderModule(LoadSpirv("timeline2d_solid.frag.spv"));
        var generalVert = CreateShaderModule(LoadSpirv("timeline2d_general.vert.spv"));
        var generalFrag = CreateShaderModule(LoadSpirv("timeline2d_general.frag.spv"));
        try
        {
            var pushRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.VertexBit,
                Offset = 0,
                Size = (uint)sizeof(PushConstants)
            };

            // Solid: push constants only, no descriptor sets.
            var solidLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushRange
            };
            if (_context.Api.CreatePipelineLayout(_context.Device, in solidLayoutInfo, null, out var solidLayout) !=
                Result.Success)
            {
                throw new Exception("Failed to create the 2D solid pipeline layout.");
            }

            SolidPipelineLayout = solidLayout;

            // General: push constants + the atlas sampler set.
            var setLayout = AtlasSetLayout;
            var generalLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &setLayout,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushRange
            };
            if (_context.Api.CreatePipelineLayout(_context.Device, in generalLayoutInfo, null,
                    out var generalLayout) != Result.Success)
            {
                throw new Exception("Failed to create the 2D general pipeline layout.");
            }

            GeneralPipelineLayout = generalLayout;

            // Solid vertex: x, y | r, g, b, a — 6 floats.
            var solidBinding = new VertexInputBindingDescription
            { Binding = 0, Stride = 6 * sizeof(float), InputRate = VertexInputRate.Vertex };
            var solidAttrs = stackalloc VertexInputAttributeDescription[2];
            solidAttrs[0] = new VertexInputAttributeDescription
            { Binding = 0, Location = 0, Format = Format.R32G32Sfloat, Offset = 0 };
            solidAttrs[1] = new VertexInputAttributeDescription
            { Binding = 0, Location = 1, Format = Format.R32G32B32A32Sfloat, Offset = 8 };
            SolidPipeline = CreatePipeline(solidVert, solidFrag, solidBinding, solidAttrs, 2, SolidPipelineLayout);

            // General vertex: x, y | r, g, b, a | u, v | type — 9 floats.
            var generalBinding = new VertexInputBindingDescription
            { Binding = 0, Stride = 9 * sizeof(float), InputRate = VertexInputRate.Vertex };
            var generalAttrs = stackalloc VertexInputAttributeDescription[4];
            generalAttrs[0] = new VertexInputAttributeDescription
            { Binding = 0, Location = 0, Format = Format.R32G32Sfloat, Offset = 0 };
            generalAttrs[1] = new VertexInputAttributeDescription
            { Binding = 0, Location = 1, Format = Format.R32G32B32A32Sfloat, Offset = 8 };
            generalAttrs[2] = new VertexInputAttributeDescription
            { Binding = 0, Location = 2, Format = Format.R32G32Sfloat, Offset = 24 };
            generalAttrs[3] = new VertexInputAttributeDescription
            { Binding = 0, Location = 3, Format = Format.R32Sfloat, Offset = 32 };
            GeneralPipeline = CreatePipeline(generalVert, generalFrag, generalBinding, generalAttrs, 4,
                GeneralPipelineLayout);
        }
        finally
        {
            _context.Api.DestroyShaderModule(_context.Device, solidVert, null);
            _context.Api.DestroyShaderModule(_context.Device, solidFrag, null);
            _context.Api.DestroyShaderModule(_context.Device, generalVert, null);
            _context.Api.DestroyShaderModule(_context.Device, generalFrag, null);
        }
    }

    private Pipeline CreatePipeline(ShaderModule vert, ShaderModule frag,
        VertexInputBindingDescription binding, VertexInputAttributeDescription* attrs, uint attrCount,
        PipelineLayout layout)
    {
        var entry = Marshal.StringToHGlobalAnsi("main");
        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit,
                Module = vert, PName = (byte*)entry
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit,
                Module = frag, PName = (byte*)entry
            };

            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = attrCount,
                PVertexAttributeDescriptions = attrs
            };

            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
                PrimitiveRestartEnable = false
            };

            var viewportState = new PipelineViewportStateCreateInfo
            { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };

            var rasterizer = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                LineWidth = 1.0f,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise
            };

            var multisampling = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit
            };

            // The COLOUR factors are the removed GL path's exact glBlendFunc
            // semantics, under which output parity was proven at the port
            // (43866e8d) — and colour parity is unaffected by the ALPHA
            // factors, which is what let this ship wrong. SrcAlpha on alpha
            // computes dstA = srcA² + dstA·(1 − srcA), not the "over" operator:
            // from the pass's alpha-1 clear a draw at srcA 0.5 leaves 0.75, so
            // the imported BGRA8 texture Avalonia composes is 25% see-through
            // wherever a translucent layer drew. Same fault, same file shape,
            // as the 3D pipelines' — so the same owner.
            var blendAttachment = BlendAttachment;
            var blend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1,
                PAttachments = &blendAttachment
            };

            var dynamicStates = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamic = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynamicStates
            };

            var info = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &rasterizer,
                PMultisampleState = &multisampling,
                PColorBlendState = &blend,
                PDynamicState = &dynamic,
                Layout = layout,
                RenderPass = RenderPass,
                Subpass = 0
            };

            if (_context.Api.CreateGraphicsPipelines(_context.Device, default, 1, in info, null,
                    out var pipeline) != Result.Success)
            {
                throw new Exception("Failed to create a 2D timeline pipeline.");
            }

            return pipeline;
        }
        finally
        {
            Marshal.FreeHGlobal(entry);
        }
    }

    private ShaderModule CreateShaderModule(byte[] code)
    {
        fixed (byte* pCode = code)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = (uint*)pCode
            };
            if (_context.Api.CreateShaderModule(_context.Device, in info, null, out var module) != Result.Success)
                throw new Exception("Failed to create a 2D shader module.");
            return module;
        }
    }

    private static byte[] LoadSpirv(string shaderFileName)
    {
        // Same embedded-resource convention as the 3D engine:
        // "DotCharter.Rendering.Shaders.<filename>" (DotCharter.Rendering.csproj).
        var resourceName = $"DotCharter.Rendering.Shaders.{shaderFileName}";
        using var stream = typeof(Timeline2DVulkanCore).Assembly.GetManifestResourceStream(resourceName)
                           ?? throw new FileNotFoundException(
                               $"Embedded shader resource not found: '{resourceName}'.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
