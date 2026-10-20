# Rendering / perf — deep reference (UI router)

Extracted verbatim from `DotCharter.UI/AGENTS.md`. The always-on hard rules
(IsVisible vs Opacity, the 250 Hz coalescing rule, InvalidateVisual gating,
ComboBox virtualization, seek throttling) stay in `DotCharter.UI/AGENTS.md`;
everything below is the per-subsystem detail.

**This file is a ROUTER.** What stays here is the ONE time↔screen anchor rule,
which every subsystem below depends on; the rest is one file per row of the
table at the bottom, under [`rendering-perf/`](rendering-perf/).

## Rendering / perf rules (the detail bullets)

- **A CATALOGUE-length list must be windowed, and a NESTED items panel never
  is** (2026-09-12). The rig's gear palette was a `ScrollViewer` over an
  `ItemsControl` of groups whose item template was another `ItemsControl` — the
  inner one is measured with an unbounded height, so its viewport IS its content
  and it realizes every row it has. The cost is not paid where it is written:
  the rig screen is FORCE-realized during prewarm (`PlayModeView.HiddenSurfaces`
  calls `EnsureRigScreen`), so the whole list is built on the boot drain, and it
  is rebuilt on the UI thread on every tab click, favourite toggle and coalesced
  search keystroke — inside the ≤150 ms switch budget. The JuceSharp 0.9 gear
  wave took that catalogue 57 → 100, i.e. ~680 → ~1,200 elements, and sixty
  preset cards on the SAME screen had already been enough for the owner to
  report lag. Fixed by flattening the list and letting a `ListBox`'s own
  `VirtualizingStackPanel` own it: **only a control that brings its own scroller
  can virtualize, because the scroller is what supplies the viewport** — an
  `ItemsControl` given a `VirtualizingStackPanel` inside someone else's
  `ScrollViewer` is not windowed, it just looks like it should be. Measured
  headless over a synthetic 160-module catalogue: 8 realized containers / 178
  realized descendants, against 160 / 2,914 unvirtualized. Detail in
  [pedalboard.md](pedalboard.md); the guard is
  `Perf/PlayModeScreensUiPerfSmokeTests` and it asserts a STRUCTURAL ceiling
  (realized containers against a viewport's worth, both read off the laid-out
  tree), never a millisecond.

