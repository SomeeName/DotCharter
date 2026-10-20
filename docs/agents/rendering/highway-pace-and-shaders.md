# Highway pace, shaders and the camera

Deep reference for `DotCharter.Rendering/AGENTS.md`. The two things a user
setting can move through the whole frame: the scroll pace / draw distance
(`DotCharter.Core/Graphics/HighwaySpeedPolicy.cs`) and the shader effect bits + camera framing
(`Shaders/`). Both are RUNTIME state on `RenderContext` / `FrameSnapshot` —
never a `const`.

## Highway pace + draw distance

- **Highway pace + draw distance (`DotCharter.Core/Graphics/HighwaySpeedPolicy.cs`) — the geometry is
  RUNTIME, never a constant.** Scroll speed and draw distance are user settings.
  `Fretboard3DEngine.RenderFrame` resolves them once per frame through the pure
  `HighwaySpeedPolicy` (clamped there — a zero scroll speed divides into an
  infinite scale) and puts the results on `RenderContext`: `TimeToZScale`,
  `FadeStartZ`, `FadeEndZ`, `HorizonMs`, `GetZDepthForTime`, `ZForMs`,
  `AnticipationMs`. **Renderers read the context, never `FretboardConstants`** —
  a `const` folds into every call site and silently ignores the setting, which is
  exactly why the pace was unchangeable before.
  - **Scroll speed is in ROCKSMITH UNITS: LOWER IS FASTER.** Counter-intuitive,
    but it is the format's convention (a CDLC stores it, DLC Builder's tooltip
    says so), and inverting it would make every imported chart scroll backwards
    from how it was authored. `HorizonMs = drawDistance × scrollSpeed × 50`.
  - **There is ONE look-ahead notion: `RenderContext.HorizonMs`.** There used to
    be three that disagreed (an unnamed `FadeEndZ / TimeToZScale`, a stale
    `VisibilityWindowMs = 2500`, and six hardcoded `+ 10000` pre-filters). Do not
    reintroduce a literal — the horizon reaches 12 s. One of those six survived
    in `HighwayRenderComponent.RenderHandShapes` until 0.35: at max draw
    distance with a slow scroll speed a hand-shape bracket between 10 s and 12 s
    of look-ahead was culled while the board still showed that stretch, so the
    bracket popped in mid-board instead of flying in from the horizon. Pinned by
    `HighwayHorizonCullTests` (GPU-free — `QuadBatcher` only stages floats until
    it is flushed, and `QuadBatcher.StagedFloatCount` exposes what a pass
    emitted).
  - **Anything that FEEDS the renderer must size itself from the horizon**, not a
    fixed window: `Preview3DRenderDriver`'s look-ahead is
    `max(Preview3DLookAheadMs, horizon + margin)`, and `GameplayStartPlan.LeadInForHorizon`
    keeps the lead-in ≥ the horizon so the first note flies in from the far edge
    instead of appearing mid-board. A fixed feed silently stops projecting notes
    the board can still show.
  - Resolution precedence lives in `HighwaySpeedPolicy.ResolveScrollSpeed`
    (per-playthrough > global override > the chart's own value > Rocksmith's 1.3)
    — the single source of truth, shared by Settings and Play Mode so they cannot
    disagree. `Arrangement.ScrollSpeed` is **nullable on purpose**: null = "the
    author said nothing", which must stay distinct from a deliberate 1.3.

## `Shaders/` — GLSL, `.spv` and the effect bits

- `Shaders/` — `highway.*` + `text.*` GLSL **and** committed `.spv`; both are
  embedded resources named `DotCharter.Rendering.Shaders.<file>`. Vulkan loads
  the **precompiled `.spv`** only.
  **Recompile step (required after ANY .vert/.frag edit):**

  ```powershell
  dotnet run --project tools/ShaderCompiler -- DotCharter.Rendering/Shaders
  ```

  `tools/ShaderCompiler` (deliberately outside DotCharter.sln) uses
  Silk.NET.Shaderc (same Silk.NET version as the renderer — no Vulkan SDK /
  glslc install needed) targeting Vulkan 1.0 with performance optimization.
  Commit the regenerated `.spv` together with the GLSL change; only regenerate
  the shaders you actually changed (the compiler is not byte-stable across
  versions, so blanket regeneration churns the other .spv for nothing).
  **In-shader effects (0.15.6):** the highway push-constant block carries a
  `uint uEffects` bit set (`HighwayEffects` in C# — keep the GLSL `EFFECT_*`
  constants in sync, and the block layout-identical in `highway.vert` AND
  `highway.frag`). `Fretboard3DEngine.RenderFrame` computes the bits once per
  frame from the snapshot toggles (e.g. `EnableHighwayLighting` →
  `HighwayEffects.Lighting`, plan 5.6: two-sided key+fill directional shading
  from the streamed normals, surface modes only — glow modes 2/6/7/8/10 stay
  emissive; `EnableStringShimmer` → `StringShimmer`, plan 5.7: uTime light
  band on the string cores' vertex mode 12, Live-Play-suppressed like the
  other time-based effects — uTime is SONG ms so it freezes when paused,
  consistent with the idle gate); the ortho passes (crowd background,
  vignette) always pass `None`. Plan 5.7's hit sparks are NOT a shader
  effect: `HitSparkRenderComponent` draws stateless string-colored mode-8
  bursts derived per frame from the pure `HitSparkMath` (unit-tested), gated
  by `EnableHitSparks && !LivePlayActive`. New toggle flow mirrors the
  0.14.2 effects: AppConfig →
  App startup mirror / SettingsViewModel / PlayModeViewModel →
  `ProjectState`/`UserSettingsState` → `Preview3DRenderDriver` staging copy →
  `FrameSnapshot.Enable*` → engine bit.
  - **`Preview3DCameraFill` (0.20.1) rides that exact flow** — an int percent
    (`GraphicsSettingsPolicy.ClampCameraFillPercent`, 80–120, default 100) →
    `FrameSnapshot.Preview3DCameraFill` → `CalculateCameraMvp`, where it is a
    pure MULTIPLIER on both post-projection screen scales. Surfaced as the
    Settings → Grafik / Play-Mode "Kamera-Distanz" slider (distance = 200 −
    fill, so the label direction is intuitive). **Default 100 = the ORIGINAL
    aspect-derived framing, byte-for-byte** (`X = Min(0.5, 1/aspect)`,
    `Y = Min(1.0, 0.5·aspect) + 0.05`).
  - **CAMERA FRAMING IS OWNER-SETTLED (2026-07-16): do not re-tune it.** Two
    reframe attempts (a constant vertical fill 1.28 with a floored/clamped X,
    then an aspect-correct unfloored variant) were both rejected and reverted:
    the constant fill squished the board when the window HEIGHT changed and
    read "way too zoomed" on 6-string charts; the unfloored variant letterboxed
    the neck on the ultra-wide editor pane. The original aspect-derived scales
    adapt to resize (no squish) and are the owner's preferred look; anyone
    wanting a bigger/smaller board uses the Kamera-Distanz slider. See git
    history (fce4284e → 2c655575 → the 0.20.1 restore) before touching this.
