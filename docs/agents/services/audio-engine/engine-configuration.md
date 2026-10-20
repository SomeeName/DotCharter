# `Audio/` — the playback service, its gates and the engine config (deep reference) — engine configuration

## Engine configuration

- New audio features go native-first (JuceSharp ABI) with the service as a thin coordinator. No managed per-sample processing on the playback path.
- Engine config changes should try `Reconfigure`/`SetDeviceType` (in-place, preserves VSTs/transports) before falling back to a full engine replace.

- **Every engine setter the service calls has exactly two homes, and a lint
  enumerates them (0.44).** A fresh engine starts on the native defaults, so
  anything not in `JuceEngineConfig` is gone after a rebuild unless it is
  replayed: MASTER-BUS state in
  `JuceAudioPlaybackService.ReapplyMasterBusStateLocked`, PER-SEAT state in
  `AttachPerPlayerStateToEngine`. `EngineRebuildStateReplayTests
  .EveryEngineSetterTheServiceCalls_IsReplayedByTheRebuild_OrIsExemptWithAReason`
  fails on any that is in neither, unless it is on the exemption list with a
  sentence naming what re-derives it. That list is the thing to extend, never
  the exemption. The per-seat routing and the EQ each shipped missing from it.
  - **The candidate list is DERIVED from `JuceAudioEngine` by reflection, not
    from a regex over one spelling of the receiver** (0.44.x). The earlier scan
    was `_?engine.Set…(`, and two whole classes of engine state could never have
    failed it: a settable PROPERTY — `engine.StemGroupTempo = v` is a real
    `dca_engine_set_stem_group_tempo` write and the service makes two of them —
    and a setter reached through any local not called `engine` (`createdEngine`,
    `reloadEngine` and `e` all exist in these files). The lint now takes every
    `Set*` method and every settable property off the wrapper TYPE and looks for
    a call *or an assignment* on any receiver. `SetDeviceType` is the one member
    excluded, because it BUILDS an engine rather than layering state on one.
  - Exemptions today: `SetRemoteBridge` (the sandbox re-arm), `SetStretchQuality`
    (a function of "are stems loaded"), and `StemGroupTempo` /
    `StemGroupPitchSemitones` — the shared stem-group stretcher only exists once
    a stem has been grouped onto it, so `LoadStemsCore` writes both from
    `_playbackSpeed`/`_pitchShift` immediately after grouping and the rebuild
    re-issues the whole set through `RestoreStemsAfterEngineRebuild`. Replaying
    them in `ReapplyMasterBusStateLocked` would write a bus a fresh engine has
    not got. **A companion test fails on a STALE exemption** — one naming a
    member the service no longer writes, which would sit there excusing the
    write on the day something re-introduces it.
  - **The per-seat monitor trim was the third such miss and is now recorded in
    `PlayerInputRoutingState` like the count and the channel.** It was DRIVEN
    by couch co-op's per-seat slider (0.58, removed 2026-09-19); the layout
    still carries the trim, so the replay keeps it.
- **The master-bus LIMITER is part of the replayed master-bus state, and the app
  has to OPT IN to it at all (2026-09-11, JuceSharp ABI v35).** The look-ahead
  soft limiter over the final device buffer arrived default-ON in ABI v34 and
  became default-OFF in v35 — it sits on the LIVE MONITOR path, so default-ON
  cost every player ~1 ms of latency on their own playing and moved every
  consumer's click alignment by 48 samples at 48 kHz, for a feature they had not
  asked for. Nothing else bounds the sum of the song bus, every seat's monitor
  mix, the click bus and both instrument legs, and the streaming mirror carries
  FLOAT, so samples past ±1.0 survive into OBS / Discord and wrap in their
  encoder. Two halves, both required: `ApplyAudioConfigurationCore` derives
  `Audio/Engine/MasterLimiterSetting.FromConfig` (`AppConfig.EnableMasterLimiter`
  — on by default, config-file-only, no Settings UI yet — plus
  `MasterLimiterCeilingDb`, clamped to the native [−24, 0] dBFS; a NON-FINITE
  ceiling is REJECTED native-side rather than clamped, so it falls back to the
  shipped −0.3 dB here) and pushes it through `ApplyMasterLimiter`, and
  `ReapplyMasterBusStateLocked` replays it on every rebuild. **Anything that
  caches `OutputLatencySamples` must re-read after a toggle** — the delay line is
  only in the path while the stage is on — which is why `ApplyMasterLimiter`
  drops `_latencyCache`. `MasterLimiterOptInTests` pins the derivation, the
  config-path verb and the cache drop; `EngineRebuildStateReplayTests` catches
  the replay half generically.

