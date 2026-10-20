# DotCharter.Rendering Agent Guide

GPU chart-rendering engine — **Vulkan-only since the v0.33 OpenGL removal**.
Renderers living here:

- **Vulkan 3D = the "highway"/preview** — the primary, fully-owned pipeline.
- **Vulkan 2D (v0.33, `Vulkan2D/`) = the composed 2D editor/timeline
  renderer** — the ONLY 2D path: renders the layer-batched timeline into
  D3D11 keyed-mutex shared images Avalonia's compositor composes
  in-airspace. The historical OpenGL 2D path (fallback + parity baseline)
  was deleted after GL↔Vulkan parity was proven at the port (commit
  `43866e8d`); init failure now surfaces a localized error state in the UI
  instead of a fallback (see `DotCharter.UI` `ChartRenderer`).

Windows-only presentation through the compositor: exported D3D11 keyed-mutex
images, no Vulkan surface and no swapchain (`Rendering/NoNativeSwapchainTests`
keeps the deleted native child-HWND path out). `net10.0`,
`AllowUnsafeBlocks`. References **`DotCharter.Core` only** (UI depends on
Rendering, never the reverse). NuGet: `Avalonia`, `Silk.NET.*`
(Core/Maths/Vulkan/D3D11/DXGI), `SkiaSharp`.

**This guide is a ROUTER, not a reference.** It auto-loads for every agent that
opens a file in this project, so it carries only what everyone needs: the folder
map, the rules that bite from anywhere, and the ONE extra file to open for the
area you are touching. Every invariant, measured number and hard-won gotcha that
used to sit inline is in [docs/agents/rendering/](../docs/agents/rendering/),
moved VERBATIM — nothing was summarised away. **Read the routed file before you
touch that area**, and update it in the same change when you change its
subsystem. Every bare `*.md` name below is a file in that one folder; they are
plain links rather than `@import`s on purpose — an import would re-load the bytes
and defeat the point.

## Layout + routing — one row per area

| Folder / file | What lives there | Read this first |
|---|---|---|
| `Engines/Fretboard3DEngine.cs` | the orchestrator: loads SPIR-V, builds the `RenderScene`, computes the anchor-follow camera MVP, runs the frame passes (crowd-background ortho underlay → opaque → transparent → 3D text → vignette ortho overlay → orthographic HUD text), owns `IsCameraAnimating` and the `ComputeRegisterSignature` glyph gate | `scene-and-components.md` |
| `Engines/HighwayViewport.cs`, `Data/SplitHighwayLayout.cs` | the SPLIT HIGHWAY (built for couch co-op in v0.45; the app has drawn one viewport since co-op was removed on 2026-09-19, and the solo frame still runs through this overload): ONE `BeginFrame`/`EndFrame`, one depth buffer, one present, N scissored scene recordings, each with its OWN snapshot and camera | `split-highway.md` |
| `Scene/`, `Components/`, `Components/NoteRendering/`, `Batching/`, `Text/`, `Assets/` | `IRenderable` + the four ordered scene lists, the scene elements themselves (board, highway, notes, showlights, hit feedback, fog, text overlay, HUD, chord names), the indexed-quad batchers, the shared glyph-atlas base and the embedded texture sheets | `scene-and-components.md` |
| `Data/` — `FrameSnapshot`, `Data/Render/`, `*Constants` | the per-frame, GC-reused flat `Render*[]` projection of the visible chart slice, its windowing contract, dead notes and the gameplay tone HUD | `scene-and-components.md` |
| `DotCharter.Core/Graphics/HighwaySpeedPolicy.cs` (moved to Core in 0.62 so a Presentation settings screen can use the pure math — this assembly still consumes it), `Shaders/` | scroll speed / draw distance as RUNTIME state on `RenderContext`, the `uEffects` push-constant bit set, the `.spv` recompile step and the camera framing/fill | `highway-pace-and-shaders.md` |
| `Vulkan/`, `Vulkan/Composed/` | `VulkanRenderer`/`VulkanContext`/`VulkanSwapchainManager`/`VulkanPipelineManager`/`VulkanResourceManager`/`FontAtlas`, and the Avalonia-composed D3D11-shared-image presentation path | `vulkan-device-lifetime.md` |
| `Vulkan2D/`, `Timeline2D/` | the composed-Vulkan 2D timeline (the only 2D path) + the backend-neutral CPU geometry it consumes (`TimelineBatcher`, `PolylineRibbonMath`) | `vulkan2d-timeline.md` |
| `Headless/`, `Vulkan2D/Timeline2DHeadlessRenderer` | offscreen, windowless golden-frame rendering for both renderers — and the harness's `--dump` | [../docs/agents/testing-headless-rendering.md](../docs/agents/testing-headless-rendering.md) |
| `Diagnostics/` | `FrameTimeRing` — the frame-time history the HUD and the perf baselines read | `highway-pace-and-shaders.md` |

