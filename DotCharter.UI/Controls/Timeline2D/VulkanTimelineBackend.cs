using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using DotCharter.Rendering.Timeline2D;
using DotCharter.Rendering.Text;
using DotCharter.Rendering.Vulkan2D;
using DotCharter.UI.Controls.Themes;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.UI.Controls.Timeline2D;

/// <summary>
///     The composed-Vulkan presentation backend for <see cref="ChartRenderer" />
///     (v0.33; since the v0.33 OpenGL removal the ONLY backend): frames render
///     on the shared 2D Vulkan device (<see cref="Timeline2DVulkanDevice" />)
///     into D3D11 keyed-mutex shared images that Avalonia's compositor
///     composes in-airspace — overlays, popups and the marquee keep layering
///     above the timeline.
///     <para>
///         Pacing: <c>TopLevel.RequestAnimationFrame</c> — one build+submit
///         per compositor tick, so the damage-driven discipline (idle
///         timeline renders nothing) is preserved. Everything runs on the UI
///         thread; the shared <see cref="ComposedPresentPump" /> imports each
///         submitted image and completes it back to the pool (exactly one
///         completion per frame — the composed invariant).
///     </para>
///     <para>
///         Lifetime: initialized on viewport attach (async — the compositor
///         interop is awaited), fully torn down on detach; pop-out /
///         re-attach rebuilds it against the new TopLevel's compositor. ANY
///         initialization failure funnels into <see cref="Fail" />: on a real
///         compositor the owner shows a visible, localized error surface
///         (never a silent blank timeline); under the Avalonia.Headless test
///         platform — where no GPU compositor exists by design — the control
///         degrades to a no-op render instead.
///     </para>
/// </summary>
internal sealed class VulkanTimelineBackend
{
    private readonly ChartRenderer _owner;
    private readonly Action<TimeSpan> _onAnimationFrame;

    private CompositionSurfaceVisual? _visual;
    private CompositionDrawingSurface? _surface;
    private ComposedPresentPump? _pump;
    private Timeline2DComposedRenderer? _renderer;

    private bool _ready;
    private bool _detached;
    private bool _framePending;
    private float _lastLogicalWidth;
    private float _lastLogicalHeight;
    private double _lastVisualWidth = -1;
    private double _lastVisualHeight = -1;

    // The compositor's adapter LUID survives re-attach (pop-out / redock):
    // the shared device keeps running, so later attaches only rebuild the
    // per-viewport resources. Same caching pattern as the composed 3D canvas.
    private static byte[]? _cachedAdapterLuid;
    private static bool _loggedActive;

    /// <summary>True when the Avalonia.Headless test platform is driving the
    /// UI — no GPU compositor exists there by design, so composed-init
    /// failure is EXPECTED and must stay a silent no-op (the headless UI
    /// tests attach real ChartRenderers). On the real app the assembly is
    /// never loaded, so a failure always surfaces the error state.</summary>
    private static readonly Lazy<bool> IsHeadlessTestPlatform = new(static () =>
        AppDomain.CurrentDomain.GetAssemblies().Any(static a =>
            a.GetName().Name?.StartsWith("Avalonia.Headless", StringComparison.Ordinal) == true));

    public VulkanTimelineBackend(ChartRenderer owner)
    {
        _owner = owner;
        _onAnimationFrame = OnAnimationFrame;
    }

    public ChartFontAtlas? FontAtlas => _renderer?.FontAtlas;

    public bool IsReady => _ready;

    /// <summary>Monotonic skip counters from the per-viewport renderer
    /// (fence-timeout / pool-exhausted) for the env-gated [2DStats] log
    /// line. Zeros while presentation is unavailable.</summary>
    internal (long FenceSkips, long PoolSkips) SkipCounters
        => _renderer is { } r ? (r.FenceTimeoutSkips, r.PoolExhaustedSkips) : (0L, 0L);

    public void RequestFrame()
    {
        if (_framePending || !_ready || _detached) return;
        var topLevel = TopLevel.GetTopLevel(_owner);
        if (topLevel == null) return;
        _framePending = true;
        topLevel.RequestAnimationFrame(_onAnimationFrame);
    }