- **The master-bus replay may not abort the rebuild, and a failed bring-up must
  DESTROY the engine it created.** Both were real holes in
  `ReplaceEngineGated`'s one big try: (a) the setters were bare, so one throw —
  an export a degraded DLL lacks, an `ObjectDisposedException` from a raced
  teardown — was logged as "Failed to start JUCE engine" and cost the user all
  audio instead of one setting; each group is individually guarded now.
  (b) the catch set `_engine = null` and walked away from a live native engine.
  Dropping the managed reference does not close the device — the engine stays
  alive holding it, and an ASIO device is typically exclusive, so every later
  rebuild INCLUDING the watchdog's recovery then failed to reopen it for the
  rest of the process. The catch detaches the DSP rigs, disposes the engine and
  clears `_activeEngineConfig` (which otherwise names a device we do not hold,
  in the recovery log). Pinned by `EngineRebuildStateReplayTests`.

- **Every FALLBACK rung keeps the channel COUNTS, and dropping them is not a
  shrug (2026-09-04).** `BuildEngineAttempts` (pure, pinned by
  `Services/Audio/EngineDeviceWiringTests`) is the ladder
  `CreateEngineWithFallback` walks: the caller's own config, then default-ASIO,
  then WASAPI, then Default. Every rung below the first clears BOTH device ids,
  which sends the native side down
  `initialiseWithDefaultDevices(inputChannels, outputChannels)` — and that reads
  `input_channels <= 0` as **open no capture channels at all**. The WASAPI and
  Default rungs left `OutputChannels`/`InputChannels` at 0, so an ASIO interface
  that was offline at launch cost the user their whole LIVE INPUT — no tuner, no
  input meter, no judged run — with nothing in the log but "started on fallback
  driver". The channel MASKS still do NOT travel: a mask names physical channels
  of the device the user picked, and a fallback device may not have them.

