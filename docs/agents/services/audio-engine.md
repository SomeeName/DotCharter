# `Audio/` — the playback service, its gates and the engine config (deep reference)

One file per subject under `audio-engine/`. Read the one you need:

- [playback-service.md](audio-engine/playback-service.md) — `Audio/JuceAudioPlaybackService.cs` + audio health logging
- [equalizer-and-silent-transport.md](audio-engine/equalizer-and-silent-transport.md) — `Audio/Equalizer/` (the six-band parametric EQ, ABI v29); `Audio/SilentTransport/`
- [threading-contracts.md](audio-engine/threading-contracts.md) — threading contracts
- [engine-configuration.md](audio-engine/engine-configuration.md) — engine configuration
- [audio-architecture.md](audio-engine/audio-architecture.md) — audio architecture (JUCE): layers, ABI, per-player rigs

## Focused interfaces for view models (PENDING OWNER APPROVAL)

The UI rewrite's view models may not name `IAudioPlaybackService` or
`JuceAudioPlaybackService` ([target-architecture.md § 2.3](../ui/target-architecture.md)).
They take one of these seams instead. **The shapes below are a proposal: wave 3
must not build on them until the owner approves.** All five live in
`DotCharter.Services/Audio/Abstractions/`; `IAudioPlaybackService` EXTENDS all
five (members moved verbatim, no new behaviour), so `JuceAudioPlaybackService`,
`NullAudioPlaybackService` and the tests' `FakeAudioPlaybackService` implement
them with no new code. Each registered seam is a forwarding singleton onto the
one instance (`App.AddAudioSeams`); none is `IDisposable` itself.

| Seam (members) | Members (signatures) | Uses it today / will use it | Threading |
|---|---|---|---|
| `IPlaybackTransport` (27) | `CurrentTimeMs`, `RawPlayheadMs`, `IsPlaying`, `SeekGeneration` { get; }; `PlaybackSpeed` { get; set; }; `event EventHandler<double> PositionChanged`; `Play()`, `Pause()`, `Stop()`, `SeekTo(double)`; `LoadAudioAsync(string)`, `LoadAudioAsync(string, CancellationToken)`, `CurrentMediaGeneration`, `OwnsMedia(string, long)`, `RunIfOwned(string, long, Action)`, `LoadTemporaryAudioAsync(string, CancellationToken, out long)`, `TryRestoreAudioAsync(string?, string, long, double)`, `LoadAudioFromMemoryAsync(ReadOnlyMemory<byte>, string, string?)`, `FreeAudio()`, `FreeMasterAudio()`, `GetTotalAudioLengthMs()`, `CurrentAudioPath`; `PlayMetronomeTick(bool)`, `PlayClap()`, `TryScheduleClick(int, double)`, `ClearScheduledClicks()`, `TryScheduleCalibrationClap(out long)` | **now:** `ChartRenderer`, `Preview3DRenderDriver`. **will:** every Play Mode screen that plays a song (Countdown, Playing HUD, Paused, Results listen-back), `AudioPlaybackCoordinator` | `PositionChanged` on a thread-pool timer at up to 250 Hz — coalesce to ≤ 60 Hz before UI state. `Play`/`Pause`/`Stop`/`SeekTo` reach `_gate` (short holds). Schedule against `RawPlayheadMs`, never `CurrentTimeMs` |
| `IAudioDeviceControl` (22) | `ActiveSampleRate`, `ActualBufferFrames`, `EstimatedOutputLatencyMs`, `MeasuredOutputLatencyMs`, `IsDeviceLost` { get; }; `AudioDelayMs`, `MidiOffsetMs` { get; set; }; `event DeviceBufferFramesChanged` (`int`), `DeviceLost`, `DeviceReconnected` (`AudioDeviceReconnectedEventArgs`); `ApplyAudioConfiguration(AppConfig)`, `EnsureAsioDevicesProbed(AppConfig)`, `EnsureWasapiDevicesProbed(AppConfig)`, `TryOpenAsioControlPanel(IntPtr, out string?)`, `GetWasapiOutputDevices()`, `GetAsioDevices()`, `GetWasapiInputDevices()`, `GetAsioInputDevices()`, `GetInputDeviceChannelCount(string, string)`, `GetOutputDeviceChannelCount(string, string)`, `GetOutputChannelNames(string, string)`, `GetInputChannelNames(string, string)` | **now:** `LiveInputChannelViewModel`, the Settings ▸ Audio tab. **will:** the status bar, Diagnostics, the latency wizard | the three events may arrive on any thread (watchdog timer, engine rebuild). `ApplyAudioConfiguration`, the probes and the ASIO panel may rebuild or stop the engine — slow, never on a hot path |
| `IStemMixer` (9) | `SongVolume` { get; set; }; `HasStems`, `LoadedStemNames` { get; }; `LoadStemsAsync(IReadOnlyList<StemLoadRequest>)`, `ClearStems()`, `SetStemVolume(string, double)`, `SetStemMute(string, bool)`, `SetStemSolo(string, bool)`, `SetMasterMixMuted(bool)` | **now:** `StemMixerViewModel`, `StemTrackViewModel`, `StemDuckFollower`. **will:** the rewritten mixer pane | no events; `LoadStemsAsync` opens the stem files (async) |
| `IVstChainHost` (17) | `IsVstEffectEnabled`, `IsVstInstrumentEnabled` { get; set; }, `VstEffectVolume`, `VstInstrumentVolume` { get; set; }; `IsInstrumentVstLoaded`, `IsInstrumentVstLoading` { get; }; `IsGuitarVstSlotLoaded(Guid)`; events `InstrumentVstLoadChanged` (`bool`), `GuitarVstSlotLoadChanged` (`(Guid, bool)`), `GuitarVstSlotEditorClosed` (`Guid`), `InstrumentVstEditorClosed`; `ScanVstPlugins()`, `SetGuitarVstChain(IReadOnlyList<VstChainEntry>)`, `ToggleGuitarVstSlotEditor(Guid, bool)`, `SetGuitarVstSlotBypass(Guid, bool)`, `LoadInstrumentVst(string, bool)`, `ToggleInstrumentVstEditor(bool)` | **now: nobody — so it is NOT registered.** Its only caller, the editor's `TransportViewModel`, also drives the master bus and the transport. **will:** the editor wave's VST pane | events may arrive on the caller's thread or a worker (load task, sandbox pipe reader). A load returns before the plugin is up; the result is the `…LoadChanged` event |
| `ILiveInputCapture` (7) | `InputSampleRate` { get; }; `InputPitchShiftSemitones` { get; set; }, `InputPitchShiftMode` { get; set; }; `ReadInputCapture(float[])`, `ReadInputCapture(float[], out ulong)`, `ReadInputCaptureWindow(float[])`, `TryGetInputCaptureSync(out ulong, out long)` | **now:** `Audio/Tuning/LiveTuner` (the Tuner dialog and the Tools popover's readout). **will:** the rig tuner, the guided tuning check, onboarding, the tone matcher, Live Play's detection. The latency wizard does NOT take this: it needs the calibration clap and the seat-0 routing override too, so it has its own `Audio/Calibration/ICalibrationAudio` (`CalibrationAudio` over the engine) | reads copy out of a native ring, take no managed lock, never throw — any thread. `ReadInputCapture` is SINGLE-consumer (a read cursor); everyone else uses `ReadInputCaptureWindow` |

**Deviations from the plan's six names, and why:**

- **No `IPlaybackHealth`.** Device-lost state is folded into `IAudioDeviceControl`:
  both callers that read it (`StatusBarViewModel`, `DiagnosticsViewModel`) also
  read the device's rate and buffer, so a separate seam only added a parameter.
- **No `IMidiInput`.** `IAudioPlaybackService` has no MIDI INPUT; its MIDI members
  are the chart preview's OUTPUT (`SetMidiInstrument`, `UpdateMidiNotes`,
  `MidiOffsetMs`), used by Services and the editor transport. The MIDI input seam
  already exists as `Audio/Midi/IRigMidiInputSource` (rig controller input,
  implemented by `JuceAudioPlaybackService` only and reached with an `as` cast;
  it decodes CCs, program changes and NOTE-ONs (`RigMidiNoteReceived`, velocity
  above 0) on the MIDI thread and does nothing else there). Its second consumer is
  the editor's MIDI binds (#310): `Services/Infrastructure/Input/MidiTriggerInput`
  turns notes and CCs crossing UP through 64 into presses, queued (never merged,
  capped at `QueueCapacity`) and drained on the UI thread through `IUiDispatcher`
  — `MidiTriggerInputTests`. It opens no device of its own: the one MIDI input is
  the rig's (`RigConfig.RigMidiInputDevice`, restored at startup).
  `IPlayerInputRouting` is the per-seat live-input seam. Both stay as they are.
