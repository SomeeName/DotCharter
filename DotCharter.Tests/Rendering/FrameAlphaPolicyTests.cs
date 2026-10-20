using DotCharter.Rendering.Vulkan;
using DotCharter.Rendering.Vulkan2D;
using Silk.NET.Vulkan;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The 3D frame must come out of the render pass OPAQUE.
///     <para>
///         The shipped scene pipelines blended alpha with
///         <c>(SrcAlpha = One, DstAlpha = Zero)</c>, i.e. <c>dstA = srcA</c>:
///         every translucent draw overwrote the framebuffer's alpha with its own
///         instead of accumulating coverage. Nothing in the colour output moved,
///         so it was invisible for releases — until the presented image was
///         composited with that alpha, at which point the frame was transparent
///         everywhere the vignette's border quads had stamped their 70→0 alpha
///         and opaque only in the untouched middle. That reads as a large
///         hard-edged rectangle at x, y ∈ [0.16, 0.84] of the viewport, plus a
///         translucent rectangle around every note head (each glow quad stamping
///         its own low alpha over its whole rectangle).
///     </para>
///     <para>
///         These are pure evaluations of the Vulkan blend equation, so the rule
///         is pinned in the DEFAULT gate rather than only on a GPU.
///     </para>
/// </summary>
public class FrameAlphaPolicyTests
{
    /// <summary>The shipped scene attachment (opaque + transparent + text
    /// pipelines) can never punch a hole in an opaque frame.</summary>
    [Fact]
    public void TheSceneBlendKeepsAnOpaqueFrameOpaque()
    {
        var scene = FrameAlphaPolicy.SceneAttachment();

        foreach (var srcAlpha in FrameAlphaPolicy.OpacitySweep)
            Assert.Equal(1f, FrameAlphaPolicy.DestinationAlphaAfter(scene, srcAlpha, 1f), 5);

        Assert.True(FrameAlphaPolicy.KeepsFrameOpaque(scene));
    }

    /// <summary>The additive bloom accumulation must hold the same line for any
    /// source alpha — it blends, so the destination is at its mercy.</summary>
    [Fact]
    public void TheAdditiveBloomBlendKeepsAnOpaqueFrameOpaque() => Assert.True(FrameAlphaPolicy.KeepsFrameOpaque(FrameAlphaPolicy.PostAttachment(additive: true)));

    /// <summary>
    ///     The scene composite draw does NOT blend, so it writes its source alpha
    ///     through verbatim — which is safe only because all three post fragment
    ///     shaders (<c>post_blit</c>, <c>post_extract</c>, <c>post_blur</c>) end
    ///     in <c>vec4(rgb, 1.0)</c>. Pinned as the alpha it can actually emit, not
    ///     as a blanket sweep, so the statement stays true rather than
    ///     comfortable.
    /// </summary>
    [Fact]
    public void TheCompositeDrawWritesAnOpaqueFrame()
    {
        var composite = FrameAlphaPolicy.PostAttachment(additive: false);
        Assert.False(composite.BlendEnable);
        Assert.True(FrameAlphaPolicy.KeepsFrameOpaque(composite, [1f]));
        // ...and it really is pass-through: a hypothetical shader emitting 0 here
        // would zero the frame, which is why the shader contract is load-bearing.
        Assert.Equal(0f, FrameAlphaPolicy.DestinationAlphaAfter(composite, 0f, 1f), 5);
    }

    /// <summary>
    ///     The vacuity guard: the exact state that shipped — identical to the
    ///     scene attachment except <c>DstAlphaBlendFactor = Zero</c> — must FAIL
    ///     the same check, and must fail it at the vignette's own 70/255 edge
    ///     alpha. Without this, "keeps the frame opaque" could be satisfied by an
    ///     evaluator that always answers 1.
    /// </summary>
    [Fact]
    public void TheShippedBlendIsRejected()
    {
        var broken = FrameAlphaPolicy.SceneAttachment();
        broken.DstAlphaBlendFactor = BlendFactor.Zero;

        Assert.False(FrameAlphaPolicy.KeepsFrameOpaque(broken));

        // dstA = srcA: the frame's alpha becomes the vignette's edge alpha.
        Assert.Equal(70f / 255f, FrameAlphaPolicy.DestinationAlphaAfter(broken, 70f / 255f, 1f), 5);
        // ...and a fully transparent fragment (the vignette's inner edge, and
        // the clear corners of every note glow quad) zeroes it outright.
        Assert.Equal(0f, FrameAlphaPolicy.DestinationAlphaAfter(broken, 0f, 1f), 5);
    }

