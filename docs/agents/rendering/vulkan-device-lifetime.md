# Vulkan device, swapchain and frame lifetime

Deep reference for `DotCharter.Rendering/AGENTS.md`. Everything about the DEVICE
and the presented frame: the renderer's own folders, the Avalonia-composed
presentation path, and the threading/lifetime rules that each cost a driver-
dependent freeze, a validation error or a visibly wrong frame. **These are the
rules the root `AGENTS.md` means by "swapchain lifetime rules".**

## `Vulkan/`

- `Vulkan/` — `VulkanRenderer` (`IVulkanRenderService`, frame submission),
  `VulkanContext` (instance/device/queue — NO surface; `InitializeComposed` or
  `InitializeDeviceHeadless`), `VulkanSwapchainManager` (the name is historical:
  there is no `VkSwapchainKHR` — it owns the views, the MSAA/depth pair, the
  intermediate, the bloom targets and the framebuffers over the composed pool's
  images or the headless offscreen image), `VulkanPipelineManager`,
  `VulkanResourceManager`, `VulkanTexture`, `FontAtlas` (Skia-rendered glyph
  atlas). Depth D32, `MaxFramesInFlight=2`; MSAA level (off/2×/4×/8×, default
  4×) is a 0.14.2 Settings → Graphics value clamped by the pure `MsaaPolicy`
  (unit-tested). Textures loaded through `VulkanResourceManager.LoadTexture` get a full mip
  chain (linear blits) and 16× anisotropic filtering when the device has the
  `samplerAnisotropy` feature (`TextureFilterPolicy`, unit-tested); the glyph atlas
  passes `mipmapped: false`, because mip averaging fades far text. **The present-mode setting is inert**: it is still persisted,
  parsed (`SwapchainPolicy.ParsePresentMode`, `SwapchainPolicyTests`) and
  recorded by `SetPresentMode`, but it only ever applied to the native
  swapchain, which is gone — `SupportedPresentModes` is always `["Fifo"]` and
  the composed path is paced by the compositor. The MSAA color + depth
  attachments are ONE shared transient pair, not per-output-image (see the
  gotcha below). `VulkanValidationDiagnostics` counts validation output when the
  app runs with `DOTCHARTER_VK_VALIDATION=1` (Khronos layer + debug-utils
  messenger, both init paths; silently skipped when the layer isn't
  installed).

## `Vulkan/Composed/`

