using DotCharter.Rendering.Vulkan;

namespace DotCharter.Rendering.Data;

public class RenderResources
{
    public VulkanTexture? NotesTexture { get; set; }
    public VulkanTexture? FingeringTexture { get; set; }
    public VulkanTexture? InlayTexture { get; set; }
    public FontAtlas? FontAtlas { get; set; }
}
