# `Audio/` — the playback service, its gates and the engine config (deep reference) — threading contracts

## Threading contracts

- `PositionChanged` is raised on a thread-pool timer thread (`System.Threading.Timer` since 0.49; the `System.Timers.Timer` it replaced allocated an `ElapsedEventArgs` + `DateTime.Now` per tick) at up to 250 Hz. `EstimatedOutputLatencyMs` is cached per engine for one second (`Engine/OutputLatencyCache`) — a rebuilt engine misses on its reference and the in-place `Reconfigure` invalidates; it used to be two P/Invokes per tick and per renderer frame. Consumers that touch Avalonia bindings must coalesce + post to the UI thread (see `Audio/Coordination/AudioPlaybackCoordinator`, through `IUiDispatcher`). `ProjectState.CurrentTimeMs` has an allocation-free hot-path setter — keep it that way.
- **The smoothed playhead is `Engine/PlayheadInterpolator` (2026-08-30), and its state is ONE immutable sample.** The tick feeds it the native `AudiblePosition` (which advances only per device buffer — 256 frames @ 48 kHz ≈ 5.3 wall-ms, 10.7 song-ms at 2×); it extrapolates the previous report by `elapsed × speed`, low-passes toward native (0.05/tick) and snaps on >50 ms drift. `CurrentTimeMs` extrapolates between ticks from that same sample. The service used to hold the report and its timestamp in two fields, so a render-thread read between the tick's two writes paired a fresh report with a stale timestamp (one tick period × speed of overshoot, then held by the monotonic clamp); and `System.Threading.Timer` callbacks CAN overlap, so ticks serialize under the interpolator's own lock (never taken on an audio thread) and an overlapped tick with an older timestamp extrapolates by zero rather than backwards. **Every `Reset` (seek, pause, stop, load) moves `PlayheadInterpolator.Epoch`, and the last-reported position is `Engine/PlayheadFloor`, stamped with it (#309):** the tick reads the epoch BEFORE the native position and `TryTick` refuses a reading a seek overtook; `CurrentTimeMs` floors only within one epoch, and a read that extrapolated from a pre-seek sample reports the seek's target instead of writing the old position back (which held the playhead there for up to one 4 ms tick). `SeekTo` resets the interpolator BEFORE the floor — `PlayheadFloorTests` pins the order. Pinned by `PlayheadInterpolatorTests` with a fake clock at 0.5×/1×/2×: monotonic, zero snaps, per-tick increment cv 8.9 % (all of it the modelled ±0.6 ms timer jitter). The 2× viewport jitter the owner reported on 2026-08-30 was NOT here — it was the renderer's wall-clock trust budget compared against song-time clocks; see `docs/agents/ui/rendering-perf-detail.md`.
- **`PositionChanged`/`CurrentTimeMs` are LATENCY-COMPENSATED — they report what the
  user is HEARING. Never schedule audio against them.** Anything that must land ON a
  point of the song (metronome, claps, count-in) has to aim at `RawPlayheadMs`, the
  UNcompensated position — the song time the engine is currently WRITING INTO the
  output buffer, one output latency ahead of the audible one. Use
  `TryScheduleClick(type, chartMs)` (ABI v26 `dca_engine_schedule_click` over the
  render clock): it places the voice in the same output samples as the note's own
  audio, so the two cross the identical device buffer and arrive together — the
  latency cancels, and a WRONG latency estimate cannot bias the result. Trigger-now
  (`PlayClap`/`PlayMetronomeTick`) is correct only where "now" is genuinely what the
  caller means (count-in, preview button); used on a chart crossing it is
  systematically one output latency LATE, which is the bug v0.18 fixed.
  Scheduling is cleared on seek/pause/stop/speed change — the render clock keeps
  running while the transport does not.
- Engine/source/plugin mutation is serialized under `_gate`; VST load/unload is serialized by `_vstGate` off the UI thread. `_vstGate` is a `SemaphoreSlim`: the sandbox paths hold it across AWAITED pipe round trips (never a blocking wait — see the VST sandbox section of `audio-engine.md`); a Monitor is never held across an await. Never enumerate `_stems` outside `_gate` — snapshot under the lock and iterate the copy, so the lock is never held across a P/Invoke and `PositionChanged` is never raised with it held.
  **"Never across a P/Invoke" includes the SHORT ones that block the audio
  callback**, not only the multi-second opens: `dca_plugin_detach_from_input` is
  a wait-one-block barrier and at ABI v28 walks every seat. Six UI-thread
  members lock `_gate` and Play / Pause / Stop / SeekTo all reach it through
  `SnapshotStems()`, so tens of milliseconds there land between a click and the
  first frame, against a 150 ms budget. **`JuceAudioPlugin.Bypass`'s SETTER is
  one of those calls** — that is invisible at the call site, and it is how the
  Bypass button (`SetGuitarVstSlotBypass`) and the chain diff both came to run
  it under the lock in the one function whose phases 1b/1c had been extracted
  from it for exactly this reason. Collect the transition under the lock, apply
  it unlocked. `PlaybackGateHoldGuardTests` lints `.Bypass =` /
  `.AttachToInput(` / `.DetachFromInput(` inside a `lock (_gate)` block
  alongside the media opens.
  **…and the GETTER is not the app's intent.** Since the same wrapper audit
  `JuceAudioPlugin.Bypass` asks the engine instead of caching a flag, so it means
  "not in any seat's input chain" — a plugin loaded but never attached reads
  `true`. The app's own answer is `_guitarSlotBypass`, the twin of
  `_guitarSlotPlayer` and kept managed-side for the same reason: the two
  rehydration snapshots (engine rebuild, sandbox mode switch) and the chain
  differ all read it, and reading the property instead brought an ACTIVE plugin
  back bypassed after a device change while skipping a needed re-seat and
  emitting a phantom bypass op. `VstChainBypassIntentTests` lints that nothing in
  the service READS `.Bypass` off a plugin (assignments are the sanctioned write
  side) and that the intent map is maintained wherever the seat map is.
- **`_gate` is never held across a media OPEN.** `JuceAudioEngine.OpenFile`/
  `OpenMemory` spend 1–3 s inside JUCE's format readers — times 4–6 for a stem
  set — and six UI-thread members lock the same gate (`CurrentAudioPath`,
  `HasStems`, `LoadedStemNames`, the volume setters, and `SnapshotStems()`,
  which every one of `Play`/`Pause`/`Stop`/`SeekTo` calls first), so holding it
  across a decode froze the window for the whole load. `SwapMasterSource`,
  `LoadStemsCore` and `ReplaceEngineGated`'s song re-open all use the same phase
  split `ApplyGuitarVstChainCore` uses: **read state + detach under the lock,
  open UNLOCKED, publish under the lock.** Two things make the unlocked window
  safe and both are required: `_mediaLoadGate` (a separate lock only the
  background load paths take) serializes the publishes, and the publish step
  re-checks `ReferenceEquals(_engine, engine)` and DISCARDS what it opened if a
  device change replaced the engine mid-decode — a source belonging to a
  destroyed engine must never reach the mixer. Pinned by
  `PlaybackGateHoldGuardTests` (a source lint: the invariant is structural, and
  proving it at runtime would need a real device and a real multi-second
  decode). Engine create/destroy under `_gate` in `ReplaceEngineGated` stays —
  that path is the engine's exclusive owner by design.

- Dispose order in `JuceAudioPlaybackService.Dispose` is deliberate (stop sources → detach instrument → unload guitar chain → free audio → destroy engine): VST hosts crash if teardown interleaves with the audio thread. Don't reorder.
- The JuceSharp wrappers drain in-flight P/Invokes before destroying handles
  (`NativeHandleOwner`), so a position poll racing a dispose surfaces as
  `ObjectDisposedException` (caught), never a native crash. **This line was a
  SAFETY CLAIM that was false on three counts until 0.61** — read the caveats,
  because a reader who stops at the first sentence stops looking:
  - The drain used to give up after **2 s and free the handle anyway**, and the
    calls that legitimately run longer are the ones a user reaches (a modal ASIO
    control panel, a device `Reconfigure`, a cold VST3 scan at 25 s+). It now
    waits on the last `Release` and, on expiry, LOGS at Error and **leaks** the
    handle. A leak beats a use-after-free.
  - The drain is **PER-HANDLE**, and every child wrapper owns its own — so the
    engine's counter said nothing about a `dca_source_*` call in flight, and the
    4 ms position poll here was exactly that call. `JuceAudioEngine` now keeps a
    weak registry of its sources/plugins/MIDI inputs and drains them before
    `dca_engine_destroy`. Pinned by JuceSharp's `NativeHandleDrainTests` /
    `EngineChildDrainTests`.
  - **The DSP tone rig NOW participates in it** (2026-09-11, JuceSharp 0.8.x).
    It did not until then: `DotCharter.Services/Audio/Dsp/DspNativeInterop` held
    a bare `dca_engine*` from the unpinned `JuceAudioEngine.NativeHandle`
    (`TryPeek()`) and P/Invoked it through the app's OWN copy of the ABI
    (`DspNativeMethods.cs`, deleted), so its calls were invisible to the drain
    and it was safe only by the convention below. The wrapper now binds the rig
    (`JuceAudioEngine.GetDspChain(player)` → `JuceDspChain`/`JuceDspNode`), and
    every call there `Acquire`s the handle for its duration — so a rig call
    racing an engine dispose is a caught `ObjectDisposedException`, like every
    other child wrapper. `DspNativeInterop` is the one adapter;
    `Services/Audio/DspNativeBindingTests` fails the gate on any `DllImport` /
    `LibraryImport` of the audio library anywhere in `DotCharter.Services` or
    `DotCharter.UI`, so a second copy cannot come back.
    **The gate and the detach rule both STAY.** Pinning closes the window
    around a call that is IN FLIGHT; a call that has not started yet pins
    nothing, so `AttachEngine(null)` before `dca_engine_destroy` is still the
    barrier — and `_gate` is also what keeps the managed mirror coherent across
    a re-attach. Do not add a second caller that bypasses `JuceDspChainService`.
    **And that "every P/Invoke" was itself false until 0.62**:
    `ProcessOfflineBuffer` (the offline render path — the take re-amp then, the AI match `RigRenderer` now) was expression-bodied straight
    into `Safe(() => _native.ProcessBuffer(...))` with NO lock, over a
    non-volatile `EngineHandle` — so `AttachEngine(nint.Zero)` plus
    `dca_engine_destroy` could complete inside the interop's own
    null-check-then-call window. It holds `_gate` now (the caller renders in
    chunks, so the hold is one block, not the whole file), and the claim is
    mechanical rather than a convention: `Services/Audio/Dsp/DspChainGateHoldTests`
    fails the default gate on any `_native.` use outside a `lock (_gate)` body,
    with an INVERTED exemption list — the only members allowed out are the four
    static-catalogue ones (`Player`, `LoadCatalog`, `LoadValueLabels`,
    `LoadStep`), which take no engine handle, and each exemption carries a reason
    and is asserted to still name a member that exists.

