# Rendering / perf — the 3D preview's render loop

Part of the rendering/perf reference. Router: [../rendering-perf-detail.md](../rendering-perf-detail.md).

- **The 3D preview render loop is idle-gated (0.14 P6) and fps-capped (0.14.2).**
  Since 0.16 the loop lives in the shared `Preview3DRenderDriver` (both the
  native and the composed canvas drive the identical code). When the
  transport is stopped and clock/size/camera/wake events report no
  change, the driver parks at a 4 ms signal poll with a ≤10 Hz trickle
  (`Preview3DFrameGate.Decide` — pure, unit-tested). **The trickle runs at the
  configured rate only for `Preview3DFrameGate.TrickleSettleMs` (2 s) after the
  last WOKEN frame, then relaxes to 1 Hz** (#446): every trickle frame is a full
  present, and the composed path's `UpdateWithKeyedMutexAsync` makes Avalonia
  snapshot each present into a NEW pane-sized texture (`glTexImage2D` under the
  compositor tick) — a paused editor re-created one ~9 times a second forever.
  The settle clock (`Preview3DRenderDriver._sinceLastWake`) restarts only on a
  frame `Preview3DFrameGate.IsWake` says woke, never on a trickle frame, and the
  HUD prints the rate the loop RUNS. Pinned by
  `Preview3DFrameGateTests.AParkedMinute_PresentsAboutOncePerSecond_NotTenTimes`
  and `Preview3DDriverWiringTests.TheLoop_FeedsTheGateItsSettleClock_AndOnlyAWakeRestartsIt`.
  Wake sources: `CurrentTimeMs` polling + EVERY `ProjectState` property change
  (each 3D setting the snapshot reads is written through it),
  `ChartEditorViewModel.ChartDataChanged`,
  `ISelectionService.SelectionChanged`, `ChartTheme.ThemeChanged`, the canvas's
  styled properties, and `Fretboard3DEngine.IsCameraAnimating` (easing keeps
  rendering until it snaps settled). New paused-visible state must flow through
  one of these or accept the trickle; `Preview3DComposedCanvas.RequestRender()`
  wakes explicitly. **The gate's clock compare must use the UNLED transport
  clock** (`Preview3DRenderDriver._lastRenderedRawTimeMs`): the frame is
  rendered at `CurrentTimeMs` plus the lead WHILE PLAYING — the lead goes through
  `Controls/RenderLeadEase` (the 2D timeline's rule too, #112): live while playing,
  eased out over 120 ms on pause, then exactly 0, because a parked playhead drawn
  at `paused + EMA` crept and flickered while paused (`RenderLeadEaseTests`). On the
  composed path `ComposedPresentPump` folds a present-latency sample into that
  lead's EMA after EVERY present — so comparing the LED clock made
  present → EMA → "the time changed" → render self-sustaining and the park
  unreachable, i.e. a paused editor composing full frames at the fps cap
  forever. Pinned by `Preview3DIdleGateLeadTests` (which also keeps the
  led-clock feed as a live demonstration that it never parks). `FrameStats` never records idle gaps (the first post-park
  frame is unsampled), so the HUD fps reads the trickle rate while parked — that
  is the gating working, not a perf bug. Settings → Graphics feeds the loop via
  the `Preview3DPacing` statics (fps cap, 0 = unlimited; trickle Hz 1–10):
  a **cap wait is not a park** — wake flags stay armed and the elapsed time
  counts as a real frame interval (camera easing + FrameStats stay honest).
  The cap floor (15 fps, `GraphicsSettingsPolicy.MinCappedFps`) sits above the
  10 Hz trickle ceiling on purpose so the cap can never starve the safety net.
  - **The loop's `Thread.Sleep`-based pacing REQUIRES 1 ms timer resolution**,
    pinned process-wide at startup by `Platform.WindowsTimerResolution` (v0.33).
    Windows quantises `Sleep(n)` to the system timer resolution, which defaults
    to ~15.6 ms — measured on the owner's machine, `Sleep(1)` = 15.5 ms
    unraised, 1.5 ms raised. Without the pin, `Preview3DFrameGate.WaitForFpsCap`
    sleeps a full ~15.6 ms quantum whenever a frame finishes faster than the cap
    interval (always, on a high-refresh monitor), so the loop caps at ~64 fps
    regardless of the monitor-refresh-derived cap and every pace/park injects a
    15 ms hitch. JUCE raises it too, but only while an audio device is active,
    so a paused editor / scrub would otherwise fall back to the coarse default.
    Do not remove the startup pin.
  - **The CAP WAIT itself uses `Platform.PreciseSleep` (v0.32.5)** — a
    high-resolution waitable timer (Win10 1803+, `Thread.Sleep` fallback):
    even pinned, `Sleep(1)` costs ~1-1.6 ms, which overshot every high-refresh
    cap interval (294 fps cap paced ~5 ms ≈ 200 fps; precise waits hold ±0.3 ms
    → ~cap rate). Two v0.32.5 fixes feed the cap the RIGHT number: the
    display-refresh probe resolves the primary monitor's device name
    explicitly (`EnumDisplaySettingsW(null)` returns FALSE on multi-adapter
    Win11 — `DisplayRefreshProbeTests`), and the driver's probe TTL no longer
    starts at `long.MinValue` (the subtraction wrapped negative, so the probe
    NEVER ran and every uncapped user was pinned to the 60 Hz fallback).
    The default present mode is Mailbox since v0.32.5 (identical pixels, no
    vblank block; one-time Fifo→Mailbox config migration in
    `JsonConfigurationService.NormalizeAppConfig`), so the loop's pacing — not
    the FIFO acquire — is what times frames.
- **The driver VERSIONS its selection, and a DECLINED frame re-arms the wake
  flag (2026-09-02).** Two wirings the 2D renderer had and the 3D driver did
  not. `RunSnapshotUpdate` left `SnapshotUpdateContext.SelectionVersion` at its
  -1 default, which `FrameSnapshot.Update` reads as "unversioned → re-hash all
  six lookup sets every frame" — O(selection) × 6 on the render thread at the
  fps cap for as long as anything was selected (select-all on a 3,000-note
  arrangement ≈ 450,000 hash inserts per second of pure waste). It feeds
  `ISelectionService.SelectionVersion` now, beside the three `Selected*` lists.
  And the wake flag is consumed BEFORE the frame is attempted, so a
  `BeginFrame` decline (composed back-pressure, a native acquire hiccup) used to
  swallow the request: a paused editor's edit / selection / theme change that
  landed on a decline waited for the idle trickle (100 ms default, 1 s at the
  1 Hz floor). The decline branch re-arms it exactly as the failed-snapshot
  branch always did. **And so does a frame that THREW (2026-09-04)** — the same
  argument one branch over, missed by the 2026-09-02 pass: the loop deliberately
  survives a transient device hiccup, so without the re-arm the edit that landed
  on one waited for the trickle too. **So does the ABANDONED iteration** — the
  loop re-checks `_isRenderable` between the gate and the present, because a hide
  can land in the milliseconds the snapshot build takes. **The rule is: the flag
  is consumed BEFORE the attempt, so every branch that produces no frame puts it
  back.** Four do now (failed snapshot, decline, exception, abandon).
  **And every path that becomes RENDERABLE must ARM a frame, not merely unlatch**
  — the parked loop's self-heal probe wrote `_isRenderable` directly instead of
  going through `NotifyEffectiveVisibility`, so a heal (exactly the case where
  the visibility watcher's notification is what went missing) unfroze the loop
  and then PARKED it, leaving on screen the stale frame the heal exists to
  replace. All pinned by `UI/Preview3DDriverWiringTests`.
- **The visual lead is scaled by the playback speed on BOTH paths
  (`VisualLeadTiming.LeadScale`, 2026-09-02).** `EffectiveLeadMs` is WALL ms
  (present latency); the clock it is added to is SONG ms, which runs `speed`×
  faster — in `lead` wall-ms the audio advances `lead × speed` song-ms.
  Unscaled, 2× drew half a lead behind the audio and 0.5× half a lead ahead:
  a constant A/V offset, not jitter, and the same wall-vs-song confusion
  `TimelineViewMath.SpeedFactor` fixed for the lag budget one expression
  earlier. The scale is LINEAR (a slower song wants a smaller lead; the lag
  budget's factor floors at 1 on purpose and the two must not be merged), 1 for
  anything but a positive finite speed, and the `MaxLeadMs` clamp stays on the
  wall lead. The idle gate still compares the UNLED clock. Pinned by
  `TimelineViewMathTests.ResolveRenderPlayheadMs_ScalesTheLeadByThePlaybackSpeed`
  + the wiring lint above.
- **The corner HUD's fps and its percentiles measure different seconds, and
  until 2026-09-16 it did not say so.** `10 fps · p50 7,0 / p99 7,1 ms` was
  reported as a render regression (7 ms frames cannot be 10 fps). Nothing was
  throttled and no counter was wrong: the loop was PARKED, every one of those
  ten frames was an idle-trickle frame, and a trickle frame comes out of a park
  — so `_idleSkipped` substitutes a nominal 60 Hz delta and records NOTHING in
  `FrameStats`. The fps number was this second's trickle rate; the percentiles
  were the last second the loop actually ran. Only the labels lied.
  `Controls/Preview3DStatsLine` now formats the line: a window whose frames were
  ALL trickle frames reads `idle N Hz · last active p50 … / p99 … ms`
  (N = `Preview3DPacing.IdleFps`, the Graphics setting, not a constant), one
  real frame in the window puts it back to `N fps · p50 …`, and a window with no
  sample behind it at all prints `p50 — / p99 —` rather than a perfect 0,0 ms.
  The driver counts `_idleFrameCount` beside `_fpsFrameCount` for it. The idle
  GATING itself is unchanged — it was never the bug. Pinned by
  `UI/Preview3DStatsLineTests` (culture-explicit: the app formats with
  `CurrentCulture`, which is why the owner's screenshots show a decimal comma).
- **The composed 3D surface has the same whole-pixel requirement as the 2D
  timeline** — `UpdateSizes` takes both its `visual.Size` and its
  `_driver.SetSize` from one `ComposedSurfaceGeometry.Measure` call and snaps
  its origin onto the pixel grid. See the composed-surface bullet in
  [timeline-2d.md](timeline-2d.md) for the mechanism; do not let the two hosts
  drift apart again.
- **The HUD's frame time is the interval between PRESENTED frames, and it was
  the interval between ATTEMPTS (2026-09-16).** The parked-state fix above left
  the ACTIVE case still contradictory — `34 fps · p50 7,0 / p99 48,4 ms`, which
  is 143 fps beside 34. `FrameStats.Add` ran BEFORE `RenderFrame`, so every
  DECLINED attempt (composed back-pressure: the compositor still owns every pool
  image) contributed a short interval to the percentiles while counting for
  nothing in the fps tally — p50 was the decline loop's spin rate.
  `Preview3DRenderDriver._sincePresent` samples on the success path now (still
  skipping the first frame out of a park, which is the park, not a frame), so
  `fps ≈ 1000 / p50` holds by construction. The line names it `frame p50` and
  carries a PACING clause — `cap N` / `trickle N Hz` / `uncapped`, plus
  `N declined` whenever there were any — because the cap alone does not explain
  34 fps under a 63 fps ceiling and a reader with only the cap has to guess.
  `frameTime` (the camera-easing delta) is unchanged and still the raw stopwatch
  interval. Pinned by `UI/Preview3DStatsLineTests`, which asserts the rate and
  the interval RECONCILE rather than only checking the wording.
- **The Live Play half of the snapshot staging is `Controls/LivePlayFeedbackDrain`**
  (#656): `RunSnapshotUpdate` calls its `Drain` once, after stamping the frame's
  `LivePlayActive`, and it owns the crowd meter, the sticky EARLY/LATE + tier cues
  (re-stamped into EVERY frame — the two snapshots ping-pong) and the marker
  `Seq`. A `LivePlayFeedbackEvent.IsGrade` event moves the cues only: no crowd
  verdict, no second marker. Pure and allocation-free; pinned by
  `UI/LivePlayFeedbackDrainTests` and
  `Preview3DDriverWiringTests.TheSnapshot_DrainsLivePlayFeedbackThroughTheDrain_Once`.