- **The 2D viewport is decoupled from the playhead (0.16 6.6).** The ONE
  time-to-screen anchor is `ProjectState.TimelineViewAnchorEffectiveMs`
  (the RENDERED playhead while `TimelineFollowsPlayhead` is on — the default,
  never persisted; the pan anchor `TimelineViewAnchorMs` while off).
  **"Rendered", not "coalesced" (0.35):** `ChartRenderer` resolves its playhead
  from the LIVE audio clock and adds `VisualLeadTiming.EffectiveLeadMs`, so the
  coalesced `Playback.CurrentTimeMs` trails what is on screen by the
  de-coalescing gap plus the lead — up to ~100 ms, i.e. **100 px at 1 px/ms** —
  for the entire duration of playback, and every hit-tester mapped through that
  lagging value. The resolution is now the pure
  `TimelineViewMath.ResolveRenderPlayheadMs` and the MAIN renderer publishes its
  result to `ProjectState.TimelineRenderedAnchorMs` once per frame while playing
  (stacked override rows must not publish; `NaN` = no live frame, which is the
  only case that falls back to `Playback.CurrentTimeMs` — paused, the two agree
  anyway and a stale value would survive a seek). Pinned by
  `TimelineViewMathTests`. Renderer
  (`ChartRenderer.RenderTimelineFrame`), `ChartHitTester`/`MarqueeHitTester`
  (`viewAnchorMs` param) and `ToolCoordinator.ExactTimeMs` all map through
  it — ANY new time↔screen consumer must too, or clicks land on the wrong
  chart time when panned. (The renderer DRAWS from that anchor's view start
  snapped to a whole device pixel, `TimelineViewMath.SnappedViewStartMs`, #113;
  hit-testers keep the exact anchor — the two differ by under half a pixel.)
  **The resolver's trust budget is SPEED-SCALED
  (2026-08-30).** `MaxAudioClockLagMs` (50) bounds how far the live audio
  clock may sit from the coalesced UI value before a seek is assumed; it is a
  WALL-clock budget (≤60 Hz publish + dispatch) but both clocks are SONG time,
  so at 2× the ordinary de-coalescing gap crossed it on ~15 % of frames and the
  frame drew the stale UI value instead — the whole viewport stepped back
  ~50 song-ms and forward again (owner: "notes, bars and markers all jitter at
  2×"; measured 90 backward steps per 600 frames in the deterministic model, 0
  after). `ResolveRenderPlayheadMs` takes `playbackSpeed` and multiplies the
  budget by `SpeedFactor` (never below 1); `ChartRenderer` MUST pass
  `PlaybackSpeed` — the parameter defaults to 1.0, so an omitted argument
  compiles and silently brings the bug back. Both pinned in
  `TimelineViewMathTests` (the coordinator-publish + frame-sampling model, and a
  source lint on the call site). Pure math lives in `TimelineViewMath`
  (unit-tested); state mutation in `TimelineNavigation` (wheel pan, anchored
  zoom). Wheel-with-no-edit-target and middle-drag PAN (never seek — see the
  seek-flush rule above); turning Follow off freezes the view in place
  (`TimelineFollowsPlayhead` setter seeds the anchor). Follow re-locks on
  `Transport.Play` and in `ChartEditorViewModel.ExecuteSeek` — the
  deliberate-seek funnel (minimap, sheet strip, keybind seeks, inspector
  jumps); route new deliberate navigations through `ExecuteSeek` so they
  re-lock too. Zoom while unfollowed anchors on the pointer (Ctrl+wheel) or
  the viewport center (`EditorUiState.TimelineViewportWidthPx`, published by
  the main renderer); while following it stays the historical
  zoom-around-playhead. `PlayheadLayer` draws the playhead at
  `rc.PlayheadScreenX` (== `CenterScreenX` when following — frames stay
  byte-identical) and skips when panned off-screen. Toolbar toggle in the
  Zoom group + `view.followPlayhead` in the palette.

## Where the detail lives

| Read this when… | File |
|---|---|
| you are touching how a frame is PRESENTED: the composed-vs-native 3D path, the Vulkan-only 2D backend, the init-failure surface, or the Win32 composition mode (**the black-popup trap that shipped twice**) | [rendering-perf/presentation-paths.md](rendering-perf/presentation-paths.md) |
| you are touching the 3D preview's render loop — the idle gate, the fps cap, snapshot staging, `FrameStats` | [rendering-perf/preview-3d-loop.md](rendering-perf/preview-3d-loop.md) |
| you are touching the 2D timeline's CONTENT: culling, the density LOD, the waveform layer + its pyramid, auto-collapsing ribbons, `Timeline2DVisuals`, marquee selection, the sheet strip, or editing-time GC | [rendering-perf/timeline-2d.md](rendering-perf/timeline-2d.md) |
| you are adding startup work, a hidden `IsVisible`-gated surface, or anything touching `MainWindowViewModel`'s constructor — the four prewarm cases, the drain order, the DEFERRED Play Mode screen bodies and the local-only probes | [rendering-perf/startup-and-prewarm.md](rendering-perf/startup-and-prewarm.md) |
| you are adding a tooltip, a hover popup or anything that can create a popup on pointer-enter | [rendering-perf/tooltips-and-popups.md](rendering-perf/tooltips-and-popups.md) |
| you are measuring a view/tab/screen switch or a cold start — the ≤150 ms budget, the measured A/B per surface, the **measured per-phase cold-start table** (`r2r=yes`, and what `bind=` is), and the `r2r=` rule that decides whether a number is a product measurement at all | [rendering-perf/responsiveness-budget.md](rendering-perf/responsiveness-budget.md) |
| you are touching what an EDIT costs on a big chart — a bulk delete and its undo, a technique verb over thousands of selected sounds, the spacing post-pass that runs after every committed edit, arrow-key seek, the inspector's chip fold; the measured before/after table and what was deliberately left | [rendering-perf/editor-latency.md](rendering-perf/editor-latency.md) |
