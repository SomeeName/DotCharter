# Headless rendering (testing) — golden-frame tolerance policy; determinism; Vulkan validation

## Golden-frame tolerance policy

**Not** byte-exact pixel equality. GPU rasterization (MSAA, blend rounding,
driver texture filtering) varies frame-identical scenes by a few LSBs across
vendors/driver versions, so a byte compare is flaky by construction. Assert
STRUCTURAL properties with explicit tolerances instead (`GoldenFrame`):

- `NonBlackRatio` — the frame rendered something (not an all-black failure).
- `ColorfulRatio` — saturated colour clusters present (string lanes / gems /
  ribbon stroke), without pinning exact hues.
- `DifferenceRatio(a, b, perChannelTol)` — two frames are distinct (or identical)
  beyond a small tolerance that absorbs AA jitter.
- `ColumnLuminanceSpread` — a column varies vertically (gradient/atmosphere) vs a
  flat opaque band.
- `NonBlackRatioInRegion` — content in a sub-rectangle.

Byte-exact comparison is reserved for values that are genuinely stable — e.g.
the readback dimensions (`width*height*4`) — never rendered pixels. On failure,
tests write the frame PNG via `GoldenFrame.Attach(...)`.

## Determinism

A golden frame must be a pure function of its inputs:

- **Fixed `timeMs` per golden** — never wall-clock. The facades take `timeMs`
  and pin `snapshot.CurrentTimeMs` to it.
- **Time-driven effects must be pinned by the caller.** Anything that reads song
  ms — string shimmer / `uTime` shader band, hit sparks, beat pulse, crowd
  energy — is a function of `CurrentTimeMs`, so a fixed `timeMs` freezes them.
  Live-Play-suppressed effects stay suppressed off a `LivePlayActive=false`
  snapshot. Do not introduce any random/`DateTime.Now` input.
- **Camera convergence (3D).** `HeadlessHighwayRenderer.Render` eases the
  anchor-follow camera to convergence (a large fixed ease `frameTime`, which only
  drives the ease rate and snaps to the exact target) before reading pixels, so
  the framing is stable regardless of call order or the engine's prior state.

## The software lane: same goldens, no extra tolerance

Measured 2026-10-14 (#315) over the 62 frames the `Rendering/Headless`
classes dump with `DOTCHARTER_GPU_DUMP=1`, lavapipe 26.2.4 against an RTX 3080:

- **lavapipe vs lavapipe, 3 runs: byte-identical** (all 62 frames, max channel
  difference 0). The software lane is deterministic.
- **lavapipe vs GPU:** worst frame 0.144 % of pixels beyond the ±8-per-channel
  tolerance `GoldenFrame.DifferenceRatio` uses (median 0 %, mean 0.019 %); mean
  absolute difference ≤ 0.81 LSB per channel; the largest single-channel
  difference, 92, is on anti-aliased gem and tail edges. 2D timeline frames
  differ by at most 3 LSB.

So the lane runs the SAME tests with the SAME structural thresholds — every one
passes unchanged — and there are no software-specific goldens. If a future
lavapipe or test fails only on the software lane, re-measure the two lanes
against each other before touching a threshold.

## Vulkan validation (fail-on-validation)

The seed Vulkan tests set `DOTCHARTER_VK_VALIDATION=1` before constructing the
renderer, so when the Khronos validation layer (`VK_LAYER_KHRONOS_validation`) is
installed it is enabled and each rendered frame asserts
`VulkanValidationDiagnostics.ErrorCount == 0` (a validation error fails the test).
When the layer is NOT installed, `VulkanValidationDiagnostics.Active` stays false
and the assertion is skipped gracefully. **This machine (2026-07) has no explicit
Khronos validation layer registered**, so the assertion currently no-ops here;
install the Vulkan SDK / validation layer to exercise it.

