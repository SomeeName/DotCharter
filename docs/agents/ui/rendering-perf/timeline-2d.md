# Rendering / perf — the 2D timeline — culling, LOD, waveform, ribbons, marquee, sheet strip

Part of the rendering/perf reference. Router: [../rendering-perf-detail.md](../rendering-perf-detail.md).

- **Both 2D editor surfaces are gated on EFFECTIVE visibility, and that is the
  only thing that stops them.** The editor subtree is `IsVisible`-gated
  (`Views/MainWindow.axaml`) — hidden, NOT detached — and Avalonia leaves
  `Bounds` at its last arranged value when `IsVisible` goes false, so
  `ChartRenderer.RenderTimelineFrame`'s `Bounds.Width/Height <= 0` guard does
  not fire for a hidden viewport, and the `ProjectState` subscription stays
  live. Before the gate that meant `CurrentTimeMs` at 60 Hz kept requesting
  frames
  while the editor was off screen and the whole pipeline (snapshot → ribbon
  layout → 10 layers → vertex upload → Vulkan submit → compositor import) ran
  at compositor rate — worst in Play Mode, where the invisible 2D timeline
  competes with the 3D highway for the same UI thread and GPU;
  `MinimapOverviewControl` had the same hole at ~30 Hz. **The ~30 Hz
  playhead gate itself is `Controls/RepaintThrottle` (0.49)**, held by value by
  the minimap AND by `LivePlaySectionGraph` — which until then repainted its
  whole phrase + section strip on every one of the coordinator's up-to-60 Hz
  publishes. `RepaintThrottleTests` pins the gate with an injected clock and
  lints both playhead paths.
  `ChartRenderer.RequestNextFrameRendering` and the minimap's `RequestRepaint`
  are now the single funnels, both no-op while not effectively visible, with ONE frame
  requested on the false→true flip so nothing shows stale. The mechanism is
  `Controls/EffectiveVisibilityWatcher` — the same one
  `Preview3DComposedCanvas`/`MenuBackdropControl` use.
  **Never an `IsEffectivelyVisible` property branch**: it is a computed CLR
  property Avalonia 12 raises no change for, and six surfaces' park/wake logic
  was dead code that way. Pinned by
  `Perf/HiddenEditorRenderGateUiPerfSmokeTests` (out of the default gate — a
  hidden ANCESTOR needs a real window).
- **A subscription torn down on DETACH must be rebuilt on ATTACH, not from a
  property change.** The chart timeline composite and `Preview3DView` must stay
  SINGLE instances across layout toggles and pop-out (`DockPaneContractTests`),
  so a re-parent is detach + attach on the SAME object and raises no property
  change. `ChartRenderer.SyncRibbonSubscription` was reachable only from
  `OnPropertyChanged`, so after the first pop-out/redock a ROWS ribbon toggle
  produced no frame for the rest of the session
  (`Perf/ChartRendererRibbonSubscriptionUiPerfSmokeTests`). The mirror-image
  fault is subscribing TWICE: `MinimapOverviewControl` subscribed to the
  selection collections from both paths, because `App.axaml.cs` sets the window
  DataContext BEFORE `Show()` so the `Editor` binding lands before the attach —
  detach then removed one of the two copies and the dead control stayed rooted
  by the editor view model's collections
  (`Perf/MinimapSelectionSubscriptionUiPerfSmokeTests`). One `Sync*`
  method per subscription, `ReferenceEquals`-guarded, called from BOTH paths.
