# Headless rendering (testing)

Offscreen, **windowless** rendering for both Vulkan renderers, wired into the
test suite as a golden-frame capability. It renders the REAL frame paths — the
same `Fretboard3DEngine` (3D highway) and the same layer stack + batcher (2D
timeline) the app runs — into an offscreen image and reads the pixels back to
the CPU. **Nothing opens a window, a swapchain/compositor surface, or an
audio/input device.** Use it to assert what actually reaches the framebuffer,
not just that pure helper math is correct.

- 3D facade: `DotCharter.Rendering.Headless.HeadlessHighwayRenderer`
  (the one way to review a highway visual change with no display)
- 2D facade: `DotCharter.UI.Graphics.Vulkan2D.HeadlessVulkanTimelineRenderer`
  (offscreen renderer: `DotCharter.Rendering.Vulkan2D.Timeline2DHeadlessRenderer`
  — the composed 2D path's sibling). The historical GL facade/context
  (`HeadlessTimelineRenderer` over an EGL pbuffer) was deleted with the
  OpenGL path in v0.33 after the GL↔Vulkan parity gate passed (commit
  `43866e8d`).
- Test helpers: `DotCharter.Tests.Rendering.Headless.GoldenFrame`
- Seed tests: `DotCharter.Tests/Rendering/Headless/HeadlessVulkanRenderTests.cs`
  (3D), `HeadlessTimeline2DRenderTests.cs` (2D behavior goldens — the ported
  GL seed scenes) and `HeadlessTimelineGoldenTests.cs` (2D structural goldens
  + repeat-render byte stability; formerly the GL↔Vulkan parity gate —
  parity was proven at the port, commit `43866e8d`, then the GL baseline was
  removed together with the OpenGL path)

## Gating

GPU-dependent, so gated behind an env var (repo convention, like
`DOTCHARTER_PERF_GATE`):

```powershell
$env:DOTCHARTER_GPU_TESTS = "1"
dotnet test --project DotCharter.Tests\DotCharter.Tests.csproj --no-build `
  --filter-class "*HeadlessVulkanRenderTests" --filter-class "*HeadlessTimeline2DRenderTests" --filter-class "*HeadlessTimelineGoldenTests"
```

Without the var the tests `Assert.SkipWhen(...)` (a CI-less machine skips them).
They MUST pass on a machine with a working GPU. `GoldenFrame.GpuTestsEnabled` is
the single gate.

### The software lane (`DOTCHARTER_GPU_TESTS=sw`, no GPU needed)

The same tests run on **Mesa lavapipe**, a CPU Vulkan driver (#315):

```powershell
pwsh -NoProfile -File tools/ci/Get-SoftwareVulkan.ps1   # once: pinned, SHA-256-checked, unpacked to %LOCALAPPDATA%\DotCharter\swvk\<version>
$env:DOTCHARTER_GPU_TESTS = "sw"
pwsh -NoProfile -File tools/agent/Invoke-Gate.ps1 -FilterClass "DotCharter.Tests.Rendering.Headless.*"
```

- `Rendering/Headless/SoftwareVulkanLane` resolves the lane; the first
  `GoldenFrame.GpuTestsEnabled` read sets `VK_DRIVER_FILES` + `VK_ICD_FILENAMES`
  (and `VK_LOADER_LAYERS_DISABLE=~implicit~`) in the TEST PROCESS only. That
  REPLACES the loader's driver list, so lavapipe is the only device the
  headless pick can see. Nothing is installed, no registry key is written.
- `DOTCHARTER_SWVK_ICD` overrides the manifest path. A missing manifest FAILS
  the test (the lane was asked for by name), it does not skip.
- `SoftwareVulkanLaneTests.OnTheLane` asserts the device really is `llvmpipe`
  — the one test that would notice a loader that silently fell back to the GPU.
- The archive ships an empty `share/vulkan/icd.d`, so the script writes
  `lvp_icd.x86_64.json` itself next to `bin\vulkan_lvp.dll`.
- CI: the manual `software-gpu-tests` input of `.github/workflows/build.yml`
  runs the `gpu-goldens-software` job (frames uploaded as an artifact).
- `Perf/RenderPerfBaselineTests` still reads `DOTCHARTER_GPU_TESTS == "1"`
  only: a CPU rasterizer's frame time says nothing about the GPU path.

- `DOTCHARTER_GPU_DUMP=1` — tests also write their frame as a PNG under
  `<testoutput>/TestResults/headless/` (not only on failure) for eyeballing.
- **Perf measurements: pass `reusePixels` (v0.30).** Both facades' `Render`
  take an optional caller-owned readback buffer (exactly `w*h*4`). Without it
  every call allocates fresh readback arrays — which is fine for golden tests
  but drowned the perf harness: the original baselines read 2.88 MB (2D) /
  921 KB (3D) "per frame" that was 100 % instrument. With reuse both frame
  paths measure **0 B/iter** — the app paths are allocation-free at steady
  state; any nonzero number in a future harness run is a real regression.
- All GPU render tests share `[Collection("HeadlessGpu")]` (serialized) — one
  live graphics context at a time, and (Vulkan) no race on the process-wide
  validation static channel.

## Rendering a Vulkan (3D highway) frame in a test

```csharp
using var r = new HeadlessHighwayRenderer();          // creates the offscreen device + pipelines
var snapshot = /* a populated FrameSnapshot */;
byte[] bgra = r.Render(snapshot, width: 640, height: 360, timeMs: 0);
// bgra is tightly-packed BGRA8, top row first, width*height*4 bytes.
HeadlessHighwayRenderer.SavePng(bgra, 640, 360, path);   // optional PNG
```

Build a `FrameSnapshot` by setting its public arrays directly (no
`SnapshotUpdateContext`/Core domain objects needed): `Anchors`/`AnchorCount`,
`Notes`/`NoteCount`, `Beats`/`BeatCount`, `StringCount`, plus the `Enable*`
toggles and `HighwayScrollSpeed`. See `HeadlessVulkanRenderTests.BuildFixtureChart`.

## Rendering a 2D timeline frame in a test

```csharp
using var r = new HeadlessVulkanTimelineRenderer(width: 900, height: 400);
var rc = new ChartRenderContext { /* Width, Height, CurrentZoom, ViewStart/EndMs, margins, ... */ };
var layers = new ITimelineRenderLayer[] { new WaveformLayer() };   // or a subset / DefaultLayers()
byte[] bgra = r.Render(rc, layers);
```

`HeadlessVulkanTimelineRenderer.DefaultLayers()` returns the real
`ChartRenderer` stack order (with `EventLayer` fed a fresh
`PinnedLabelHitRegistry`). **Most seed tests pass a SUBSET** because several
layers dereference `rc.Editor`/`rc.Transport`, which the headless harness does
not spin up (a real `TransportViewModel` constructs the JUCE audio engine —
forbidden here):

- `WaveformLayer` reads only `WaveformData` + geometry → runs standalone.
- `BeatGridLayer`/`BackgroundLayer` measure-pill path reads `rc.Snapshot.Beats`
  and touches `rc.Transport.GridDivisor` once (in `DrawSubdivisions`). Supply a
  `RuntimeHelpers.GetUninitializedObject(typeof(TransportViewModel))` — its
  `GridDivisor` defaults to `0` (the `= 4` field initializer never runs), so
  subdivisions short-circuit and no audio engine is built. `rc.Editor` is not
  touched by the beat-grid path.

