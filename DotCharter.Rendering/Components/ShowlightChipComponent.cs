using System.Numerics;
using Avalonia.Media;
using DotCharter.Rendering.Batching;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Scene;
using DotCharter.Rendering.Vulkan;

namespace DotCharter.Rendering.Components;

/// <summary>
///     The editor 3D preview's show-light chip: a small dark plate in the
///     top-left corner reading <c>Fog: … · Beams: … · Lasers: …</c>. Drawn by the
///     renderer like the rest of the 3D HUD (the preview is a composed GPU image;
///     its overlays are rendered, not Avalonia controls). The text comes
///     resolved and localized in <see cref="FrameSnapshot.ShowlightChipText" />;
///     empty means no chip, which is what Play Mode hands in.
///     <para>
///         Two halves: <see cref="DrawPlate" /> runs in an ortho QUAD pass the
///         engine opens before the HUD text pass, and <see cref="Render" /> (a
///         <c>HudElements</c> entry) stages the text on top of it.
///     </para>
/// </summary>
public sealed class ShowlightChipComponent : IRenderable
{
    private FontAtlas? _atlas;

    public void Initialize(IVulkanRenderService vulkanRenderer, RenderResources resources) =>
        _atlas = resources.FontAtlas;

    public void DrawPlate(QuadBatcher batcher, string text, int height)
    {
        var scale = ShowlightChipLayout.TextScale(height);
        var m = ShowlightChipLayout.Margin(height);
        var w = MeasureWidth(text) * scale + 2f * ShowlightChipLayout.PadX(height);
        var h = ShowlightChipLayout.PlateHeight(height);
        var plate = Color.FromArgb(190, 10, 10, 16);
        batcher.AddQuad(new Vector3(m, m, 1f), new Vector3(m + w, m, 1f),
            new Vector3(m + w, m + h, 1f), new Vector3(m, m + h, 1f), new Vector3(0, 0, -1), plate);
    }

    public void Render(RenderContext context)
    {
        var text = context.Snapshot.ShowlightChipText;
        if (string.IsNullOrEmpty(text)) return;
        var (x, y) = ShowlightChipLayout.TextOrigin(context.ScreenHeight);
        context.TextBatcher.DrawText2D(text, new Vector2(x, y), Colors.White,
            ShowlightChipLayout.TextScale(context.ScreenHeight));
    }

    private float MeasureWidth(string text)
    {
        if (_atlas == null) return 0f;
        var width = 0f;
        foreach (var c in text)
        {
            if (_atlas.TryGetGlyph(c, out var g)) width += g.AdvanceX;
        }

        return width;
    }
}