**Task routing** (the same answer, keyed by what you are doing):

| Doing this | Read this |
|---|---|
| adding a scene element, a note visual or 3D text | `scene-and-components.md` (3D text may ONLY be staged from the `UiElements` pass) |
| adding a per-frame field the renderers read | `scene-and-components.md` § `Data/` (window it, and set the three `SnapshotUpdateContext` range fields) |
| changing a shader, or adding an effect toggle | `highway-pace-and-shaders.md` (recompile the `.spv` and commit it) |
| anything about scroll speed, draw distance or look-ahead | `highway-pace-and-shaders.md` (read `RenderContext`, never `FretboardConstants`) |
| touching the output images, MSAA, render scale or post effects | `vulkan-device-lifetime.md` |
| anything on a blend attachment | `vulkan-device-lifetime.md` § the frame-opacity rule |
| the 2D timeline, its device or its pacing | `vulkan2d-timeline.md` |
| more than one highway in a frame | `split-highway.md` |
| a golden-frame test | [../docs/agents/testing-headless-rendering.md](../docs/agents/testing-headless-rendering.md) |

## Hard-rule index — one line each, full text one file away

These are the rules that have cost this project a shipped bug. If your change is
near one, open the file it points at (all under `docs/agents/rendering/`).

- **THE FRAME MUST COME OUT OPAQUE, and `Vulkan/FrameAlphaPolicy.cs` owns every
  blend attachment IN THIS ASSEMBLY, 2D included.** The alpha half is not
  free-form: it is the textbook "over" operator, `dstA = srcA + dstA·(1 − srcA)`.
  Get it wrong and the vignette reads INVERTED and every note head grows a
  translucent rectangular halo — invisible for releases, because colour output is
  byte-identical either way and every golden-frame helper skipped index +3.
  → `vulkan-device-lifetime.md`
- **3D TEXT MAY ONLY BE STAGED FROM THE `UiElements` PASS.** The transparent pass
  runs before `_textBatcher.SetState(FontAtlas, sceneMvp)`, so text staged from it
  is drawn against a null atlas and then the previous frame's ORTHO matrix — with
  no exception and no log line. That cost three releases of invisible chord names,
  and then it RECURRED in `BendRenderer`'s peak labels, so it is a LINT now:
  `Rendering/Text3DPassTests` walks the call graph out of every opaque/transparent
  component and fails on any `DrawText3D` it reaches. A geometry pass that knows a
  label's position uses `RenderContext.Defer3DLabel`, which
  `Components/BendLabelComponent` drains in the Ui pass. If 3D text does not
  appear, check the scene list FIRST.
  → `scene-and-components.md`
- **Renderers read `RenderContext`, never `FretboardConstants`** — scroll speed and
  draw distance are user settings resolved once per frame by the pure
  `HighwaySpeedPolicy`. A `const` folds into every call site and silently ignores
  the setting. There is ONE look-ahead notion, `RenderContext.HorizonMs`; do not
  reintroduce a literal window. Scroll speed is in ROCKSMITH UNITS — **lower is
  faster.**
  → `highway-pace-and-shaders.md`
- **CAMERA FRAMING IS OWNER-SETTLED (2026-07-16): do not re-tune it.** Two reframe
  attempts were rejected and reverted; the Kamera-Distanz slider
  (`Preview3DCameraFill`, default 100 = the original aspect-derived framing,
  byte-for-byte) is the sanctioned knob.
  → `highway-pace-and-shaders.md`
- **Keep the opaque board-face classes a hair apart in z or they fight** — capo
  `-0.05` → fret bars `-0.01` → inlay dots `-0.005` → (transparent landing
  frames `-0.0025`) → string plane `0`. Do not re-collapse two classes onto one z.
  → `scene-and-components.md`
- **Every snapshot builder must set `ViewStartMs`/`ViewEndMs`/`VisibleRangeBufferMs`**
  — every projection except chord templates is windowed off them.
  → `scene-and-components.md`
- **There is no swapchain and no surface — do not bring one back.** The native
  child-HWND path (surface, acquire/present, present semaphores, re-target) was
  deleted; `Rendering/NoNativeSwapchainTests` fails on any of its identifiers.
  Output recreation is still always deferred (`SwapchainNeedsRecreation` + `Resize`
  before `BeginFrame`), and `Resize` consumes the flag FIRST and only ever SETS
  it afterwards.
  → `vulkan-device-lifetime.md`
- **A frame-loop `Result` is CLASSIFIED by `Vulkan/FrameResultPolicy`, never
  compared against one value.** Every `WaitForFences`/`QueueSubmit` in
  `VulkanRenderer` is an argument to its private
  `Check(...)`. `VK_ERROR_DEVICE_LOST` LATCHES (`IsDeviceLost` +
  `DeviceLostReason` + the one-shot `DeviceLost` event) and stops the loop —
  `Resize` must never rebuild on a dead device, which is what a TDR used to do
  forever, silently.
  → `vulkan-device-lifetime.md`
