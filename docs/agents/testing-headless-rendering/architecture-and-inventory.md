# Headless rendering (testing) — architecture notes; seed test inventory; future CI

## Architecture notes

- **Vulkan headless is the composed path's sibling.** `VulkanContext`
  `InitializeDeviceHeadless` creates a device with no surface/swapchain
  extensions; `VulkanSwapchainManager.RecreateHeadless` allocates ONE plain
  device-local BGRA8 image (`ColorAttachment|TransferDst|TransferSrc`) through the
  shared `CreateImageResources` tail. `VulkanRenderer` runs `_headlessMode`
  branches in `BeginFrame`/`EndFrame` (no acquire, no semaphores); `EndFrame`
  records `vkCmdCopyImageToBuffer` into a persistently-mapped host-visible buffer
  and fence-waits. It shares the pipeline manager's non-presentable output layout
  with the composed path (the color image ends in `COLOR_ATTACHMENT_OPTIMAL`,
  never `PRESENT_SRC`). All output modes work (Direct/MSAA/Blit/Sampled); the blit path
  leaves the image copy-ready.
- **Texture substitution is a CHOICE now, not a limitation.** The three chart
  sheets are `EmbeddedResource`s of `DotCharter.Rendering` and decode with
  SkiaSharp, so they load headless perfectly well — the historical
  `avares://DotCharter/Assets/...` coupling is long gone.
  `Fretboard3DEngine.InitializeHeadless(useRealChartTextures: false)` is still the
  DEFAULT and still substitutes 1×1 solid-white textures, because every golden
  frame here was captured against them and a texture-less gem is deterministic
  flat vertex colour (that is also why the 1×1 white INLAY is opaque and occludes
  the string plane — see the seed-test caveat below).
  **Pass `true` when a human is going to LOOK at the frame**: `new
  HeadlessHighwayRenderer(true)` renders the real sheets, which is what
  a review frame should — a white board and untextured gems read
  as a rendering bug to a reviewer. **The app paths are unchanged either way**;
  both flags only exist behind the headless one.
- **3D text renders headless too, since v0.42.2 — and enabling it was a BUG FIX,
  not a convenience.** The atlas used to be skipped, because `FontAtlas` once
  rasterized through Avalonia's `RenderTargetBitmap` on the UI thread. That
  stopped being true when the rebuild-deadlock fix moved it to SkiaSharp (no
  Avalonia type, no thread affinity), but the skip stayed — and `TextBatcher`
  answers a null atlas by making every text draw a **silent no-op**. So every 3D
  golden frame was blind to text while passing, and three releases of invisible
  chord names shipped under a green gate. **If you ever re-disable the atlas
  headless, every 3D-text assertion in this suite silently stops testing
  anything**; `HeadlessChordNameTests.TheHeadlessRendererReallyRasterizesText`
  is the tripwire for exactly that.
- **2D text renders headless** because the shared `ChartFontAtlas` base
  rasterizes glyphs with SkiaSharp directly (no Avalonia app); the headless
  renderer's `VulkanTimelineFontAtlas` instance only uploads the sheet.
  (Historical note for archaeology: the deleted GL headless twin ran an EGL
  **pbuffer** on Avalonia's shipped ANGLE runtime; its `EglInterface`
  export-prefix gotcha died with it — see the v0.33 GL-removal commits if it
  is ever needed again.)

## Seed test inventory (what each guards)

**Vulkan** (`HeadlessVulkanRenderTests`):

- `RendersFixtureChart_BoardStringsAndGemsVisible` — a fixture 6-string chart
  renders: board visible, saturated string/gem colour present, lower-centre board
  region populated, no all-black frame.
- `CameraFill_80_100_120_ProduceDistinctFramings` — the Kamera-Distanz knob
  (`Preview3DCameraFill`) visibly changes the framing at 80/100/120.
- `FogAtmosphere_NoOpaqueEdgeBand` — regression guard for the 2026-07
  fog/atmosphere fixes: the background fades vertically at the frame edges, never
  a flat opaque vertical band.
- `ChordsAnchorsAndStringHighlight_Render` — a 6-string chart WITH CHORDS and an
  imminent hand-position change (fret 1→7 at 700 ms, inside the anchor-preview
  lead): exercises the board-face depth stack (fret bars vs inlay dots), the
  active-string highlight and the upcoming-anchor floor sweep. Structural asserts
  only (renders / colourful / lower-board populated). One headless caveat
  remains: the 1×1 white inlay fallback is OPAQUE, so it occludes the string
  plane behind it and the string-brightening is not visible headless. (Text no
  longer no-ops — the fret numbers DO render here now.)

**Vulkan 3D chord names** (`HeadlessChordNameTests`): renders one named chord
twice, changing only the template NAME, so the differing pixels ARE the label —
then asserts it rendered at all, is large enough to be a label, sits above the
neck, and does not touch a frame edge (the shape a clipped/off-screen label
takes). Plus a vacuity guard that headless text rasterizes at all. This is the
test the v0.41–v0.42.1 invisible-chord-name bug needed and did not have.

**Vulkan 2D goldens** (`HeadlessTimelineGoldenTests`): waveform ribbon
(solid stream), measure pills (general stream incl. the Vulkan font atlas —
content asserted inside the metadata band), a combined interleaved-stream
scene (proves the second stream contributes vs a waveform-only render of
the same context), and repeat-render BYTE stability. Formerly
`HeadlessTimelineParityTests`, which rendered every scene through GL AND
Vulkan and asserted them structurally close — that parity gate PASSED at
the v0.33 port (commit `43866e8d`: 0.000% pixel diff on the interleaved
streams) and the GL baseline was removed together with the OpenGL path.

**Vulkan 2D behavior goldens** (`HeadlessTimeline2DRenderTests` — the GL
seed scenes, ported unchanged onto the Vulkan facade at the GL removal):

- `Waveform_RibbonOutline_ContinuousAcrossDirectionChange` — the waveform
  silhouette is ONE miter-joined ribbon: no interior gap column across the peak's
  direction change (`AddPolylineRibbon`).
- `Waveform_LodGate_OutlineFlipsAtThreshold` — the documented
  `drawOutline` LOD gate (`stepWidthPx >= 3`) flips the layer's output: the
  silhouette ribbon appears/disappears across the threshold (same shape/zoom,
  same LOD level).
- `MeasurePills_BpmChangeDedup_ChangesTextRegion` — the measure-pill dedup
  (`MeasurePillPolicy`): a BPM-change chart draws extra "BPM" pill text vs a
  constant-BPM chart (more content in the metadata band).

## CI: the software lane

These tests stay env-gated OUT of the default gate. On a GPU-less box, or a CI
runner, they run on Mesa lavapipe (`DOTCHARTER_GPU_TESTS=sw`; how-to in
[gating-and-rendering-frames.md](gating-and-rendering-frames.md), numbers in
[tolerance-determinism-validation.md](tolerance-determinism-validation.md)).
`InitializeDeviceHeadless` needs no surface, so the software device covers BOTH
renderers unchanged. lavapipe was chosen over SwiftShader because it has a
maintained Windows x64 zip release (SwiftShader publishes no Windows binaries).
CI runs it only through the manual `software-gpu-tests` input
(`gpu-goldens-software` job; `CiWorkflowLintTests` keeps it opt-in).