- **Added `ILiveInputCapture`.** The capture ring is the most widely shared
  slice of the service: the tuner, the rig tuner, the guided tuning check,
  onboarding, the latency wizard, the tone matcher and Live Play all read it,
  and for both tuners and the guided tuning check it is ALL they read.
- **Left on `IAudioPlaybackService` only** (Services and the composition root):
  the master bus (`Volume`, `InputVolume`, `MonitorVolume`, `Pan`, `PitchShift`,
  the three fixed filters), the parametric EQ, the MIDI chart preview, waveform
  decoding, `DeviceManager`, `Diagnostics`, `InitializeAsync`/`ShutdownAsync`,
  `SupportsCleanShutdown` and `Dispose`. The editor wave decides whether the bus
  and EQ become a sixth seam.

**What pins it** (`DotCharter.Tests/UI/AudioSeamRegistrationTests.cs`, default
gate): the service's direct interfaces are exactly the five plus `IDisposable`;
every implementer found by reflection maps every seam member; the registered
set is exactly the four with a consumer; each registered seam resolves to the
SAME object as `IAudioPlaybackService` (proved non-vacuous: pointing `IStemMixer`
at `new NullAudioPlaybackService()` failed it); and no Presentation type holds
the whole service. That last rule exists because the banned-API analyzer
(`BannedSymbols.Presentation.txt`) flags `typeof` and members DECLARED on the
banned type but not a field, property or parameter typed as it, nor a call to a
member it inherits from a seam — measured with a probe class in
`DotCharter.Presentation` on 2026-09-19.

**Disposal:** the container captures the one instance once per seam it resolves,
so `ServiceProvider.Dispose` may call `Dispose` on it more than once.
`JuceAudioPlaybackService.Dispose` is idempotent (an `Interlocked` flag), and the
clean-shutdown path disposes the audio service explicitly BEFORE the container
(`App.RunCleanShutdown`), so the engine's teardown order is unchanged.

Decoded project master loads and stale-load compensation use `FreeMasterAudio`: the mixer
can finish loading the current project's stems independently of the queued master.
Full cleanup uses `FreeAudio`, which also clears stems. Master teardown
detaches under the apply gate and disposes outside it. Both register an empty request
first and release under the next `_gate` hold only while that request is still the
newest: a song requested in between keeps its source and stems (`FreeAudioFor`,
`FreeMasterAudioFor`).
`MediaRebuildOwnershipTests` drives the orderings on the Null device. Native coverage:
`LibraryDeviceRateAudioNativeTests.A_master_reset_preserves_native_stems_while_full_reset_clears_them`.

## Rebuild publication (#530)

Current controls of restored stems merge into pending recovery by stem name; missing
stems keep their retained path, offset and controls. Explicit `ClearStems` clears
retained stem intent even if no source reopened, and bumps `_stemClearGeneration`: a
rebuild records it with its stem snapshot and restores nothing once it moved, checked
before the replay and again before `LoadStemsCore` publishes the restored set.
Ordinary `LoadStemsAsync` requests also capture the clear generation before queueing;
publication drops the set if an explicit clear landed while it waited or decoded.
Rebuild restores retain the same-song/borrowed-take ownership policy.
Seat order uses the same validated
device channel count as the engine mask, so a discarded mask keeps default order.

- `Engine/MediaRebuildRecovery` keeps a durable song and stem-mix snapshot across
  failed engine creation, master reopen and partial stem restore. It is released
  only after media restoration succeeds, or superseded by logical media changes.
  Rebuild replay uses the same guitar-chain request generation; it must not turn
  an old captured chain into a newer request. Pending chains remain until applied.
- Chain requests publish a generation before scheduling. Workers reject obsolete
  generations after entering `_vstGate`, for both backends. In-process chain
  changes and bypass toggles detach all slots before attaching active slots in
  requested order through `Vst/VstInputOrder`, retaining plugin instances and state.
  Rebuild snapshots preserve that order separately from dictionary insertion order.

- Lock order is `_engineConfigGate` -> `_vstGate` -> `_mediaLoadGate` -> `_stemLoadGate` ->
  `_mediaOperationGate` -> playback `_gate` -> `_midiGate`. Decoding holds only its
  load gate (song or stem set). Ownership changes and publication take the operation lock before
  the playback gate, including cancellation, hand-back, rebuild and teardown.
  `RunIfOwned` checks ownership briefly under `_gate`, then runs synchronous transport
  and mix actions holding only the operation lock. Owned native seek/play/stop waits do not
  retain `_gate`; the audio callback takes neither lock. Owned callbacks must not load
  media or rebuild the engine. `Engine/MediaRebuildReplay` accepts these gates and injected I/O.
- A `MediaLoadRequest` keeps song identity (the normalized audio/display path)
  separately from its master-load generation. An obsolete master reopen is dropped,
  but its stem snapshot is restored when the identity still matches, including a
  same-song reload queued during the rebuild or a failed master decode. A different
  song or a cleared session does not receive the old stem mix. `_mediaLineage` counts
  explicit loads and frees, not takes: a borrowed take handed back to its song during
  the rebuild changes the identity but not the lineage, so the song's stems return.
