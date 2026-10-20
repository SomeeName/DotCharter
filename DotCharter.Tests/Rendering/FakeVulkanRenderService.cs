using System.Numerics;
using DotCharter.Rendering.Vulkan;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     A GPU-free <see cref="IVulkanRenderService" /> for the CPU-side halves of
///     the renderer: the glyph sheet's rebuild bookkeeping and the text
///     batcher's staging. Nothing here touches Vulkan — <see cref="LoadTexture" />
///     hands back a <see cref="VulkanTexture" /> whose handles are all zero, so
///     its <c>Dispose</c> is a no-op that still flips <c>IsDisposed</c>.
/// </summary>
internal sealed class FakeVulkanRenderService : IVulkanRenderService
{
    /// <summary>When set, <see cref="LoadTexture" /> throws it — the VRAM-pressure
    /// case a font-atlas rebuild has to survive without freeing the live sheet.</summary>
    public Exception? LoadTextureThrows { get; set; }

    /// <summary>When true, <see cref="LoadTexture" /> returns <c>null</c>: an
    /// atlas with glyphs but no texture, which is what makes
    /// <c>TextBatcher.Flush</c> decline to draw.</summary>
    public bool LoadTextureReturnsNull { get; set; }

    public int LoadTextureCount { get; private set; }

    /// <summary>The mipmapped flag of every LoadTexture call, in order.</summary>
    public List<bool> LoadTextureMipmapped { get; } = [];
    public int WaitDeviceIdleCount { get; private set; }

    /// <summary>Every <c>DrawTextBatch</c> this service was handed.</summary>
    public List<(int Count, Matrix4x4 Mvp, VulkanTexture Texture)> TextBatches { get; } = [];

    public VulkanTexture LoadTexture(uint width, uint height, byte[] pixels, bool mipmapped = true)
    {
        LoadTextureCount++;
        LoadTextureMipmapped.Add(mipmapped);
        if (LoadTextureThrows != null) throw LoadTextureThrows;
        return LoadTextureReturnsNull ? null! : new VulkanTexture(null!, default);
    }

    /// <summary>The vertical extent (min, max y) of every text batch's vertices.</summary>
    public List<(float MinY, float MaxY)> TextBatchYSpans { get; } = [];

    public void DrawTextBatch(TextVertex[] vertices, int count, Matrix4x4 mvp, VulkanTexture texture)
    {
        TextBatches.Add((count, mvp, texture));
        var min = float.MaxValue;
        var max = float.MinValue;
        for (var i = 0; i < count; i++)
        {
            min = Math.Min(min, vertices[i].Position.Y);
            max = Math.Max(max, vertices[i].Position.Y);
        }

        TextBatchYSpans.Add((min, max));
    }

    public void WaitDeviceIdle() => WaitDeviceIdleCount++;

    // ---- the rest of the contract: inert ---------------------------------

    public bool IsDeviceLost => false;
    public string? DeviceLostReason => null;
    /// <summary>Never raised — a fake device is never lost. The explicit
    /// accessors keep it from being a field the compiler calls unused.</summary>
    public event Action<string>? DeviceLost
    {
        add { }
        remove { }
    }
    public bool SwapchainNeedsRecreation => false;
    public uint CurrentWidth => 0;
    public uint CurrentHeight => 0;
    public bool IsDeviceInitialized => false;
    public bool IsPipelinesInitialized => false;
    public string DeviceName => "fake";
    public int LastDrawCallCount => 0;
    public int LastVertexCount => 0;
    public IReadOnlyList<string> SupportedPresentModes => ["Fifo"];
    public string CurrentPresentMode => "Fifo";
    public IReadOnlyList<int> SupportedMsaaSamples => [1];

    public void SetPresentMode(string presentMode) { }
    public void SetMsaaSamples(int samples) { }
    public void SetRenderScalePercent(int percent) { }
    public void SetPostEffects(bool bloom, bool filmicTonemap) { }
    public void InitializePipelines(byte[] vertSpirv, byte[] fragSpirv) { }
    public void InitializeTextPipeline(byte[] textVertSpirv, byte[] textFragSpirv) { }

    public void InitializePostPipelines(byte[] fullscreenVertSpirv, byte[] extractFragSpirv,
        byte[] blurFragSpirv, byte[] blitFragSpirv)
    { }

    public void Resize(uint width, uint height) { }
    public bool BeginFrame() => false;
    public void SetViewportRect(int x, int y, int width, int height) { }

    /// <summary>Every quad <c>DrawBatch</c> this service was handed, in order:
    /// the staged float count and the texture it was bound with.</summary>
    public List<(int FloatCount, VulkanTexture? Texture)> QuadBatches { get; } = [];

    /// <summary>A copy of the floats of each entry in <see cref="QuadBatches" />,
    /// same order: lets a test read WHERE each batch drew, not only how much.</summary>
    public List<float[]> QuadBatchVertices { get; } = [];

    public void DrawBatch(float[] vertices, int count, Matrix4x4 mvp, Vector3 camPos, float fadeStart,
        float fadeEnd, float timeMs, bool depthWrite, HighwayEffects effects = HighwayEffects.None,
        VulkanTexture? texture = null)
    {
        QuadBatches.Add((count, texture));
        QuadBatchVertices.Add(vertices.AsSpan(0, count).ToArray());
    }

    public void EndFrame() { }

    public void Dispose() { }
}