- `Vulkan/Composed/` — the **Avalonia-composed presentation path**, the app's
  ONLY 3D presentation path. The UI's native child-HWND host is gone, and so is
  the renderer's native-swapchain path (Win32 surface, `VkSwapchainKHR`,
  acquire/present, per-image present semaphores, the pop-out surface re-target,
  surface-based device selection) — `Rendering/NoNativeSwapchainTests` fails the
  default gate if any surface/swapchain identifier or a window-handle-shaped
  member comes back. No child HWND, no swapchain — presentation is exporting
  D3D11-shared images to Avalonia's compositor. `VulkanContext.InitializeComposed` picks the
  device by the compositor's adapter LUID (pure `ComposedDevicePolicy`; a
  LUID that matches no exportable device HARD-fails — never cross-GPU) and
  requires `VK_KHR_external_memory_win32` + `VK_KHR_win32_keyed_mutex`.
  `VulkanComposedImagePool` owns 3 BGRA8 D3D11 keyed-mutex shared textures
  (created on the LUID-matched adapter, imported as dedicated allocations;
  the import NT handle is closed after `AllocateMemory` — import never takes
  NT-handle ownership; a FRESH export handle is minted per compositor
  import). Rotation/back-pressure is the pure `ComposedImagePoolPolicy`
  (`ComposedPathPolicyTests`); `IVulkanComposedPresenter` is the UI pump's
  contract — **every `ComposedFrameReady` must get exactly one
  `CompleteComposedPresent`** (success or failure) or a pool slot wedges.
  Sync: the frame submit chains `Win32KeyedMutexAcquireReleaseInfoKHR`
  (acquire key 0 / release key 1); the compositor
  `UpdateWithKeyedMutexAsync(img, 1, 0)`s back to 0. **The acquire timeout is
  FINITE and comes from `Composed/ComposedSubmitPolicy.AcquireTimeoutMs`
  (1,000 ms) — both composed submits used `uint.MaxValue`, i.e. INFINITE**, so a
  compositor that never released key 0 (a lost D3D11 device, a wedged
  `UpdateWithKeyedMutexAsync`) hung the graphics queue until the driver reset the
  GPU, with no escape hatch. A submit that fails now raises NO `ComposedFrameReady`
  and calls `MarkBroken` on its slot, because a slot left in `Rendering` is never
  Free again and never gets its matching `CompleteComposedPresent`
  (`Timeline2DComposedRenderer.SubmitFailures` counts them). `FindAdapter` also
  loops on SUCCESS rather than on "not `DXGI_ERROR_NOT_FOUND`": any other failing
  HRESULT left the `ComPtr` null and the very next line dereferenced it, then
  incremented the index without bound. Pinned by
  `Rendering/ComposedSubmitAndAdapterTests`. NO semaphores exist in
  this mode (the frame fence paces the CPU); `SceneOutputMode.Direct` and
  the composite pass end in COLOR_ATTACHMENT_OPTIMAL (unconditionally — the
  pipeline manager has no presentable output layout any more) — PRESENT_SRC is
  only legal for swapchain images.
  Chosen over Avalonia's `IVulkanSharedDeviceGraphicsContextFeature`
  because the app pins `Win32RenderingMode.AngleEgl` (kept for the
  compositor's D3D11 GPU interop + the verified composed-visual
  Scale(1,-1,1) flip — see `Program.cs`; historically also for the 2D
  editor's OpenGlControlBase, removed v0.33), so a shared Vulkan device is
  unavailable by construction — external D3D11 export keeps our render
  thread and Avalonia's production backend both intact. UI side:
  `DotCharter.UI` `Preview3DComposedCanvas` (+ `Preview3DRenderDriver`).

## Threading / lifetime gotchas (hard-won — keep them)

- **THE FRAME MUST COME OUT OPAQUE — `Vulkan/FrameAlphaPolicy.cs` owns every
  blend attachment IN THIS ASSEMBLY, 2D included, and the ALPHA half is not
  free-form.** The 2026-08-01 fix covered only the 3D pipelines;
  `Timeline2DVulkanCore` kept an inline literal with
  `SrcAlphaBlendFactor = SrcAlpha`, which computes `dstA = srcA² + dstA·(1 −
  srcA)` — not "over". From that pass's alpha-1 clear a draw at `srcA = 0.5`
  leaves **0.75**, and `HandShapeLayer` draws its preview bar at exactly
  128/255, so a quarter of the backdrop showed through the imported BGRA8
  texture Avalonia composes. The 2D core now exposes
  `Timeline2DVulkanCore.BlendAttachment` (= `FrameAlphaPolicy.SceneAttachment()`)
  so there is one owner and the next fix cannot cover half the renderers again.
  **Colour parity with the removed GL path is unaffected by the ALPHA factors** —
  that is what made the inline literal look justified for a release.
  The scene pipelines
  shipped with `SrcAlpha = One, DstAlpha = Zero`, i.e. `dstA = srcA`: every
  blended draw OVERWROTE the framebuffer's alpha with its own instead of
  accumulating coverage. Colour output is byte-identical either way, so it was
  invisible for releases — until something composited the presented image with
  that alpha, and then two things appeared at once (owner report 2026-08-01,
  "that box vignet thing around"):
  - `DrawVignette`'s four border quads stamped their 70→0 alpha across the
    border bands and left the untouched middle at 255, so the frame was
    transparent everywhere EXCEPT a hard-edged rectangle at
    x, y ∈ [0.16, 0.84] of the viewport — the vignette read INVERTED;
  - every translucent quad (note-head glows, hit feedback, sustain shimmer)
    stamped its own low alpha over its whole RECTANGLE, giving each note head a
    translucent rectangular halo.
  The rule is the textbook "over" operator, `dstA = srcA + dstA·(1 − srcA)`:
  from the pass's alpha-1 clear it stays 1 for every source alpha. Colour blend
  is unchanged (`SrcAlpha/OneMinusSrcAlpha`), so no pixel colour moves.
  **The post/composite chain relies on all three of its fragment shaders ending
  in `vec4(rgb, 1.0)`** — the scene composite draw does not blend, so it writes
  its source alpha straight through. Evidence: `FrameAlphaPolicyTests` (default
  gate, pure evaluation of the blend equation) + `HeadlessFrameOpacityTests`
  (GPU golden — every pixel alpha 255, with the vignette on AND off).
  `GoldenFrame.NonOpaqueBounds` / `CompositeOverBackdrop` exist because the
  alpha byte was this suite's blind spot: every other helper reads B, G, R and
  skips index +3, so no golden frame could see it. **`HeadlessTimelineGoldenTests`
  had the same hole and now closes it** (`TheRenderedTimelineFrameIsFullyOpaque`,
  over both the solid and the general stream) — a 2D golden test that asserts
  only `NonBlackRatio`/`DifferenceRatio` proves nothing about the channel the
  compositor reads.
- **Single render lock:** every `VulkanRenderer` frame op runs under
  `lock (_renderLock)` — **`Resize` INCLUDED, which was not true until v0.50.**
  Only its flag clear was locked; `DeviceWaitIdle`, `RebuildFor`,
  `Recreate`/`RecreateComposed`/`RecreateHeadless`,
  `RecreateRenderFinishedSemaphores` and the `CurrentWidth`/`CurrentHeight`
  writes all ran unlocked, and it never consulted `_isDisposed`. `Dispose()`
  takes the same lock and destroys the swapchain manager, the pipelines and the
  context (it nulls nothing), so a UI-thread `Dispose` landing inside a
  render-thread `Resize` was a use-after-free. `Resize` is now one lock block
  from its first statement to its last, opening with
  `if (_isDisposed || !IsPipelinesInitialized) return;`. The four request
  setters only write flags under the same lock, so the wider hold cannot
  deadlock. Pinned by `Rendering/SwapchainRecreationRequestTests`
  (`TheWholeOfResizeRunsUnderTheRenderLock`, `ResizeRefusesOnceDisposed`).
- **Every frame-loop result is CLASSIFIED, never compared against one value.**
  `Vulkan/FrameResultPolicy.Classify` is the single decision table
  (Success→Continue, `SuboptimalKhr`→ContinueAndRecreate, Timeout/NotReady→Skip,
  OutOfDate/SurfaceLost/FullScreenExclusiveLost→Recreate,
  **`ErrorDeviceLost`→DeviceLost**, anything else→Fatal), and every
  `WaitForFences` / `QueueSubmit` in `VulkanRenderer` is an ARGUMENT to the
  private `Check(result, site)` that consults it (the acquire/present call sites
  went with the native swapchain; the table keeps its swapchain rows because it
  is a pure classification of `Result`). Until v0.50 `grep -rn ErrorDeviceLost DotCharter.Rendering`
  returned **zero** hits: every fence result was compared only against
  `Result.Timeout`, so a `VK_ERROR_DEVICE_LOST` after a TDR read as a
  *successful* wait — the frame proceeded, the acquire failed,
  `SwapchainNeedsRecreation` was set, and `Resize` re-entered `DeviceWaitIdle`
  → `CleanupImageResources` → `CreateSwapchain` on a dead device forever, with
  no log line and no error surface. A device loss now LATCHES
  (`IVulkanRenderService.IsDeviceLost` / `DeviceLostReason` / the one-shot
  `DeviceLost` event, raised OUTSIDE the render lock like `ComposedFrameReady`),
  logs to `Console.Error` (not `Debug.WriteLine` — that is compiled out of
  Release), and makes `BeginFrame`, `EndFrame` **and `Resize`** refuse. Pinned
  by `Rendering/FrameResultPolicyTests` (the table, driven by a fake result
  sequence) + `Rendering/DeviceLostHandlingTests` (the call-site shape).
  A composed submit that does not reach the GPU raises NO `ComposedFrameReady`
  and marks its pool slot `Broken` (`VulkanComposedImagePool.MarkBroken`), so
  the slot cannot wedge waiting for a `CompleteComposedPresent` that will never
  come.
- **Output recreation is deferred:** a `SwapchainNeedsRecreation` flag + a
  `Resize` before `BeginFrame`. **`Resize` CONSUMES the flag at the top, under
  `_renderLock`, and only ever SETS it afterwards — it must never assign a
  computed boolean to it.** It used to end with
  `SwapchainNeedsRecreation = <degenerate?>`, i.e. an unconditional write of
  `false` on the success path. The four request setters (`SetPresentMode`,
  `SetMsaaSamples`, `SetRenderScalePercent`, `SetPostEffects`) run on the UI
  THREAD, and the work in between idles the device and rebuilds every image
  resource — a millisecond-wide window in which a request was set and then
  read back out by that trailing write. **None of those four changes the surface
  SIZE**, and the consume point (`Fretboard3DEngine.RenderFrame`) re-enters
  `Resize` only on a size change or the flag, so the setting was then silently
  never applied. Most visible as a render-scale slider whose final tick lands
  inside the last rebuild. Pinned by
  `Rendering/SwapchainRecreationRequestTests` (exactly one clear, it comes
  first, every other write is a literal `true`, and all four setters still take
  the lock). The flag name is historical: what it rebuilds is the composed
  pool (or the headless image) and everything downstream.
- **Output sizing:** with no surface caps, `RecreateComposed` /
  `RecreateHeadless` clamp the requested size to the device's
  `MaxRenderTargetWidth/Height`. A zero size (collapsed panel) keeps NO images:
  `Resize` keeps `SwapchainNeedsRecreation` set (`_composedPool.Count == 0` /
  no framebuffers) and `BeginFrame` refuses. `CurrentWidth/Height` stay the
  REQUESTED size (the render loop compares them against the control size);
  render area/viewport/scissor follow the actual `VulkanSwapchainManager`
  `RenderExtent`.
- **The render pass's external subpass dependency covers depth**, not just
  color (early+late fragment tests / depth-stencil write in both masks) — the
  depth buffer is cleared each frame and the previous frame may still be
  testing against it (WAW hazard).
- **MSAA color + depth are ONE shared TRANSIENT attachment pair** across all
  framebuffers (0.14 P6), not per-output-image: their contents never
  outlive a render-pass instance (both are `StoreOp=DontCare` — color is
  resolved into the output image and never sampled later), and the external
  subpass dependency above orders each frame's layout transition + clear
  against the previous frame's attachment writes. Allocated with
  `TRANSIENT_ATTACHMENT` usage preferring `LAZILY_ALLOCATED` memory with a
  device-local fallback constrained by `memoryTypeBits` (pure selection in
  `MemoryTypePolicy`, unit-tested in `MemoryTypePolicyTests`). If you ever
  need to store or sample these attachments after the pass, go back to
  per-image copies — do not just flip the StoreOp.
- **Scene output modes (0.15.6 — `SceneOutputMode`):** the scene pass ends in
  one of three shapes chosen by the pure `PostProcessPolicy.ChooseOutputMode`
  (post effects requested → **Sampled**, else render scale ≠ 100% → **Blit**,
  else **Direct**). All three passes are COMPATIBLE (only load/store ops +
  layouts differ), so a mode flip rebuilds ONLY the render pass — pipelines
  survive. **Sampled** (bloom and/or filmic tonemap): the intermediate is
  `ColorAttachment|Sampled`, final layout SHADER_READ_ONLY; EndFrame records
  the bloom mip chain (threshold extract → separable gaussian at half+quarter
  res, ping-pong; blur with direction (0,0) doubles as the downsample copy)
  and ONE composite pass into the output image (opaque fullscreen scene
  draw — which also absorbs render scaling, no TRANSFER_DST needed — plus
  additive bloom draws). The post/composite render passes + 4 fullscreen
  pipelines are STATIC (single-sample, built once in
  `InitializePostPipelines`, never rebuilt on MSAA/mode changes); bloom
  targets + sampling descriptor sets + per-image composite framebuffers are
  (re)created inside `VulkanSwapchainManager.CreateImageResources` (the shared
  tail of `RecreateComposed`/`RecreateHeadless`) and die in
  `CleanupImageResources` (composite framebuffers reference the output image
  views — they MUST die with them, before `pool.Recreate` destroys the
  images). Bloom/tonemap FLAG flips while
  Sampled is active are free (`SetPostEffects` per frame from
  `Fretboard3DEngine.RenderFrame`, before the resize check); only
  entering/leaving the post chain rides the deferred recreation. Inter-pass
  hazards ride EXTERNAL subpass dependencies on the static passes
  (fragment-read ↔ color-write, both directions — post targets are shared
  across frames in flight); the scene pass's second dependency targets the
  fragment-shader read in Sampled mode (transfer read in Blit).
- **Render scale (0.14.2) — 100% means the intermediate does not exist:**
  at any other setting the pass renders at `VulkanSwapchainManager.RenderExtent`
  into a scaled single-sample intermediate (`ColorAttachment|TransferSrc`,
  plain device-local — deliberately NOT transient: its contents outlive the
  pass) that `EndFrame` blits (linear) onto the frame's output image.
  Invariants: (1) render area/viewport/scissor use `RenderExtent`, never
  `Extent`; (2) the blit-mode render pass leaves its single-sample color
  output in `TRANSFER_SRC_OPTIMAL` and carries a second (0→EXTERNAL,
  color-write → transfer-read) dependency — never blit without it; (3) the
  incoming EXTERNAL→0 dependency's src scope includes TRANSFER in blit mode:
  the intermediate is ONE image shared across frames in flight, so the next
  frame's pass must wait for the previous frame's blit READ (WAR — execution
  dependency only); (4) both output-image sources (the composed pool and the
  headless image) carry `TRANSFER_DST`, so
  `VulkanRenderer.EffectiveRenderScalePercent`'s force-100% fallback is
  defensive only; (5) the output image is transitioned UNDEFINED→TRANSFER_DST
  (full overwrite) and then TRANSFER_DST→COLOR_ATTACHMENT_OPTIMAL (composed)
  or →TRANSFER_SRC_OPTIMAL (headless, for the readback copy) around the blit
  with TRANSFER-stage barriers.
  A blit-mode flip rebuilds ONLY the render pass (load/store ops + layouts
  do not affect render-pass compatibility, so pipelines survive); sizing is
  the pure `RenderScalePolicy` (unit-tested).
- **MSAA level changes rebuild the scene render pass + the scene/text
  pipelines (0.14.2):** they bake `RasterizationSamples` and the pass
  structure differs — MSAA on = [msaa color, depth, output resolve], MSAA
  OFF = [output color rendered directly (StoreOp **Store**, final
  COLOR_ATTACHMENT_OPTIMAL), depth] with NO resolve attachment and no transient color image
  at all (depth stays transient/DontCare at every level). The 0.15.6
  post/composite passes + pipelines are static single-sample and SURVIVE.
  `VulkanRenderer.Resize` detects the effective-level change
  (`MsaaPolicy.Clamp` vs `VulkanPipelineManager.CurrentSampleCount`), waits
  the device idle, `RebuildFor` (retained SPIR-V; descriptor set layout/pool
  and pipeline cache survive so textures keep their sets), THEN recreates the
  output resources against the new pass. Framebuffer attachment layout in
  `VulkanSwapchainManager.CreateImageResources` must mirror `CreateRenderPass`'s shape;
  `BeginFrame`'s clear values are color=[0]/depth=[1] in both shapes
  (ClearValueCount 3 vs 2).
- **No semaphores exist.** The composed submit syncs across APIs with the
  images' keyed mutex and the headless submit fence-waits; the per-frame fence
  alone paces the CPU. (The image-available and per-swapchain-image present
  semaphores went with the native path.)
- **3D preview pop-out / re-parent is a composed re-attach:** keep a SINGLE
  renderer instance. `InitializeDeviceComposed` is idempotent — device,
  pipelines and textures survive; the canvas tears down and rebuilds only its
  compositor-side surface and pump (`docs/agents/ui/docking.md`). There is no
  Vulkan surface to re-target.
- **Device selection is a policy, never `devices[0]`.** Composed filters on
  the compositor's adapter LUID + interop extensions (`ComposedDevicePolicy`),
  headless scans for any graphics family. Never reintroduce an index guess —
  `devices[0]` fed `uint.MaxValue` into `DeviceQueueCreateInfo` on machines
  that enumerate a software/compute device first and died as a generic
  "Failed to create logical device."
- **Asking WITHOUT a device: `Vulkan/Composed/ComposedDeviceProbe`.** The
  first-run wizard's system check (#288) creates an instance, enumerates the
  devices, reduces each to the same `ComposedDeviceCandidate` (interop
  extensions + a graphics queue family) and runs `ComposedDevicePolicy` with NO
  target LUID (none exists before a window composes), then destroys the
  instance. No device, no queue, nothing on the UI thread; the pure half is
  `Classify` (`Rendering/ComposedDeviceProbeTests`). A machine that passes it
  can still fail the LUID match in `InitializeComposed` and says so where it
  always did. The app reaches it through `Services/Infrastructure/Graphics/
  IGpuRenderProbe`, implemented by `DotCharter.UI/Platform/VulkanGpuRenderProbe`.
- **Device extensions:** composed enables exactly
  `VK_KHR_external_memory_win32` + `VK_KHR_win32_keyed_mutex` (no instance
  extensions beyond the optional debug-utils); headless enables none. No
  surface or swapchain extension is requested anywhere.
- **Composed mode invariants (0.16):** the presentation mode is fixed for the
  renderer's lifetime — `InitializeDeviceComposed` and
  `InitializeDeviceHeadless` are mutually exclusive (each throws on a
  cross-mode call). `RecreateComposed` shares the exact
  `CreateImageResources` tail with `RecreateHeadless`
  (views/MSAA/depth/intermediate/bloom/composite over whatever is in
  `_swapchainImages`) — old views/framebuffers die BEFORE `pool.Recreate`
  destroys the images under them. Pending compositor reads of a dead pool survive via COM
  refcounts (the compositor holds its own texture reference); a faulted
  present marks the image Broken and `BeginFrame` flags the deferred pool
  rebuild. `ComposedFrameReady` fires OUTSIDE the render lock.
  **`Recreate` builds its slots ALL-OR-NOTHING through
  `Vulkan/NativeResourceSet.BuildAllOrRollback`.** It empties `_slots` first and
  fills a LOCAL array, and `CreateSlot` throws on a failed image create, a failed
  shared-handle export or a failed memory import — so a throw part-way used to
  strand every earlier slot's `VkImage` + imported `DeviceMemory` +
  `ID3D11Texture2D` where neither `DestroySlots` nor `Dispose` could reach them,
  each dedicated import pinning its D3D texture. The amplifier is
  `Preview3DRenderDriver`'s per-frame catch, which retries up to **120**
  consecutive times: one sustained VRAM shortage leaked a fresh batch per retry,
  feeding the condition that caused it. The rollback runs the SAME
  `DestroySlot(Slot)` a live pool uses, so the two cannot drift.
  `Rendering/NativeResourceSetTests` is the gate.
- **Every CREATION/ALLOCATION result is consumed — `Vulkan/VulkanCheck.Ok`, or an
  explicit test.** Forty were dropped on the floor across `VulkanContext`,
  `VulkanPipelineManager`, `VulkanRenderer`, `VulkanResourceManager`,
  `VulkanSwapchainManager` and `VulkanComposedImagePool`. A failed
  `AllocateDescriptorSets` returns a NULL set that `UpdateDescriptorSets` wrote
  into and a later frame bound; a failed `MapMemory` leaves a null pointer the
  next frame's `Buffer.MemoryCopy` writes through; a failed `CreateFramebuffer`
  leaves a zero handle `BeginFrame` hands to `CmdBeginRenderPass` — all
  surfaced to the user as nothing at all. `VulkanCheck` is distinct from
  `FrameResultPolicy` on purpose: the frame loop survives its results, nothing
  survives a failed create. `Rendering/VulkanResultConsumptionTests` lints every
  `.cs` under `Vulkan/` and carries a count floor so a receiver rename cannot
  make it vacuous.
- **Init order:** device → pipelines (command pool, mapped host-visible
  vertex buffers, frame fences, white texture) → text pipeline → textures/scene.
  Guarded by `IsDeviceInitialized` / `IsPipelinesInitialized`.
- Persistent-mapped vertex buffers are partitioned per frame-in-flight;
  over-budget/misaligned batches are dropped, never throw. **The TEXT buffer's
  size is `Vulkan/TextVertexBudget`, and it is the number
  `Batching/TextBatcher` sizes its staging array from — one owner, or the
  batcher's own auto-flush is guaranteed to be dropped.** It was:
  `MaxTextVertexBufferSize` was `50000 * 9 * 4` for the WHOLE buffer, which
  `BeginFrame` splits across `MaxFramesInFlight`, so a frame's chunk held
  **25,000** vertices against a batcher staging **50,000** — past ~6,250 glyph
  quads in a frame further text batches vanished, and a batch that hit the
  batcher's flush threshold was a 100% loss (a ja-JP lyric frame plus chord
  names plus a split highway carrying two HUDs). The buffer is sized per FRAME
  now (3.6 MB), and a drop is COUNTED (`DroppedTextBatchCount`) and said once
  per session on `Console.Error` — `Debug.WriteLine` is compiled out of Release,
  which is why it was silent. Pinned by `Rendering/TextVertexBudgetTests`.