    private void OnAnimationFrame(TimeSpan _)
    {
        _framePending = false;
        if (!_ready || _detached) return;
        _owner.RenderTimelineFrame(this);
    }

    public bool BeginFrame(double logicalWidth, double logicalHeight, double renderScaling)
    {
        var renderer = _renderer;
        if (renderer == null || !_ready) return false;

        // Size the swapchain and the visual TOGETHER, off one pixel count, so
        // the compositor's destination rectangle is exactly as many pixels as
        // the surface has texels (ComposedSurfaceGeometry explains why the old
        // ceil(bounds x scale) + Size=bounds pair stopped being equivalent the
        // day a fractional shell scale became the default).
        var size = ComposedSurfaceGeometry.Measure(logicalWidth, logicalHeight, renderScaling);
        SyncVisual(size, renderScaling);
        renderer.Resize((uint)size.PixelWidth, (uint)size.PixelHeight);
        // The ortho projection is built from these, so they must be the DERIVED
        // DIP size — feeding the raw bounds would map a half-pixel-wider view
        // onto the surface and reintroduce the resample inside the renderer.
        _lastLogicalWidth = (float)size.DipWidth;
        _lastLogicalHeight = (float)size.DipHeight;

        // Atlas rebuild first: glyphs registered just before this call are
        // uploaded before any text quad is batched.
        renderer.FontAtlas.RebuildIfNeeded();

        return renderer.TryBeginFrame();
    }

    public void EndFrame(TimelineBatcher batcher)
    {
        var renderer = _renderer;
        if (renderer == null) return;
        // Timeline body background — theme-fed so it follows the palette.
        var bg = ChartTheme.BottomMarginBgColor;
        renderer.EndFrame(batcher, _lastLogicalWidth, _lastLogicalHeight,
            bg.R / 255f, bg.G / 255f, bg.B / 255f);
    }

    public void Detach()
    {
        _detached = true;
        _ready = false;
        // The pump keeps completing in-flight presents even after shutdown
        // (a dropped completion would wedge a pool slot forever); it only
        // stops accepting NEW frames.
        _pump?.Shutdown();
        _pump = null;
        // Device-idles internally; pending compositor reads of the dead pool
        // survive via COM refcounts, late completions are generation-checked.
        _renderer?.Dispose();
        _renderer = null;

        ElementComposition.SetElementChildVisual(_owner, null);
        _visual = null;
        _surface?.Dispose();
        _surface = null;
    }

    /// <summary>
    ///     Composition + device bring-up. Async because the compositor's GPU
    ///     interop is awaited; every failure path funnels into
    ///     <see cref="Fail" /> → the owner's error state (or a silent no-op
    ///     under the headless test platform).
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            // Verification seam (same pattern as DOTCHARTER_UI_AUTOPILOT):
            // forces the init-failure path so the timeline error surface can
            // be exercised on a healthy machine without breaking a driver.
            if (Environment.GetEnvironmentVariable("DOTCHARTER_TIMELINE2D_FORCE_FAIL") == "1")
            {
                Fail("Composed 2D init failure forced by DOTCHARTER_TIMELINE2D_FORCE_FAIL.");
                return;
            }

            var selfVisual = ElementComposition.GetElementVisual(_owner);
            if (selfVisual == null)
            {
                Fail("No compositor visual — composed 2D output cannot start.");
                return;
            }

            var compositor = selfVisual.Compositor;
            _surface = compositor.CreateDrawingSurface();
            _visual = compositor.CreateSurfaceVisual();
            _visual.Surface = _surface;
            ElementComposition.SetElementChildVisual(_owner, _visual);

            var interop = await compositor.TryGetCompositionGpuInterop();
            if (_detached || _surface == null) return; // detached while awaiting

            if (interop == null ||
                !interop.SupportedImageHandleTypes.Contains(
                    KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle))
            {
                Fail("Compositor GPU interop unavailable or no D3D11 NT-handle support.");
                return;
            }

            _cachedAdapterLuid ??= interop.DeviceLuid?.ToArray();

