using System.Runtime.InteropServices;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Engines;
using DotCharter.Rendering.Vulkan;
using SkiaSharp;

namespace DotCharter.Rendering.Headless;

/// <summary>
///     Test-facing facade that renders the REAL 3D highway
///     (<see cref="Fretboard3DEngine" />, all frame passes) into an offscreen,
///     windowless Vulkan image and returns the pixels on the CPU — no window, no
///     swapchain, no surface, no audio/input device. Used for golden-frame tests.
///     <para>
///         Construct once (creates the device + pipelines), call
///         <see cref="Render(FrameSnapshot,int,int,double,int,byte[])" /> per frame, dispose
///         at the end. Not thread-safe: use one instance per test thread.
///     </para>
///     <para>
///         <b>Determinism:</b> a render eases the anchor-follow camera to
///         convergence before reading pixels, so the framing is stable regardless
///         of call order or the engine's prior state. Everything else the frame
///         depends on is a pure function of the supplied <see cref="FrameSnapshot" />
///         at the fixed <c>timeMs</c> — so callers MUST pin every time-driven field
///         (song ms, and any shimmer/uTime-style effect reads <c>CurrentTimeMs</c>)
///         to get byte-stable output. Wall clock and randomness never enter here.
///     </para>
/// </summary>
public sealed class HeadlessHighwayRenderer : IDisposable
{
    // A large per-frame delta so the camera easing converges in a handful of
    // frames (it drives ONLY the camera ease rate, never song position or any
    // visible content — those come from the snapshot's fixed timeMs). The
    // converged state is frameTime-independent (the ease snaps to its exact
    // target), so this only trades warm-up frames for speed.
    private const float CameraEaseFrameTime = 1.0f;
    private const int DefaultMaxWarmupFrames = 240;

    private readonly Fretboard3DEngine _engine;
    private readonly VulkanRenderer _renderer;
    private bool _disposed;

    /// <param name="useRealChartTextures">
    ///     Leave FALSE for golden frames — they were all captured against the 1×1
    ///     solid-white substitutes, and a texture-less gem is deterministic flat
    ///     vertex colour. Pass TRUE when the frame is going to be LOOKED AT (the
    ///     3D highway harness's <c>--dump</c>): it decodes the real embedded
    ///     note/fingering/inlay sheets, so the picture is the one the user sees
    ///     rather than a white stand-in that reads as a rendering bug.
    /// </param>
    public HeadlessHighwayRenderer(bool useRealChartTextures = false)
    {
        _renderer = new VulkanRenderer();
        _engine = new Fretboard3DEngine(_renderer);
        _engine.InitializeHeadless(useRealChartTextures);
    }

    /// <summary>The GPU the offscreen device was created on.</summary>
    public string DeviceName => _renderer.DeviceName;

    /// <summary>
    ///     Applies the two DEVICE-level graphics settings — MSAA level and render
    ///     scale — that live on <see cref="IVulkanRenderService" /> rather than on
    ///     the snapshot, so an offscreen frame can be rendered at the same quality
    ///     as the live window. Both are clamped by the renderer's own policies and
    ///     ride the deferred recreation on the next
    ///     <see cref="Render(FrameSnapshot,int,int,double,int,byte[])" />; the
    ///     readback size is unaffected (a scaled render is blitted up to the full
    ///     output image, exactly as it is onto a swapchain image).
    ///     <para>
    ///         Bloom and the filmic tonemap are deliberately NOT here: they are
    ///         per-frame <c>FrameSnapshot.EnableBloom</c> /
    ///         <c>EnableFilmicTonemap</c> state the engine already forwards.
    ///     </para>
    /// </summary>
    public void ApplyGraphicsSettings(int msaaSamples, int renderScalePercent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _renderer.SetMsaaSamples(msaaSamples);
        _renderer.SetRenderScalePercent(renderScalePercent);
    }