- **`Vulkan/FontAtlas.RebuildIfNeeded` builds, SWAPS, then disposes — never the
  other way round — and it COUNTS what would not fit.** It used to
  `Texture?.Dispose()` first (freeing the image, view, sampler **and descriptor
  set**) and only then rasterize 16 MB and upload; any throw in between left
  `Texture` pointing at the disposed object, `TextBatcher.Flush` only
  null-checks, and `NeedsRebuild` stayed true — so it retried EVERY frame, the
  "one sustained VRAM shortage leaks per retry" amplifier `NativeResourceSet`
  was written to stop. Overflow was a bare `break`: because packing is
  code-point-sorted the glyphs that fall off the bottom of the sheet are always
  the HIGHEST code points, i.e. CJK, and `TryGetGlyph` substitutes `'?'` — the
  shipped app rendered `?` for Japanese lyrics and section names with no
  diagnostic. `OverflowedGlyphCount` / `LastOverflowWarning` /
  `OverflowWarning(...)` are ported from `Text.ChartFontAtlas`, warned once per
  sheet on `Console.Error`. `VulkanTexture.IsDisposed` exists so a test can see
  the difference (the fields are not nulled, so identity proves nothing). Pinned
  by `Rendering/FontAtlasOverflowTests`. **Still missing on this side:** the 3D
  atlas has no `RenderScale` notion at all, unlike `ChartFontAtlas`, so the
  ortho HUD pass rasterizes at base size and stretches on a 150% display.
