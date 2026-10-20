using DotCharter.Rendering.Data;
using DotCharter.Rendering.Vulkan;

namespace DotCharter.Rendering.Scene;

public interface IRenderable
{
    void Initialize(IVulkanRenderService vulkanRenderer, RenderResources resources);
    void Render(RenderContext context);
}
