using Silk.NET.Vulkan;

namespace DotCharter.Rendering.Vulkan;

/// <summary>
///     The colour-blend attachment states every 3D pipeline is built from, and
///     the one rule they all have to satisfy: <b>a frame that starts opaque must
///     stay opaque.</b>
///     <para>
///         <b>Why this exists (the "box vignet" artefact
///         around the frame).</b> Scene pipelines with
///         <c>SrcAlphaBlendFactor = One, DstAlphaBlendFactor = Zero</c> break it. On the
///         COLOUR channels that is irrelevant (colour blends
///         SrcAlpha/OneMinusSrcAlpha), but on the ALPHA channel it means
///         <c>dstA = srcA</c> — every blended draw <i>overwrites</i> the
///         framebuffer's alpha with its own source alpha instead of accumulating
///         coverage. The frame's alpha channel therefore ends up holding
///         whatever the last fragment to touch each pixel happened to be drawn
///         with:
///     </para>
///     <list type="bullet">
///         <item>
///             the vignette's four border quads (alpha 70 → 0 inward) stamped
///             alpha 70…0 across the border bands and left the untouched middle
///             at 255, so the frame was transparent everywhere EXCEPT a
///             hard-edged rectangle at the vignette's inner boundary
///             (x, y ∈ [0.16, 0.84] of the viewport);
///         </item>
///         <item>
///             every translucent quad — note-head glows, hit feedback, sustain
///             shimmer — stamped its own low alpha over its whole RECTANGLE, so
///             each note head got a rectangular translucent halo the size of its
///             quad.
///         </item>
///     </list>
///     <para>
///         Neither is visible while the presented image is treated as opaque,
///         which is why this survived for releases and why the headless golden
///         frames never saw it — <c>GoldenFrame</c> only ever read B, G and R.
///         The moment anything composites the frame with its alpha, both
///         artifacts appear at once, and the owner's screenshot reconstructs
///         EXACTLY as <c>rgb + backdrop · (1 − a)</c> from a headless frame of
///         the same scene.
///     </para>
///     <para>
///         The fix is the textbook "over" operator on alpha
///         (<c>dstA = srcA + dstA·(1 − srcA)</c>): starting from the render
///         pass's alpha-1 clear it is 1 for every source alpha, so the frame is
///         opaque by construction no matter what is drawn into it. Colour output
///         is byte-for-byte unchanged — only the alpha channel moves.
///     </para>
/// </summary>
public static class FrameAlphaPolicy
{
    /// <summary>The blend attachment shared by the opaque, transparent and text
    /// scene pipelines: straight-alpha colour blending, "over" on alpha.</summary>
    public static PipelineColorBlendAttachmentState SceneAttachment() => new()
    {
        ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit |
                         ColorComponentFlags.ABit,
        BlendEnable = true,
        SrcColorBlendFactor = BlendFactor.SrcAlpha,
        DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
        ColorBlendOp = BlendOp.Add,
        // NOT (One, Zero) — that overwrites the frame's alpha with each draw's
        // own alpha. See the type remarks.
        SrcAlphaBlendFactor = BlendFactor.One,
        DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
        AlphaBlendOp = BlendOp.Add
    };

    /// <summary>The blend attachment for the post/composite fullscreen pipelines.
    /// <paramref name="additive" /> is the bloom accumulation pass; the scene
    /// composite draw does not blend at all (it fully overwrites the target).
    /// The alpha rule is the same either way: never lower the frame's alpha.</summary>
    public static PipelineColorBlendAttachmentState PostAttachment(bool additive) => new()
    {
        ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit |
                         ColorComponentFlags.ABit,
        BlendEnable = additive,
        SrcColorBlendFactor = BlendFactor.One,
        DstColorBlendFactor = BlendFactor.One,
        ColorBlendOp = BlendOp.Add,
        SrcAlphaBlendFactor = BlendFactor.One,
        DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
        AlphaBlendOp = BlendOp.Add
    };

