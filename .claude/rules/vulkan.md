---
paths:
  - "DotCharter.Rendering/**"
  - "tools/ShaderCompiler/**"
---

# Vulkan rendering

Vulkan here is MANAGED code (Silk.NET, `unsafe` C#) in `DotCharter.Rendering`.
Lifetime/threading rules: `docs/agents/rendering/vulkan-device-lifetime.md`;
shaders: `docs/agents/rendering/highway-pace-and-shaders.md`. Read the
relevant one before changing `Vulkan/`, `Vulkan2D/` or `Engines/`.

- **Validation during development.** Run with `DOTCHARTER_VK_VALIDATION=1`
  (Khronos layer + debug-utils messenger, counted by
  `VulkanValidationDiagnostics`). If the layer is not installed the app logs
  `[VULKAN] Validation requested but VK_LAYER_KHRONOS_validation is not
  installed.` and runs WITHOUT validation — say so rather than reporting a
  clean run. Investigate every validation error or warning; never silence one
  with a message-ID filter or by dropping severity.
- **Reason explicitly, in the change description, about:** resource lifetime
  (who owns it, when it is destroyed, whether the GPU can still be using it),
  synchronization (fences, semaphores, the single `_renderLock`), pipeline
  barriers and image layout transitions, queue-family ownership transfers,
  frames in flight (per-frame resources are indexed by frame, never shared),
  and destruction order (wait idle / per-frame fence before destroy; children
  before parents; swapchain recreation is deferred through
  `SwapchainNeedsRecreation`).
- Every frame-loop `Result` is classified, never compared against one value.
- **Shaders:** after editing a `.vert`/`.frag`, recompile with
  `dotnet run --project tools/ShaderCompiler -- DotCharter.Rendering/Shaders`
  and commit the `.spv` (Vulkan loads only the embedded `.spv`). When the
  Vulkan SDK is installed, also run `spirv-val` on each changed `.spv`.
- Do not "simplify" synchronization, barrier, layout or destruction code, and
  do not merge per-frame resources, without an explicit request. It is
  verbose because the ordering is the correctness.
- Evidence: the headless renderer golden/structure tests
  (`docs/agents/testing-headless-rendering.md`) — assert structure, never
  wall-clock time.