- **Device selection is `ComposedDevicePolicy` (composed) or a graphics-family
  scan (headless), never `devices[0]`** — an index guess fed `uint.MaxValue` into `DeviceQueueCreateInfo`
  on machines that enumerate a software device first. A composed LUID that matches
  no exportable device HARD-fails; never cross-GPU.
  → `vulkan-device-lifetime.md`
- **Every `ComposedFrameReady` must get exactly one `CompleteComposedPresent`**
  (success or failure) or a pool slot wedges. Composed mode has NO semaphores, the
  color image ends `COLOR_ATTACHMENT_OPTIMAL`, and views/framebuffers die BEFORE
  `pool.Recreate`.
  → `vulkan-device-lifetime.md` · `vulkan2d-timeline.md`
- **The 2D path cannot borrow the 3D device singleton** (mode-locked at init), runs
  entirely on the UI THREAD by design, and `RecordDraws` must call
  `batcher.FinalizeFrame()` — dropping it silently loses every frame's final run.
  A `ITimelineRenderLayer` must never call a graphics API.
  → `vulkan2d-timeline.md`
- **`LowLatencyDxgiSwapChain` composition mode is opt-in, never the default** — it
  gives every top-level an opaque swapchain, which paints a black frame around
  every rounded/shadowed POPUP HWND. It WAS the default for v0.32.5–v0.34.1 and
  that is exactly what went wrong.
  → `vulkan2d-timeline.md`
- **The camera easing state is per VIEW, not per engine**, and
  `IVulkanRenderService.SetViewportRect` sets viewport AND scissor — a viewport
  alone does not CLIP, so player 1's sky paints across player 2's half. Frame-GLOBAL
  state is resolved ONCE from all views together. Stacked is the default split.
  → `split-highway.md`
- **After ANY `.vert`/`.frag` edit, recompile and commit the `.spv`**
  (`dotnet run --project tools/ShaderCompiler -- DotCharter.Rendering/Shaders`) —
  Vulkan loads the precompiled `.spv` only. Regenerate only what you changed.
  → `highway-pace-and-shaders.md`
- **The 3D chart textures are `EmbeddedResource`s of THIS assembly** and decode
  with SkiaSharp — there is no soft coupling on `DotCharter.UI`'s assembly name any
  more, and `AssemblyLayeringTests` says this project may reference Core and
  nothing else.
  → `scene-and-components.md` § Assets

## Headless rendering (testing)

Offscreen, **windowless** golden-frame rendering for both Vulkan renderers, wired
into the test suite. It renders the real frame paths and reads the pixels back to
the CPU — no window, no swapchain/compositor surface, no audio/input device.
**Full how-to (API, fixtures, tolerance policy, gating, determinism, validation,
future-CI) plus the renderer-side notes:
[docs/agents/testing-headless-rendering.md](../docs/agents/testing-headless-rendering.md).**

Three facts worth carrying without opening it: `HeadlessHighwayRenderer` does NOT
skip the font atlas (that skip is what let three releases of invisible 3D chord
names pass every golden frame — do not reintroduce it); the GPU tests are
gated behind `DOTCHARTER_GPU_TESTS=1` (or `=sw`, the software-Vulkan lane) and serialized under
`[Collection("HeadlessGpu")]`; and it DOES skip the three chart textures by
default (1×1 white substitutes) — `new HeadlessHighwayRenderer(true)` /
`InitializeHeadless(useRealChartTextures: true)` opts out, which is what the
a REVIEW frame can opt into, so it looks like the app instead of a white board.
The default stays OFF so every existing golden is byte-identical.

## Who else drives this engine

Nothing outside the app and its tests. The service layer's run-video exporter
(replay → MP4, over the headless facade) was the one non-app host until 0.59,
when that feature was removed; `DotCharter.Services` no longer references this
project at all, and `AssemblyLayeringTests` is what keeps that honest.

A standalone `DotCharter.Highway3D` harness — a bare Win32 HWND into the
engine's since-deleted native-swapchain init plus the headless facade for
`--dump` — was built and then REMOVED at the owner's request on 2026-08-08; the
browser look-dev route replaced it. Do not restore it without asking. The engine
can no longer host on a foreign HWND at all (composed or headless only); the
headless facade still produces a review frame that matches the app.

## Keeping this current

**When you change a subsystem in a way that would make its guide misleading — a
new/removed folder, a changed invariant, a new gotcha, a moved entry point —
update the routed `docs/agents/rendering/*.md` file in the same change**, and add
a row here only if a whole FOLDER appeared or moved. Terse, accurate bullets with
real file/type names beat prose. This file is auto-loaded for every agent in the
project: anything that is not routing or a cross-cutting rule belongs in the topic
file, not here.