- **Editing-time GC discipline (v0.32.5).** A committed edit fires the whole
  chain (history → spacing enforcement → inspector sync → minimap → damage
  renders); it measured 4.2 MB and ~35 ms of UI thread PER KEYSTROKE, i.e.
  "GC kicks in way too often while editing" (owner report; gen1 every ~5
  edits). Three rules keep it at ~0.85 MB/edit now: (1)
  `MinimapOverviewControl` rate-limits static-layer rebuilds (≤5 Hz with a
  guaranteed trailing rebuild) and REUSES its `RenderTargetBitmap` when the
  size is unchanged — recreating the ~2560-wide surface per edit was the LOH
  / gen2 churn; (2) `BendEditorControl` keeps its grid layer (size+theme
  dependent only) across the DataContext swap the inspector performs per
  selection change — only the curve/dynamic layers rebuild; (3)
  `ChartSpacingEnforcer.EnforceSoundSpacing` is a pooled-array backward
  sweep (semantics pinned in `ChartSpacingEnforcerTests`), not LINQ +
  dictionaries. Measurement seam: `DOTCHARTER_AUTOPILOT_EDIT=1` inside the
  perf autopilot logs `[EditGC]` (per-edit alloc + GC counts) and
  `[CloneStats]` (one crash-recovery-style assemble+clone: measured 8.9 ms /
  3.2 MB on a real 4-arrangement chart — the sanctioned whole-project
  `ChartProjectCloner` is NOT the editing churn; don't scope it without new
  evidence).
- **Every 2D layer culls on screen X, including the note path.** The frame
  snapshot is deliberately built with a ±5000 ms buffer
  (`RenderingConstants.VisibleRangeBufferMs`) — load-bearing for sustains that
  start off-screen left and for the sticky-prefix consumers — so a layer that
  walks the snapshot without a viewport test does 2.0× the needed work at the
  default 0.2 px/ms and 11.4× at 2.0. It is NOT overdraw (the scissor is set to
  the render extent, so off-screen quads never rasterize); the cost is CPU float
  writes, the host-visible upload and vertex processing, ~666 floats per
  Full-LOD note. `NoteLayer` + `HighwaySustainRenderer` + `HighwayNoteHeadRenderer`
  cull through `NoteCulling.IsOffScreen(x, width, rc.Width)` (margin 120 px,
  sized for the bend label box / slide arrowhead / selection halo); the sustain
  renderer additionally clips its vibrato/tremolo squiggle loops to the visible
  span. Test the SPAN, never just the onset — a 5 s drone whose head is 2 s
  off-screen left is still fully visible. Pinned by `NoteLayerCullingTests`.
