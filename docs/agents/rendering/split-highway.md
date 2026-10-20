# The split highway (built for couch co-op)

Deep reference for `DotCharter.Rendering/AGENTS.md`. `Engines/HighwayViewport.cs`
+ `Data/SplitHighwayLayout.cs`: how one frame draws N players' highways.
**The app has no multi-viewport caller since couch co-op was removed on
2026-09-19** (its app-side feed and `Preview3DRenderDriver.SetCoopParts` went
with it; restore from tag `pre-cut-2026-09`). The renderer capability stays: the
solo frame is the one-viewport case of the same overload, and
`HeadlessSplitHighwayTests` still pins the N-view path.

- **`Engines/HighwayViewport.cs` + `Data/SplitHighwayLayout.cs` — the SPLIT
  HIGHWAY (couch co-op, v0.45).** `Fretboard3DEngine.RenderFrame` has a second
  overload taking `IReadOnlyList<HighwayViewport>`: ONE
  `BeginFrame`/`EndFrame` pair, one depth buffer, one present, N scene
  recordings — each scissored to its own player's rect and driven by its OWN
  `FrameSnapshot` and its OWN `HighwayCameraState`. The single-snapshot
  overload delegates to it with one full-frame view, so there is exactly one
  frame implementation and the editor/solo frame is byte-identical (pinned by
  `HeadlessSplitHighwayTests.ASingleViewportSplit_MatchesTheOrdinarySoloFrame`).
  Rules that are load-bearing:
  - **The camera easing state is per VIEW, not per engine.** It used to be
    three fields on the engine, which is exactly why a split could not exist —
    two players would have shared one `_camX`/`_fretSpan` and both halves would
    have panned to whichever player's hand moved last. Everything else the
    engine owns (pipelines, textures, the glyph atlas, the scene graph) is
    genuinely shared and must NOT be duplicated per player.
  - **`IVulkanRenderService.SetViewportRect` sets viewport AND scissor.** A
    viewport alone maps NDC into the sub-rect but does not CLIP, and the ortho
    passes (crowd background, vignette) span the whole NDC square on purpose —
    so with only a viewport, player 1's sky paints straight across player 2's
    half. Both are already dynamic pipeline state, so this costs no rebuild;
    the post/blit passes reset the full rect themselves, so a split frame still
    composites and presents whole. The rect is given in REQUESTED panel pixels
    and rescaled onto `RenderExtent` inside the renderer, so it is correct
    under render scale and a surface-clamped swapchain.
  - **Frame-GLOBAL state is resolved ONCE, ahead of the frame, from all views
    together.** A per-view `SetPostEffects` flags a swapchain rebuild every
    frame the moment two players disagree about bloom; a per-view glyph-atlas
    signature sees two alternating values inside one frame, misses every time
    and re-probes both charts' characters per frame. Hence
    `ComputeRegisterSignature(viewports, count)`.
  - **`SplitHighwayLayout` is pure and in the DEFAULT gate** because its two
    failure modes are cheap there and expensive on a GPU: a zero/negative
    extent is `VUID-VkViewport-width-01770`, and an overlapping pair means one
    player drawing into the other's half of the shared depth buffer. It also
    guarantees the tiling reaches the far edge (the last view absorbs the
    integer remainder, so an odd height leaves no unpainted row of raw clear
    colour), leaves exactly `DividerPx` between neighbours, and degrades to ONE
    full viewport rather than something illegal on a frame too small to split.
  - **Stacked is the default split, not side-by-side.** The camera framing is
    aspect-derived and owner-settled (see the camera note below): halving the
    WIDTH of a 16:9 frame yields a ~0.9 aspect the framing has never faced and
    squashes the board vertically, which is the exact failure both rejected
    reframe attempts produced. Halving the HEIGHT keeps each view in the wide
    family the editor's preview pane already lives in. `Auto` flips to
    side-by-side only on a portrait window.
  - Evidence: `SplitHighwayLayoutTests` (default gate) and
    `HeadlessSplitHighwayTests` (GPU-gated) via
    `HeadlessHighwayRenderer.RenderSplit`. The golden frames check each half
    separately, check the divider band stays black (the scissor proof) and
    isolate the second view by re-rendering it with player 1's chart.