    /// <summary>An attachment that does not write alpha at all leaves the frame
    /// alone — the evaluator has to model the write mask, not just the
    /// factors.</summary>
    [Fact]
    public void AnAttachmentThatDoesNotWriteAlphaCannotChangeIt()
    {
        var masked = FrameAlphaPolicy.SceneAttachment();
        masked.DstAlphaBlendFactor = BlendFactor.Zero;
        masked.ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit;

        Assert.True(FrameAlphaPolicy.KeepsFrameOpaque(masked));
        Assert.Equal(0.5f, FrameAlphaPolicy.DestinationAlphaAfter(masked, 0f, 0.5f), 5);
    }

    /// <summary>
    ///     The composed-2D TIMELINE pipelines were the half the 2026-08-01 fix
    ///     missed: they shipped <c>SrcAlpha / OneMinusSrcAlpha</c> on ALPHA too,
    ///     which computes <c>dstA = srcA² + dstA·(1 − srcA)</c> — not the "over"
    ///     operator. From the pass's alpha-1 clear a draw at 0.5 leaves 0.75, and
    ///     <c>HandShapeLayer</c> draws its preview bar at exactly 128/255, so a
    ///     quarter of the backdrop shows through the imported BGRA8 texture
    ///     Avalonia composes.
    /// </summary>
    [Fact]
    public void TheTimeline2DBlendKeepsAnOpaqueFrameOpaque()
    {
        var timeline = Timeline2DVulkanCore.BlendAttachment;

        Assert.True(FrameAlphaPolicy.KeepsFrameOpaque(timeline));
        foreach (var srcAlpha in FrameAlphaPolicy.OpacitySweep)
            Assert.Equal(1f, FrameAlphaPolicy.DestinationAlphaAfter(timeline, srcAlpha, 1f), 5);

        // Colour parity with the removed GL path — the reason the shipped state
        // was justified in the first place — is untouched by the alpha half.
        Assert.Equal(BlendFactor.SrcAlpha, timeline.SrcColorBlendFactor);
        Assert.Equal(BlendFactor.OneMinusSrcAlpha, timeline.DstColorBlendFactor);
        Assert.Equal(BlendOp.Add, timeline.ColorBlendOp);
        Assert.True(timeline.BlendEnable);
    }

    /// <summary>
    ///     The vacuity guard for the case above: the state the 2D core actually
    ///     shipped must FAIL, and must fail at the 128/255 the hand-shape preview
    ///     bar draws with.
    /// </summary>
    [Fact]
    public void TheShippedTimeline2DBlendIsRejected()
    {
        var shipped = Timeline2DVulkanCore.BlendAttachment;
        shipped.SrcAlphaBlendFactor = BlendFactor.SrcAlpha;

        Assert.False(FrameAlphaPolicy.KeepsFrameOpaque(shipped));
        // srcA² + dstA·(1 − srcA): a half-transparent draw leaves 0.75, so a
        // quarter of whatever is behind the timeline shows through.
        Assert.Equal(0.75f, FrameAlphaPolicy.DestinationAlphaAfter(shipped, 0.5f, 1f), 5);
        Assert.Equal(0.75f, FrameAlphaPolicy.DestinationAlphaAfter(shipped, 128f / 255f, 1f), 4);
        // The worst case is srcA = 0.5 exactly; both ends recover, which is why
        // a spot check at one alpha could have missed this entirely.
        Assert.Equal(1f, FrameAlphaPolicy.DestinationAlphaAfter(shipped, 0f, 1f), 5);
        Assert.Equal(1f, FrameAlphaPolicy.DestinationAlphaAfter(shipped, 1f, 1f), 5);
    }

    /// <summary>The colour half is untouched by this change — the fix moves the
    /// alpha channel only, so no pixel colour may move with it.</summary>
    [Fact]
    public void TheColourBlendIsUnchanged()
    {
        var scene = FrameAlphaPolicy.SceneAttachment();
        Assert.Equal(BlendFactor.SrcAlpha, scene.SrcColorBlendFactor);
        Assert.Equal(BlendFactor.OneMinusSrcAlpha, scene.DstColorBlendFactor);
        Assert.Equal(BlendOp.Add, scene.ColorBlendOp);
        Assert.True(scene.BlendEnable);

        var additive = FrameAlphaPolicy.PostAttachment(true);
        Assert.Equal(BlendFactor.One, additive.SrcColorBlendFactor);
        Assert.Equal(BlendFactor.One, additive.DstColorBlendFactor);
        Assert.True(additive.BlendEnable);
        Assert.False(FrameAlphaPolicy.PostAttachment(false).BlendEnable);
    }
}
