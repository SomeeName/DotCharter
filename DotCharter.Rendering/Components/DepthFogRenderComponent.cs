using Avalonia.Media;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Scene;
using DotCharter.Rendering.Vulkan;

namespace DotCharter.Rendering.Components;

/// <summary>
///     0.14.2 C: highway depth fog — a black floor-gradient overlay across the
///     far end of the highway (FadeStartZ → FadeEndZ) that dissolves distant
///     content into the void and masks the hard pop-in at the visibility edge.
///     Drawn LAST in the transparent pass so it layers over everything on the
///     highway; the depth test (against the opaque near fretboard) keeps it
///     from bleeding onto the upright board. Two stacked gradients approximate
///     a smoothstep with plain vertex colors — no shader involvement.
/// </summary>
public class DepthFogRenderComponent : IRenderable
{
    // Slightly wider than the backdrop so the fog never shows side seams.
    private const float XMin = -40f;
    private const float XMax = 70f;

    public void Initialize(IVulkanRenderService vulkanRenderer, RenderResources resources)
    {
    }

    public void Render(RenderContext context)
    {
        if (!context.Snapshot.EnableDepthFog) return;

        var y = FretboardConstants.ChartboardYPosition + 0.02f;
        var zStart = context.FadeStartZ;
        var zEnd = context.FadeEndZ;
        var zMid = zStart + (zEnd - zStart) * 0.55f;

        // Deliberately soft. A far alpha of 235 (92%) would paint the
        // far highway a near-opaque BLACK slab — the "black over
        // the note highway". The backdrop below the horizon is
        // already near-black and the lane lines fade distally, so the fog only
        // needs a gentle darkening cue, not a black wall.
        var clear = Color.FromArgb(0, 0, 0, 0);
        var mid = Color.FromArgb(25, 0, 0, 0);
        var far = Color.FromArgb(70, 0, 0, 0);

        // Near half: barely-there onset; far half: a soft ramp — never opaque.
        context.QuadBatcher.AddSafeFlatQuad(XMin, XMax, zStart, zMid, y, clear, mid);
        context.QuadBatcher.AddSafeFlatQuad(XMin, XMax, zMid, zEnd, y, mid, far);
    }
}