- **`DeviceBufferFramesChanged` is raised by `ReportDeviceBufferFrames`
  (`.Devices.cs`) and was raised by NOTHING until 2026-09-04** — its declaration
  carried a `#pragma warning disable CS0067`, i.e. the compiler had already said
  so, while Settings ▸ Audio's buffer dropdown and the status-bar readout both
  subscribed. Both therefore showed the size the app ASKED for while the device
  ran at another one, which is exactly what `AudioSettingsViewModel`'s own
  comment says must not happen (the Focusrite-ASIO case, where the buffer is
  pinned from the driver's control panel). Three sites publish it — the full
  bring-up, the in-place `Reconfigure`, and the return from the modal ASIO
  control panel — through `Engine/DeviceBufferReporter`, which drops a repeat and
  never publishes a non-positive size (the subscribers snap it through
  `NormalizeBufferSize`, which answers the config DEFAULT for 0, so one transient
  zero would rewrite the user's row). The pragma is gone deliberately: with
  warnings-as-errors, deleting the invoke is now a build failure.

- **The streaming mirror is given a BARE device name, never the dropdown's
  label** (`ResolveMirrorDeviceName`). The mirror list is filled from
  `GetWasapiOutputDevices()`, i.e. from `LabelWasapiModes`, so a device JUCE
  registers under several WASAPI types reaches the config as
  `"Speakers (Realtek) (Exclusive)"`. `MirrorOutput::open` assigns that straight
  to `AudioDeviceSetup.outputDeviceName`, so the open failed with "No such
  device" and the mirror silently stayed off for exactly those users. The suffix
  could never have meant anything to it either — the mirror always forces the
  shared "Windows Audio" type, because ASIO would deadlock against the primary
  engine on the same hardware.

- **`AppConfig.EnableHighProcessPriority` is applied by
  `ApplyAudioConfigurationCore` and by nothing else — and it was applied by
  NOTHING at all until 2026-09-04.** The switch shipped ON by default, was
  loaded and saved by `AudioSettingsViewModel`, forced a full engine restart when
  toggled, and no line in the app read it: a row promising "helps avoid glitches
  / dropouts when running a VST effect" that did nothing. `Engine/ProcessPriorityPolicy`
  holds the decision (raised = `AboveNormal`, resting = `Normal`; JUCE already
  runs the callback on a time-critical THREAD, and `High` outranks the desktop's
  own responsiveness machinery — which is the "makes the rest of your system
  laggy" outcome the row offers as the reason to turn it OFF). It writes only
  when the current class is one this switch itself produces, so a process a
  launcher parked at `RealTime`/`Idle` is left alone.

- **48 kHz-for-neural-amp policy (PLAN-0.16 4.5) — DOWNGRADED TO AN
  OPTIMIZATION in 0.36.** NAM/AIDA-X captures are 48 kHz-native. The native
  `namamp` module used to dry-BYPASS at any other engine rate; since 0.36 it
  RESAMPLES around the capture (measured flat within 0.005 dB 20 Hz-16 kHz,
  +1.42 ms round trip at 44.1 kHz — see
  [dsp-tone-rig.md](../dsp-tone-rig.md)),
  so a device that refuses 48 kHz still HEARS the amp and the request below only
  buys back that latency. Everything else about the policy is unchanged and
  still load-bearing. `IDspChainService` raises
  `NodesChanged` after any add/remove (fired off-lock); `JuceAudioPlaybackService`
  subscribes and, when a `namamp` node appears, does ONE best-effort in-place
  48 kHz request per engine (`CloneWithSampleRate` → `ApplyEngineConfig`, a
  rate-only delta → `engine.Reconfigure`; off the UI thread via `Task.Run`,
  serialized by `_neuralRateGate`, guard `_neuralRateAttempted` reset on every
  engine (re)create). It NEVER tears the engine down and NEVER writes
  `AppConfig.AudioSampleRate` (transient, re-requested when the tone re-adds
  namamp). A device that refuses 48 kHz keeps running; since 0.36 the card shows
  the INFORMATIONAL `Nam_ResampledBadge` rather than a warning, and
  `rateMismatch` only fires when the converters could not be built at all.
  **"Never tears the engine down" is only true because of
  `ApplyEngineConfig(cfg, allowReplaceFallback: false)` — do not drop that
  argument.** With the default fallback, a device pinned to 44.1 kHz makes
  `Reconfigure` THROW, and the catch answers by destroying and rebuilding the
  whole engine — killing playback, dropping the stems and reloading every VST,
  because the user added a NAM pedal. And since `ReplaceEngineGated` clears
  `_neuralRateAttempted`, the still-44.1 kHz rebuild did it all again on the
  next pedal add/remove. That was a real shipped bug, fixed in v0.17.1 — and
  since the 2026-07-25 audit it is **enforced mechanically** by
  `EngineConfigPolicyTests`, which also pins `CloneWithSampleRate` property-by-
  property (a field it forgets reads as a delta to `CanReconfigureInPlace`, and
  the rate request stops being an in-place one) and every
  `allowReplaceFallback` exit of `ApplyEngineConfig`.

- **Every engine-CONFIG change goes through `_engineConfigGate`** —
  `ApplyEngineConfig`'s in-place `Reconfigure` and the full `ReplaceEngineCore`
  cycle both take it. The settings-dialog apply (UI thread), the watchdog's
  recovery rebuild (pool thread) and the NAM 48 kHz request (pool thread) are
  three independent producers of engine-config changes and nothing serialized
  them: two could be inside `engine.Reconfigure` at once and both write
  `_activeEngineConfig`, last writer wins — so `_activeEngineConfig` could stop
  describing the open device, which then feeds `CanReconfigureInPlace` AND the
  watchdog's `EngineOnDesiredDevice` probe (a spurious "not on the desired
  device" triggers a recovery rebuild). It is a `Monitor`, not a semaphore, on
  purpose: `ApplyEngineConfig` falls through into `ReplaceEngineCore` on the
  same thread and that nested acquire must be free. It is NOT `_gate` — that
  must never be held across the reconfigure. Both acquisitions are bounded at
  60 s and log-and-proceed on timeout, like the `_vstGate` wait, so a wedged
  device change cannot brick the next one.

---

# Moved here from the root `AGENTS.md` at v0.43.2

The root guide is loaded for EVERY agent, so this detail lives here and the
root keeps a pointer. Some of it restates the sections above from the engine
side; where they disagree, the ABI header wins.

