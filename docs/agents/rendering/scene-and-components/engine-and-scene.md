# Scene, components, snapshot and assets

Deep reference for `DotCharter.Rendering/AGENTS.md`. The frame's CONTENT: the
engine that orchestrates it, the scene lists, the `IRenderable` components, the
quad/text batchers, the per-frame `FrameSnapshot` projection and the embedded
texture assets. The DEVICE side (swapchain, MSAA, render scale, composed
presentation) is [vulkan-device-lifetime.md](../vulkan-device-lifetime.md); the
pace/shader knobs are [highway-pace-and-shaders.md](../highway-pace-and-shaders.md).

## The engine

- `Engines/Fretboard3DEngine.cs` — orchestrator: loads SPIR-V, builds the
  `RenderScene`, computes the anchor-follow camera MVP (with the Vulkan
  clip-space correction), runs the frame passes (crowd-background ortho
  underlay → opaque → transparent → 3D text → vignette ortho overlay →
  orthographic HUD text; the two ortho quad passes are 0.14.2 effects, each
  behind its snapshot toggle, ortho z=+1 → depth 0, no depth writes). The
  background pass also draws the **show lights' stage lights** (see
  components-and-batching.md § Show lights) after the crowd gradient. There is
  **no stage glow** behind the far end of the highway any more — that is where
  the notes are read (`HeadlessBackgroundBandingTests` asserts the sky there is
  not lit; the full-width amber horizon band went in 0.15.1). An ortho quad pass
  before the HUD text draws the editor's show-light chip plate.
  **`highway.frag` ends in ±1 LSB triangular dither scaled by 1/alpha** (capped
  at 4 LSB) — the frame is 8-bit and the dark sky and the vignette banded under
  the old ±0.5 LSB uniform noise; the same test measures it. Exposes `IsCameraAnimating` (the camera easing snaps to
  target within a sub-pixel epsilon so it settles deterministically) as the
  idle-gate seam for `Preview3DRenderDriver`'s 0.14 P6 paused-loop parking. The
  per-frame visible-text `FontAtlas.RegisterCharacters` scan is gated by
  `ComputeRegisterSignature` (reference-hash of every registrable string —
  the 2D `ChartRenderer` approach, unit-tested); `RebuildIfNeeded` stays
  outside the gate because `PreRegisterChartText` also queues glyphs.
  **Every string a component DRAWS must appear in BOTH the signature and the
  registration block, or its non-ASCII characters are silently dropped** —
  `FontAtlas` seeds ASCII `' '..'~'` only and `TextBatcher` skips a character
  `TryGetGlyph` does not know, with no fallback glyph and no log line. The
  HUD's tone badge (`HudComponent.RenderActiveTone`, the only surface drawing
  `FrameSnapshot.ActiveToneName`) was in neither for as long as it existed,
  while the 2D twin registered tone names explicitly — so a user-supplied
  preset name lost exactly the characters that made it that name. It is in
  both now, plus `PreRegisterChartText`, which takes the chart's `ToneChange`
  list as a third latched collection (`ChartTextRegistrationLatch` already had
  the overload, from the 2D side). The per-frame half is NOT redundant with the
  load-time half: with in-game tones on, the name is the RIG's applied preset,
  which no chart-level pass can see. Pinned by
  `Rendering/HudToneGlyphRegistrationTests`.

## `Scene/`

- `Scene/` — `IRenderable`, `RenderScene` (four ordered lists: Opaque /
  Transparent / Ui / Hud), `RenderContext` (per-frame DTO; carries
  `AnchorPreview` — the 0.14.2 upcoming-anchor lookup computed once per frame
  by the engine via the pure `NextAnchorWindow`, unit-tested, consumed by the
  dimmed fret-number preview and the floor sweep).

