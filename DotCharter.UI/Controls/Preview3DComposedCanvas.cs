using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using DotCharter.Rendering.Diagnostics;
using DotCharter.Rendering.Vulkan;
using DotCharter.Rendering.Vulkan.Composed;
using DotCharter.Tone.UI.Controls;
using DotCharter.UI.ViewModels.Editor;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.UI.Controls;

/// <summary>
///     The 3D preview host — the ONLY one since the native child-HWND
///     swapchain host was removed: no child HWND, no airspace limitation. The
///     <see cref="Preview3DRenderDriver" /> runs the render loop; the engine
///     renders into exportable D3D11-backed images
///     (<see cref="IVulkanComposedPresenter" />) and this control's present
///     pump imports them into a <see cref="CompositionDrawingSurface" /> via
///     <c>ICompositionGpuInterop</c> with keyed-mutex sync
///     (<c>UpdateWithKeyedMutexAsync(image, 1, 0)</c>).
///
///     Frame pacing stays on OUR render thread (idle gate, fps cap,
///     FrameStats); only the per-frame composition update marshals to the UI
///     thread, and the 3-image pool back-pressures the loop to the
///     compositor's rate. Since Avalonia 12.1 the Windows compositor runs at
///     the display's full refresh rate, so >60 fps is reachable end-to-end.
///
///     Overlays: any Avalonia content (dialogs, popups, the rig, the pause
///     menu) naturally layers ABOVE this control. There is no fallback path:
///     if the compositor cannot provide GPU interop the panel stays blank and
///     <see cref="LogUnavailable" /> says so on the status rail.
/// </summary>
public class Preview3DComposedCanvas : Control
{
    public static readonly StyledProperty<ChartEditorViewModel?> EditorProperty =
        AvaloniaProperty.Register<Preview3DComposedCanvas, ChartEditorViewModel?>(nameof(Editor));

    public static readonly StyledProperty<TransportViewModel?> TransportProperty =
        AvaloniaProperty.Register<Preview3DComposedCanvas, TransportViewModel?>(nameof(Transport));

    public static readonly StyledProperty<LivePlayViewModel?> LivePlayProperty =
        AvaloniaProperty.Register<Preview3DComposedCanvas, LivePlayViewModel?>(nameof(LivePlay));

    public static readonly StyledProperty<int> CapoProperty =
        AvaloniaProperty.Register<Preview3DComposedCanvas, int>(nameof(Capo));

    public static readonly StyledProperty<bool> GameplayPresentationProperty =
        AvaloniaProperty.Register<Preview3DComposedCanvas, bool>(nameof(GameplayPresentation));

    private readonly Preview3DRenderDriver _driver;

    private CompositionSurfaceVisual? _visual;
    private CompositionDrawingSurface? _surface;
    private ComposedPresentPump? _pump;

    /// <summary>
    ///     Bumped on EVERY attach and EVERY detach; the async composition setup
    ///     carries the value it started with and abandons itself the moment they
    ///     disagree.
    ///     <para>
    ///         <b>Why a counter and not a bool.</b> With a plain
    ///         <c>_attached</c> bool, a float/redock or pop-out round trip
    ///         flips it false and true again while the first setup is still
    ///         awaiting <c>TryGetCompositionGpuInterop</c>. The resumed FIRST
    ///         setup would then see <c>_attached == true</c> and a non-null
    ///         <c>_surface</c> — the SECOND attach's surface — build a second
    ///         <see cref="ComposedPresentPump" /> over it and overwrite
    ///         <see cref="_pump" />. The overwritten pump is never
    ///         <c>Shutdown()</c>, so its imported GPU images are never disposed
    ///         and two pumps drive one drawing surface. A counter, not a bool:
    ///         "am I attached" cannot tell one attach from the next.
    ///     </para>
    /// </summary>
    private int _attachGeneration;

