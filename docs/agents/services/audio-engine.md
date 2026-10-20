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
| `IPlaybackTransport` (20) | `CurrentTimeMs`, `RawPlayheadMs`, `IsPlaying`, `SeekGeneration` { get; }; `PlaybackSpeed` { get; set; }; `event EventHandler<double> PositionChanged`; `Play()`, `Pause()`, `Stop()`, `SeekTo(double)`; `LoadAudioAsync(string)`, `LoadAudioFromMemoryAsync(ReadOnlyMemory<byte>, string, string?)`, `FreeAudio()`, `GetTotalAudioLengthMs()`, `CurrentAudioPath`; `PlayMetronomeTick(bool)`, `PlayClap()`, `TryScheduleClick(int, double)`, `ClearScheduledClicks()`, `TryScheduleCalibrationClap(out long)` | **now:** `ChartRenderer`, `Preview3DRenderDriver`. **will:** every Play Mode screen that plays a song (Countdown, Playing HUD, Paused, Results listen-back), `AudioPlaybackCoordinator` | `PositionChanged` on a thread-pool timer at up to 250 Hz — coalesce to ≤ 60 Hz before UI state. `Play`/`Pause`/`Stop`/`SeekTo` reach `_gate` (short holds). Schedule against `RawPlayheadMs`, never `CurrentTimeMs` |
| `IAudioDeviceControl` (19) | `ActiveSampleRate`, `ActualBufferFrames`, `EstimatedOutputLatencyMs`, `MeasuredOutputLatencyMs`, `IsDeviceLost` { get; }; `AudioDelayMs`, `MidiOffsetMs` { get; set; }; `event DeviceBufferFramesChanged` (`int`), `DeviceLost`, `DeviceReconnected` (`AudioDeviceReconnectedEventArgs`); `ApplyAudioConfiguration(AppConfig)`, `EnsureAsioDevicesProbed(AppConfig)`, `EnsureWasapiDevicesProbed(AppConfig)`, `TryOpenAsioControlPanel(IntPtr, out string?)`, `GetWasapiOutputDevices()`, `GetAsioDevices()`, `GetWasapiInputDevices()`, `GetAsioInputDevices()`, `GetInputDeviceChannelCount(string, string)` | **now:** `LiveInputChannelViewModel`, the Settings ▸ Audio tab. **will:** the status bar, Diagnostics, the latency wizard | the three events may arrive on any thread (watchdog timer, engine rebuild). `ApplyAudioConfiguration`, the probes and the ASIO panel may rebuild or stop the engine — slow, never on a hot path |
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
  `VstSandboxLayout.BridgeVersion` must agree (3); a mismatched child is killed on
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

`JuceAudioPlaybackService.Sandbox.cs` drives `juce_vst_host.exe` over a named
pipe (`Vst/Sandbox/VstSandboxHost`); audio crosses the native shared-memory
bridge. **Every pipe round trip is AWAITED, never blocked on.** It goes through
`Vst/Sandbox/VstSandboxCalls`: `TrySendAsync` (returns the reply or `null`
after logging the same failure the old `catch` did; bounded by the host's reply
timeout AND an outer `WaitAsync` of timeout + 2 s, which also covers the pipe
write a wedged child can stall), `StopAsync` (bounded, kills on overrun) and
`StopForDispose`. The `…Locked`/`…Core` methods are `async Task` and run on the
`Task.Run` workers the public `IVstChainHost` members already spawn, holding
`_vstGate` — a `SemaphoreSlim`, so holding it across an await is the same
serialization as before. No Monitor (`_gate`, `_sandboxGate`, `_midiGate`) is
ever held across an await. The public API did not change shape: those members
were already fire-and-forget, with the result on the `…LoadChanged` events.

- **Two places stay synchronous, both bounded.** `RearmSandboxAfterEngineSwapLocked`
  runs inside `ReplaceEngineGated` under the `_engineConfigGate` Monitor, so it
  only re-arms the bridge (one native call) and QUEUES the `prepare` round trip
  via `NotifySandboxEngineReconfigured` behind the `_vstGate` it already holds
  (the bridge's own prepare generation re-prepares the child either way).
  `DisposeSandbox` runs from the synchronous `Dispose` and waits in
  `StopForDispose`: 3 s quit + 5 s grace inside the host, hard kill at 10 s.
- `SandboxBlockingWaitLintTests` forbids `.GetAwaiter().GetResult()`, `.Wait(`,
  `.Result` in `.Sandbox.cs`, `.VstChain.cs` and `Vst/Sandbox/`, with
  `StopForDispose` its one allow-listed site; `SandboxNonBlockingCallTests` pins
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
  replay does (`includeNotes: true`).
- **Off frees the samples (#61 part 2):** switching the synth off makes the engine
  release the voice and its pack once silent (`ChartSynthStatus.IsVoiceLoaded`,
  `PackBytes`); `ChartSynthNullDeviceTests` waits for it after a real rebuild.

- **One switch plays the chart (#138): MIDI Out** (`TransportViewModel.MidiEnabled`;
  the Mixer's "Play the chart" checkbox binds the same property). With a VST
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
  both instrument selections are the REQUEST, held by `ChartSynthEngineDriver`, written by its one `PushTo` and replayed by
  its `ReplayTo` from `AttachPerPlayerStateToEngine`; an upload the engine
  refused is not remembered. Pinned by
  `EngineRebuildStateReplayTests.TheChartSynth_IsReplayedByThePerPlayerAttach`
  and behaviourally by `ChartSynthNullDeviceTests` (a real rebuild on the JUCE
  null device).
- **Isolation:** the generator enters the rig after the capture ring, so live
  detection, scoring and the tuner never hear it — `ChartSynthNullDeviceTests`
  drains the ring while notes play and asserts it is exactly silent.
- **Instrument route** (`RouteInstrumentToRig`): the in-process VSTi into seat
  0's rig instead of after the master. Unavailable while VSTs run in the
  sandbox (`CanRouteInstrumentToRig` is `!_outOfProcessVst`), and the toggles
  print why.
- **UI:** two `CheckBox`es in the editor's Mixer ▸ MIDI synth card
  (`ChartSynthToggle`, `InstrumentThroughRigToggle`) and Play Mode's Amp/FX
  instrument card (`AmpFx…` twins), bound to `TransportViewModel.ChartSynth.cs` (the switch and the route),
  and between them ONE shared view, `Views/Editor/Inspector/ChartSynthInstrumentView`
  (guitar and bass model + pickup pickers, the coil split, a line saying whether
  the choice is real recordings or simulated, the song-tone override) over its own
  view model, `ViewModels/Editor/Inspector/ChartSynthInstrumentViewModel`, which
  the transport owns as `ChartSynthInstrument`. `ChartSynthTransportTests` pins
  that they drive the service and save the choice;
  `Perf/ChartSynthInstrumentUiPerfSmokeTests` lays the view out alone and in the
  Amp/FX panel.
