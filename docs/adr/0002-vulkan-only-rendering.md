# 0002. The highway and the editor timeline are drawn with Vulkan only

**Status:** Accepted

## Context

The 3D note highway and the editor timeline are redrawn every frame with many
thousands of notes. The project once carried an OpenGL path next to Vulkan.
Two backends meant every effect, shader and fix was written and tested twice.

## Decision

- `DotCharter.Rendering` has one GPU backend: Vulkan. The OpenGL path was
  removed once the Vulkan output matched it.
- The 3D highway and the composed 2D timeline both use it. The renderer
  presents into an Avalonia-composed surface, so it sits inside the normal
  window instead of owning one.
- Rendering depends on Core only. It never references Services, so its
  geometry and batching can be tested without the app.
- Shaders are compiled to committed `.spv` files.

## Consequences

- A GPU with Vulkan 1.2 drivers is a hard requirement; the app says so on the
  first page when it cannot run the highway.
- Swapchain and device lifetime are the delicate part of the code and have
  their own guide.
- Headless GPU tests need a Vulkan device; a software rasterizer lane exists
  for machines without one.

## Links

[DotCharter.Rendering/AGENTS.md](../../DotCharter.Rendering/AGENTS.md), the
[Vulkan device-lifetime guide](../agents/rendering/vulkan-device-lifetime.md).