    /// <summary>
    ///     Renders one deterministic frame of <paramref name="snapshot" /> at
    ///     <paramref name="timeMs" /> song time into a
    ///     <paramref name="width" />×<paramref name="height" /> offscreen image and
    ///     returns the pixels as tightly-packed BGRA8, top row first
    ///     (<c>width*height*4</c> bytes). The camera is eased to convergence first
    ///     so the framing is stable.
    /// </summary>
    public byte[] Render(FrameSnapshot snapshot, int width, int height, double timeMs,
        int maxWarmupFrames = DefaultMaxWarmupFrames, byte[]? reusePixels = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Headless render size must be positive.");

        // Pin the song time so time-driven snapshot reads are deterministic.
        snapshot.CurrentTimeMs = timeMs;

        var rendered = false;
        for (var i = 0; i < maxWarmupFrames; i++)
        {
            if (_engine.RenderFrame(width, height, CameraEaseFrameTime, snapshot)) rendered = true;
            // Stop once at least one frame landed AND the camera settled — the
            // readback buffer then holds the converged frame.
            if (rendered && !_engine.IsCameraAnimating) break;
        }

        if (!rendered)
        {
            throw new InvalidOperationException(
                "Headless frame never rendered — the renderer declined every attempt.");
        }

        // reusePixels (exactly width*height*4) makes repeated renders readback-
        // allocation-free — the perf harness measures the frame path, not the
        // instrument. Golden tests keep the allocate-per-call default.
        var byteCount = (long)width * height * 4;
        var pixels = reusePixels is { } r && r.LongLength == byteCount ? r : new byte[byteCount];
        if (!_renderer.TryReadbackBgra(pixels))
            throw new InvalidOperationException("Headless readback failed after a successful render.");
        return pixels;
    }

    /// <summary>
    ///     Renders a deterministic SPLIT frame — the couch co-op highway — with
    ///     one viewport per supplied snapshot, tiled by
    ///     <see cref="SplitHighwayLayout" />, and returns the whole
    ///     <paramref name="width" />×<paramref name="height" /> image as BGRA8.
    ///     Every view's camera is eased to convergence first, so the framing is
    ///     stable for all players regardless of call order.
    ///     <para>
    ///         This is the evidence path for the split: it drives the SAME
    ///         <c>Fretboard3DEngine</c> multi-viewport frame the app uses, so a
    ///         golden frame proving two distinct non-black halves is proving the
    ///         shipped code.
    ///     </para>
    /// </summary>
    public byte[] RenderSplit(IReadOnlyList<FrameSnapshot> snapshots, int width, int height, double timeMs,
        SplitHighwayOrientation orientation = SplitHighwayOrientation.Auto,
        int maxWarmupFrames = DefaultMaxWarmupFrames, byte[]? reusePixels = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0)
            throw new ArgumentException("A split render needs at least one snapshot.", nameof(snapshots));
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Headless render size must be positive.");

        var rects = new HighwayViewportRect[snapshots.Count];
        var rectCount = SplitHighwayLayout.Compute(width, height, snapshots.Count, orientation, rects);

        // Reuse the viewport objects across warm-up frames: the camera easing
        // lives on them, so a fresh one per frame would never converge.
        if (_splitViewports.Length < rectCount) _splitViewports = new HighwayViewport[rectCount];
        for (var i = 0; i < rectCount; i++)
        {
            _splitViewports[i] ??= new HighwayViewport();
            // Pin the song time so time-driven snapshot reads are deterministic.
            snapshots[i].CurrentTimeMs = timeMs;
            _splitViewports[i].Snapshot = snapshots[i];
            _splitViewports[i].Rect = rects[i];
        }

        var rendered = false;
        for (var i = 0; i < maxWarmupFrames; i++)
        {
            if (_engine.RenderFrame(width, height, CameraEaseFrameTime, _splitViewports, rectCount)) rendered = true;
            if (rendered && !_engine.IsCameraAnimating) break;
        }

        if (!rendered)
        {
            throw new InvalidOperationException(
                "Headless split frame never rendered — the renderer declined every attempt.");
        }

        var byteCount = (long)width * height * 4;
        var pixels = reusePixels is { } r && r.LongLength == byteCount ? r : new byte[byteCount];
        if (!_renderer.TryReadbackBgra(pixels))
            throw new InvalidOperationException("Headless readback failed after a successful split render.");
        return pixels;
    }

    private HighwayViewport[] _splitViewports = [];

    /// <summary>Encodes BGRA8 pixels (as returned by
    /// <see cref="Render(FrameSnapshot,int,int,double,int,byte[])" />) to a PNG file. Handy
    /// for attaching a failing frame to the test output.</summary>
    public static void SavePng(byte[] bgra, int width, int height, string path)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        Marshal.Copy(bgra, 0, bitmap.GetPixels(), Math.Min(bgra.Length, info.BytesSize));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.Dispose();
        _renderer.Dispose();
    }
}