- **Each 3D glyph is drawn with a font that HAS it.** `FontAtlas.Rasterize`
  asks `Text/GlyphFontChain.For(c)`: the bold primary when it covers `c`, else
  the system fallback (`SKFontManager.MatchCharacter`, steered by
  `FallbackLocale` over the whole set — kana ⇒ `ja`, Hangul ⇒ `ko`, Han alone ⇒
  `zh-Hans`). No font is bundled. Drawing an uncovered character with the
  primary is what turned every kana and kanji into the same `.notdef` box. The
  sheet is 2048 wide and 2048 tall, growing to `MaxAtlasHeight` (4096, the
  Vulkan-guaranteed minimum limit) only when the set needs it, so Latin-only
  charts keep the old sheet. Surrogate halves are never registered: an emoji
  shows as ONE `?` (`TryGetGlyph` skips the low half). Pinned by
  `Rendering/GlyphFontChainTests` and `Rendering/Headless/HeadlessCjkLyricTests`.
- **`TextBatcher.Flush` DISCARDS its staging when it declines to draw.** It used
  to return with the count intact when the atlas had no texture, so staged
  glyphs survived into the next `SetState` and were drawn with a different MVP
  and a different atlas — in-world quads under the ortho HUD matrix, silently.
  Same shape as the three releases of invisible chord names.
