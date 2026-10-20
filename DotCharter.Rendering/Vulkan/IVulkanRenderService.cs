using System.Numerics;
using System.Runtime.InteropServices;

namespace DotCharter.Rendering.Vulkan;

[StructLayout(LayoutKind.Sequential)]
public struct TextVertex
{
    public Vector3 Position;
    public Vector4 Color;
    public Vector2 TexCoord;
}

public interface IVulkanRenderService : IDisposable
{
    /// <summary>
    ///     Latched once a frame-loop call returns <c>VK_ERROR_DEVICE_LOST</c>
    ///     (a driver reset / TDR). The device is dead and every later call on it
    ///     fails identically, so the renderer STOPS: <c>BeginFrame</c> refuses,
    ///     <c>Resize</c> refuses, and nothing recreates a swapchain on it. A host
    ///     reads this (or subscribes to <see cref="DeviceLost" />) to surface the
    ///     failure instead of showing a permanently black view.
    /// </summary>
    bool IsDeviceLost { get; }

    /// <summary>Human-readable reason of the latched <see cref="IsDeviceLost" />
    /// (call site + Vulkan result), or null while the device is healthy.</summary>
    string? DeviceLostReason { get; }

    /// <summary>
    ///     Raised exactly ONCE, on the render thread and OUTSIDE the render lock,
    ///     when <see cref="IsDeviceLost" /> latches. The argument is
    ///     <see cref="DeviceLostReason" />. A handler must marshal to its own
    ///     thread — it must never call back into the renderer synchronously.
    /// </summary>
    event Action<string>? DeviceLost;

    bool SwapchainNeedsRecreation { get; }
    uint CurrentWidth { get; }
    uint CurrentHeight { get; }
    bool IsDeviceInitialized { get; }
    bool IsPipelinesInitialized { get; }
    string DeviceName { get; }
    int LastDrawCallCount { get; }
    int LastVertexCount { get; }

    /// <summary>Present-mode tokens the settings UI may offer. Always just
    /// ["Fifo"]: there is no surface (composed/headless output only), so no
    /// other mode can apply.</summary>
    IReadOnlyList<string> SupportedPresentModes { get; }

    /// <summary>Token of the live presentation mode — diagnostics only.
    /// "Composed"/"Headless" once initialized, "Fifo" before.</summary>
    string CurrentPresentMode { get; }

    /// <summary>Requests a present mode by config token ("Fifo"/"Mailbox"/
    /// "Immediate"). Recorded only — it has no effect on the composed/headless
    /// paths (the native swapchain it applied to was removed). Safe to call
    /// before initialization and from any thread.</summary>
    void SetPresentMode(string presentMode);

    /// <summary>MSAA levels (1 = off) from the offered 1/2/4/8 set that the
    /// device's framebuffer limits support; the full set before the device
    /// exists.</summary>
    IReadOnlyList<int> SupportedMsaaSamples { get; }

    /// <summary>Requests an MSAA level (1 = off); applied on the next
    /// deferred recreation — a level change rebuilds render pass, pipelines
    /// and swapchain resources. Clamped to device support. Safe to call
    /// before initialization and from any thread.</summary>
    void SetMsaaSamples(int samples);

    /// <summary>Requests a render scale in percent of the panel size
    /// (clamped 50–200). 100 = direct render into the swapchain image (the
    /// zero-cost classic path); anything else renders into a scaled
    /// intermediate target blitted (linear) onto the swapchain image.
    /// Applied on the next deferred recreation. Safe to call before
    /// initialization and from any thread.</summary>
    void SetRenderScalePercent(int percent);

    /// <summary>Requests the per-frame post effects (0.15.6 / plan 5.5):
    /// bloom and/or the filmic tonemap, both running on the sampled composite
    /// path. Flag flips while the path is active are free; enabling/disabling
    /// the whole chain rides the deferred swapchain recreation. Safe to call
    /// before initialization and from any thread.</summary>
    void SetPostEffects(bool bloom, bool filmicTonemap);

    void InitializePipelines(byte[] vertSpirv, byte[] fragSpirv);
    void InitializeTextPipeline(byte[] textVertSpirv, byte[] textFragSpirv);

    /// <summary>Builds the post-process passes/pipelines (bloom extract/blur,
    /// composite). Call once after <see cref="InitializeTextPipeline" />;
    /// required before <see cref="SetPostEffects" /> can take effect.</summary>
    void InitializePostPipelines(byte[] fullscreenVertSpirv, byte[] extractFragSpirv, byte[] blurFragSpirv,
        byte[] blitFragSpirv);
    /// <summary>
    ///     Uploads an RGBA texture. <paramref name="mipmapped" /> textures get a full mip chain
    ///     and anisotropic filtering (<see cref="TextureFilterPolicy" />); the glyph atlas opts
    ///     out, because averaging its thin strokes fades far text.
    /// </summary>
    VulkanTexture LoadTexture(uint width, uint height, byte[] pixels, bool mipmapped = true);
    void Resize(uint width, uint height);
    bool BeginFrame();

    /// <summary>
    ///     Restricts subsequent draws in the current frame to a sub-rectangle of
    ///     the render target, given in the REQUESTED panel space the engine
    ///     renders against (the implementation rescales onto the actual render
    ///     extent). This is what makes the couch co-op SPLIT HIGHWAY possible:
    ///     one begun frame, one depth buffer, one present — N scene recordings,
    ///     each clipped to its own player's half. No-ops outside a begun frame
    ///     or on a degenerate rect.
    /// </summary>
    void SetViewportRect(int x, int y, int width, int height);

    void DrawBatch(float[] vertices, int count, Matrix4x4 mvp, Vector3 camPos, float fadeStart, float fadeEnd,
        float timeMs, bool depthWrite, HighwayEffects effects = HighwayEffects.None, VulkanTexture? texture = null);

    void DrawTextBatch(TextVertex[] vertices, int count, Matrix4x4 mvp, VulkanTexture texture);
    void EndFrame();

    // NEW: Allow us to sync the CPU and GPU before destroying resources
    void WaitDeviceIdle();
}
