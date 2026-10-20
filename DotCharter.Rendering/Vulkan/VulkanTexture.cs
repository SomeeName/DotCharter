using Silk.NET.Vulkan;

namespace DotCharter.Rendering.Vulkan;

public class VulkanTexture : IDisposable
{
    private readonly Device _device;
    private readonly Vk _vk;
    private DescriptorPool _descriptorPool;
    public DescriptorSet DescriptorSet;

    public Image Image;
    public DeviceMemory Memory;
    public Sampler Sampler;
    public ImageView View;

    public VulkanTexture(Vk vk, Device device)
    {
        _vk = vk;
        _device = device;
    }

    internal void AttachDescriptorPool(DescriptorPool pool) => _descriptorPool = pool;

    /// <summary>True once <see cref="Dispose" /> has freed this texture's
    /// image, view, sampler and descriptor set. Binding it afterwards is a
    /// use-after-free — the property exists so a test can prove a live sheet
    /// SURVIVED a failed rebuild (the fields are not nulled, so an identity
    /// check alone proves nothing).</summary>
    public bool IsDisposed { get; private set; }

    public unsafe void Dispose()
    {
        IsDisposed = true;

        // Free the descriptor set back to the pool. Without this, every
        // texture load consumed one of the pool's MaxSets=1000 slots
        // permanently until the renderer was torn down — which adds up
        // when songs/cover art are loaded and unloaded many times during
        // a single session. Requires the pool to be created with
        // FreeDescriptorSetBit.
        if (DescriptorSet.Handle != 0 && _descriptorPool.Handle != 0)
        {
            var set = DescriptorSet;
            _vk.FreeDescriptorSets(_device, _descriptorPool, 1, in set);
            DescriptorSet = default;
        }

        if (Sampler.Handle != 0) _vk.DestroySampler(_device, Sampler, null);
        if (View.Handle != 0) _vk.DestroyImageView(_device, View, null);
        if (Image.Handle != 0) _vk.DestroyImage(_device, Image, null);
        if (Memory.Handle != 0) _vk.FreeMemory(_device, Memory, null);
    }
}
