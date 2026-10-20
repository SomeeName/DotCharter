# Headless rendering (testing) — renderer-side notes

## Renderer-side notes (moved from `DotCharter.Rendering/AGENTS.md`)

What the RENDERING project itself guarantees about the headless paths — the
facades, the split-highway variant, and the gating the tests rely on.

- **Vulkan (3D highway):** `Headless/HeadlessHighwayRenderer` →
  `(FrameSnapshot, w, h, timeMs)` → BGRA pixels (+ SkiaSharp PNG helper). It is
  the composed path's SIBLING: `VulkanContext.InitializeDeviceHeadless` (no
  surface/swapchain extensions), `VulkanSwapchainManager.RecreateHeadless` (one
  plain device-local BGRA8 image through the shared `CreateImageResources` tail),
  `VulkanRenderer` `_headlessMode` (no acquire/semaphores; `EndFrame` does
  `vkCmdCopyImageToBuffer` + fence into a mapped host-visible buffer;
  `IVulkanHeadlessRenderer.TryReadbackBgra`). Shares the pipeline manager's
  non-presentable layout with the composed path (color ends
  `COLOR_ATTACHMENT_OPTIMAL`, never `PRESENT_SRC` — the only layout there is,
  since the native swapchain path was removed). `Fretboard3DEngine.InitializeHeadless` substitutes
  1×1 solid textures (the Avalonia asset system is app-only) — **app paths
  unchanged**, gated behind the headless flag. **It does NOT skip the font
  atlas**: `FontAtlas` is pure SkiaSharp with no thread affinity, so text really
  rasterizes headless. That skip existed until 0.42.2 and was the reason three
  releases of invisible 3D chord names passed every golden frame —
  `TextBatcher.DrawText3D` answers a null atlas by returning silently. Do not
  reintroduce it.
- **Vulkan (3D, SPLIT):** `HeadlessHighwayRenderer.RenderSplit(snapshots, w, h,
  timeMs, orientation)` tiles the frame with `SplitHighwayLayout` and drives the
  engine's multi-viewport path — the same code the app's render driver calls, so
  a golden frame proving two distinct non-black halves is proving what ships.
  Every view's camera eases to convergence first, so the framing is stable for
  all players regardless of call order.
- **Vulkan (2D timeline):** `Vulkan2D/Timeline2DHeadlessRenderer` (+ the
  `DotCharter.UI` facade `Graphics/Vulkan2D/HeadlessVulkanTimelineRenderer`) —
  own headless device, same core pass/pipelines as the app, one BGRA8 image +
  HOST_CACHED readback. Gated by `DotCharter.Tests/Rendering/Headless/`
  `HeadlessTimelineGoldenTests` (structural goldens + byte stability) and
  `HeadlessTimeline2DRenderTests` (the ported GL seed scenes). The GL
  headless twin (EGL pbuffer over ANGLE) was deleted with the OpenGL path
  after the parity gate passed at commit `43866e8d`.
- **Test helpers/gating:** `DotCharter.Tests/Rendering/Headless/GoldenFrame`
  (structural asserts — non-black/colourful/difference/edge-spread/**non-opaque
  bounds**, PNG on failure — NOT byte-equality); GPU-gated `DOTCHARTER_GPU_TESTS=1`
  (`[Collection("HeadlessGpu")]` serialized); Vulkan frames assert zero
  `VulkanValidationDiagnostics.ErrorCount` when `DOTCHARTER_VK_VALIDATION=1` and
  the layer is installed (skipped gracefully otherwise).
