# The composed-Vulkan 2D timeline

Deep reference for `DotCharter.Rendering/AGENTS.md`. `Vulkan2D/` + `Timeline2D/`
— the ONLY 2D path since the v0.33 OpenGL removal. UI side: `DotCharter.UI`
`Controls/ChartRenderer` + `Controls/Timeline2D/VulkanTimelineBackend`; the
composition-mode consequences for popups are in
[../ui/rendering-perf/presentation-paths.md](../ui/rendering-perf/presentation-paths.md).

## `Vulkan2D/`

- `Vulkan2D/` — the **composed-Vulkan 2D timeline** (v0.33; since the GL
  removal the ONLY 2D path — no flag, no fallback: init failure surfaces the
  localized error state in `ChartRenderer`, or a silent no-op under the
  headless test platform). Consumes the `TimelineBatcher` output +
  `ITimelineRenderLayer` stack (the batcher is CPU-only geometry; **a layer
  must never call a graphics API**). The pipelines (`Shaders/timeline2d_*`)
  and the SrcAlpha/OneMinusSrcAlpha blend on color AND alpha deliberately
  keep the removed GL path's exact semantics — that is how output parity was
  proven at the port (commit `43866e8d`). The draw-command list replays in
  insertion order — `RecordDraws` calls `batcher.FinalizeFrame()` (dropping
  it silently loses every frame's FINAL run; a single-stream frame renders
  nothing). Pieces:
  - `Timeline2DVulkanDevice` — the SECOND, process-lifetime composed device
    (LUID-matched via `VulkanContext.InitializeComposed`). The 2D path cannot
    borrow the 3D singleton: that device is mode-locked to the mode it was
    initialized for (composed in the app, headless in tests) and driven from
    the 3D render thread, while 2D is always composed on the UI thread. Created lazily on the first
    viewport, never disposed; a cached failure makes every later viewport
    fail fast into the same error state. Per-viewport resources (pool,
    framebuffers, vertex streams, atlas) cycle on attach/detach (pop-out).
  - `Timeline2DVulkanCore` — single-attachment BGRA8 pass + solid (6-float
    vertex) / general (9-float) pipelines, shared per device.
    The general fragment shader branches on the vertex `type` (1 disc, 2 ring,
    3 glyph, 4 vertical AA line, 5 horizontal AA line, >= 8 DASHED vertical
    AA line). A dashed grid line is ONE quad: `TimelineBatcher.AddDashedVerticalLineAa`
    packs dash and gap (1/16 px units) into the type, the vertex shader decodes
    them, and the fragment shader discards the gaps with the phase anchored at
    the quad's top. Never draw a dashed line as one quad per dash again;
    `Timeline2DDashedGridVertexTests` fails when the grid's vertex count grows
    with the pane height.
  - `Timeline2DComposedRenderer` — per-viewport `VulkanComposedImagePool` +
    framebuffers + grow-on-demand mapped vertex streams; implements
    `IComposedFrameSource` (the pump-facing half of
    `IVulkanComposedPresenter`, extracted so 3D and 2D share ONE
    `ComposedPresentPump` contract). All composed invariants apply unchanged:
    exactly one `CompleteComposedPresent` per `ComposedFrameReady`, keyed
    mutex acquire 0 / release 1, no semaphores, color ends
    COLOR_ATTACHMENT_OPTIMAL, views/framebuffers die BEFORE `pool.Recreate`,
    LUID mismatch hard-fails.
  - `VulkanTimelineFontAtlas` — upload backend of the shared
    `Text/ChartFontAtlas` base (rasterization + every metric live in the
    base, so text geometry is a pure function of the base regardless of who
    uploads). Rebuilds device-idle (rare).
  - `Timeline2DHeadlessRenderer` — offscreen golden-frame sibling
    (own headless device). **Readback memory prefers HOST_CACHED** — reading
    write-combined host memory costs several ms per frame (measured: the
    A/B went from 2.7× slower to 4.3× faster than GL after the fix).
  - **Threading: everything on the UI THREAD** — the 2D frame is built from
    live view-model state, so there is no render thread by design; no
    renderer lock exists; per-frame fence waits use short timeouts (a skip
    beats a UI stall; the next animation tick retries).
  - **On-screen pacing:** `TopLevel.RequestAnimationFrame` per damage — i.e.
    the compositor tick. **The tick GENERATOR depends on the Win32
    COMPOSITION mode (v0.32.5 finding):** under Avalonia's classic
    `WinUIComposition`, ticks are paced by `RequestCommitAsync` completion —
    one tick per DWM commit cycle, measured ~9.5-10.3 ms p50 (≈100 fps)
    REGARDLESS of monitor refresh, with a ~3 ms compositor-thread scene
    render inside each cycle. `LowLatencyDxgiSwapChain` instead takes its
    ticks from the Win32 `SleepLoopRenderTimer` at the MAX monitor refresh
    (min 60) — a 280 Hz display then really ticks at 280 fps, but only with
    ~1 ms system timer resolution (the `timeBeginPeriod` pin; without it
    Sleep quantizes to 15.6 ms ≈ 64 fps). **That mode is opt-in, never the
    default** (`Platform/Win32CompositionModeSelector`; appconfig
    `"Win32CompositionMode": "LowLatencyDxgiSwapChain"`): it gives every
    top-level an opaque swapchain (`CreateSwapChainForHwnd` +
    `DXGI_ALPHA_MODE_IGNORE`), which paints a black frame around every
    rounded/shadowed POPUP HWND — dropdowns, flyouts, menus, tooltips. It was
    the default for v0.32.5-v0.34.1 and that is exactly what went wrong; see
    `docs/agents/ui/rendering-perf/presentation-paths.md`.

## `Timeline2D/`

- `Timeline2D/` — the backend-NEUTRAL CPU-side 2D geometry the Vulkan 2D
  path consumes: `TimelineBatcher` (solid + general vertex streams, the
  insertion-ordered draw-command list; formerly `OpenGl/OpenGlBatcher`) +
  `PolylineRibbonMath` (pure, unit-tested).