- Temporary cancellable take loads retain the previous source and requested song
  identity until publication. Cancelled or failed decodes leave rebuild intent
  unchanged. Publication rechecks cancellation and the preceding request under
  `_gate`, so a later explicit load or `FreeAudio` supersedes the take. The loaded
  and requested identities advance together only when that take is published.
- In-process effects and the instrument capture opaque state before teardown through
  `Vst/VstRebuildState`, calling only the wrapper's `SaveState()` once under `_vstGate`;
  the wrapper repeats the copy itself when the state grew after its size query.
  Stable state, including empty state, takes exactly two calls. Non-converging sizes
  or a wrapper exception are logged and return null. Native imports belong only in
  JuceSharp; the app declares no audio ABI binding.
- `VstRebuildState` restores state before attachment and merges captured state into
  queued slots only when ID and plugin path still match. Queued order, player, bypass
  and explicit state remain authoritative; new or changed plugins inherit nothing.
- MIDI frame bookkeeping belongs to one engine. Only explicit `ResetForEngine` on
  replacement clears held notes, parked events and release guards. Render anchors
  are sampled under `_midiGate`; a delayed tick is not evidence of an engine restart.
- Chart-synth uploads and rebuild replay share a publication lock through the native
  write and fingerprint publication. `IChartSynthBackend` provides an injectable
  chart-source boundary; its native adapter retains the engine's identity.
- `RebuildPublicationTests` exercises these publication paths through injected media,
  plugin-state and synth collaborators, with controlled barriers. Its mutation checks
  reject missing capture, suppressed media publication and an inverted engine check.
  `EngineReplaceStemRestoreTests` checks the real mix snapshot; `MidiPreviewSchedulerTests`
  exercises explicit replacement and delayed ticks without a device.

## MIDI chart preview (`JuceAudioPlaybackService.Midi.cs`)