    // The compositor's adapter LUID survives re-attach (pop-out / redock):
    // the singleton renderer keeps its device, so later attaches only need
    // the surface + pump rebuilt.
    private static byte[]? _cachedAdapterLuid;

    public Preview3DComposedCanvas()
    {
        // Render parking follows EFFECTIVE visibility via the watcher — the
        // old OnPropertyChanged branch keyed on a name Avalonia 12 never
        // raises and had parked/woken nothing (see EffectiveVisibilityWatcher).
        _ = new EffectiveVisibilityWatcher(this, OnEffectiveVisibilityFlip);

        // Presentation latency is compensated by rendering AHEAD of the playhead
        // (VisualLeadTiming): the present pump MEASURES its own latency, so the
        // lead is the measured EMA plus the user's trim.
        _driver = new Preview3DRenderDriver(() => IsEffectivelyVisible, () => _topLevelHwnd);
    }

    /// <summary>Driver-owned frame-time percentiles (render-loop frame
    /// intervals, not present latency).</summary>
    public FrameTimeRing FrameStats => _driver.FrameStats;

    /// <summary>Wakes the parked render loop for one immediate frame.</summary>
    public void RequestRender() => _driver.RequestRender();

    public ChartEditorViewModel? Editor
    {
        get => GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    public TransportViewModel? Transport
    {
        get => GetValue(TransportProperty);
        set => SetValue(TransportProperty, value);
    }

    public LivePlayViewModel? LivePlay
    {
        get => GetValue(LivePlayProperty);
        set => SetValue(LivePlayProperty, value);
    }

    public int Capo
    {
        get => GetValue(CapoProperty);
        set => SetValue(CapoProperty, value);
    }

    /// <summary>True while an immersive game run is presenting (see
    /// <see cref="Preview3DRenderDriver.SetGameplayPresentation" />).</summary>
    public bool GameplayPresentation
    {
        get => GetValue(GameplayPresentationProperty);
        set => SetValue(GameplayPresentationProperty, value);
    }

    // UI-thread-cached top-level HWND for the driver's display-refresh probe
    // (the render thread must never walk the visual tree). Volatile: written on
    // attach/detach (UI thread), read on the render thread.
    private volatile nint _topLevelHwnd;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var generation = unchecked(++_attachGeneration);
        var topLevel = TopLevel.GetTopLevel(this);
        _topLevelHwnd = (topLevel as Window)?.TryGetPlatformHandle()?.Handle ?? 0;
        // Render size is PIXELS on this path (Bounds x RenderScaling), and a
        // DPI change moves the SCALING while leaving the DIP Bounds alone — the
        // window keeps its size in DIPs, Windows resizes the HWND underneath.
        // Bounds is therefore not the only input that can invalidate it, and it
        // was the only one we listened to: the pop-out window dragged to a
        // 150 % monitor (or a display-scale change mid-session) went on
        // rendering at the old pixel size and the compositor rescaled it, i.e.
        // a blurry highway at the wrong scale until something else resized it.
        // The 2D renderer never had this because it re-reads RenderScaling
        // every frame; this loop reads it once per size change.
        _scalingSource = topLevel;
        _scalingSource?.ScalingChanged += OnTopLevelScalingChanged;
        _driver.AttachWakeSources(Editor);
        InitializeCompositionAsync(generation);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // Invalidate any setup still in flight BEFORE tearing anything down, so
        // a resumed continuation cannot rebuild onto the surface we are about to
        // dispose (or onto the NEXT attach's surface).
        unchecked { _attachGeneration++; }
        _topLevelHwnd = 0;
        _scalingSource?.ScalingChanged -= OnTopLevelScalingChanged;
        _scalingSource = null;

        // Idle-gate wake sources live on singletons — unsubscribe or the
        // detached canvas (and its Vulkan snapshot graph) stays rooted for the
        // process lifetime (ViewSubscriptionLeakGuardTests).
        _driver.DetachWakeSources();
        _driver.SetRenderable(false);
        _driver.Stop();
        // Let the loop's last frame retire before tearing the pump down —
        // an EndFrame racing the unsubscription would leave its image
        // waiting on a present that never comes (one wedged pool slot until
        // the next rebuild). Bounded: one loop iteration is (100 ms fence wait
        // + 100 ms acquire + frame time), and the cap keeps a truly stuck
        // driver from hanging the UI thread; the loop never blocks on the UI
        // thread, so this cannot deadlock.
        _driver.WaitForDrain(TimeSpan.FromMilliseconds(1500));
        // The pump keeps completing in-flight presents even after shutdown
        // (a dropped completion would wedge a pool slot forever); it only
        // stops accepting NEW frames and disposes its imports when the last
        // pending present retires.
        _pump?.Shutdown();
        _pump = null;

        ElementComposition.SetElementChildVisual(this, null);
        _visual = null;
        _surface?.Dispose();
        _surface = null;
    }

