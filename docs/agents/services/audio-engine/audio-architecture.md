# `Audio/` — the playback service, its gates and the engine config (deep reference) — audio architecture (JUCE): layers, ABI, per-player rigs

## Audio architecture (JUCE)

1. `JuceSharp/src/JuceSharp.Native` — C++ JUCE shared lib exporting a versioned C ABI (`include/dotcharter_audio.h`, `DCA_ABI_VERSION`). Owns `AudioDeviceManager`, mixer, transports, VST3 chain. CMake build; JUCE via FetchContent. ASIO compiles against the ASIO headers JUCE bundles; no Steinberg SDK is vendored or needed.
2. `JuceSharp/src/JuceSharp` — thin pure-C# P/Invoke wrapper (`JuceAudioEngine`, `JuceAudioSource`, `JuceAudioPlugin`). DLL deployed to `runtimes/win-x64/native/juce_sharp.dll` by CMake POST_BUILD.
3. `DotCharter.Tone/Audio/JuceAudioPlaybackService.cs` (namespace `DotCharter.Tone.Audio`; the audio host moved to DotCharter.Tone for the standalone rig) implements `IAudioPlaybackService` (DI-registered) on top of the wrapper.

Native rebuild:

```powershell
cd JuceSharp/src/JuceSharp.Native
cmake -S . -B build -G "Visual Studio 17 2022" -A x64
cmake --build build --config Release
```

**Per-player live-input rigs (ABI v28).** The engine hosts `DCA_MAX_PLAYERS`
INDEPENDENT rigs — each with its own `dcrig::DspChain`, VST3 input chain, mono
capture ring and hardware input CHANNEL — so each live seat (the "two guitars"
input setting) has its own tone. It is channel routing inside ONE open device, never a second device
open (ASIO is typically exclusive). `dca_engine_set_player_count` defaults to
**1**, and at 1 the audio callback runs the pre-v28 path byte-for-byte. DSP node
ids are engine-global (`p * DCA_PLAYER_NODE_ID_STRIDE + local`), which is why
only the CHAIN-scoped exports gained `*_for_player` variants and seat 0's ids
are unchanged. Managed entry point: `Audio/Dsp/IPlayerDspChains`;
`IDspChainService` is still `For(0)`.

**Per-seat JUDGING went with couch co-op (removed 2026-09-19; restore from tag
`pre-cut-2026-09`).** Only seat 0 is judged, by the ordinary solo Live Play
path. `IPlayerInputRouting.ReadPlayerInputCaptureStream` and
`TryGetPlayerInputCaptureSync` (the per-seat streaming read and clock pair)
stay on the interface over their ABI v28 exports, but no product code calls
them.

**The DROP TUNE is per seat (ABI v31) and it DID need a native rebuild.** The
engine held ONE `dcdsp::DropTune` applied to the raw input ABOVE the per-player
split, so on a couch with two guitars in different tunings the pedal was
unusable: dropping one dropped BOTH. `IPlayerInputRouting.SetPlayerInputPitchShift{,Mode}`
are the managed writers; seat 0 IS `IAudioPlaybackService.InputPitchShiftSemitones`,
which now routes through them so the property and the replayed state cannot
drift. **It is the FOURTH piece of per-seat state in
`PlayerInputRoutingState`/`AttachPerPlayerStateToEngine`** — the count, the
channels and the trims each shipped MISSING from that replay, and a dropped drop
tune is the loudest of the four (the guitar comes back a whole step out of tune
with the song, mid-run). Only SEAT 0 is persisted: `AppConfig.InputPitchShiftSemis`
is one integer and the higher seats are session state, not the install's.
Guards: `Services/Audio/PerPlayerDropTuneTests`,
`Services/Audio/DropTuneNativeMirrorTests` (reads the submodule's own header and
`dotcharter_audio.cpp`), `UI/DropTuneSeatTests`.

**…and `InputPitchShiftMode`'s live push was BROKEN until then.** Its setter was
`value == 1 ? 1 : 0` — the two-engine scale from before v25 — so choosing Smooth
(2) stored and sent Tight (0). The config write was correct, so the setting
appeared to work *after an app restart*: the one mode you would restart the app
to audition was the one you could not hear. Exactly the shape
`DropTunerEngineMap` was created to end, surviving in the one place that type
did not reach.

