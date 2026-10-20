---
name: dotcharter-shaders
description: Edit, recompile and validate DotCharter's Vulkan GLSL shaders (DotCharter.Rendering/Shaders/*.vert, *.frag → committed .spv), including push-constant/effect-bit sync with C# and the headless render tests. Use this whenever a .vert or .frag file changes, a .spv needs regenerating, a highway/timeline/post effect or toggle is added, a shader fails to compile or validate, or the rendered frame looks wrong after a shader change — even if the user only says "tweak the glow" or "change the highway lighting".
---

# DotCharter shaders

Vulkan loads **only the committed `.spv`**. Each one is embedded as
`DotCharter.Rendering.Shaders.<file>.spv` (see `DotCharter.Rendering.csproj`),
so a GLSL edit without a recompile changes nothing at runtime, and it
changes nothing silently. Full background:
`docs/agents/rendering/highway-pace-and-shaders.md` § Shaders.

## 1. Edit the GLSL, keeping its contracts intact

- **Push-constant blocks must be layout-identical** in every stage that
  declares them. `highway.vert` and `highway.frag` both declare the highway
  block, which carries `uint uEffects`.
- **Effect bits come in pairs:** `HighwayEffects` in C#
  (`DotCharter.Rendering/Vulkan/HighwayEffects.cs`) and the GLSL `EFFECT_*`
  constants must match bit for bit. A new effect also needs the toggle flow
  (AppConfig → settings/PlayMode VMs → state → `FrameSnapshot.Enable*` →
  the bit computed in `Fretboard3DEngine.RenderFrame`). Grep an existing
  toggle such as `EnableStringShimmer` and follow the same path.
- **Every post/composite fragment shader ends in `vec4(rgb, 1.0)`**. The frame
  must come out opaque, and `Vulkan/FrameAlphaPolicy.cs` owns alpha; see
  `docs/agents/rendering/vulkan-device-lifetime.md`.
- Descriptor sets and bindings used by a shader must match what
  `VulkanPipelineManager` creates. Check with the LSP before renaming or
  reordering them.

## 2. Recompile only what changed

```powershell
dotnet run --project tools/ShaderCompiler -- DotCharter.Rendering/Shaders
```

This targets Vulkan 1.0 with performance optimization through Silk.NET.Shaderc,
so no Vulkan SDK is needed. The tool rewrites **every** `.spv` in the folder,
and shaderc output is not byte-stable across versions. **Restore the ones
whose source you did not change** so the commit carries only real changes:

```bash
git status --short DotCharter.Rendering/Shaders
# for each modified X.spv whose X (source) is NOT modified:
git checkout -- DotCharter.Rendering/Shaders/<X>.spv
```

A compile error prints `FAIL <file>: <status>` with the shaderc message, and
the tool exits 1. Fix the GLSL; never hand-edit a `.spv`.

## 3. Validate the SPIR-V

If the Vulkan SDK is installed (`$env:VULKAN_SDK` is set, or `spirv-val` is on
PATH), validate each regenerated file:

```bash
spirv-val --target-env vulkan1.0 DotCharter.Rendering/Shaders/<X>.spv
```

If it is not installed, say so in your report instead of implying validation
ran. The install is `scoop install vulkan` (it also brings the Khronos
validation layer).

## 4. Prove it renders

- Build and run the headless rendering tests that cover the changed shader
  (see `docs/agents/testing-headless-rendering.md` for which golden and
  structure tests exist). Use `tools/agent/Invoke-Gate.ps1 -FilterClass
  "*Headless*"` or a narrower class name. These assert structure and pixels,
  never timing.
- For a visible change, run the app with `DOTCHARTER_VK_VALIDATION=1`. If the
  output log says `Validation requested but VK_LAYER_KHRONOS_validation is
  not installed`, validation did not run, and you should report that. Look at
  the frame itself before calling the change done.

## 5. Commit

Commit the GLSL and its regenerated `.spv` together, plus the C# side of any
push-constant or effect-bit change. Leave out unrelated `.spv` churn.