            // Throws when no LUID-matched device with the interop extensions
            // exists — cached in Timeline2DVulkanDevice so later viewports
            // fail fast into the same error state.
            _renderer = Timeline2DComposedRenderer.Create(_cachedAdapterLuid);
            _pump = new ComposedPresentPump(_renderer, interop, _surface, publishPreview3DDiagnostics: false);

            if (!_loggedActive)
            {
                // Once per process (stacked rows / re-attaches share the
                // device): positive evidence in the log of WHICH 2D path runs.
                _loggedActive = true;
                App.Services?.GetService<IAppLogger>()?.Info(AppLogCategory.Rendering,
                    $"Composed-Vulkan 2D timeline active on {_renderer.DeviceName}.");
            }

            _ready = true;
            _owner.OnBackendReady(this);
            RequestFrame();
        }
        catch (Exception ex)
        {
            Fail($"Composed 2D output failed to initialize: {ex.Message}");
        }
    }

    private void Fail(string message)
    {
        // Headless test platform: expected — no GPU compositor exists, the
        // control degrades to a no-op render. Real compositor: an ERROR the
        // user must see (the owner shows the localized error surface).
        var headless = IsHeadlessTestPlatform.Value;
        var logger = App.Services?.GetService<IAppLogger>();
        if (headless)
        {
            logger?.Info(AppLogCategory.Rendering,
                $"{message} Headless platform — 2D timeline rendering disabled.");
        }
        else
        {
            logger?.Error(AppLogCategory.Rendering,
                $"{message} The 2D timeline cannot render; showing the error surface.");
        }

        Debug.WriteLine($"[Timeline2DVulkan] {message}");
        Detach();
        _owner.OnComposedBackendFailed(this, showErrorSurface: !headless);
    }

    /// <summary>Visual size in DIPs; render size in PIXELS (BeginFrame).
    /// Carries the same vertical counter-flip as composed 3D: the imported
    /// D3D11 texture reaches the ANGLE-backed compositor bottom-up, so the
    /// top-down Vulkan image needs Scale(1,-1,1) about the half-height
    /// center. Tied to the AngleEgl mode pinned in Program.cs.
    /// <para>
    /// The size comes from <see cref="ComposedSurfaceGeometry" /> so that
    /// <c>Size x pixelScale</c> is the surface's exact texel count — which also
    /// puts the flip's center on a whole pixel, since it is half of that.
    /// </para></summary>
    private void SyncVisual(ComposedSurfaceSize size, double pixelScale)
    {
        if (_visual == null) return;
        SnapToPixelGrid(pixelScale);

        // ReSharper disable twice CompareOfFloatsByEqualityOperator
        if (size.DipWidth == _lastVisualWidth && size.DipHeight == _lastVisualHeight) return;
        _lastVisualWidth = size.DipWidth;
        _lastVisualHeight = size.DipHeight;
        _visual.Size = new Vector(size.DipWidth, size.DipHeight);
        _visual.Scale = new System.Numerics.Vector3(1f, -1f, 1f);
        _visual.CenterPoint = new System.Numerics.Vector3(0f, (float)(size.DipHeight / 2.0), 0f);
    }

    /// <summary>
    ///     Sub-pixel nudge onto the physical pixel grid. The timeline's own
    ///     layout offset is rounded against <c>RenderScaling</c> only, so under
    ///     a fractional shell scale the surface lands at a fractional device
    ///     origin and the compositor resamples every texel. Unconditional (not
    ///     behind the size early-out): an ancestor can move without this
    ///     control's size changing, and this runs once per built frame.
    /// </summary>
    private void SnapToPixelGrid(double pixelScale)
    {
        if (_visual == null) return;
        if (VisualPixelScale.DeviceOrigin(_owner) is not { } origin) return;

        var x = (float)ComposedSurfaceGeometry.SnapOffset(origin.X, pixelScale);
        var y = (float)ComposedSurfaceGeometry.SnapOffset(origin.Y, pixelScale);
        var offset = new System.Numerics.Vector3(x, y, 0f);
        if (_visual.Offset != offset) _visual.Offset = offset;
    }
}