- **A hand-shape bar says what it is (#480):** `HandShapeLayer` prints
  `RenderHandShape.Label` inside the bar — one text path for a committed bar's
  template name and a bar being drawn's grip (#481) — only when it fits WHOLE
  (a cut-off chord name reads as another chord) and, for committed bars, not at
  Marks LOD. An arpeggio bar is `HandShapeArpeggio`-coloured with bracket posts; a
  bar with an inspector finding (`HasFinding`: E05/D09) gets a `Warning` outline,
  a corner flag and a warning wash. Every state has a shape cue besides its hue.
  The names are registered with the glyph atlas from the template table
  (`ChartRenderer`). A SUGGESTED bar (#491, `RenderHandShape.IsSuggested`) is a
  faint bar with a dashed top and bottom edge and no lane wash; only its on-screen
  part is dashed. Pinned by `HandShapeLayerBarStyleTests`.
- **A bulk chord edit's targets wear corner brackets (#485):** a chord with
  `ChordFlags.EditTarget` (at every density — at Marks LOD there is no panel) and a
  bar with `RenderHandShape.IsEditTarget` get four `EditTarget`-coloured corner
  brackets just outside them (`NoteLayer.DrawCornerBrackets`, shared by
  `HandShapeLayer`); a preview bar never does. **The AI chart pass review (#506)**
  marks anchors too: `FretAnchorLayer` draws a live anchor the review replaces at
  `FretAnchorLayer.ReplacedAlpha` and the proposed anchors LAST, in the
  `EditTarget` colour, so a proposal on a live anchor reads "this becomes that".
  Pinned by `EditTargetLayerTests`.
- **A repeat chord draws as the chord it repeats, DIMMED (#487, #564):**
  `ChordFlags.Repeat` (the export's decision, see
  `docs/agents/rendering/scene-and-components/frame-data-text-and-assets.md`) makes
  `NoteLayer` pass `NoteDrawData.Dimmed`: the gem body, resting rim, specular cap,
  fret number and sustain tube draw at `RepeatChordInk.Dim` (0.5) of their
  BRIGHTNESS, still opaque (`Darken` — a translucent head let the selected panel's
  white wash its number out), and the resting panel at half its opacity (`Fade`).
  Technique glyphs (mute ticks, accent ring, harmonic diamond, HOPO, slap/pop,
  bends, vibrato/tremolo, slides) keep their full colour, and a selected or hovered
  repeat gets the normal rim, halo, panel and WHITE numbers, so both what differs
  between two strums and the selection read. Marks LOD draws a repeat exactly like any chord. The 3D preview keeps the
  game's frame-only box. A repeat is a chord like any other to input: nothing under
  `UI/Input`, `Services/Editing` or `Services/State` reads the repeat decision
  (`UI/Input/RepeatChordSelectionTests`). Pinned by `NoteLayerRepeatChordTests`
  (dimmed opaque geometry, full-colour techniques and selection, Marks unchanged,
  zero allocations) and, on the GPU, `Rendering/Headless/HeadlessRepeatChordTimelineTests`.
- The 2D timeline has a density LOD (`NoteLod.Compute`, thresholds in `ChartEditorConstants.RenderMetrics`, decided once per frame in `ChartRenderer.RenderTimelineFrame`): below ~18 px per note column the note-attached labels drop (`rc.DrawNoteLabels`), below ~6 px gems/sustains render as marks/strips and chord panels are skipped; `BackgroundLayer` also skips beat/measure lines closer than 2 px to the previously drawn one. Keep new note-visual work behind these gates. **The decision has HYSTERESIS (#114):** `ChartRenderer` passes the level the previous frame drew (`rc.NoteLod`) to the four-argument `NoteLod.Compute`, and each threshold is a ±`NoteLodHysteresis` (15 %) band. Scrolling alone changes the visible column count by one, so without the band a constant-density passage on a threshold blinked between gems and marks (129 flips in 10 s at 60 fps). Pinned by `NoteLodTests.AConstantDensityPassage_NeverChangesLevelWhileScrolling`.
- **The DRAWN view start is snapped to a whole device pixel (#113):** `ChartRenderer` takes `rc.ViewStartMs` from `TimelineViewMath.SnappedViewStartMs(anchor, zoom, VisualPixelScale.For(this))`, so every element's sub-pixel phase is a constant of its chart time and the frame scrolls as one rigid strip. Zoomed out the view moves under a pixel per frame, and with a fractional origin each 3 px note mark (one sample per pixel, no AA) crossed a pixel boundary on its own frame — ~97 marks moved 1 px while ~70 did not. The anchor (and so hit-testing) stays exact; the drawing is at most half a device pixel off it. A layer that ROUNDS an x must round in device pixels from that origin (`rc.PixelScale`; see `WaveformLayer`), never `(time − ViewStartMs) × zoom` per element in layout units. Pinned by `TimelineScrollSnapTests` (pure) and `Rendering/Headless/HeadlessTimelineScrollRigidityTests` (GPU: every mark takes the same step, each frame is the previous one translated).
- **A per-frame 2D layer never `foreach`es an `ObservableCollection<T>` — it
  walks it INDEXED, and that is a LINT now.** `ObservableCollection<T>` derives
  from `Collection<T>`, which has no public struct enumerator, so a `foreach`
  binds to `IEnumerable<T>.GetEnumerator()` and boxes a `List<T>.Enumerator` —
  one allocation per collection per layer per frame, even when the collection is
  empty. The rule was already written down twice on the snapshot side
  (`FrameSnapshot.Build.cs` ~106-110, `SnapshotUpdateContext.cs` ~93-96) and got
  broken anyway in the UI's own layers: `BackgroundLayer.DrawBookmarks` and
  `.DrawAnnotations` each boxed one per frame. `UI/Rendering/
  LayerCollectionEnumerationLintTests` reads every file under
  `Controls/Layers/**`, strips comments AND string literals, collects the
  `ObservableCollection<>` member names declared by `ChartRenderContext.cs`,
  `TrackEditorContext.cs`, `DotCharter.Rendering/Data/*.cs` and
  `ChartEditorViewModel*.cs`, and **resolves local aliases** (`var bookmarks =
  rc.Editor.Bookmarks;` then `foreach (… in bookmarks)`) — which is the exact
  shape both real offenders had, so a lint matching only `in rc.X` would have
  passed over both. It carries two non-vacuity floors (≥ 8 member names, ≥ 10
  layer files); reverting either loop turns it red.
- **`RenderStringCache.MmSsFf` used to stop caching 2:44 into every song.** Its
  key is `ms / 10`, so a song produces one new key per 10 ms of playback, and
  the old `if (Count < 16384) TryAdd(...)` REFUSED every entry past 16,384 keys
  = **163.84 s** — from there on `PlayheadLayer`, `CursorLayer` and
  `StatusBarViewModel` each formatted a fresh `TimeSpan.ToString` per frame at
  up to 60 Hz, for the rest of the song. The cap is a **clear-and-continue** now
  (the dictionary keeps working; it just restarts), and a one-entry
  `(key, string)` memo sits in FRONT of it, so at 60 fps roughly five frames in
  six never reach the `ConcurrentDictionary` at all. The memo is an immutable
  record published by a single reference write — three callers on two threads,
  and a mutable pair would tear one timestamp's key onto another's text.
  Pinned by `UI/Rendering/RenderStringCacheTests`
  (`MmSsFf_PastTheOldCap_StillCachesInsteadOfFormattingEveryCall`,
  `…_TheCacheStaysBounded_ByClearingRatherThanRefusing`,
  `…_KeepsAOneEntryMemoInFrontOfTheDictionary`); the first and last go red
  against the old body. No wall-clock assertion — all three are structural.
- **The waveform layer's x is `(blockIndex × blockMs − ViewStartMs) × zoom`, the
  same formula every other layer uses for a chart time — so the pyramid's block
  width has to be REAL, and it was not.** Chart time IS audio-file time here
  (`ChartData.MainAudioOffsetAdded` records silence baked into the working audio
  by `ImportAudioPreparer`, and beat 0 sits at that boundary — it is export
  metadata and no renderer may consume it), so the only thing that can put the
  waveform somewhere the notes are not is the index→time conversion. The native
  generator used to round samples-per-block to an integer, making a nominal 2 ms
  block 1.99546 ms at 44.1 kHz: the strip ran 0.227 % fast and the waveform was
  drawn to the RIGHT of its audio, growing all song (+136 px at 1 px/ms one
  minute in). Fixed in the native binner (fractional boundaries) + a
  `WaveformDiskCache` schema bump; pinned by `WaveformGenerationAlignmentTests`.
  48 kHz was always exact, which is what hid it.
- **The waveform has its OWN LOD, and its resolution ceiling is the PYRAMID,
  not the layer (v0.35).** `WaveformLayer` reads whichever mip level the pure
  `Helpers/WaveformLod.Compute` picks — the first level at least
  `TargetColumnPx` wide on screen. Level 0 is the finest level that picker can
  ever reach, so the base block (`WaveformSamples.GenerationBlockMs`, 2 ms
  since v0.35 — see `docs/agents/services/stem-separation.md`) is what actually
  decides how much detail exists. At the old 20 ms base every zoom above
  ~0.125 px/ms bottomed out at level 0 and the waveform was a staircase with
  20 ms treads (20 px at 1 px/ms, 40 px at 2) — the "it looks low-res" report;
  no renderer tuning could recover detail the pyramid never stored.
  - **`TargetColumnPx` is DERIVED as `OutlineMinStepPx / 2` and that relation is
    load-bearing.** Mip levels are octaves, so "first level at least the target
    wide" always lands in `[target, 2×target)` = `[1.5, 3)` px — strictly below
    the outline threshold — which is what switches the 6-quad-per-column
    silhouette ribbon OFF at every zoom except where the pyramid bottoms out at
    level 0. That is why 2.5× the columns of the old 2.5 px target costs LESS at
    the editing zooms. Change one constant and you change the other's meaning.
  - **The layer draws a PEAK envelope + a LOUDNESS BODY + a zero line.** Peak
    and body come from the two pyramids `WaveformSamples.FromBasePeaks` derives
    in lockstep, so ONE level indexes both and the body is inside the envelope
    by construction. The body pass is skipped at level 0 (max == mean over one
    block ⇒ pure overdraw) — which is the deepest zoom, i.e. the busiest frame.
    The zero line is ONE quad per frame and is emitted BEFORE the empty-window
    bail, so the strip keeps a baseline past the end of the audio.
  - Measured A/B vs the v0.34.1 layer (1920 px, real batcher, no GPU): 0.93× /
    0.82× the time at 0.05 / 0.2 px/ms (the ribbon switching off pays for the
    extra columns), 1.64× at 1.0, 3.0× at 0.5 and 10.7× at 2.0 — the large
    multipliers are all zooms where the old layer was cheap because it had
    nothing to draw. Absolute cost is now roughly FLAT at ~19–86 µs/frame
    instead of collapsing at high zoom; the ceiling is 1.25× the old ceiling and
    under 1% of a 100 fps frame. Zero allocations on the warm path.
  - Regressions live in the DEFAULT gate, not behind the GPU flag:
    `WaveformLodTests` (the pure picker) and `WaveformLayerGeometryTests`, which
    runs the real layer into a real `TimelineBatcher` and asserts on the staged
    floats (the `NoteLayerCullingTests` trick). **The GPU golden
    `Waveform_RibbonOutline_ContinuousAcrossDirectionChange` now scans only ABOVE
    the centreline** — the zero line spans every column, so a whole-column
    "has content" gap check over it is vacuous.
- Ribbon rows auto-collapse when EMPTY (while `RibbonVisibility.AutoHideEmpty`, the default): `ChartRenderer.TopRibbon` composes the per-track toggles with the track's content through `Editing/Tracks/RibbonLaneRules` (one tone counts as empty; the Sections row survives as the right-click event-dialog entry point when no events exist at all). Drawing AND hit-testing both read `TopRibbon`, so keep any new row logic in `RibbonLaneRules`. The "has a section / has a phrase" walk behind it is latched per renderer (`Services/Editing/Tracks/RibbonEventContentLatch`, keyed on the list, its count, `ChartEditorViewModel.HistoryRevision` and a 250 ms staleness bound, #309) — it ran on every frame and every hit test. The default `MainSplitGrid` split is `EditorSplitDefaults` (3:4 timeline-over-dock: the 1080p look of 412 : 550 as a ratio, so the timeline grows with the window instead of sitting on its floor) — `MainWindow.axaml`, `MainWindowLayoutController` and `ResetLayoutToDefaults` read it. A closed editor saves BOTH row heights (`LayoutTimelineAreaHeight` + `LayoutDockHeight`), restored as the exact star ratio (`MainWindowLayoutController.SavedSplitStars`).
- 2D timeline visual toggles (0.14.3) live in the `Controls/Timeline2DVisuals` static channel (`PolishEnabled` default on, `LaneTintEnabled` default off), persisted as `AppConfig.Timeline2DPolishEnabled`/`Timeline2DLaneTint`, mirrored at startup (`App.OnFrameworkInitializationCompleted`) and by Settings → Graphics → "2D timeline" (live-apply on flip, revert on Cancel — same pattern as `Preview3DPacing`). New decorative layer work must check `Timeline2DVisuals.PolishEnabled` and fall back to the flat look.
- **Three ways a 2D element's CLICK TARGET drifted off the box it DRAWS, all
  fixed 2026-09-04 — the shape to check whenever a layer grows a new
  clickable thing.**
  - **A rect registered beside the draw call, rather than taken from it.**
    `EventLayer.DrawPinnedLabels` painted its sticky section/phrase/tone chip
    through `DrawBadge` (9 px of text padding per side, top at
    `RibbonLayout.BarTopIn(rowY)`) and separately handed
    `PinnedLabelHitRegistry` a `new Rect(x, rowY, MeasureTextWidth + 12,
    BarHeight)` — 2.5 px high and 6 px narrow. `EventLayer.PinnedRect` is the
    one box now, off `DrawBadge`'s own returned width.
    `Rendering/EventLayerPinnedLabelHitTests` compares the registered rect with
    the chip's emitted vertices.
  - **`PinnedLabelHitRegistry` is a DI SINGLETON and there is one ChartRenderer
    per stacked track row plus the main timeline**, each with its own
    `EventLayer` clearing and refilling it every frame — so the last renderer to
    draw wiped the others and its rects, in ITS control's pixel space, were
    hit-tested against another control's pointer. Entries are keyed by the
    OWNING viewport (`Clear`/`Add`/`TryHit`/`Forget` all take it; `EventLayer`
    is constructed with its renderer, `EventInteractionTool` looks up
    `context.Viewport`, `ChartRenderer` forgets itself on detach).
  - **A NEAR MISS must never outrank an EXACT hit.**
    `ChartHitTester.HitTestNote`/`HitTestChord` report which one they found
    (`out bool exact`) and `ChartHitTester.ResolveSound` applies the
    precedence — because every press path runs the chord tester first and let a
    chord win unconditionally. A gem is 26 px across while the lane pitch is the
    lane area over the string count, so a short viewport (a stacked row, a
    six-string chart in a small pane) puts the neighbouring lane inside the 6 px
    `NearMissHitTolerance` ring: clicking a single note dead centre selected the
    chord above it, and drag/delete/context menu all followed the wrong sound.
    Two exact candidates keep the historical chord-wins rule.
  - The same class of drift, on the lyrics lane: a pill is DRAWN at least
    `LyricPillMetrics.MinPillWidthPx` wide while `HitTestLyric` read the
    syllable's DURATION alone, so at 0.2 px/ms a 25 ms syllable was an 18 px
    chip over a 5 px target and the right half of it created a new lyric. The
    floor (and the degenerate-end fallback) are one shared constant now. **The
    text-width half of the layer's widening is still unmatched** — it needs a
    font atlas the pure tester has not got, so a syllable whose TEXT is wider
    than both its duration and the floor still overhangs its click span.
- **Marquee selection (plan 6.5).** Left-drag on empty lane space in
  `HighwayInteractionTool` arms a rubber band; the rect is published through
  `IChartViewport.SetMarquee` → `ChartRenderer.MarqueeChanged` and drawn by the
  hit-test-invisible `Controls/MarqueeSelectionOverlay` sibling (main timeline
  in `MainWindow.axaml` + each stacked row) — an Avalonia layer, deliberately
  NOT the batched timeline scene, so pointer moves never redraw the chart. Rect↔note/chord
  geometry is the pure `Input/MarqueeHitTester` (shares `ChartHitTester`'s
  screen-rect builders; unit-tested, no Avalonia instance needed beyond
  `Rect`/`Point`); release commits via `ISelectionService` (Shift = additive,
  plain empty-click still clears). Shift-click RANGE selection selects all
  object classes — it no longer honours the `PasteInclude*` flags (those filter
  at paste time only, `EditingClipboardCoordinator`).
- The sheet strip (`SheetMusicView`) renders AlphaSkia chunks via `ICustomDrawOperation` straight onto the GPU canvas; the playback cursor AND the selection highlight each live on their own separate overlay so chunk bitmaps never re-blit per frame. The strip binds the shared `ISelectionService` (`Selection` styled prop) and, since 0.18, `IHistoryService` (`History` styled prop — every committed chart action re-renders the notation; both subscribe on attach / unsubscribe on detach, pane views are re-created on float/redock). **The strip is an EDITING surface:** the exporter returns a per-emitted-beat map and the renderer extracts AlphaTab's per-bar beat bounds + the tab staff's string-line lattice (`NoteHeadBounds.Y` is already ON the string line — don't re-center it); zipped per bar they resolve a click to the exact domain note/chord (`SelectSoundsRequested`; a rest click clears; `SelectRequested`/nearest-time is only the fallback when a bar's counts mismatch). Double-click inserts a note (`AddNoteRequested` → pane VM quantizes via `ChartEditorViewModel.SnapToGrid`, default fret = previous note on that string, commits through `IHighwayEditingService.CommitHandShapeDragAndDrop` — one undo step, new note selected); plain digits type frets GP-style (`SetFretRequested`, multi-digit within 800 ms, the strip is `Focusable` and takes focus on click) while unhandled keys (Entf, arrows) keep bubbling to the window's global keybind routes. **The scrollContent Grid MUST keep its `Background = Brushes.Transparent`** — without it the ScrollViewer is the hit target and every pointer gesture on the strip (including the historical click-to-seek) silently dies.

- **The 2D timeline pre-registers the WHOLE chart's text into its glyph atlas
  (`Controls/ChartTextPreRegistration`, 2026-09-02) — the 3D driver's
  `PreRegisterChartText` twin.** An atlas rebuild is a 2048² re-rasterize of
  every registered glyph, `DeviceWaitIdle`, a fresh 16 MB device image and a
  16 MB staging copy, then `QueueWaitIdle` — on the UI thread, inside
  `BeginFrame`. The seed set has no apostrophe, `!`, accented or CJK glyph, and
  the renderer registered only the strings inside the current WINDOW per
  frame, so the first `don't` or `Gtr_Lead!` to scroll into view during
  playback rebuilt the atlas mid-frame: an unmistakable hitch, once per song,
  because `ResetToSeedCharacters` empties the set on every project switch.
  `RenderTimelineFrame` now registers lyrics, phrase + section (and the
  UPPERCASED section, the drawn form) and tone names for the whole chart,
  latched on the three collections' instance + count through the same
  `ChartTextRegistrationLatch` the 3D driver uses (grown a third pair for the
  tones, and a `Reset()` the project-switch hook calls beside the seed reset),
  so the one rebuild lands on the first frame after a load or a text edit with
  the transport parked. The windowed per-frame scan stays as the safety net.
  `UI/ChartTextPreRegistrationTests` (after the pass, no windowed string of the
  chart adds a character — and the same strings DO grow the set without it)
  + `UI/Preview3DDriverWiringTests` (ordering: pre-registration → windowed
  scan → `BeginFrame`; latch reset beside the atlas reset).

- **A composed surface is sharp only while its destination rectangle is a
  WHOLE-PIXEL rectangle, and the shell's UI scale silently took that away
  (2026-09-16).** `VulkanTimelineBackend` sized its swapchain
  `ceil(Bounds × scale)` while declaring `visual.Size = Bounds`. Those two are
  the same rectangle only when `Bounds × scale` is already an integer, which
  Avalonia's layout rounding guarantees for `scale == TopLevel.RenderScaling`
  and for nothing else. The interface-scale wave (`f58ad11c3`, Auto made the
  DEFAULT on 2026-09-12) put a fractional `ScaleTransform` on `MainWindow`'s
  `RootScaler` between the timeline and the window; layout rounding rounds the
  INNER DIPs against `RenderScaling` alone, so `Bounds × uiScale × RenderScaling`
  is fractional again in **size and in origin**. The compositor then bilinearly
  resamples the entire timeline — which is invisible on a line and obvious on
  a glyph, i.e. "the chart editor's text is blurry". `VisualPixelScale` fixed
  the RESOLUTION half of this a release earlier (rendering at 1/uiScale) and
  was mistaken for the whole fix; alignment is a separate failure and a surface
  can carry exactly the right number of pixels and still be resampled.
  **`Controls/ComposedSurfaceGeometry` is now the one place both halves are
  computed**: `Measure` returns the pixel size AND the DIP size *derived from
  it* (`PixelWidth / pixelScale`, so the product is exact — never `Bounds`),
  and `SnapOffset` + `VisualPixelScale.DeviceOrigin` nudge the surface visual
  ≤ half a pixel onto the grid. The ortho projection is built from the derived
  DIP size too, and the vertical counter-flip centres on half of it so the
  mirror axis is a whole pixel as well. Pinned by
  `UI/ComposedSurfaceGeometryTests` (arithmetic, default gate),
  `UI/ComposedSurfaceWiringTests` (source lint — `visual.Size = Bounds` is a
  one-line edit that looks like a simplification) and
  `Perf/ComposedSurfaceGridUiPerfSmokeTests` (a real window under a fractional
  `RootScaler`, which is where the fractional input comes from).
  The glyph atlas needed no change: `ChartRenderer` feeds it
  `VisualPixelScale.For(this)` every frame and `SetRenderScale` repacks only on
  a real change, so it already rasterizes at the composited scale.

- **Surface alignment is only HALF the blur; the other half is the glyph quad
  itself, and it was never scale-specific (2026-09-16).** `TimelineBatcher`
  emits quads in LOGICAL units and the ortho multiplies by the surface scale, so
  a glyph's real position is `logical × renderScale`. Label positions are always
  fractional (a time→pixel mapping, a centred string), so every glyph straddled
  two pixel columns and the sampler blended them — inside a perfectly aligned
  surface. The sheet had the mirror fault: `margin = 4 × renderScale`,
  `texWidth = max(advance, right) + margin` and `padding = 8 × renderScale` are
  all fractional, so each glyph's SOURCE rectangle started mid-texel as well,
  and Skia rasterized each glyph at whatever sub-pixel phase its predecessor's
  advance left behind. Fixed at both ends: the sheet packs on whole texels
  (`MathF.Round`/`Ceiling` on margin, padding and texWidth) and `AddText` snaps
  the pen — once for y, **per glyph** for x, because a fractional advance walks
  it back off the grid after the first character — so a glyph is a 1:1 texel
  copy, which is what native renderers get from integer pen positions. The
  shift is under half a physical pixel. Pinned by
  `Rendering/GlyphPixelSnapTests`.
  **Measured, playhead time chip, shell scale 1.30, `fringe/core` (antialiased
  pixels per full-intensity pixel — background-free, so it does not move with
  the strip's alignment): v0.55.1 `2.65` → surface alignment alone `1.90` → plus
  the glyph snap `0.82`, against v0.54.0's `1.25` at scale 1.0.** Peak stroke
  luminance 244 → 252 → 255 over a 93 background. Both fixes are real and the
  glyph snap is the larger one for TEXT; surface alignment is the larger one for
  1-px GEOMETRY.

- **A capture at 1920×1080 does NOT exercise the interface scale, and three
  release-comparison captures were read that way before anyone checked
  (2026-09-16).** `AutoUiScale` derives from the window's CLIENT HEIGHT against a
  1080 baseline and **never shrinks** (`MinScale = 1.0`), so a 1080-tall window
  answers exactly 1.0 — the perf autopilot's default
  `DOTCHARTER_AUTOPILOT_BOUNDS=-2460,60,1920,1080` pins every capture to the
  un-scaled case. The tell is in the pixels: the playhead time chip is 66 px
  wide at scale 1.0 and 88 px at 1.30, and v0.54.0 and v0.55.1 captures at those
  bounds are BYTE-IDENTICAL across the chip's glyphs. **Use a window at least
  ~1400 DIPs tall** (`-Bounds '-2460,0,1920,1430'` on a 1440p panel) when
  comparing anything scale-related, and check the chip width before believing
  the comparison.

- **Measure text on the INK, not on a fixed strip.** The strip the first W2
  comparison used (400×24 over the time-label row) is dominated by the
  section-name bar underneath it — a bar row carries ~1000 bright pixels against
  a label row's ~40 — and the dock split is PERSISTED, so the same strip
  catches a different number of bar rows in each build. One bar row is ~400
  bright pixels over a 400 px strip, which is the whole of the "1343 → 956"
  gap that read as a regression. Anchor on a fixed-content element (the playhead
  chip) and use a background-free ratio.