    /// <summary>
    ///     Evaluates one <see cref="BlendFactor" /> against the alpha channel.
    ///     Only the factors this renderer can legally reach are supported — an
    ///     unsupported one is a hard error rather than a silent 0, because the
    ///     whole point of this type is that the answer is checkable.
    /// </summary>
    private static float Factor(BlendFactor factor, float srcAlpha, float dstAlpha) => factor switch
    {
        BlendFactor.Zero => 0f,
        BlendFactor.One => 1f,
        BlendFactor.SrcAlpha => srcAlpha,
        BlendFactor.OneMinusSrcAlpha => 1f - srcAlpha,
        BlendFactor.DstAlpha => dstAlpha,
        BlendFactor.OneMinusDstAlpha => 1f - dstAlpha,
        _ => throw new NotSupportedException($"Alpha blend factor {factor} is not modelled by FrameAlphaPolicy.")
    };

    /// <summary>
    ///     The destination alpha a single draw leaves behind — the Vulkan alpha
    ///     blend equation, evaluated on the CPU so the invariant below is
    ///     testable without a GPU.
    /// </summary>
    public static float DestinationAlphaAfter(
        in PipelineColorBlendAttachmentState state, float srcAlpha, float dstAlpha)
    {
        // An attachment that does not write alpha cannot change it.
        if ((state.ColorWriteMask & ColorComponentFlags.ABit) == 0) return dstAlpha;
        if (!state.BlendEnable) return srcAlpha;

        var s = srcAlpha * Factor(state.SrcAlphaBlendFactor, srcAlpha, dstAlpha);
        var d = dstAlpha * Factor(state.DstAlphaBlendFactor, srcAlpha, dstAlpha);

        var result = state.AlphaBlendOp switch
        {
            BlendOp.Add => s + d,
            BlendOp.Subtract => s - d,
            BlendOp.ReverseSubtract => d - s,
            BlendOp.Min => Math.Min(s, d),
            BlendOp.Max => Math.Max(s, d),
            _ => throw new NotSupportedException($"Alpha blend op {state.AlphaBlendOp} is not modelled.")
        };

        return Math.Clamp(result, 0f, 1f);
    }

    /// <summary>
    ///     Whether drawing through <paramref name="state" /> can ever punch a
    ///     hole in an already-opaque frame. This is the invariant the whole file
    ///     exists for: the render pass clears alpha to 1, so every pipeline that
    ///     draws into the scene must leave it at 1 for every source alpha its
    ///     fragments can carry.
    ///     <para>
    ///         <paramref name="sourceAlphas" /> defaults to
    ///         <see cref="OpacitySweep" /> — the right model for the SCENE
    ///         pipelines, whose fragments carry arbitrary vertex/texture alpha. A
    ///         pipeline whose shader pins its output alpha (the post/composite
    ///         chain writes <c>vec4(rgb, 1.0)</c> in all three fragment shaders)
    ///         passes the alphas it can actually emit instead; that distinction
    ///         matters because a NON-blending attachment writes its source alpha
    ///         through verbatim.
    ///     </para>
    /// </summary>
    public static bool KeepsFrameOpaque(
        in PipelineColorBlendAttachmentState state, IReadOnlyList<float>? sourceAlphas = null)
    {
        var alphas = sourceAlphas ?? OpacitySweep;
        for (var i = 0; i < alphas.Count; i++)
        {
            if (DestinationAlphaAfter(state, alphas[i], 1f) < 1f - 1e-5f)
                return false;
        }

        return true;
    }

    /// <summary>Source alphas a fragment realistically carries, including 70/255
    /// — the vignette's edge alpha, i.e. the exact value that produced the
    /// owner-reported rectangle.</summary>
    public static readonly float[] OpacitySweep =
    [
        0f, 1f / 255f, 25f / 255f, 44f / 255f, 70f / 255f, 0.25f, 0.5f, 0.75f, 254f / 255f, 1f
    ];
}