**The TUNER is per seat too (0.59), and it needed no native change either.**
`IPlayerInputRouting` gained `ReadPlayerInputCaptureWindow(player, …)` over the
existing `dca_engine_read_input_capture_for_player`, plus `LiveInputPlayerCount`
(the recorded count, so a surface can tell "quiet" from "this seat is not being
walked by the callback"). `RigTunerViewModel.Player` follows the rig's own seat
picker. **The WINDOWED read, never the streaming one** — the stream cursor
belongs to a streaming reader, and a needle draining it would steal its
samples. Guarded by `UI/RigTunerSeatTests`.

**Two v28 rules that each shipped broken and each have a lint now:**
1. **`VstChainEntry.Player` travels with EVERY copy of a slot** — a copy that
   drops it re-seats the slot onto player 1 the moment anything re-applies the
   chain, and a persisted `"Player": 1` is destroyed on the first launch that
   reads it. Linted by `UI/VstChainSeatTests`, including the initializers that
   MEAN seat 0: they say `Player = 0` out loud, because "0 because I decided so"
   and "0 because I forgot" are indistinguishable afterwards.
   `JuceAudioPlugin` learns its seat ONLY from `AttachToInput`, so a slot loaded
   or re-seated while BYPASSED still believes it is on seat 0 — every un-bypass
   path has to say the seat first.
2. **The player count and the per-seat channels are NOT in
   `_activeEngineConfig`**, so a device change / NAM 48 kHz request / watchdog
   recovery silently ends the per-seat routing.
   `Audio/Abstractions/PlayerInputRoutingState` records them and
   `AttachPerPlayerStateToEngine` replays them wherever the DSP rigs are
   re-attached to a live engine.

**The live SEAT COUNT follows the input CONFIGURATION (0.44.3).** The owner reported two guitars on a Focusrite's inputs 0 and 1
coming out one per ear. The native side was already right — at `players > 1`
every seat is mono from its own channel and duplicated L/R, i.e. centred; at
`players == 1` it fills the chain's L and R from channels 0 and 1, which is the
STEREO-PICKUP reading. Nothing outside an armed couch co-op run (since
removed) ever asked for a second seat, and the run's exit hard-coded one. Three pieces:

- **`Audio/Abstractions/LiveInputSeatLayout`** — count + per-seat channel +
  per-seat trim as ONE immutable value, and `FromInputConfig` is the only
  producer of the resting one. **The channels index the OPEN set, not the
  jacks**: ASIO builds an input MASK and JUCE compacts the enabled bits
  ascending, so a rig on inputs 3 and 4 reads 0 and 1 — and a user who typed the
  HIGHER jack in the "left" box gets the seats swapped. WASAPI has no mask
  (`ApplyAudioConfigurationCore` builds one only when `AppConfig.AudioDriver` is
  ASIO), so the channel numbers do nothing there and the settings row hides them.
- **The intent is EXPLICIT: `AppConfig.InputChannelsAreSeparateGuitars`.**
  Unticking Mono has always meant "give the chain a real L and R" for a genuine
  stereo pickup; inferring "two players" from it would silently collapse that
  user's stereo image to a centred mono, and nothing in the signal tells the two
  cases apart. **Do not "simplify" this into an inference.**
- **Two layers, ONE writer.** `PlayerInputRoutingState` holds a BASELINE (from
  `ApplyAudioConfigurationCore`, so startup, every settings apply and the
  watchdog's recovery rebuild all reach it) and an OVERRIDE
  (`IPlayerInputRouting.SetLiveInputOverride`), and `RestoreLiveInputBaseline`
  hands the engine back rather than resetting it. A config apply arriving
  mid-override records the new baseline without disturbing it.
  `JuceAudioPlaybackService.WriteRoutingTo` is the single place
  `dca_engine_set_player_count` is reached from, live push and rebuild replay
  alike — `Services/Audio/LiveInputRoutingWiringTests` lints that no second
  product file writes it, and that the config apply is what sets the baseline.
  The three old primitives (`SetLiveInputPlayerCount` /
  `SetPlayerInputChannel` / `SetPlayerMonitorGain`) are GONE from
  `IPlayerInputRouting`: a caller that could set a count apart from its channels
  could leave seat 2 live on seat 1's jack.
- **The OUTPUT PAIR is a mask on every driver, picked in two places.**
  `Audio/Abstractions/OutputChannelPairs` is the shared reading, the output twin
  of `GuitarInputChannels`: `ChannelMask(config, deviceOutputCount)` turns
  `RigConfig.AsioOutputChannelLeft` / `Right` (the names are historical; both
  drivers use them) into `JuceEngineConfig.OutputChannelMask` with NO driver
  condition. JUCE's WASAPI device keeps a channel map from the enabled bits to the
  endpoint's mix-format channels (`WASAPIDeviceBase::channelMaps`), clears bits
  at or past the endpoint's maximum channel count and writes the n-th enabled bit's
  buffer to the n-th mapped channel, so shared mode reaches outputs 3 and 4 as
  ASIO does; both devices hand the callback the enabled outputs PACKED, which is
  why the master bus is 2 wide whichever pair is picked. A pair with a channel
  at or past the device's count (`GetOutputDeviceChannelCount`, off the live
  enumeration) falls back to mask 0, the first pair, with a WARN
  (`PairPastDevice`); an unknown count (0) leaves the mask alone, and the default
  pair (outputs 1 and 2, `IsDefaultPair`) on a one-output device opens that output
  as mask 1 with no WARN, because nobody chose it. The mask is
  part of `JuceEngineConfig`, so a device change, the NAM 48 kHz request and the
  watchdog rebuild replay it with the rest. `PairOptions` lists the device's
  outputs two at a time ("Out 1 + 2", "Out 3 + 4"), a last odd output or a
  one-output device as a mono row ("Out 5 (mono)"), and always the pair the
  config names. Each row carries the driver's own name for the pair when it has
  one ("Out 3 + 4 · Headphones"): `GetOutputChannelNames` reads them off the OPEN
  device (`dca_engine_get_channel_names`, ABI v49; another device gets none, so its
  rows stay numbered) and `Audio/Abstractions/ChannelNames` drops a name that only
  repeats the number (JUCE's WASAPI "Output channel N", "Out 3", "Ch 5"). The input
  jacks get the same treatment ("Input 2 · Hi-Z", `GetInputChannelNames`) in
  Settings, the wizard and the rig footer. A mono row opens one output channel and
  the NATIVE engine folds the stereo bus into it: with one active output it renders
  a two-wide bus into preallocated buffers and writes (L + R) / 2
  (`renderFoldedToMono`, pinned by `RtMonoOutputFoldsLAndRWithoutAllocating`).
  Settings ▸ Audio (`AudioSettingsViewModel
  .OutputPairOptions`) and the onboarding wizard's Audio page
  (`OnboardingOutputPair`, whose test click plays through the picked pair) bind
  the same rows. Guards: `Services/Audio/OutputChannelPairsTests`,
  `Services/Audio/ChannelNamesTests`, `UI/ChannelNamePickerTests`,
  `UI/AudioSettingsOutputPairPickerTests`, `UI/Onboarding/OnboardingOutputPairTests`,
  `EngineDeviceWiringTests.TheEngineConfig_TakesTheOutputMask_OnEveryDriver`.
- **The guitar's JACK is picked in four places and written by ONE helper
  (2026-08-30).** The owner's guitar sits on the interface's SECOND input, and
  until then the only way to say so was a pair of unlabeled index boxes in
  Settings ▸ Audio — nothing where the amp is picked, nothing on the first-run
  wizard's Input page (which opened the device and lit a meter without asking).
  `Audio/Abstractions/GuitarInputChannels` is the shared reading:
  `JackOptions` (the rows, from `IAudioPlaybackService.GetInputDeviceChannelCount`
  — `MaxInputChannels` off the live enumeration, a generic eight when unknown),
  `SelectJackForSeat` (seat 0 = the left box, seat 1 = the right; a pick that
  collides with the other box SWAPS them, because two open channels must be
  two different jacks), `JackForOpenChannel` (the compacted-mask rule above,
  inverted). Settings' left box is now a `ComboBox` over those rows
  (`AudioSettingsViewModel.InputJackOptions`, which the onboarding wizard's
  Input page binds too), and the Amp & FX rig footer carries
  `ViewModels/Settings/LiveInputChannelViewModel` (`RigInputChannelPicker`),
  which follows the rig's seat picker and applies AT ONCE — by saving the
  config and calling `ApplyAudioConfiguration`, i.e. through the one baseline
  writer above, never by touching the seat layout. It ships disabled with its
  reason on WASAPI (no mask) and with input off. Guards:
  `Services/Audio/GuitarInputChannelsTests`, `UI/LiveInputChannelViewModelTests`,
  `UI/AudioSettingsJackPickerTests`, the rig / onboarding / latency-wizard
  headless smokes.
- **Seat 0's capture ring reads OPEN CHANNEL 0 at one live seat, whatever jack
  the guitar is on — so the latency calibration analysed the wrong channel
  (2026-08-30).** With both channels open as one stereo pair the native chain
  fill takes channels 0/1 as L/R, but `writeInputCapture` takes
  `inputCaptureChannel_[0]`, which is 0: a guitar on the second jack lit the
  input meter and "never heard the click", and looping the output into jack 0
  made the wizard "work". The fix does NOT add a writer: the wizard defaults
  its **Listen on** picker to `GuitarInputChannels.SeatZeroOpenChannel` (the
  seat layout's own answer), routes seat 0's ring there for the run through
  `IPlayerInputRouting.SetLiveInputOverride` — the override door, over the
  config's baseline with only seat 0's channel replaced — and
  `RestoreLiveInputBaseline`s when it stops. `Audio/Calibration/LoopbackChannelSearch`
  wraps one `LatencyCalibrationSession` per open channel: nothing back on the
  chosen channel ⇒ the next open one, and the result names the channel it
  heard the click on and, when that is not the configured one, says to pick
  that jack as the guitar input. Live judging still listens on the
  CONFIGURED channel, which is why the message points at the picker rather
  than silently adopting the fallback. Guards: `Services/Audio/Calibration/
  LoopbackChannelSearchTests` (a synthetic two-channel capture, click on one
  channel only) and `UI/LatencyCalibrationWizardChannelTests` (the wizard over
  a fake engine that serves the routed channel: heard where chosen, fallback
  reported, baseline restored on complete AND on cancel).
- **The loopback listens with a LEVEL detector, not the note analyser
  (2026-08-30, the owner's friend: output looped into a Focusrite's input 1,
  "didn't detect the clicks").** The wizard analysed the ring with
  `GuitarInputAnalyzer`, whose absolute attack gate (`MinAttackRms` = 0.01)
  exists to keep hiss from scoring as a pluck. The engine's clap is 90 ms of
  white noise at base gain 0.45; measured through that analyser it is heard
  at unity, -10 and -20 dB of return level and NOT at -30 dB — which is where
  an interface with its output and input knobs anywhere moderate lands, so
  the run failed with "no click heard" while the meter moved.
  `Audio/Calibration/LoopbackClapDetector` (the wizard's default
  `ILiveInputDetector` now) compares each 64-sample hop's RMS with a floor it
  learns from QUIET hops only — 4× the floor (+12 dB) above an absolute
  -70 dBFS bar fires, the onset is the first sample over 2× the floor, and a
  90 ms hold (the clap's own length) keeps the decay from being a second
  onset; anything after that is the session's to ignore (an accepted echo
  puts it in Settling). Heard down to -40 dB over a -70 dBFS hiss floor within
  2 ms of the true start, block-size independent, quiet on hiss and on a dead
  ring. Guards: `Services/Audio/Calibration/LoopbackClapDetectionTests` (the
  synthesised clap through BOTH detectors — the analyser's 5/5-at-unity and
  0/5-at--30 dB are pinned so the reason stays measured) and
  `UI/LatencyCalibrationWizardChannelTests.QuietLoopback_IsStillHeard_WithTheProductDetector`
  (the wizard with NO detector seam, click returning at -48 dBFS). The
  `DetectorFactory` seam is unchanged; the tapping steps never used the
  analyser (Tap is a keyboard command).

**Parametric EQ over the master bus (ABI v29).** The engine had exactly three
FIXED filters — high pass, low pass, band pass, frequency and Q each — and the
inspector's EQ card was a picture of them. v29 adds six TYPED bands with
per-band gain, a per-band solo, a routing target (song bus / every seat's
post-rig guitar / both), an output trim, an active-band COUNT and a magnitude
spectrum for an analyser. The three fixed filters are unchanged and still run
ahead of the six; everything EQ is off at engine start, so an app that never
calls the new exports runs the pre-v29 callback byte for byte. Three rules
reach beyond the native side:

1. **`dca_engine_get_eq_active_count` is the only honest answer to "how many
   bands are running?"** The native side publishes a band only when it is
   AUDIBLE (power on, and either soloed or enabled-with-nothing-soloed), so the
   export and the audio callback are one predicate rather than two copies of
   one. The inspector's "n/6" badge reads it — never a managed count.
   **But it can only answer about a section it has been GIVEN**: the engine is
   built lazily, so a cold start really does produce a live engine that has
   never heard of the module's section, and its truthful zero is
   indistinguishable on a badge from "nothing is switched on". The owner
   reported exactly that — `0/6` standing over lit band chips.
   `IAudioPlaybackService.EngineEqActiveCount` therefore answers **−1** for
   "nothing to ask", covering both no-engine and no-push, and
   `EqModuleViewModel.RefreshLiveState` pushes on the transition into a live
   engine. That does not weaken the rule above; it says which question the
   export is authoritative about.
2. **The EQ is master-bus state, so it belongs in the engine-REBUILD replay**
   next to `SetHighPass`/`SetLowPass`/`SetBandPass` in
   `JuceAudioPlaybackService`. A device change / NAM 48 kHz request / watchdog
   recovery otherwise drops it silently — the same shape as the per-seat routing
   state that shipped exactly that way.
3. **`IAudioPlaybackService.SupportsParametricEq` must be BELIEVED by the UI.**
   The inspector module ships visibly disabled with its reason printed when it
   is false, and pushes nothing at all — the couch co-op `CoopRigApplier` rule applied
   before the fact rather than a release later.

The managed side is `DotCharter.Services/Audio/Equalizer/` (five pure types:
`EqBand`/`EqLimits`, `EqCurve`, `EqSection`, `EqPresets`, `EqSpectrum` — the
count agrees with the "Five PURE types" line near the top of this file now; it
said four here and omitted `EqSpectrum`) and the ranges are
clamped on BOTH sides of the ABI deliberately — a band the engine silently
moved is a band whose drawn curve and audible curve disagree. The two sides'
agreement is a GATE, not a convention: `EqNativeMirrorTests` reads the native
header (see the `EqLimits` bullet above).

### Audio rules

- All real-time audio lives in the native engine. Never add managed audio pumps on the hot path.
- The managed wrapper stays a thin shell: marshalling + lifetime only. Use `NativeHandleOwner` (acquire/release around every P/Invoke; drain-on-dispose) for any new handle-owning wrapper.
- JuceSharp changes: commit in the submodule first, then bump the pointer in DotCharter.
- Incompatible ABI change ⇒ bump `DCA_ABI_VERSION` and rebuild the DLL before running managed code.
- `PositionChanged` fires at up to 250 Hz on a timer thread. UI-visible state must go through a coalescing post to the UI thread — `IUiDispatcher` from Services (see `Audio/Coordination/AudioPlaybackCoordinator`, which posts one publish at a time with `PostNormal`, the Normal priority `Dispatcher.UIThread.Post` uses).
- **Rig structure changes are crossfaded by the native `DspChain`, not by the app** (JuceSharp `2760b7c`, 2026-08-28): bypass ramps out over 10 ms, un-bypass ramps in with the core running, remove waits for the fade-out, move/retag fade out and back in. A managed caller must NOT add a mute/ramp around `SetBypass` / `Remove` / `Move` — and must not expect them to be instant: remove and move block the calling thread for up to ~10 ms + three blocks while the device runs (milliseconds with no device open). Details and the tests that pin it are in `JuceSharp/AGENTS.md`.

