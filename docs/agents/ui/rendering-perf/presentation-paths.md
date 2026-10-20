# Rendering / perf — the 3D and 2D presentation paths

Part of the rendering/perf reference. Router: [../rendering-perf-detail.md](../rendering-perf-detail.md).

## 3D presentation path (a sub-bullet of `Controls/` in the Layout map)

  - **3D presentation path: composed ONLY.** `Preview3DView` always constructs `Preview3DComposedCanvas` — the native child-HWND host, its `appconfig` opt-out and the airspace workarounds (dialog panel-collapse, pedalboard freeze-frame) are gone, pinned by `UI/Preview3DComposedOnlyTests`. There is NO fallback: a compositor without GPU interop (or without D3D11 NT-handle support) leaves the panel blank and `Preview3DComposedCanvas.LogUnavailable` puts `Preview3D_ComposedUnavailable` on the status rail. Overlays (dialogs, popups, the rig) layer above the preview like any Avalonia content. The `Preview3DRenderDriver` holds the whole render loop + snapshot staging + idle gate + FrameStats; the canvas supplies the engine-init delegate. Composed specifics: a `CompositionSurfaceVisual`+`CompositionDrawingSurface` per attach — and the async setup carries an ATTACH GENERATION (`Preview3DComposedCanvas._attachGeneration`, bumped on attach AND detach, pinned by `Preview3DComposedAttachTests`): a float/redock or pop-out round trip inside the `TryGetCompositionGpuInterop` await used to resume against the NEXT attach's surface and build a SECOND pump over it, orphaning the first (never `Shutdown()`, so its GPU imports leak). `ComposedPresentPump` imports the renderer's exported D3D11 images and `UpdateWithKeyedMutexAsync(img, 1, 0)`s them (INVARIANT: every frame gets exactly one `CompleteComposedPresent`, or a pool slot wedges); the visual carries a `Scale(1,-1,1)` counter-flip (ANGLE imports D3D11 textures bottom-up — verified live, tied to the AngleEgl mode in `Program.cs`); render size is PIXELS and is recomputed on a `TopLevel.ScalingChanged` too, not only on a `Bounds` change — a DPI change moves the SCALING while leaving the DIP bounds alone (the window keeps its DIP size; Windows resizes the HWND), so a pop-out dragged to a 150 % monitor used to go on rendering at the old pixel size and let the compositor rescale it; the 2D renderer never had this because it re-reads `RenderScaling` every frame; the render clock LEADS the playhead by `Preview3DComposedTiming.EffectiveLeadMs` (EMA of measured present latency + owner trim, clamped 0–50 ms, pure `ComputeLeadMs` unit-tested) so the compositor's added latency cancels against audio — interval JITTER (σ, published to `RenderDiagnostics.Preview3DPresentInterval`) is the non-compensable metric to watch. **The pixel size is NOT `Bounds × RenderScaling`** (it was until 2026-09-12 and the doc said so until 2026-09-16): it is `Bounds × VisualPixelScale.For(this)` — `RenderScaling` folded with every layout transform above the canvas, because `MainWindow`'s `RootScaler` scales the whole shell — and both it and the visual's DIP size come out of ONE `ComposedSurfaceGeometry.Measure` call so the destination rectangle is exactly as many device pixels as the surface has texels, at a whole-pixel origin. Sizing them independently is what made the chart editor blurry through v0.55.1; the mechanism is written up once, in the composed-surface bullet of [timeline-2d.md](timeline-2d.md). Benchmark hooks: `DOTCHARTER_3D_STRESS=1` (continuous render) + `DOTCHARTER_3D_STATS_LOG=1` (5 s `[3DStats]` app-log line, both paths).

## 2D presentation path (Vulkan-only)

  - **The 2D timeline presents through composed Vulkan — the ONLY backend**
    (the OpenGL path and its rollout flag were removed in v0.33) —
    `ChartRenderer` is a Decorator owning frame CONTENT only; presentation is
    `Controls/Timeline2D/VulkanTimelineBackend`. Frames are built on the UI
    thread on `TopLevel.RequestAnimationFrame` ticks (the compositor's
    cadence, damage-driven — an idle timeline renders nothing); the shared
    `ComposedPresentPump` imports each submitted image with keyed-mutex sync
    (every frame gets exactly ONE completion). The host visual carries the
    same Scale(1,-1,1) counter-flip as composed 3D — centred on half the
    DERIVED DIP height, not half `Bounds`, so the mirror axis lands on a whole
    pixel. Input, hit-testing
    (`ICustomHitTest` — the ChartRenderer itself is always the pointer
    source), `IChartViewport` and every consumer are presentation-independent.
    **Failure mode:** composed-init failure on a real compositor puts a
    localized, theme-token error surface into the Decorator's Child slot
    (hit-test invisible; `Timeline2D_RenderInitFailed*` resx keys) + an Error
    log line — never a silent blank pane; under Avalonia.Headless (no GPU
    compositor by design) the control silently no-ops instead, keeping the
    headless UI tests green. A re-attach retries init.
    `DOTCHARTER_TIMELINE2D_FORCE_FAIL=1` forces the failure path on a
    healthy machine (verification seam, like `DOTCHARTER_UI_AUTOPILOT`).
    **On-screen fps ceiling = the Win32 COMPOSITION mode (v0.32.5):** under
    Avalonia's classic `WinUIComposition` the render tick fires once per DWM
    commit cycle (`RequestCommitAsync` completion) — measured ~9.5-10.3 ms
    p50 ≈ 100 fps regardless of monitor refresh. `LowLatencyDxgiSwapChain`
    instead ticks on the Win32 render timer at the MAX monitor refresh
    (min 60; the ~1 ms system timer resolution pin is what lets it pace above
    ~64 fps), measured 2D interval p50 9.5 → 3.8-7 ms.
    **It is NOT the default, and must not become one — it breaks every popup.**
    Avalonia's `DxgiRenderTarget` builds each top-level's swapchain with
    `CreateSwapChainForHwnd` + `DXGI_ALPHA_MODE_IGNORE`, and `DxgiConnection`
    reports `RequiresNoRedirectionBitmap == false`, so `WindowImpl` resolves
    `_defaultTransparencyLevel` to `WindowTransparencyLevel.None` — **every**
    HWND is opaque. With `OverlayPopups` off (the default before 0.49.3; it is
    always on now) ComboBox dropdowns, flyouts, menus and tooltips each get
    their OWN popup HWND, so every pixel the theme leaves transparent — the
    gutter outside a rounded `FlyoutPresenter`, the ComboBox popup border's
    `Margin="0,4,0,12"`, the `BoxShadow` feather — presents as flat BLACK.
    The project shipped that twice (`b9592ce5` "ToolTip … black halo" removed
    the setting; `85fb67e5`/v0.32.5 reinstated it as the default and brought
    the halo back on every dropdown). The mapping now lives in the pure
    `Platform/Win32CompositionModeSelector` and is pinned by
    `Win32CompositionModeSelectorTests`; appconfig
    `"Win32CompositionMode": "LowLatencyDxgiSwapChain"` opts back into the
    fast pacing *and* its black popup frames. Rendered-output
    goldens are GPU-gated Vulkan-only tests (GL↔Vulkan parity was proven at
    the port, commit `43866e8d`, before the GL baseline was removed).
    Per-phase live attribution: `DOTCHARTER_2D_STATS_LOG=1` → 5 s `[2DStats]`
    log lines (phase avg/max, per-layer ms, fence/pool skip counters).