- **Pitch is `SoundingPitch`'s rule, never a table of its own.** `UpdateMidiNotes`
  prices every (string, fret) through `Audio/Midi/MidiStringPitches`, which caches
  `SoundingPitch.OpenStringMidi` per arrangement (standard open pitch + the
  `TuningOffsetResolution` offset + the bass octave) and re-reads it only when the
  arrangement, its type, its `CentOffset`, its letters or an offset changed — the check allocates
  nothing, because it runs per 4 ms tick. A private guitar table played every bass
  an octave high (issue #37). An A220 reference (a 5/6-string bass on a guitar
  path, #161) is folded in as an octave of the note — a MIDI note has no cents.
  A fret below the capo plays AT the capo here
  (`SoundingPitch.MidiFor` refuses it). `MidiStringPitchesTests`.
- MIDI preview keeps each of the supported nine strings independent in
  `NoteLegatoIndex` and instrument MIDI channels. Ample's six stock channels
  retain their reverse order; extra strings use channels 6..8. Invalid indices
  are ignored by the legato index, never wrapped onto another string.
  `NoteLegatoIndexTests` and `NoteEndComputationTests`.
- Rig tuner choices use invariant ASCII identifiers; `TunerTunings.Find` also
  accepts legacy Unicode minus signs and directional marks.
  `TunerTuningsCultureTests` and `RigTunerTuningTests`.
- **WHEN a preview event sounds is `Audio/Midi/MidiPreviewScheduler`'s — a pure
  type; `UpdateMidiNotes` only finds and prices the due notes.** For a VSTi in the
  engine — IN-PROCESS or in the out-of-process SANDBOX — it is sample-accurate:
  each tick reads a consistent {render frame, raw position} pair
  (`TryReadRenderAnchor`, shared with `TryScheduleClick`), the due window reaches
  `LookaheadMs` (30 ms wall) ahead, and every NoteOn/NoteOff goes to
  `dca_engine_send_midi_at` at `anchor + (t − now) / speed × rate`
  (`JuceInstrumentMidiOutput.Send`). The gate is
  `MidiPreviewScheduler.SchedulesOnRenderClock`. The sandbox qualifies since
  bridge v3 (JuceSharp #28): the engine parks the event until its block and the
  shared mapping carries its sample offset to `juce_vst_host.exe`
  (`VstSandboxLayout.OffMidiOffsets`). Only WinMM / GS Wavetable stays immediate —
  it has no render clock. `juce_sharp.dll`, `juce_vst_host.exe` and
  `VstSandboxLayout.BridgeVersion` must agree (4); a mismatched child is killed on
  its `hello` and the sandbox falls back to in-process hosting.
- **A parked NoteOn survives every managed flush.** Pause/Stop/SeekTo and a jump
  (back > 50 ms, forward > 500 ms) send NoteOffs + All Sound Off NOW, which cannot
  reach an event parked for a future frame, so the scheduler remembers each
  scheduled NoteOn ~100 ms past its frame and answers it with a NoteOff one frame
  later; a later NoteOn on that key is never placed at or before that NoteOff. The
  window never moves back (a speed drop shrinks the lookahead), and a seek passes
  its target so a note exactly on it (a loop START) still sounds. Known limit: a
  note within the lookahead past a loop end is scheduled before the wrap's seek and
  sounds for one sample before its guard. `MidiPreviewSchedulerTests` replays every
  event in frame order through a voice model.

## VST sandbox (out-of-process host)

`JuceAudioPlaybackService.Sandbox.cs` owns a fixed array of four
`Vst/Sandbox/VstSandboxSeat` contexts (ABI v50). Each context owns one
`juce_vst_host.exe` child, its live slots, desired state, recovery policy and
state capture/replay. `IVstSandboxHost` extends the control-channel seam with
the child lifecycle and its unique shared-memory/event names.

- **Lazy lifecycle:** a seat's first sandboxed plugin starts its child; its
  last unload stops it and disarms only that seat. The sandboxed instrument
  belongs to seat 0 and keeps that child alive without a guitar-chain slot.
  An empty seat has no child and no armed bridge. Mode switch off, shutdown
  and dispose stop all children and disarm all four seats.
- **Ownership:** load, unload, bypass, editor and state operations use the
  slot owner's channel. A move awaits the old child's unload before loading
  the slot on the destination. A rejected or failed unload from a live child
  preserves its old ownership and aborts that move while unrelated edits continue.
  A source that dies during unload cannot retain the slot or fail its move. The
  source state is captured and copied to the destination's desired state before
  unload, then restored after load. A failed capture logs the fallback to its last
  saved copy. Health capture removes only state outside the desired chain and
  instrument, preserving copies awaiting a destination load or retry.
  `VstSandboxChainEdit` visits changed seats and retries missing desired slots
  unless recovery is pending or has given up, or the slot's unchanged entry has a
  remembered child rejection or missing file; transport failures stay retryable.
  Entry changes clear those marks; there is no separate guitar-slot reload API.
  It always cleans those seats up,
  and reports failures together after all seat edits. `VstSandboxChainPlan.LoadRequest` carries
  `VstChainEntry.Player`; processing order is sent separately to each child.
- **Recovery:** crash reports, restart budgets, stale-host checks, heartbeat
  reads (`TryGetRemoteBridgeStats(player, ...)`) and state replay are per seat.
  Recovering one seat leaves every other child's channel and bridge alone.
  An unchanged seat in backoff or after giving up is not ensured by another
  seat's edit. Pending attempts retain their expected host identity and an
  explicit edit invalidates obsolete attempts. Dead hosts are disposed before
  replacement.
  Empty-child teardown waits until both chain and instrument replay finish,
  so one failed chain load cannot discard a recovered instrument.
  `VstSandboxPerSeatTests` exercises these paths with injected hosts and engine.
- **Rebuild:** `RearmSandboxAfterEngineSwapLocked` re-arms every surviving
  child's bridge with `SetRemoteBridge(player, ...)` and its own object names.
  Preparation visits every surviving child behind `_vstGate`; the instrument's
  MIDI sink is rebound to the new engine on seat 0. A failed re-arm kills that
  child and enters its normal crash budget and replay path.
- **Native scheduling:** the engine posts all seats' requests, then collects
  responses against one block deadline. A slow seat misses on its own bridge;
  other seats continue processing. The shared layout stays bridge v4, and
  object names are unique per child. See JuceSharp's ABI v50 contract.

Every pipe round trip is awaited through `VstSandboxCalls.TrySendAsync`,
bounded by both the host timeout and an outer timeout covering pipe writes.
`StopAsync` kills on overrun; `StopForDispose` provides bounded synchronous
teardown. `_vstGate` remains a `SemaphoreSlim` held across awaited control
calls. No Monitor (`_gate`, `_sandboxGate`, `_midiGate`) crosses an await.
The public `IVstChainHost` members return before loading finishes and report
the result through their load events.

- **Two places stay synchronous, both bounded.** `RearmSandboxAfterEngineSwapLocked`
  runs inside `ReplaceEngineGated` under the `_engineConfigGate` Monitor, so it
  only re-arms the bridges (one native call per live seat) and QUEUES the `prepare` round trip
  via `NotifySandboxEngineReconfigured` behind the `_vstGate` it already holds
  (the bridge's own prepare generation re-prepares the child either way).
  `DisposeSandbox` uses `VstSandboxShutdown`: every child's quit is requested
  before one wait over the group, bounded at 10 s, then unfinished stops are
  killed. Mode switch off uses the same group and bound asynchronously.
  `IVstSandboxStopWait` injects the shared wait for structural tests.
- `SandboxBlockingWaitLintTests` forbids `.GetAwaiter().GetResult()`, `.Wait(`,
  `.Result` in `.Sandbox.cs`, `.VstChain.cs` and `Vst/Sandbox/`, with
  `StopForDispose` (single-child convenience) and `WaitForDispose` (the group)
  the bounded synchronous sites; `SandboxNonBlockingCallTests` pins
  that a call whose reply never arrives returns to its caller, still gives up at
  its bound, and still logs its fallback warning.

## Spillover and re-amp (ABI v37)

- **Spillover**: a delay/reverb (catalogue category `delay` or `reverb`) that a
  tone switch REMOVES rings on as a post-chain tail (native `DspChain`
  `SpillTail`, at most 4 per seat, 8 s / -90 dBFS for 200 ms, retired off the
  audio thread). OFF natively at engine start; the setting is
  `RigConfig.RigSpillover` (default ON), seeded into every seat's chain by
  `ToneSessionSet.Create`, written by `PedalboardViewModel.Spillover` (the Tones
  head's `RigSpilloverToggle`), RECORDED by `JuceDspChainService` and replayed on
  EVERY engine attach (`ReplaySpillover` beside `ReplayMetering`). Pinned by
  `SpilloverSettingTests` (push, rebuild replay, config seed, the switch) and
  natively by `JuceSharp.Tests` `SpilloverOfflineTests` + the RT gate's
  `RtSpilloverTailRingsAfterRemovalWithoutAllocating`.
- **Re-amp**: `Audio/Reamp/` — `IReampEngine` (implemented by
  `JuceAudioPlaybackService.Reamp.cs`, which forwards the take half to
  `Audio/Reamp/ReampTakes`, reached with an `as` cast like
  `IRigMidiInputSource`, so the null transport has none and the UI shows the
  panel disabled with its reason), `DiTakeRecorder` (follows the capture ring
  with the CURSORLESS `dca_engine_read_input_capture_from_for_player`, so it
  never steals from live detection's streaming cursor; gaps written as silence),
  `DiTakeLibrary` (`<per-user folder>/DI Takes`, list/rename/delete/import/decode
  to mono) and `DiTakeResampler` (Kaiser-windowed sinc, band-limited both ways).
  A take is REMEMBERED by the service and replayed onto a rebuilt engine
  (`ReampTakes.ReplayTakesTo` from `AttachPerPlayerStateToEngine`, through the same
  `PushTakeTo` a live load uses, resampled to the new rate, at its last
  position and play state); a take that cannot be replayed is dropped and the
  panel says so (`Rig_Reamp_Lost`). Pinned by `EngineRebuildStateReplayTests`,
  `DiTakeTests`, `RigReampViewModelTests`, `RigReampUiPerfSmokeTests` and
  `JuceSharp.Tests` `InputInjectionNullDeviceTests` + the RT gate's
  `RtInputInjectionReplacesTheSeatInputWithoutAllocating`.
  `DiTakeRecorder` stops accepting samples permanently when the capture sample
  rate changes; the rig's next poll saves the old-rate WAV and stops recording.
  Stop and Dispose invalidate both pending decode and engine-upload work, so a
  late take cannot restart the rig transport.

  The cancellation-aware `IPlaybackTransport.LoadAudioAsync` overload is used
  for Results listen-back. `SwapMasterSource` retains the previous source while
  decoding this temporary load and checks cancellation before publication;
  cancellation discards the decoded source without replacing the song. This
  load never resumes playback implicitly. Ordinary loads retain their existing
  detach-before-open behavior. Native opens still run outside `_gate`.

## External process insert (ABI v47)

- A GENERIC send/return per seat: the dry input goes to another process through a
  named shared-memory ring and its processed audio replaces the seat's rig (format,
  real-time contract and statuses: `JuceSharp/docs/agents/native-engine-abi.md`
  § ABI v47). Underrun = the DRY input for that block, never silence.
- Managed: `Audio/ExternalInsert/ExternalProcessInserts` (collaborator of
  `JuceAudioPlaybackService`, reached through the `IExternalInsertEngine` seam:
  what is open per seat, the open, the replay) and `JuceExternalProcessInsert`, the
  `Audio/Abstractions/IExternalProcessInsert` implementation for one seat (the
  caller names the header magic). **Not registered in DI**: nothing drives it yet;
  the session that does constructs it over the playback service.
- **Rate.** The native side does not resample. A `SampleRateMismatch` refusal makes
  the collaborator request the ring's rate IN PLACE
  (`RequestExternalInsertSampleRate`, the neural-amp request's twin: same gate,
  `allowReplaceFallback: false`, never persisted) and open again; a device that
  cannot leaves the open refused and `LastOpenResult` says why.
- **Rebuild replay.** `ExternalProcessInserts.ReplayTo` from
  `AttachPerPlayerStateToEngine`: a seat the engine reports CLOSED (a replaced
  engine) is re-opened; a reconfigured engine kept its session. A rate refusal in
  the replay defers the rate request to the thread pool (the replay runs under
  `_gate`); a block that is gone is forgotten. Pinned by
  `EngineRebuildStateReplayTests.TheExternalProcessInsert_IsReplayedByThePerPlayerAttach`
  and `ExternalProcessInsertsTests`.

## Chart synth (ABI v38, instrument models v40)

The built-in note generator that plays the chart THROUGH the guitar rig (#39:
step 0 the plumbing, step 1 + the bass step the voice; #50 the instrument models).

- **The voice is REAL RECORDINGS** — the native guitar / bass sample voice
  (`JuceSharp/docs/agents/chart-voice.md`). Its eight packs (218 MB on disk,
  LOSSLESSLY coded since #408, 594 MB of int16 decoded: the CC0
  Karoryfer sets, one EGFxSet Stratocaster pack per pickup position and the
  guitar articulation pack from Guitar-TECHS — real palm mutes, natural and pinch
  harmonics, #52 — both CC BY 4.0) are NOT in git: `build_voice_pack.py` writes them to
  `artifacts/voice-pack/` from a pinned, hashed manifest, `DotCharter.UI.csproj`
  copies them to `voices/` beside the exe, and `New-Release.ps1` builds missing
  ones — and STALE ones, whose `voice-pack.stamp` (SHA-256 of the manifest and the
  builder) no longer matches — and refuses a payload without them
  (`VoicePackPackagingTests`). Without a pack the voice falls back to a string
  model — it never goes silent, which is exactly why the packaging is pinned.
- **Only the zones the chart plays are in RAM (#417).** The engine loads a pack's
  zone table and decodes the recordings the uploaded chart can play (its strings,
  pitches, slides, bends, articulations) when the voice is built and inside
  `UploadChartNotes` BEFORE the notes are published — so an edit never plays a
  missing recording. Another song swaps in fresh packs (cross-faded) and the old
  song's recordings are freed. Measured on real charts
  (`ChartVoiceZoneRamHarnessTests`, opt-in: `DOTCHARTER_VOICE_RAM_PACKS` + the
  corpus): Strat 86.5 MB -> 16-49 MB, Gretsch 168 MB -> 67-120 MB, bass 124 MB ->
  40-50 MB. `ChartSynthStatus.PackBytes` is what is DECODED, so it grows with the
  chart.
- **Bass slap is MODELLED, never recorded here (#109, #757, #767).** The one
  recorded-slap source (a Rickenbacker 4001's, Project 16, articulation 9) is
  CC BY-NC-SA 3.0, modified ("not in a sampling library or related product"),
  so no release builds or ships it: the manifest marks the source
  `"private": true`, a plain `build_voice_pack.py build` leaves it out (the
  release's `bass.dcvp` is CC0 only and the voice MODELS the slap), and
  `New-Release.ps1` refuses a `voices/` holding a recorded slap or a
  `<pack>.LICENSE.txt`. A `.dcvp` hand-copied into `voices/` still loads (the
  native store reads whatever is there); the app has no hint, setting or
  download for it. `VoicePackPackagingTests` pins the release side. No pop
  recording exists to us; the pop stays the #52 model.
- **One request, two instruments:** the service requests the built-in voice
  (`ChartSynthVoiceSelection.Default` = Guitar) and every upload plays with
  `ChartSynthVoiceSelection.Effective` — a bass PART (its notes carry
  `JuceChartNoteFlags.Bass`, set from `ArrangementInstrument.IsBassPart`: the Bass
  path, or a 5/6-string bass charted on a guitar path at A220, #161) gets the Bass
  voice, anything else the Guitar; an explicit Placeholder is honoured. A
  guitar-path bass is ALREADY in the bass register (`MidiFor` + the −1200 ¢), so
  the flag changes the voice and the rules, never the pitch — its low B goes out
  as 23 and a 6-string's top C as 48, on string indices 4 and 5 the voice places
  itself. `ChartSynthVoiceSelectionTests`, `GuitarPathBassSynthTests`.
- **Instrument models (#50):** `GuitarInstrument` / `BassInstrument`
  (`ChartSynthInstrument`: model, pickup, coil split) — default the owner's Strat
  HSS on its bridge humbucker and a Jazz bass on both pickups. Names are parsed
  by the pure `ChartSynthInstrumentSelection` (nonsense falls back to the default;
  `ChartSynthInstrumentSelectionTests`). What a model offers is native
  (`GetChartSynthModelInfo`, cached per run — it stats the pack files).
- **Whose instrument (#66):** `DotCharter.Services/Audio/ChartSynth/ChartSynthInstrumentCoordinator`
  decides it (the transport writes the service directly only when it was built
  without one — the null transport, tests). It plays the
  active PLAYER PROFILE's choice (`ProfileChartSynthInstrumentStore`,
  `profiles/<id>/chart-synth-instrument.json`, BY NAME) unless the SONG TONE that
  is playing has an override (`ChartSynthToneOverride`, per family, null = follow
  the profile; saved under `"instruments"` in the song's `SongToneEditStore`
  file, keyed by the package tone key). It follows `ActiveProfileChanged`, and
  `RsTonePlaybackCoordinator.ActiveToneChanged` (`ActiveToneInfo.SongIdentity` /
  `PackageToneKey` — null for the project's own tones) / `DrivingStopped`. The
  transport's pickers show and edit `Effective(bass)`: the override while one is
  active ("Only for this tone" creates it, "Use my instrument" drops it), else
  the profile's. Writes go to one serial background chain (`Flush()`); until a
  write lands, a re-read (profile switch, the song's next run) serves the queued
  value, never the stale file.
  `ChartSynthInstrumentCoordinatorTests`, `ProfileChartSynthInstrumentStoreTests`.
- **`RigConfig.ChartSynth{Guitar,Bass}{Model,Pickup,CoilSplit}` are MIGRATION
  INPUT ONLY** (#50's app-wide choice): the coordinator's `Start` moves them into
  the active profile once, then empties them. **A config apply must never read
  them** — it runs on every settings apply and watchdog rebuild and would swap the
  profile's (or the tone's) instrument back (`EngineRebuildStateReplayTests
  .AConfigApply_NeverResetsTheChartSynthInstrument`). The service holds the
  effective request and replays it itself (`ChartSynthNullDeviceTests
  .ARebuild_ReplaysTheEffectiveInstrument_ToneOverrideThenProfile`).
- **Live changes are pushed OFF the caller's thread**
  (`ChartSynthEngineDriver.PushToCurrentEngine`: one serial, coalesced chain — enabling or
  changing the instrument loads a pack and decodes the chart's recordings
  natively, ~0.05–0.5 s); `WaitForChartSynthPushes`
  is the test seam. A live push does NOT re-upload the notes (that would choke and
  re-start every sounding note instead of the engine's cross-fade); the rebuild
  replay does (`includeNotes: true`). **A caller that reads the status after
  enabling must wait for that push first:** `ChartSynthStatus.IsClockPlaying` and
  `NotesStarted` are written only by blocks `ChartScheduler` RAN (synth on natively,
  with a voice), so before the push lands they keep their old values while the song
  plays on (#215: a starved thread pool let an 8 s test song play out un-synthed,
  and the stale "not playing" read like a stopped clock).
- **A CPU stall is not a seek (#215).** The song clock is sample-counted, never wall
  time: `TransportSource::readSongClockRt` reports the transport's read position
  minus the stretcher's read-ahead, and every callback, however late, pulls and
  advances exactly one block. A preloaded song has no read-ahead thread at all; a
  streamed one's `juce::BufferingAudioSource` fills an unread section with silence
  but still advances by the block. So a slow block or a starved read-ahead never
  moves the position by the block plus `kJumpToleranceSeconds` (250 ms) that
  `ChartScheduler` calls a discontinuity — on the null device or on WASAPI / ASIO,
  where a starved callback is an audible dropout, not a jump. The clock stops only
  when the transport does (pause, end of the song); a resume after a pause IS a
  re-seat, and the notes held across it are chased.
- **Off frees the samples (#61 part 2):** switching the synth off makes the engine
  release the voice and its pack once silent (`ChartSynthStatus.IsVoiceLoaded`,
  `PackBytes`); `ChartSynthNullDeviceTests` waits for it after a real rebuild.

- **One switch plays the chart (#138): MIDI Out** (`TransportViewModel.MidiEnabled`;
  the Mixer's "Chart sound on/off" checkbox binds the same property). With a VST
  instrument chosen it plays that plugin; without one it plays the built-in synth
  (`TransportViewModel.ApplyMidiOutRouting`). The Windows GS Wavetable fallback is
  gone: the per-tick MIDI preview runs only for a plugin, the effect chain, or a
  MIDI device picked in Settings ▸ Audio (`IEditorTransport.PreviewsMidiToExternalDevice`).
  **MIDI in is the synth's gain** too (`ApplyVolumes` → `ChartSynthGain`, mute and
  solo included). `UI/MidiOutRoutingTests`.
  **Its doors (#574):** the Mixer checkbox, Tools ▸ MIDI instrument ▸ Out, the
  editor toolbar's piano toggle (`ToolbarView`'s `ChartSynthToolbarToggle`), the
  `ToggleChartSynth` keybind (Shift+T, EOF's "MIDI Tones") and the palette's
  `transport.toggleChartSynth` — the last three through
  `TransportViewModel.ToggleChartSynthCommand`. **The switch is live only while
  something could sound** (`TransportViewModel.CanPlayChart`: a chart synth AND an
  engine up); otherwise the toolbar toggle, the Mixer checkbox and the command are
  disabled and the toolbar tip says why (`ChartSynthToggleTip`). With no voice pack
  installed the stand-in voice still sounds, so the switch stays live and the tip
  says so. An engine (re)build raises `IChartSynthEngine.ChartSynthAvailabilityChanged`
  (from `ReplaceEngine`'s finally, success or failure), which re-reads all of it.
  `UI/ChartSynthToolbarToggleTests`, `Perf/ChartSynthToolbarToggleUiPerfSmokeTests`.
- **The synth plays through the chart's own tones by default (#138)**, not only
  while the headphones toggle is on: `ToneRigViewModel` hands the synth's on/off to
  `ChartTonePreviewController.SetChartSynthPlaying`, which drives the rig for EITHER
  reason through ONE coordinator preview; a project with no convertible tone leaves
  the charter's rig alone, and leaving the editor gives the rig back
  (`StopOutsideTheEditor` / `ResumeInTheEditor`). **Mixer ▸ MIDI synth ▸ Synth tone
  ▸ My rig** (#164, `AppConfig.ChartSynthPlaysOwnRig`, `TransportViewModel` →
  `ChartTonePreviewController.SetChartSynthUsesOwnRig`) plays it through the
  player's own board instead: the synth then never asks for the chart's tones and
  hands back a rig it held only for itself; the toggle still previews them. One rig
  per seat, so while the toggle (or a Play Mode run) drives the song tones the synth
  shares them. `ChartTonePreviewSynthTests`. With the toolbar's **Preview in Rocksmith**
  switch (#7) the same preview also goes to the game through tonelab, synth included
  ([tonelab-bridge.md](tonelab-bridge.md) § The editor's playhead preview).
- **Seam:** `Audio/ChartSynth/IChartSynthEngine` — implemented by
  `JuceAudioPlaybackService.ChartSynth.cs`, which forwards the request and its
  engine writes to `Audio/ChartSynth/ChartSynthEngineDriver` (#838), reached with an `as` cast like
  `IReampEngine`, so `NullAudioPlaybackService` has none and the toggles read
  unavailable. Seat 0 only (the editor and a solo run).
- **The chart crosses ONCE per change, never per tick.**
  `ChartNoteEventBuilder` flattens the arrangement (pitch through
  `SoundingPitch.MidiFor` + `CentOffset`, so tuning, capo and the bass octave are
  the Core rule — and since ABI v39 the arrangement's `Capo` rides on every note
  as well, because harmonic nodes are measured from it (#51); every articulation as a `JuceChartNoteFlags` bit; chord ids;
  bend curves in semitones; a chord's STRUM DIRECTION from the beat grid,
  `StrumFor` — on the beat and the even eighth / sixteenth subdivisions down,
  between them up, palm-muted or accented down, a single note `Unknown`, #52,
  `ChartNoteEventBuilderTests.ChordStrums_FollowTheBeatGrid`).
  **Releases are its own rule, `ReleaseFor`** (owner
  rules, #40): only a note that starts inside a HAND SHAPE (chord or arpeggio)
  rings across later notes — to the shape's end, its own longer sustain winning,
  damped by the next note on its OWN string; every other note ends no later than
  the next note on ANY string (a sweep never smears), held legato into it when the
  gap from its sustain is under a QUARTER NOTE at the local tempo
  (`QuarterNoteMsAt`, from the beat map); accented and muted notes stay short.
  **The song's playing style can change those rules** (#160,
  `ChartProject.SynthPlayStyle`, saved as `dcSynthPlayStyle` in the `.rscp`): one
  `ChartSynthPlayRules` for guitar arrangements and one for bass PARTS
  (`ArrangementInstrument.IsBassPart` in `Build`, `Fingerprint` and the inspector's
  family — a guitar-path bass at A220 plays and edits the bass rules, #161), each with "hand
  shapes ring", a hold over short gaps for single notes and one for CHORD notes, and
  the note value that counts as short for each (1/2…1/32, default 1/4). The
  hammer-on / tap / link ring-in (#100) is not a taste and stays fixed. The style is
  in the fingerprint, so a change re-uploads; it is edited in Mixer ▸ MIDI synth
  (`ChartSynthPlayStyleViewModel`, an undoable `SetSynthPlayStyleAction`) and pinned
  by `ChartSynthPlayStyleTests`.
  How it ends travels as `EndDamped` / `EndShape` so the voice damps or releases. The editor's
  `AudioPlaybackCoordinator` tick runs `ChartSynthFeeder`, which fingerprints the
  chart at most every 250 ms and uploads only when it moved — the native engine
  then schedules every note against the song clock, natively, so seeks, loops,
  pause and speed need no managed help (contrast the MIDI preview above, which
  re-derives a frame per event from a render anchor every tick). Do not add a
  per-tick note feed here.
- **The song clock** is the master song source, bound by `BindSongClock`
  wherever `_source` is PUBLISHED (`SwapMasterSource` and the rebuild's
  re-open). A disposed source unbinds natively. Pinned by
  `EngineRebuildStateReplayTests.TheSongClock_IsBoundWhereverTheSongIsPublished`.
- **Rebuild:** enabled / voice / gain / the last upload / the instrument route /
  the seat route / both instrument selections are the REQUEST, held by `ChartSynthEngineDriver`, written by its one `PushTo` and replayed by
  its `ReplayTo` from `AttachPerPlayerStateToEngine`; an upload the engine
  refused is not remembered. Pinned by
  `EngineRebuildStateReplayTests.TheChartSynth_IsReplayedByThePerPlayerAttach`
  and behaviourally by `ChartSynthNullDeviceTests` (a real rebuild on the JUCE
  null device).
- **Chart sound gain (#753).** The editor fader reaches 200%. The chart synth
  and routed VSTi use the fader as input gain through unity. Above unity, the
  generator enters the rig at unity and the seat's monitor ramp boosts the
  processed chart and live guitar together. The final mix still goes through
  the master limiter. Rebuild replays the full chart gain through
  `ChartSynthEngineDriver.ReplayTo` and the instrument gain through
  `ReapplyMasterBusStateLocked`. The callback derives the boost from those
  requests. MIDI Out off caps a loaded plugin at unity.
- **Isolation:** the generator enters the rig after the capture ring, so live
  detection, scoring and the tuner never hear it — `ChartSynthNullDeviceTests`
  drains the ring while notes play and asserts it is exactly silent.
- **Instrument route** (`RouteInstrumentToRig`): the in-process VSTi into the
  rig instead of after the master. Unavailable while VSTs run in the
  sandbox (`CanRouteInstrumentToRig` is `!_outOfProcessVst`), and the toggles
  print why. Saved since #751 (`AppConfig.InstrumentThroughRig`).
- **Seat route (#751, ABI v51)** (`IChartSynthEngine.ChartSeatRoute`, Mixer
  "Route instrument"): which live instrument seats' rigs the chart sound enters —
  Guitar only (seat 0, the default and the pre-#751 path), Bass only (seat 1, the
  second jack of a two-instrument input), Guitar and bass. The chart source's
  mask and, while routed to the rig, the instrument's mask are written by
  `ChartSynthEngineDriver.PushTo` through the pure `ChartSoundRouting.SeatMask`; with
  one live seat every choice is seat 0 (`CanRouteChartToSeats` false, the Mixer shows
  it disabled with `Mixer_ChartSound_SeatRoute_Unavailable`). Under Song tones the
  chart SOURCE stays on seat 0 whatever the route (`ChartSoundRouting.ChartSourceSeatMask`,
  `IChartSynthEngine.ChartSynthPlaysSongTones`): the song tone lives on seat 0's rig only,
  so routed to seat 1 it would play through the bass player's own board. Two seats
  each take 1/2 of a generator (native `shareRt`), so every route plays at one level. A live-seat change
  (`PushRoutingToEngine` → `NotifyChartSeatsIfChanged`) re-pushes it and raises
  `ChartSynthAvailabilityChanged`. Saved in `AppConfig.ChartSoundSeatRoute`.
- **Sound source (#751)** (`TransportViewModel.SoundRouting`, `Inspector/ChartSoundRoutingViewModel`: the D Mixer's
  four values, `Views/Editor/Inspector/ChartSoundRoutingView`'s segments) is NOT engine
  state: Song tones / Own rig = the built-in voice with `ChartSynthPlaysOwnRig`
  off / on; My instrument / Instrument through rig = the loaded VST instrument with
  `RouteInstrumentToRig` off / on. "Use my instrument" is
  `AppConfig.ChartSoundUsesInstrument` (default on = the old "a loaded plugin
  plays"); without a loaded plugin the instrument values are refused and read
  unavailable (`Mixer_ChartSound_MyInstrument_Unavailable`). `ChartSoundRoutingTests`,
  `UI/ChartSoundRoutingTransportTests`.
- **UI:** the editor's Mixer ▸ MIDI synth toggle (`ChartSynthToggle`; the instrument route is the Routing
  card's Sound source value "Instrument + rig") and Play Mode's Amp/FX
  instrument card (`AmpFx…` twins), bound to `TransportViewModel.ChartSynth.cs` (the switch and the route),
  and between them ONE shared view, `Views/Editor/Inspector/ChartSynthInstrumentView`
  (guitar and bass model + pickup pickers, the coil split, a line saying whether
  the choice is real recordings or simulated, the song-tone override) over its own
  view model, `ViewModels/Editor/Inspector/ChartSynthInstrumentViewModel`, which
  the transport owns as `ChartSynthInstrument`. `ChartSynthTransportTests` pins
  that they drive the service and save the choice;
  `Perf/ChartSynthInstrumentUiPerfSmokeTests` lays the view out alone and in the
  Amp/FX panel.


## Audio architecture (JUCE): root-guide rules (moved verbatim from the root AGENTS.md)

Three layers, bottom up:

1. `JuceSharp/src/JuceSharp.Native` — C++ JUCE shared lib exporting a versioned
   C ABI (`include/dotcharter_audio.h`, `DCA_ABI_VERSION`). Owns
   `AudioDeviceManager`, mixer, transports, VST3 chain.
2. `JuceSharp/src/JuceSharp` — thin P/Invoke wrapper. The DLL is deployed to
   `runtimes/win-x64/native/juce_sharp.dll` by CMake POST_BUILD.
3. `DotCharter.Tone/Audio/JuceAudioPlaybackService.cs` (namespace
   `DotCharter.Tone.Audio`) implements `IAudioPlaybackService` on the wrapper.

The toolchain that actually exists on the owner's machine (VS 2026 Build Tools
with its bundled CMake, neither on PATH; CLion is not installed) is in `JuceSharp/AGENTS.md` — the
generic `cmake -G "Visual Studio 17 2022"` line does not work there.

- All real-time audio lives in the native engine. **Never add managed audio
  pumps on the hot path.** The wrapper stays marshalling + lifetime only.
- **The DLL is a build artifact, not tracked.** An ABI bump means rebuilding it
  before any managed code runs, or engine open fails with a version mismatch.
- **The native pair imports only Windows system DLLs (#176).** `juce_sharp.dll`
  and `juce_vst_host.exe` link the MSVC runtime statically (JuceSharp's
  `JUCESHARP_STATIC_CRT`); v1.0.0 linked it dynamically and failed to load on a
  Windows without the VC++ redistributable. `Services/Update/NativeImportAudit`
  enforces it two ways: `Services/Update/NativeImportAuditTests` in the default
  gate (over `JuceSharp/src/JuceSharp/runtimes/win-x64/native`), and
  `tools/release/CheckPayloadBinaries.cs` over every staged payload and extracted
  zip. There is no exception: ONNX Runtime (a NuGet binary on the dynamic
  runtime) is satisfied by the four app-local VC++ DLLs the release copies beside it from
  the pinned redistributable (`tools/release/vcredist.json`, `VcRedist.psm1`).
- **Contributors get it prebuilt (#832).** When `juce_sharp.dll` is MISSING, the
  build (`tools/setup/NativeRuntime.targets`, hooked by `Directory.Solution.targets`
  and `Directory.Build.targets`) runs `tools/setup/Get-NativeRuntime.ps1`: it
  downloads the pair for the gitlink from JuceSharp's `native-prebuilt` release
  and installs it only if its SHA-256s match `tools/setup/native-runtime.json` and
  `dca_abi_version()` matches the header. The manifest may name only
  `SomeeName/JuceSharp` or `SomeeName/JuceSharp`, and the ABI probe loads the
  DLL only after both hashes matched, in a child PowerShell without any GitHub
  token. A present DLL is never touched; the
  build in CI never fetches unless `DotCharterFetchNative=true` (CI's `native` job
  calls the script as a step instead, with `SUBMODULES_TOKEN` as `GH_TOKEN` on the
  private repo only, and compiles C++ only if that fails or the native suite
  is asked for); `DotCharterSkipNativeFetch=true`
  turns it off. **A gitlink bump publishes its pair**
  (`tools/release/Publish-NativeRuntime.ps1 -Upload`) and commits the pin with
  it — `Infrastructure/NativeRuntimeFetchTests` fails while the gitlink is unpinned.
  The publish refuses a pair without a BUILD RECORD that binds both files' hashes to
  the gitlink's `src/JuceSharp.Native` tree: use `tools/setup/Build-NativeRuntime.ps1`
  with committed native sources. It clean-builds and deploys both targets, then records their
  hashes in `artifacts/native-runtime/native-build.provenance.json`; a failed build
  invalidates the previous record (`Infrastructure/NativeBuildProvenanceTests`).
  That tree is every tracked input (the one CMake file and the dependency pins live in
  it; the tests fail a CMake file or an include outside it). The untracked ones are shut
  out: the configure runs with `--fresh` (a reused build folder's cached pins and
  `FETCHCONTENT_SOURCE_DIR_*` are dropped), and `CFLAGS`/`CXXFLAGS`/`LDFLAGS`/
  `CMAKE_TOOLCHAIN_FILE` in the environment refuse the build.
  Public export with `-NativeDir` checks the same record against the exported native
  source tree; `-ProvenancePath` selects a record from a build of that exported tree.
- JuceSharp changes: commit in the submodule first, then bump the pointer here.
  **Push the submodule before the parent** or the gitlink dangles.
- `PositionChanged` fires at up to 250 Hz on a timer thread. UI-visible state
  goes through a coalescing `Dispatcher.UIThread.Post` (see `AudioPlaybackCoordinator`).
- **Any master-bus or per-seat state must be replayed on engine REBUILD** (device
  change, NAM 48 kHz request, watchdog recovery), or it is dropped silently.
- **Device watchdog (unplug/replug).** `JuceAudioPlaybackService.DeviceWatchdog.cs`
  probes every 3 s; `AudioDeviceProbeBuilder` builds each probe (liveness, presence,
  device-list changes, the thinned healthy-tick scan). An unplugged ASIO interface stays listed (registry) and its
  driver's reset request makes JUCE re-open and restart the absent device: the
  sample rate stays published but the render clock reads "no block yet" forever.
  `AudioCallbackStallDetector` therefore counts an unreadable clock on an OPEN
  device as frozen (two ticks = lost, `DeviceLost`, status-bar error). A recovery
  rebuild only counts once the new engine's clock moves
  (`WaitForCallbackProgress`, 2 s budget); after a failed attempt
  `AudioDeviceRecoveryStateMachine` retries on a topology change, or every 30
  probes while the engine is dead (no audio to glitch). `DeviceReconnected`
  shows "audio device recovered".
- **Fast unplug handling (#1053).** The stall heartbeat above is only the
  fallback. At the moment of an unplug the engine publishes a sample rate of 0
  for a short while (JUCE stopped the device) before the driver's reset re-opens
  it; the 3 s tick misses that, the health monitor's 20 ms poll does not.
  `AudioDeviceClosedDetector` turns "closed, and no callback for 750 ms" into a
  report (a driver reset that starts calling back again is not one), and
  `OnEngineReportedDeviceClosed` runs a watchdog check right away, so the loss
  is declared within about a second. The check runs whether or not audio
  logging is on, and is ignored during a settings apply or rebuild.
  `DeclareDeviceLost` stops the managed transport clock (playhead and position
  timer) BEFORE it raises `DeviceLost` and pauses the native sources: pausing a
  source whose device no longer calls back waits up to a second per source in
  JUCE's transport stop, and until then the playhead lagged and jittered.
  While the device is lost the watchdog checks once a second
  (`WatchdogIntervalFor`); rebuild attempts stay rare (15 s back-off after a
  failed one, 30 s blind retry), but a topology change during the back-off (the
  interface coming back) starts the next attempt at once. The recovery rebuild
  reuses the channel counts the last healthy apply read
  (`DeviceChannelCountMemory`) instead of two full live rescans of every driver
  through the dead engine, and logs how long the rebuild took. In the owner's
  log the dead engine's own destroy and the new engine's create took about
  225 ms together, so the time before them is the part to watch.
- **The live seat count follows the input configuration, and exactly one place
  writes it**: `LiveInputSeatLayout.FromInputConfig` (recorded from
  `ApplyAudioConfigurationCore`). A temporary override (the latency wizard's
  channel probe) layers on top and hands it back. "Two guitars" is an explicit
  setting, never inferred from a second open channel (that is a stereo pickup).
- **A capability the app cannot honour must never look live.** The UI ships it
