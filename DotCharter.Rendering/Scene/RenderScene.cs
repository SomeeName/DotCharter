using DotCharter.Rendering.Data;
using DotCharter.Rendering.Vulkan;

namespace DotCharter.Rendering.Scene;

public class RenderScene
{
    public List<IRenderable> OpaqueElements { get; } = [];
    public List<IRenderable> TransparentElements { get; } = [];
    public List<IRenderable> UiElements { get; } = []; // 3D text and overlays
    public List<IRenderable> HudElements { get; } = []; // True 2D Orthographic UI

    public void Initialize(IVulkanRenderService vulkanRenderer, RenderResources resources)
    {
        foreach (var element in OpaqueElements) element.Initialize(vulkanRenderer, resources);
        foreach (var element in TransparentElements) element.Initialize(vulkanRenderer, resources);
        foreach (var element in UiElements) element.Initialize(vulkanRenderer, resources);
        foreach (var element in HudElements) element.Initialize(vulkanRenderer, resources);
    }

    public void RenderOpaque(RenderContext context)
    {
        foreach (var element in OpaqueElements) element.Render(context);
    }

    public void RenderTransparent(RenderContext context)
    {
        foreach (var element in TransparentElements) element.Render(context);
    }

    public void RenderUi(RenderContext context)
    {
        foreach (var element in UiElements) element.Render(context);
    }

    public void RenderHud(RenderContext context)
    {
        foreach (var element in HudElements) element.Render(context);
    }
}