    private void OnEffectiveVisibilityFlip(bool visible) => _driver.NotifyEffectiveVisibility(visible);

    /// <summary>The top-level this canvas is attached to, held only to unhook
    /// <see cref="OnTopLevelScalingChanged" /> on detach (the canvas outlives no
    /// window, but a pane view is re-created on every float/redock).</summary>
    private TopLevel? _scalingSource;

    /// <summary>A DPI change resizes the surface without moving the DIP Bounds
    /// — recompute the pixel render size.</summary>
    private void OnTopLevelScalingChanged(object? sender, EventArgs e) => UpdateSizes();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
        {
            UpdateSizes();
            return;
        }

        if (change.Property == CapoProperty && change.NewValue is int c) _driver.SetCapo(c);
        else if (change.Property == EditorProperty) _driver.SetEditor(change.NewValue as ChartEditorViewModel);
        else if (change.Property == TransportProperty) _driver.SetTransport(change.NewValue as TransportViewModel);
        else if (change.Property == LivePlayProperty) _driver.SetLivePlay(change.NewValue as LivePlayViewModel);
        else if (change.Property == GameplayPresentationProperty && change.NewValue is bool gp)
            _driver.SetGameplayPresentation(gp);
    }

    /// <summary>Visual size in DIPs, render size in PIXELS: nothing rescales
    /// for us — render at the true pixel size or high-DPI displays get a
    /// blurry upscale.
    /// <para>Both numbers come out of ONE
    /// <see cref="ComposedSurfaceGeometry.Measure" /> call, and the visual's
    /// DIP size is derived from the pixel size rather than read from
    /// <c>Bounds</c>. Sizing the two independently is what let a fractional
    /// shell scale put a whole-pixel surface into a fractional destination
    /// rectangle.</para></summary>
    private void UpdateSizes()
    {
        // RenderScaling folded with every layout transform above us. The global
        // UI scale wraps the WHOLE shell in a LayoutTransformControl, so Bounds
        // is in that transform's inner units and `Bounds x RenderScaling` alone
        // under-sizes the swapchain by exactly the UI scale — the same blurry
        // upscale this method's own summary warns about, from a second input.
        var scaling = VisualPixelScale.For(this);

        // …and the pixel COUNT being right is only half of it: the surface is
        // sampled 1:1 only while its destination rectangle is exactly that many
        // device pixels at a whole-pixel origin. Layout rounding guaranteed both
        // while the only factor was RenderScaling; a fractional shell scale
        // multiplies that guarantee away. See ComposedSurfaceGeometry.
        var size = ComposedSurfaceGeometry.Measure(Bounds.Width, Bounds.Height, scaling);

        if (_visual != null)
        {
            _visual.Size = new Vector(size.DipWidth, size.DipHeight);
            if (VisualPixelScale.DeviceOrigin(this) is { } origin)
            {
                _visual.Offset = new System.Numerics.Vector3(
                    (float)ComposedSurfaceGeometry.SnapOffset(origin.X, scaling),
                    (float)ComposedSurfaceGeometry.SnapOffset(origin.Y, scaling),
                    0f);
            }
            // Vertical counter-flip (verified empirically 0.16): the imported
            // D3D11 texture reaches the ANGLE-backed compositor with the
            // OpenGL bottom-up texture orientation, so the scene arrives
            // upside-down. Flipping the VISUAL (not the projection) keeps
            // every render pass — including ortho HUD text — untouched on
            // both paths; center at half height so the flip maps y → h − y
            // in place. Tied to the AngleEgl rendering mode pinned in
            // Program.cs — re-verify if that backend ever changes.
            _visual.Scale = new System.Numerics.Vector3(1f, -1f, 1f);
            // Half the DERIVED height, so the flip's axis is a whole pixel too:
            // mirroring about a half-pixel line shifts the whole highway by half
            // a texel, which is a resample of its own.
            _visual.CenterPoint = new System.Numerics.Vector3(0f, (float)(size.DipHeight / 2.0), 0f);
        }

        _driver.SetSize(size.PixelWidth, size.PixelHeight);
    }

    private async void InitializeCompositionAsync(int generation)
    {
        try
        {
            var selfVisual = ElementComposition.GetElementVisual(this);
            if (selfVisual == null) return; // headless / no compositor
            var compositor = selfVisual.Compositor;

            _surface = compositor.CreateDrawingSurface();
            _visual = compositor.CreateSurfaceVisual();
            _visual.Surface = _surface;
            ElementComposition.SetElementChildVisual(this, _visual);
            UpdateSizes(); // size + the vertical counter-flip (see below)

            var interop = await compositor.TryGetCompositionGpuInterop();
            // Detached — or detached AND re-attached — while awaiting. The
            // generation is what tells those two apart; see _attachGeneration.
            if (generation != _attachGeneration || _surface == null) return;

            if (interop == null ||
                !interop.SupportedImageHandleTypes.Contains(
                    KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle))
            {
                LogUnavailable(
                    "Compositor GPU interop unavailable or no D3D11 NT-handle support — " +
                    "the 3D preview cannot start.");
                return;
            }

            var renderService = App.Services!.GetRequiredService<IVulkanRenderService>();
            if (renderService is not IVulkanComposedPresenter presenter)
            {
                LogUnavailable("Renderer does not implement composed output.");
                return;
            }

            _cachedAdapterLuid ??= interop.DeviceLuid?.ToArray();
            var luid = _cachedAdapterLuid;

            _pump = new ComposedPresentPump(presenter, interop, _surface, publishPreview3DDiagnostics: true);

            UpdateSizes();
            _driver.SetEditor(Editor);
            _driver.SetTransport(Transport);
            _driver.SetLivePlay(LivePlay);
            _driver.SetCapo(Capo);
            _driver.SetGameplayPresentation(GameplayPresentation);
            _driver.SetRenderable(true);
            _driver.Start(engine => engine.InitializeComposed(luid));
        }
        catch (Exception ex)
        {
            LogUnavailable($"Composed 3D output failed to initialize: {ex.Message}");
        }
    }

    private static void LogUnavailable(string message)
    {
        App.Services?.GetService<IAppLogger>()?.Error(AppLogCategory.Rendering, message);
        Debug.WriteLine($"[Preview3DComposed] {message}");
        // Composed is the ONLY 3D path — there is no fallback — so a blank
        // panel must SAY why rather than leave it to the log.
        try
        {
            App.Services?.GetService<DotCharter.Services.State.ProjectState>()?.SetMessage(
                DotCharter.Localization.LocalizationService.Instance["Preview3D_ComposedUnavailable"],
                DotCharter.Services.State.StatusSeverity.Error);
        }
        catch
        {
            // A status rail that is not up yet must never take the canvas down.
        }
    }
}
