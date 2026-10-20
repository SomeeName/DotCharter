# `Audio/Dsp/` — the in-house tone rig (deep reference)

Extracted verbatim from `DotCharter.Services/AGENTS.md` so it stops riding in every agent's auto-loaded context. **Read this before adding a DSP module, a param, a branch tag or a tone preset** — string-param marshalling, branch tags, enum labels, the RS in-game-tone conversion and the loudness contract all have traps.

See also: [docs/tone-preset-authoring.md](../../tone-preset-authoring.md)

---

**PER-PLAYER CHAINS (ABI v28) — read this before touching anything below.**
`IDspChainService` is now ONE SEAT's rig. The native engine hosts
`DspPlayers.MaxPlayers` independent chains (each with its own VST3 input chain,
capture ring and hardware input channel) so each live seat (the "two guitars"
input setting) can have its own sound, and `Audio/Dsp/IPlayerDspChains` (`PlayerDspChains`) is how you
reach a seat that is not player 1. **DI still registers `IDspChainService` as
`For(0)`**, so every existing consumer in this document — the pedalboard, the RS
in-game-tone path, `DspTonePresetService`, `DcRigApplier` —
drives exactly the chain it always did, and nothing below changes for a solo
player. What IS new:
* **node ids are ENGINE-global** (`DspPlayers.NodeIdStride`, seat `p` →
  `p * stride + local`), which is why only the CHAIN-scoped native ops gained a
  player argument; seat 0's ids are unchanged;
* **the module catalogue is loaded once, on seat 0, and shared** — it is static
  native data, identical for every seat;
* **`IDspTonePresetService.ApplyToPlayer(player, preset)`** puts a preset on one
  seat's chain (`ApplierFor` keeps one long-lived `DcRigApplier` per chain, which
  is what keeps a mid-song switch a minimal diff); with no registry a seat above
  0 is a logged NO-OP, never a write onto seat 0;
* **`ToneSessionSet`** is one `ToneSessionService` per seat, all writing into the
  ACTIVE PROFILE's `tone-rig.json` (see
  [Per-profile rigs (0.45)](#per-profile-rigs-045--the-outer-axis) — profile and
  seat are different axes and a profile owns a rig per seat);
* **a session saves NOTHING while a song's tone is on its seat** (#166):
  `ToneSessionService.BoardDisplaced` is wired in `App.StateServices` to
  `RsTonePlaybackCoordinator.IsPlayersBoardDisplaced` for seat 0 (the only seat the
  coordinator drives). The flag rises before a drive's first apply and falls only
  when the background restore has put the player's board back, so the persist poll
  and the shutdown `FlushAll` never write a song tone as the player's rig, and an
  `AbandonPreview` at shutdown leaves the last saved board on disk. Before this the
  poll saved whatever drove the chain every second (the chart synth, the tone
  preview, a Play Mode run), and an exit mid-song lost the player's board. Pinned
  by `SongToneRigPersistenceTests`;
* **the detach-before-destroy rule binds every seat**
  (`JuceAudioPlaybackService.AttachDspChainsToEngine`).

**Live preset insertion (ABI v32):** `DcRigApplier` adds regular modules with
`AddNode(..., initiallyBypassed: true)`, configures params/content/branch and
ordering, then un-bypasses them through the chain's existing fade. Rejected
configurations stay bypassed. A tail node is already in the signal path, so
ordinary add-then-configure exposes its defaults. The original native add
entry points retain immediate wet insertion for offline consumers. Split/merge
routing remains immediate; this is not an atomic whole-rig replacement.
`DcRigInsertionTests` covers publication order; `DspStagedInsertionNativeTests`
renders the parked and activated states through the real per-seat ABI.

## Live cabinet, delay, lane and control state

- **Dual cabinet:** native `cab` has `irPath` (A) and `irPathB` (B), plus
  `blend`, `gainADb`/`gainBDb`, `invertA`/`invertB`, `delayAMs`/`delayBMs` and
  `outputDb`, alongside model/mix/cuts. `DualCabCore` uses the selected built-in
  IR when A is empty. B contributes only with a custom IR: missing or cleared B
  means full A, even at blend 1. Alignment is a wet-path delay, not a reported
  device latency or microphone-position model. Independent IR mailboxes adopt
  loaded files on the native audio thread. The selected editor disables unloaded
  B's audio controls and blend while retaining its load button.
- **Delay sync/trails:** `delay` adds `sync` and `trails` (both default off),
  `bpm` (20–400, default 120), and `division` (the native named note choices,
  default quarter note). Sync derives delay time from BPM/division and bounds it
  to 10–2000 ms; `timeMs` remains the stored manual setting and its editor is
  inactive while sync is on. Opt-in bypass tails use the module's explicit
  `DspChain` tail capability, mixing dry input with retained wet decay instead
  of applying the generic bypass fade to that decay. Re-enable/reset discards
  stale bypass history. Default-off behavior retains the legacy delay path.
- **Lane mixer:** the explicit native merger owns `level1..3`, `pan1..3`,
  `mute1..3` and `solo1..3`. An audible branch is unmuted and either soloed or
  there are no active solos. These switches preserve the level and each effect's
  bypass state; preset/session persistence uses the ordinary numeric parameters.
  `RigLaneMixerViewModel` exposes capabilities from that merger, including the
  unavailable case when routing ends in an implicit merge.
- **Persisted assignments:** `RigControlAssignment` stores a module parameter
  ID, macro index (0–3 or disabled), target range and optional zero-based MIDI
  channel/CC pair. `RigControlAssignments` validates against module metadata;
  reversed ranges are supported. `DspNode`, `DcRigModule`, `UserToneModuleDto`,
  `DcRigApplier` and session snapshots carry the assignments. Macro positions
  are projections of current parameter values, not another persisted value bank.
- **Hardware MIDI control:** `JuceAudioPlaybackService.RigMidi.cs` owns the
  `JuceMidiInput` under the engine gate, closes it before engine teardown and
  reopens the selected device on rebuild. Closing increments the input generation.
  `RigMidiInputViewModel` persists the device name, suspends delivery during
  asynchronous selection changes, and coalesces CCs by channel/controller into
  at most 2,048 pending entries. Receive and UI drain check the generation;
  learning clears pre-existing pending values. Parameter writes occur on UI
  delivery, not on the native MIDI callback or a managed audio pump.
- **Tempo bank:** `RigTempoViewModel` follows the edited board's live delay
  `bpm`/`sync` targets. BPM writes and monotonic tap timing change only BPM;
  sync remains the caller's choice. Empty and mixed target sets are explicit.

Native regression coverage lives in the JuceSharp delay-sync/trails, dual-cab
and lane-mixer tests; the integrated native build reported those offline tests
passing. UI projection/source checks do not establish audio quality, callback
latency or a calibrated response plot. Atomic module replacement/duplication
and that response plot are still separate unimplemented features.

**An apply BLOCKS its caller, and the UI gesture must not be that caller
(0.62).** The v0.48 bypass crossfade shipped natively: `DspChain::fadeOutAndWait`
marks a node bypassed, publishes, and then SLEEPS on the calling thread until the
audio thread has ramped the node's weight to zero — bounded at
`kFadeSeconds (10 ms) + 3 blocks + 5 ms`, ≈47 ms at 512 frames / 48 kHz — and
BOTH `RemoveNode` and `MoveNode` go through it. `DcRigApplier` is a minimal-diff
planner, so an eight-module preset swap is on the order of sixteen of those:
hundreds of milliseconds against the 150 ms switch budget, driven straight from a
tone-card click. `Audio/Dsp/Presets/ToneApplyQueue` is the seam — one worker per
seat, last-writer-wins, completion posted back — and `PedalboardViewModel` is
what hands the browser a real `Task.Run` plus the dispatcher (an inline
scheduler is precisely the old behaviour, so that wiring is linted by
`Services/Audio/Dsp/ToneApplyQueueTests`).
**A `DcRigApplier` single-flights its OWN plan (0.62), so no caller has to.** The
class doc used to say "callers must serialize `Apply` (the coordinator
single-flights it)" and that was false: five sites reached seat 0's applier —
`ToneSessionService.RestoreOnce` (engine-attach thread), the preset browser (UI),
couch co-op's rig applier (since removed), `ProfileToneRigCoordinator` and
`RsTonePlaybackCoordinator` —
and only the last did, under a PRIVATE gate no other caller could take. A PLAN is
not an op: the whole diff comes from ONE `GetNodes()` snapshot while
`JuceDspChainService._gate` serializes only the individual ops, so an interleave
removes nodes the other planned to reuse and shifts every `MoveNode` index,
leaving a blend of two tones with the damage counted as `FailedOps` and misread
as native rejection. One lock per applier — i.e. per chain, i.e. per SEAT, so
seat 1 never waits on seat 0 — and the ordering is one-way, so it cannot deadlock
against the chain's gate. Pinned by
`Services/Audio/Dsp/DcRigApplierSingleFlightTests`.
**`RsTonePlaybackCoordinator._applyGate` is NOT redundant with it and must
STAY** — the tidy-up looks obviously right and is not. The applier's lock makes
each PLAN atomic; the coordinator's gate decides which plan happens FIRST,
ordering a run's tone applies against `StopDriving`'s
generation check + restore. Removed from the two bare applies, an outgoing run's
restore evaluates its check mid-apply and lands after it, so the incoming run
wears the outgoing run's tone —
`DcRigApplierSingleFlightTests` asserts the gate is still there.

**One apply path is still SYNCHRONOUS on the UI thread and that is deliberate,
not an oversight**: `ProfileToneRigCoordinator.OnActiveProfileChanged`, because
`PedalboardViewModel` subscribes to the same event and its POSTED `ResyncAll`
relies on the coordinator's reload having already finished. It is a
once-per-gesture loading moment rather than an interactive one — but it is the
one to move next, and moving it needs a completion event first.
Full rules in [Per-player rigs (ABI v28)](#per-player-rigs-abi-v28--the-full-rules)
at the end of this file, and in `JuceSharp/AGENTS.md`.

- `Audio/Dsp/` — the in-house tone-rig chain (`IDspChainService` +
  `DspModel.cs`: reorderable `DspNode`s over the native `dca_engine_dsp_*` ABI).
  **ONE BINDING, and it is JuceSharp's** (2026-09-11). The app drives the rig
  through the wrapper's managed surface —
  `JuceAudioEngine.GetDspChain(player)` → `JuceDspChain` / `JuceDspNode`, plus
  the engine-free `JuceDspModuleCatalog` — with
  `Audio/Dsp/DspNativeInterop.cs` as the single adapter from the wrapper's
  throw-shaped API to this seam's `bool`/`out` shape. It used to be the app's
  OWN P/Invoke copy of ~30 exports (`DspNativeMethods.cs`, deleted), which
  inherited neither the wrapper's UTF-8 marshalling lint (six of its
  declarations were still `LPStr`) nor its handle pinning. `DotCharter.Tests/
  Services/Audio/DspNativeBindingTests` fails the gate on any `DllImport` /
  `LibraryImport` of the audio library in `DotCharter.Services` or
  `DotCharter.UI`, because a missing export is easier to P/Invoke than to add
  to the submodule and that is exactly how the first copy appeared —
  **add the export to JuceSharp instead.** `DspNativeInterop` caches
  `JuceDspNode` by id (written before `JuceDspChain.TryGetNode(int)` existed;
  without a lookup every `SetParam` in a knob drag costs an extra P/Invoke).
  `JuceDspNode.TypeId`/`Bypass` read the ENGINE since ABI v33
  (`dca_engine_dsp_get_node_type` / `dca_engine_dsp_get_bypass`), but the app
  never reads them: `JuceDspChainService`'s mirror records the type from the
  id `AddNode` passed and is cleared on every engine-handle change.
  **String-valued params (ABI v22):** file-path / free-text params that are NOT
  float knobs. `IDspChainService.SetNodeString(nodeId, paramId, value)` →
  `bool` and `GetNodeString(nodeId, paramId)` → `string?`; the module catalogue
  metadata gains `DspModuleType.StringParams` (`DspStringParamInfo`: id /
  displayName / `FileFilter` — a `;`-separated picker wildcard list, empty = free
  text / no picker), read once from `dca_dsp_module_string_param_count/_info`.
  Params: `namamp` `modelPath` (`*.nam;*.aidax`), `cab` / `acousticsim` `irPath`
  (`*.wav`), `miku` `phrases` (free text). Marshalling belongs to the JuceSharp
  wrapper — `JuceDspNode.GetString`/`SetString`, which own the size-then-copy
  dance (null buffer / capacity 0 to size, returns the full UTF-8 byte length
  excluding the NUL, `-1` = unknown node/param; the copy is always
  NUL-terminated) and the UTF-8 marshalling. **The app declares no P/Invoke of
  its own here any more** (see the "one binding" note below): `GetString`
  reports an unknown node/param by THROWING and `DspNativeInterop` turns that
  back into this seam's `false`. **Setter semantics (from native, surfaced verbatim in the
  interface doc-comment):** `""` reverts to the module's built-in default; a
  failed synchronous IR load returns `false` and KEEPS the previous value (the
  managed mirror only advances on native success); a `namamp` `modelPath` is
  accepted immediately (`true`) and loads asynchronously. **Poll-only status
  seam (NOT ParamSpecs / knobs):** read `namamp` `"loaded"` / `"rateMismatch"`
  and `miku` `"trackedHz"` / `"syllable"` through the existing `GetParam`
  (float) path — they are deliberately absent from the catalogue's `Params`
  list, so the pedalboard renders no slider for them. `NullDspChainService`
  returns `false`/`null` (throw-free). **Presets CAN now carry string params
  (0.16 tone-catalogue pass):** `DcRigModule.StringParams`
  (`IReadOnlyDictionary<string,string>?`, null for the common module) holds
  file-path / free-text params (`namamp` `modelPath`, `cab`/`acousticsim`
  `irPath`, `miku` `phrases`); `DcRigApplier` pushes each via `SetNodeString`
  after node creation — on ADD (always) and on REUSE (diffed against the node's
  string cache so an identical re-apply stays a `NoOp`), counted into
  `paramsSet`. A preset that CARRIES the string therefore survives a re-add too;
  omitted strings on REUSE are reset to the native default (empty string), so
  a factory tone or restored snapshot never inherits the previous capture/IR.
  Explicitly saved strings survive either path. `PedalboardViewModel.BuildCurrentRig()` (the ONLY save-as-a-tone
  snapshot since 0.60 deleted the service's dead `CaptureCurrent` duplicate) and
  `ToneSessionService.SnapshotLiveChain` copy each node's `StringValues` into
  `DcRigModule.StringParams` (and its `Branch`), making a Save-as-user-tone carry
  a pedal-card-set capture/IR that the applier then re-pushes on re-add.
  **The whole chain has to carry them or the capture is lost** —
  `UserToneModuleDto.StringParams` (JSON, omitted when null) is the persistence
  leg: it used to drop them, so a saved user tone persisted a MODELLESS `namamp`
  and re-applying it brought back a silent amp. Pinned by
  `DcRigApplierTests` (`StringParam_*` + `PresetStringParam_*`),
  `PedalboardViewModelTests.BuildCurrentRig_carries_manual_string_params_*` and the
  end-to-end `PedalboardAuditRegressionTests.User_tone_save_and_reapply_keeps_the_capture_*`.
  **Split / parallel branch tags (ABI v23):** the `splitter`/`merger` routing
  modules mark a 2–3 branch parallel region; each node carries a branch tag via
  `IDspChainService.SetNodeBranch(nodeId, int)` → `bool` / `GetNodeBranch(nodeId)`
  → `int` (0 = unassigned → branch 1; 0 fallback, throw-free; `NullDspChainService`
  no-ops). Marshalling is the wrapper's
  (`JuceDspNode.Branch`; out-of-range/unknown = `InvalidArg`, which
  `DspNativeInterop` turns into `false`). The tag hydrates onto `DspNode.Branch` on sync; a branch
  change is a ROUTING tweak, so it does NOT raise `NodesChanged`. Native tags
  survive move/reorder but NOT remove/re-add, so **`DcRigModule.Branch`** is
  serialized (default 0, omitted from `UserToneModuleDto` JSON when 0) and
  `DcRigApplier` RE-PUSHES it after add (only when non-zero — a fresh node is
  already branch 0) and diffs it on reuse (identical reapply stays `NoOp`);
  `BuildCurrentRig` captures it. Pinned by `DcRigApplierTests` (`PresetBranch_*`),
  `JuceDspChainServiceTests` (branch round-trip), `UserTonePresetDtoBranchTests`,
  and the real-engine `DspBranchNativeRoundTripTests` (skips without the DLL).
  **Enum value labels (ABI v24):** discrete "model picker" params carry their
  named values. `DspParamInfo.ValueLabels` (`IReadOnlyList<string>?`, null for a
  continuous/numeric param — the vast majority — so the UI keeps a knob) is
  loaded ONCE with the catalogue (`JuceDspChainService.LoadValueLabels` over
  `dca_dsp_module_param_label_count`/`dca_dsp_module_param_label`, size-then-copy
  UTF-8, `-1` sentinel = continuous). The labels come from
  `JuceDspModuleCatalog` (`JuceDspParamInfo.Labels`, null for a continuous
  param), which reads them once per process; a pre-v24 DLL degrades to null via
  the `Safe(...)` wrapper.
  The label STRINGS come from native and are NEVER duplicated into resx.
  **Every discrete param is labelled (0.19).** Not just the models (amp `voicing`
  24, cab `model` 12, overdrive/distortion `model` 4): every named choice
  (tremolo `shape`, chorus `lfoShape`, ringmod/vibrato `waveform`, wah `mode`
  Auto/Manual, autowah `direction`, dimension `mode` 1–4, miku/mikustomp
  `octave`+`pattern`, resonator `key`/`chord`/`octave`, filterseq
  `mode`/`trigger`/`filterType`, feedbacker `harmonic`, glitch `mode`/`reverse`,
  swell `mode`, subharmonic `waveform`, merger Average/Sum) AND every two-state
  switch (delay `pingPong`, harmonizer/whammy `lowLatency`, whammy `formant`,
  freeze `capture`, tapestop/glitch `trigger`). There is no half a square wave —
  a discrete param must never be a continuous knob.
  **Labels used to be WITHHELD from the footswitches, and the reason is now
  fixed at the cause.** The gear rail treated a module's FIRST labelled param as
  its "browse models" param, so labelling freeze `capture` would have let the rail
  add a pedal PRE-STOMPED (`dcdsp_freeze.h`: a preset must never ship
  `capture = 1`). `GearCardViewModel.ModelParamOf` now resolves the DECLARED model
  param (`DspModuleVisuals.ModelParamId`: amp `voicing`, cab/od/dist `model`, else
  none) — so labels and models are independent. Don't re-infer the model param.
  **THREE params look discrete and are NOT** — they are continuous blends, verified
  in their headers, and labelling them would lie about the DSP: `compressor.detector`
  (`level = (1-det)*peak + det*rms`), `univibe.mode` (smoothed chorus↔vibrato morph)
  and `envfilter.direction` (`cutoff = cutUp + dir*(cutDown-cutUp)` — note the
  autowah's same-named param IS an int switch). `PedalboardSkinReachabilityTests`
  asserts they stay knobs.
  **Ordinal counts carry a STEP instead (ABI v27):** `dca_dsp_module_param_step` →
  `DspParamInfo.Step` (0 = continuous), loaded with the catalogue
  (`JuceDspChainService.LoadStep`, `Safe(...)`-degraded so a pre-v27 DLL just
  reports continuous). Set for chorus `voices`, phaser `stages` (**step 2** — the
  core forces the allpass count EVEN), bitcrusher `bits`/`downsample`,
  slapback/glitch `repeats`, filterseq `steps`, splitter `enabledBranches`. The UI
  snaps to the grid and shows an integer (no "6.5 stages"). **Adding a discrete
  param = value labels (named) or a step (ordinal) — never neither**;
  `PedalboardSkinReachabilityTests` sweeps the live native catalogue for int-flavoured
  param ids and fails on any that renders as a plain knob.
  **`namamp` — the amp face and the end of the 48 kHz bypass (0.36).** A NAM /
  AIDA-X capture is one amp at ONE knob setting: it has no controls and its tone
  stack cannot be recovered from it, so the module used to expose exactly two
  params (`inputDb`/`outputDb`) and the player got a voice they could not shape.
  It now carries a full face — **`gain` (PRE-capture), `bass`/`mid`/`treble`/
  `presence`/`resonance`/`master` (POST-capture)**, all `0..1`, all defaulting to
  `0.5`. The stack is POST by decision: the capture is a black box that already
  contains preamp AND power amp, so there is no seam to insert a stack into, and
  a pre-capture stack would only re-spectrum the drive feeding a nonlinearity
  that has its own stack baked in. `gain` is pre for the opposite reason — level
  into the model is the one thing the capture is genuinely a function of.
  **NEUTRAL IS A STRICT NO-OP** (native: each band is skipped rather than run at
  0 dB), which is what keeps every preset and user tone authored before 0.36
  bit-identical — pinned end-to-end by
  `NamAmpFaceNativeTests.NeutralFace_IsABitIdenticalNoOp_AndAMovedKnobIsNot`
  and natively by `tone_response.exe face neutral` (0 differing samples over
  2.05 M). Measured absolute response (input −18 dBFS): bass ±11.98 dB @ 20 Hz,
  mid ±11.96 @ 630 Hz, treble ±12.00 @ 16 kHz, presence ±8.98 @ 16 kHz,
  resonance ±8.87 @ 100 Hz, master −inf..+12.04 dB, gain ±18 dB into the model.
  **`resonance` is NOT `dcdsp_cab.h`'s `kResonance`** — that one is always on
  inside the cab's wet path (it restores the driver Fs the IR bank has no trace
  of); this one is player-facing and flat by default, so a stock rig gets the
  bump exactly once.
  Two managed consequences, both of which have bitten this file's neighbours
  before: **`DcRigTargetCatalog`'s ParamSpec mirror gains all seven params with
  the SAME defaults** (a drifted default is silently re-applied to every preset
  that omits the param — the cab `lowCutHz` 80 → 60 lesson), pinned by
  `NamAmpFaceNativeTests.TheAmpFaceParams_MirrorTheNativeCatalogue`; and
  **`DcRigLoudnessNormalizer` folds `gain` into the drive and `master` into the
  post level** (both 0 dB at their defaults, so every pre-0.36 capture preset
  estimates identically).
  **The 48 kHz dry-bypass is GONE.** `namamp` resamples around the capture
  (windowed-sinc, built on the module's worker thread as part of the loaded
  model, so the audio thread still never allocates); measured flat within
  0.005 dB from 20 Hz to 16 kHz with +1.42 ms round-trip latency at 44.1 kHz.
  The managed 48 kHz device request in `JuceAudioPlaybackService` therefore
  DOWNGRADES to an optimization — it buys back that 1.4 ms when the device
  agrees, and its failure is no longer audible. **Do not delete it** (it is
  source-pinned by `EngineConfigPolicyTests` for the v0.17.1 teardown bug), and
  do not weaken `ApplyEngineConfig(cfg, allowReplaceFallback: false)`.
  `rateMismatch` survives as a poll-only param but now only fires when the
  converters could not be built; the new poll-only `resampling` reports whether
  they are in the path and drives the pedal card's informational
  `Nam_ResampledBadge`.
  **The built-in `amp` module is DISPLAYED as "Legacy Amp"; its type id is still
  `amp` and must never change** — that id is persisted in every `DcRigPreset`,
  the 83 algorithmic factory presets (70 use it), every saved user tone's JSON
  and `DcRigTargetCatalog`. Module display names come from the native catalogue
  and are never duplicated into resx.

  **LOW-END CONTRACT (0.35) — every in-path bass filter is a BOUNDED shelf.**
  The owner reported that every tone, factory and user-made, sounded
  bass-light. It was not one filter: three separate native sites modelled a
  bounded circuit network (cathode-bypass cap, Tube Screamer feedback-leg cut,
  RAT input network) as an UNBOUNDED high-pass and then cascaded them, so the
  response kept falling at 6-24 dB/oct through the register the instrument's
  fundamentals live in. Measured with
  `JuceSharp/src/JuceSharp.Native/tools/tone_response.cpp` (sine-probe
  describing function, dB re the stage's own 200 Hz-4 kHz log-mean, at the low
  E, input -18 dBFS): amp voicings -0.1..-15.0, screamer overdrive -20.6, every
  distortion model -11.5..-19.7, whole factory chains -9..-18. The cab was the
  other half — its generated IR bank has a box rolloff but NO driver Fs
  resonance, so the DEFAULT model 0 sat at -7.4 dB on its own. Fixed natively
  (`cplCutDb` / `preCutDb` / `kTightCutDb`+`kBodyCutDb` / `kResonance`; the full
  rule and the accepted alias cost are in JuceSharp's AGENTS.md); after, amp
  voicings -0.1..-3.5 and whole chains -1.8..-3.9. **The one managed
  consequence: the cab's `lowCutHz` default moved 80 -> 60 Hz, so
  `DcRigTargetCatalog`'s ParamSpec had to move with it** — that mirror is the
  preset-apply default (`FillDefaults`), and a drifted default silently
  re-applies the old low cut to every preset that omits the param. Pinned by
  `DotCharter.Tests/Services/Audio/Dsp/ToneLowEndResponseTests` (drives the real
  native DLL offline; skips without it). **A preset's OWN explicit `lowCutHz` is
  unchanged and still authoritative** — the metal presets deliberately sit at
  95-110 Hz and were re-measured, not retouched.
  Pinned by `JuceDspChainServiceTests`
  (`LoadCatalog_maps_value_labels_*`) + the real-engine
  `DspValueLabelNativeRoundTripTests` (skips without the DLL).
  **RS in-game-tone conversion** ("Use in-game tones" feature) lives under it as
  pure, deterministic, throw-free code:
  - `Audio/Dsp/Tones/` (Phase 0, parse) — `RsToneJsonParser` turns the raw
    tone-JSON the package tone importer (`IPackageToneImporter`) surfaces into an `RsTone` (`RsGear`/`RsAmp`/
    `RsCab`/`RsPedal`, subtype by slot key); `RsGearCatalog.Shared` loads the
    embedded `tone_gear_data.json` snapshot (63/450/83/17, verbatim from
    Rocksmith2014.NET — see the folder README for provenance) and exposes each
    knob's `RsKnobUnit`/range + the AUTHORITATIVE routing `GetCategory` (the
    tone JSON's own Category is a loc-id, not trusted).
  - `Audio/Dsp/RsToneConversion/` (Phase 1, convert) — `RsToneConverter.Convert(
    RsTone, RsGearCatalog, RsGearOverrides) -> DcRigPreset` (ordered
    `DcRigModule{ModuleTypeId,Params,Bypassed}`). Generic catalogue-driven
    normalization (RS knob range → target param range via the static
    `DcRigTargetCatalog` mirror of the native module ranges), with embedded
    `rs_gear_overrides.json` for character (amp voicing, cab model, explicit
    per-key module routing via `targetModule`, the compressor-vs-sustainer
    split via `sustainerKeys`, static pins). **`targetModule` is no longer
    drive-only (0.9 gear wave)** — it is THE routing table for every category,
    consulted first by each `Build*` method, so a key moves onto a different
    module without touching `RsToneConverter.cs`. Every override key is a REAL
    catalogue key and the amp/cab tables cover the WHOLE catalogue (all 63 amps;
    all 450 cab keys via longest-prefix family stems) — enforced by
    `RsGearOverridesLivenessTests` + `RsGearOverridesTests`.
    **Amp coverage is now "steered explicitly", not "has a voicing"**:
    `EveryCatalogueAmp_IsSteeredExplicitly` accepts a voicing row OR a
    `targetModule` row, because the 18 bass heads and the 3 DI preamps take the
    second route and their 21 voicing rows were DELETED — a row nothing reads is
    dead data that reads as live, which the other half
    (`AmpVoicing_CarriesNoRowForAnAmpThatIsRoutedElsewhere`) now forbids.
    `TargetModule_RoutesEveryKeyToALiveModuleLegalForItsCategory` keeps a
    per-CATEGORY allow-list rather than one flat set, so a cross-category
    routing (a reverb rack pointed at a fuzz) cannot hide behind "the module
    exists".
    Ints use the NATIVE module semantics: amp `voicing` is the v24 ROSTER model
    (0..18 for guitar — labels in `DspModuleFactory.cpp`), cab `model` the
    26-IR roster (0..25: 0..11 the original bank incl. the three bass boxes
    9/10/11; 12..25, #74, the 4x10 / 1x10 / 2x15 guitar, PA, hi-fi and 2x10 /
    3x10 / 6x10 / 1x12 / 2x12 / 2x15 / 4x15 / 15+10 bass layouts Rocksmith's
    cabinets use, built from MEASURED driver responses by
    `JuceSharp/src/JuceSharp.Native/tools/cab_irs/gen_cab_irs.py`). A family's
    layout is read off its key and the game's own cabinet art in
    the gear archive. Until 2026-10 the tables used only voicings 0..2 and cab
    models 0..3 and every bass cab rode the guitar 4x12;
    every lossy decision is recorded in a `ConversionReport`. Mapped RS
    categories (full 37-module catalogue since 2.2): Amps→amp, Cabinets→cab,
    Distortion→overdrive/distortion, Delay→delay (tape/analog echoes→tapedelay),
    Reverb→reverb (spring→springreverb, plate→platereverb),
    Modulation→chorus/flanger/phaser/rotary/univibe, Tremolo→tremolo
    (amp-vibrato units→vibrato), Dynamics→compressor/gate (sustain-style
    pedals→sustainer), Filter→eq (wah→wah, auto-filter→envfilter, graphic
    EQs→graphiceq via nearest-band fold), Pitch→harmonizer (semitone knob) or
    octaver (octave pedals; octave-UP is approximated as sub-octave).
    Skipped: unknown keys only — `Pedal_LoFiFilter` and `Pedal_BassEmulator`, the
    last two dropped catalogue keys, convert since 2026-10 (#77).
    **What every key becomes is generated, not written here:**
    [rs-tone-coverage.md](rs-tone-coverage.md) (rendered by
    `RsGearCoverageAuditTests` from the real converter, ranked by the owner's
    library usage); the calibration harness is `RsToneFidelityHarnessTests`.

    **0.9 gear wave retargets (native catalogue 57 → 100).** Each replaces an
    APPROXIMATION with a model of the circuit the RS pedal actually is, or — for
    `svf`/`acousticsim` — rescues gear the converter DROPPED. Pinned one case
    each by `RsToneConverterGearWaveTests`, which asserts the module AND its knob
    values AND that the OLD target is gone:

    | RS key(s) | → module | knob map |
    |---|---|---|
    | `Pedal_VintageDistortion`, `Pedal_MarshallGuvnorPlus` | `rat` | Gain→`distortion`, Tone→`filter` **INVERTED** (a Rodent's filter pot is darker clockwise; every RS Tone is brighter clockwise), Level→`volume` |
    | `Pedal_Distortion` | `ds1` | Gain→`dist`, Tone→`tone`, Level→`level` |
    | `Pedal_MetalDistortion` | `hm2` | Gain→`dist`, Tone→the TILT between `lowDb` and `highDb` (both are boost-only resonant bells, not a bass/treble pair). Its `tightHz` static pin is GONE — that was a `distortion` param |
    | `Bass_Pedal_BassDistortion` | `bassod` | Gain→`drive`, **Blend→`blend`** (dropped entirely before), Bass/Treble→the ±12 dB shelves, Level→`level` |
    | `Bass_Pedal_BassOverdrive` | `bassod` | with an install, through the knob curves read from it since #157 ("The game's own knob curves" below): Blend→`blend` + `level` together, Gain→`drive`, Tone→`treble` |
    | every `Amp_*`, `Bass_Amp_*`, `DI_Amp_*` (63) | `rsamp` | the amp's own chain, built from the player's install, since #88 (below): `model` = the amp's row, `k1`..`k10` = its knobs' positions on their own min..max, in manifest order |
    | `Pedal_Limiter` | `limiter` | Limit→`ceilingDb` **descending** (0…−20), Rate→`releaseMs` **descending** (500…5) |
    | `Bass_Pedal_MBComp` | `compressor` with an install, else `multiband` | **With an install (#167):** one broadband `compressor` fitted to the pedal's own chain read from it (`RsMbCompModel`, below) — Compress slides `thresholdDb` and sets `makeupDb`, Recovery Speed → `attackMs` through the meter's curve, Transition Band drives nothing in the bank and is reported unused. **Without one** (the generic path, reported as a fallback): Compress→all three `*Ratio`, Rate→`releaseMs` descending, Transition Band→`lowXoverHz` (clamps at the crossover's 500 Hz) |
    | `Pedal_Compression` | `compressor` (was `optocomp` until 2026-10, #69) | with an install, through its knob curves: Comp→`thresholdDb` + `ratio` + `makeupDb` together, Attack/Release→`attackMs`/`releaseMs` 1:1 — see "The game's own knob curves" below |
    | `Rack_SynthFilter`, `Pedal_BobFilter` | `svf` | `source`=Envelope, Sens→`envSens`, Attack/Release→the follower times, FilterType→`mode`, Mix→`mix`. **Both were SKIPPED outright** |
    | `Pedal_AcousticEmulator` | `acousticsim` | Body→`body`, Tone→`brightness`. **Was SKIPPED** though the module has existed since 0.15.2 |
    | `Pedal_AnalogueDelay`, `Pedal_NPNDelay` | `bbddelay` | Time→`timeMs` (a BBD stops at 600 ms — a longer RS time CLAMPS and is reported), Mix, and **Feedback as a per-repeat GAIN** (capped 0.95): `BbdFeedbackForRegen` inverts the module's regen curve, which self-oscillates past `feedback` ≈ 0.82 — fed straight, the Analog Delay's own default (92) ran away (#451). `RsDelayRegenerationTests` renders every delay gear at full Feedback and asserts the tail dies |
    | `Rack_StudioDelay` | `multitap` | `taps`=2, `pan`=1, TimeL→`timeMs`, Filter→`tone`; the second tap time is approximated and reported |
    | `Rack_StudioChamber` (`space` pinned Chamber), `Rack_StudioVerb`, `Pedal_DigitalVerb` | `hallreverb` | Time→`decayS` (0.3–8 s), **the `Depth`-keyed DIFFUSION knob→`diffusion`** (dropped before; `hallreverb` is the only reverb module with one), Tone→`dampingKHz`, Mix→`mix` × `RsReverbMixScale` |
    | `Pedal_OctaveUp`, `Pedal_Octavius` | `octfuzz` | Mix→`octave`, Tone→`tone`. The old target was the SUB-octave octaver — the wrong direction, which the report apologised for on every conversion |
    | `Pedal_RingMod` | `ringmod` | **Pre-existing gap:** it rendered as a CHORUS. Its knob KEYS disagree with its labels — the "Rate" knob's key suffix is `Attack` and carries a PERIOD in ms (→ `freqHz` = 1000/ms), and "Mix" is `Sensitivity` |

    **Pot names in this converter are the RS knob KEY SUFFIX, never the display
    name**, and the two disagree often (`Rack_StudioChamber`'s "Diffusion" knob
    has the key suffix `Depth`). Read `tone_gear_data.json` for the keys.

    **The game's own knob curves (2026-10, #68 / #69 / #77, #157).** A knob label is
    not always the value the game applies, and the game says what it applies:
    every gear manifest in the game's gear archive (`manifests/gears/gear_*.json`) names a
    Wwise soundbank (`audio/windows/<gear>.bnk`; the optional plugin's Wwise bank reader reads bank
    version 91 only) holding the effect plug-in, its base parameter block and an
    RTPC curve per knob (the knob's RTPC id is the FNV-1 hash of its lower-cased
    `Key`; a curve stores points `(x, y, shape)` with a scaling — `Log` stores
    log10(Hz), `dB` stores `sign·(1 − 10^(−|dB|/20))`). That is the FORMAT the
    reader decodes; what any one gear's chain or curves hold is the install's and
    is written nowhere in this repository or its guides. Seven gears map through
    their curves onto DotCharter modules (and the MB Comp's chain is fitted onto one, below): `Rack_StudioEQ` (`eq` nodes; its gain
    knobs pass their dB labels through, because the curve shape they use is
    unpublished), `Pedal_Compression` (`compressor`: Comp drives threshold, ratio
    AND make-up), `Pedal_LoFiFilter` (two manual `svf` corners; slopes are not
    decodable — reported), `Pedal_BassEmulator` (`harmonizer` −12 plus an `eq`
    bell), `Pedal_BitCruncher` and `Bass_Pedal_BassOverdrive` (below) and
    `Pedal_AcousticEmulator`'s mid band (a notch `svf` blended with dry).
    **None of these numbers is in the repository (#755):** `RsGameCurves` holds
    the gear keys, the plug-in param ids each curve is read from
    (the plugin's game-table builder) and the converter's own constants; the curves come
    from the PLAYER'S install at run time (see "Rocksmith's game tables" below).
    Without an install every one of these gears takes the generic, label-reading
    path it had before (`RsGameTablesFallbackTests`). **Inferred, not published,
    and said so in the code:** the dB transform and that a scaled curve
    interpolates in its stored domain. `Infrastructure/RsGameDocsLintTests`
    fails the gate if a known install-derived figure comes back into a guide.

    **The MB Comp (#167).** `Bass_Pedal_MBComp`'s bank is not a ratio compressor:
    it is EQ stages, Wwise Meters writing Game Parameters, and Guitar Distortion
    bells (distortion off) whose gains ride those meters — a level-dependent cut.
    The plugin's `RsMbCompChain` decodes that chain from the install (the Parametric EQ,
    Meter and Guitar Distortion layouts are on the type), evaluates its STATIC
    curve for a sine at the meter-driven bells' geometric-mean frequency in the
    `GameLevelDb` frame, and keeps the chain's cut sampled against the level its
    meters see (`RsMbCompModel`, cached with the tables, `FormatVersion` 3). A
    Compress gain in front of the meters is an input gain, so it shifts which part
    of that cut the playing range (−60..−18 dBFS RMS at the rig input, the
    converter's choice) reaches; every Compress gain is make-up. Per Compress
    setting, `RsMbCompModel.GainComputer` fits the `compressor` node's soft-knee
    gain computer (threshold, ratio, knee, plus a make-up trim;
    `RsMbCompModel.CompressorGainDb` mirrors `dcdsp_compressor.h`) for the
    smallest worst-case miss over that range, the threshold kept inside the
    node's −60..0 dBFS. A bank outside what the decoder knows (another plug-in, a
    curve on Transition Band, a Compress gain between meters) drops the set and
    the pedal stays generic. Limits, reported per conversion: broadband where the
    game cuts a band; the game's cut can run out at the top of its meters where a
    compressor keeps compressing; a threshold, make-up or attack past the node's
    range clamps. The decoder is checked only on the SYNTHETIC bank, never on a
    real one. The plugin's own tests hold the fidelity target there (the converted
    curve within 3.5 dB of the chain's over the playing range at four Compress
    settings); `RsGameDataLintTests` fails on an `RsMbCompModel` or an MB Comp
    compressor parameter written as literals, `RsGameDocsLintTests` on the
    issue's figures quoted in a guide.

    **The choruses (2026-10, #76).** Six chorus gears map through the curves and
    the delay read from the install (`RsFlangerChorus`,
    `RsToneConverter.GameCurveChorus`): Mix → wet/dry %, Depth → % of the delay
    (peak deviation = depth × delay / 2, inferred), Rate → Hz through its curve
    (the "Hz" label is not what plays), one voice per effect object, Stereo →
    phase spread. When a chain names several, the FIRST IN THE BANK's object
    order supplies the curves and the delay (not the manifest's order — that is
    how the tables the rig was tuned against were read). Until then Depth spread
    over the module's whole 1–15 ms and Mix went 1:1. The read delays still ride
    the module's fixed 12 ms base (reported).

    **The Bit Cruncher and the Bass Overdrive (2026-10-05, #157).** With an
    install, the Bit Cruncher converts through its knob curves onto
    `bitcrusher`'s FutzBox params (the Lo-Fi section names — DownSample, Filter,
    Bits — are McDSP's published manual's), in the game's level frame
    (`RsGameCurves.GameLevelDb`, rsamp's +18 dB): the distortion saturates the
    crushed path BEFORE the quantizer, which is the character. Without an install
    it is bits, rate and Mix by their labels, reported. The Bass Overdrive's
    Blend drives `bassod`'s `blend` and `level` together, Gain its `drive` and
    Tone its `treble`, each through its curve; its Filter knob still folds.

    **The song tones' input gate (2026-10-05, #155).** A converted song tone is
    built for a gated input. A high-gain rsamp chain's small-signal gain is
    +40..+64 dB (measured on our renders), so on a raw input an idle guitar's
    hiss and hum come out as loud as the notes. `RsInputGate` (DotCharter.Tone)
    prepends a `gate` in `autoFloor` mode — it measures the floor whenever the
    input sits still, threshold 6 dB over it, ceiling −50 dB, 50:1 (the
    module's hard-gate end), 1 ms / 10 ms — to EVERY song tone in
    `RsTonePlaybackCoordinator`, not in the converter: it belongs to the input,
    not to the tone's gear (the coverage audit and the AI anchors see no extra
    node). No figure of it is read from the game (JuceSharp #63).

    **The Acoustic Emulator's mid band (2026-10, #73).** With an install, Mid-range
    and Mid Frequency follow their curves read from it. The `eq` bell stops at
    −18 dB, so the band rides a NOTCH `svf` blended with dry
    (`mix = 1 − 10^(dB/20)`, exact at the centre), the node after `acousticsim`
    (`RsToneConverter.GameCurveAcoustic`). Body / Tone stay on `acousticsim`.

    **The cab mic layer (2026-10).** 441 of the 450 `Cab_*` keys encode mic ∈
    {57, Condenser, Ribbon, Tube} × position ∈ {Cone, Edge, OffAxis};
    `RsToneConverter.BuildCabMic` puts a `micpos` node after every such cab
    (57→"57", Ribbon→"121", Condenser→Condenser, Tube→Tube (mic 4, fitted to the
    Neumann U 67's published cardioid response, #75), Cone→position 0, Edge→1,
    OffAxis→position 0 turned `angleDeg` 45° (`MicLayerOffAxisDegrees`: the
    mic's own published polar response — Sound On Sound's 2007 guitar-amp
    test is the 45° source; Rocksmith states no angle), distance 15 cm =
    `kFloorOnsetCm`, level 1/1.3 = unity). Nothing in the mic layer is reported
    as an approximation since #75. So a converted tone has one more
    node than its RS slots — and a four-band `Rack_StudioEQ` two `eq` nodes
    (`BuildHiMidBand`), since its HiMid used to be folded away; the Lo-Fi Filter
    is two manual `svf` nodes (low cut, then high cut) and the Bass Emulator a
    `harmonizer` plus an `eq` (`RsToneConverter.BuildCompanion` adds the second).
    Everything else in the wave (`gatedreverb` `reversereverb` `convreverb`
    `twelvestring` `talkbox` `vocoder` `basssynth` `looper` `freqshift` `ducker`
    `expander` `tilt` `midside` `volume` `granular` `barberpole` `ensemble`
    `detune` `pitchcorrect` `autopan` `tzflanger` `tapesat` `transient`
    `duckdelay` `rhythmdelay` `comb` `poweramp` `acpre`) has **no Rocksmith gear
    to convert FROM**, so those reach users through the pedalboard and the
    factory presets only.
    **Loudness of a CONVERTED tone (#67, 2026-10): levelled by its RENDERED
    loudness at playback.** The converter's trailing gain (below) is only the
    level a tone STARTS on. After a prepare, `RsTonePlaybackCoordinator`
    (`.Levelling` partial) hands the tones the ARRANGEMENT plays to
    `Audio/Coordination/RsToneLevelling`, which reads
    `IToneLoudnessMeter` (`RenderedToneLoudnessMeter`, DI in `App.StateServices`):
    `Loudness/CalibrationPhrase` (a generated 6.6 s guitar phrase, -18 dBFS peak)
    rendered through the chain without its trailing gain on the AI matcher's
    device-less offline engine (`IOfflineRigHost`, never the live one), on one
    BelowNormal pool thread (median 0.84 s per tone over the owner's library),
    drained first and measured after a 250 ms pre-roll
    (the applier's 10 ms fade-in leaks the dry input), as `Loudness/IntegratedLoudness`
    (BS.1770-style, K-weighted, gated). Cached in memory per chain
    (`Loudness/ToneLoudnessKey`, every param round-trip; bump `MeasurementVersion`
    when the phrase, meter or render changes), never on disk. The level is
    `Loudness/RsToneLoudnessPolicy`: **-24 LUFS** (the library median under the
    old walker) under a **-5 dBFS** phrase-peak ceiling, plus the tone's
    Rocksmith `Volume` minus the MEDIAN Volume of the arrangement's tones,
    **±3 dB** — decided from the owner's library (Volume moves within a song are
    small and do not track how driven a tone is; the reasoning and numbers are in
    the policy's doc comment). The trailing gain stops at ±24 dB; what it cannot
    give goes into the output trim (`level`) of the tone's Rocksmith amp chain
    (`RsToneLevelling.WithLevel`, #88), because the game's own clean chains can sit
    far under the reference. **A song never waits for it**: the measurement
    starts after the prepare has published, a cached chain is levelled before
    the run starts, and a tone already on the rig when its measurement lands
    GLIDES there (`GlideAsync`: ≤ 0.5 dB steps over 1.2 s, each an ordinary
    apply with the gain, and the amp chain's trim with it, changed). A glide is only queued onto an EMPTY apply
    channel and stops at the first newer request, so it can never displace a
    tone switch. The player's saved song-tone edits are never levelled.
    Pinned by `RsTonePlaybackCoordinatorLevellingTests`, `RsToneLevellingTests`,
    `RenderedToneLoudnessMeterTests`, `RsToneLoudnessPolicyTests`,
    `IntegratedLoudnessTests` and, on the real rig, by
    `RsToneFidelityHarnessTests.RenderedLevelling_PutsTheStockTones_OnOneLoudness`
    (judged with the harness's OWN reference DI, not the calibration phrase).
    Factory presets are untouched (still the walker).
    **The estimate (2.5, absolute since the 0.16 loudness fix):** the trailing
    master gain is `DcRigLoudnessNormalizer.ComputeTrailingGainDb(chain)` — a
    pure peak-walker heuristic that estimates the chain's static output for a
    −18 dBFS reference guitar input (linear level params multiply absolutely,
    dB level params add, saturating drive/amp stages cap at the −3 dBFS
    ceiling; EQ shelves/cab/mixes are level-neutral character) and pulls it to
    the **−12 dBFS target** (clamped: boost ≤ +18 dB, cut to the gain module's
    −24 dB floor). The RS tone JSON's `Volume` is **DISCARDED** (it targets
    Rocksmith's own mix bus and parked converted tones ~15 dB under the
    backing track); the discard is a `ConversionReport` note so the badge
    shows it. Same seam the factory-preset apply path uses — one absolute
    reference for both.
    **The 0.9 gear wave added 20 cases to the walker** (`rat` `ds1` `hm2`
    `tapesat` `bassod` `tubepre` `octfuzz` `bassamp` `poweramp` `acpre` `micpos`
    `limiter` `optocomp` `multiband` `transient` `tilt` `midside` `volume`
    `twelvestring` `basssynth`). **Every drive figure is the range the SHIPPED
    GUIDE states for that core's knob** — `JuceSharp/docs/guides/gear-*.md`, read
    the same way the pre-0.9 cases were read out of the dcdsp headers, never
    guessed; the comment at each case names it. Two are worth knowing: `limiter`
    is the one stage the walker can model EXACTLY
    (`min(level + gainDb, ceilingDb)` — and its `gainDb` is INPUT gain, not
    makeup), and `acpre`/`micpos` scale `level` by 1.3 in the core, so their 0.7
    default is -0.8 dB (the walker said ×1.4 until 2026-10) and the RS mic layer's
    1/1.3 is exact unity. **Measured against renders (`RsToneFidelityHarnessTests`)
    the walker is far off on driven tones:** a cab IR raises a saturated amp's PEAK
    by +5..+11 dB while the walker counts it as neutral, so the stock lead tone
    renders +13 dB over its estimate. **RS conversion adds a measured per-model
    trim** on top of the walker: `RsAmpLevelTrims.ByKey` for the game's
    own chains (63 amps, BY GEAR KEY since #755 because the rows are the install's;
    pinned ±1.5 dB, owner-only, by
    `RsToneFidelityHarnessTests.RsAmpModelTrims_MatchTheRig`; the walker counts
    `rsamp` as a unity saturating stage), and `AmpVoicingTrimDb` for the roster
    fallback, both applied by `TrailingGainDb`: the walker ignores the amp's roster model, but at equal
    knobs the 19 guitar models' rendered peaks span 37 dB (Jazz Solid 120 sat
    ~24 dB under the Twin until #71 re-staged it in `dcdsp_amp.h`; it is now within
    1 LU, pinned by `RsToneFidelityHarnessTests.JazzSolid120_SitsWithTheCleanRoster_AtEqualKnobs`).
    The table is a measurement, pinned ±1.5 dB by
    `RsToneFidelityHarnessTests.AmpVoicingTrims_MatchTheRig` — re-measure it when
    `dcdsp_amp.h` changes a voicing's level. It covers EVERY voicing since #70
    (0..26), the bass/DI rows 19-23 included even though RS conversion never
    lands on them. Factory presets do NOT get it.
    **Rocksmith's own amps (#88): `rsamp`.** Every RS amp — 42 guitar amps, 18
    bass heads, 3 DI preamps — is a CHAIN of up to four Wwise plug-ins in its
    soundbank (the gear archive, `audio/windows/<amp>.bnk`), every knob a set of
    RTPC curves onto their parameters, and RS conversion runs THAT chain:
    `RsToneConverter.RsAmp.cs` emits the native `rsamp` module at the amp's row
    with each knob's POSITION; nothing is mapped by reading a label, so no amp
    knob folds. The native table is NOT compiled in (JuceSharp #25, ABI v45):
    The plugin's game-table builder builds it with the managed index `RsAmpChains` (model
    order, knob order and ranges, which stages each knob moves) from the player's
    own install — its row builder decodes each slot's block and curves and
    bakes the RTPCs no knob sets at their `init.bnk` defaults
    (the bank reader's `ReadRtpcDefaults`; `init.bnk` is in `audio.psarc`) — and
    `Audio/Engine/RsAmpTableHandover` (owned by `JuceAudioPlaybackService`) hands the blob
    (`RsGameTables.NativeAmpTable`, JuceSharp's `RsAmpChainTable` format) to
    `dca_rsamp_set_table` when the tables are published AND on every engine build
    (`ReapplyMasterBusStateLocked`). The native table is process-wide; without it
    every `rsamp` module passes audio through, and the index is empty, so the
    converter emits no `rsamp` at all. Index and native rows share the order: amp
    keys sorted ordinal, which JuceSharp's public row list
    (`dcdsp_rsamp_models.h`) checks key by key. The plug-ins: Audiokinetic **Guitar Distortion**
    (0x007E0003: three pre-EQ bands, a waveshaper, three post-EQ bands — band b's
    RTPC param ids 10(b-1) + {0 type, 1 gain, 2 Hz, 3 Q, 4 on}, 60-65 type, drive,
    tone, rectification, output, wet), McDSP **FutzBox** (0x006E1003; filters,
    distortion, EQ, levels — its block layout is INFERRED from the Wwise help
    page's defaults), the Wwise **Compressor**, and on Windows iZotope **Trash
    Multiband Distortion** (0x00091033, unpublished), which the game's own Mac
    build replaces with a Guitar Distortion block in the same bank — the block
    `rsamp` runs. What the clone does exactly and what it infers (the drive law,
    the FutzBox distortion, the input level, the S-curves) is in `dcdsp_rsamp.h`'s
    header; the converter reports the inexact part PER KNOB
    (`RsToneConverter.RsAmpKnobCaveats`). The rig labels the ten generic knobs
    with the picked amp's own knob names (`PedalNodeViewModel.RelabelRsAmpKnobs`);
    without the tables they go inactive with the reason "Rocksmith's own amp
    chains need a Rocksmith 2014 installation" (`Rig_RsAmpNeedsInstall`).
    JuceSharp's `GearRsAmpTests` measures the chain maths on synthetic tables.

    **Rocksmith's game tables (#755): read from the PLAYER'S install.** The repo
    holds no number from the game's soundbanks, and neither does JuceSharp since
    #25. The plugin's game-table builder (over its Wwise bank reader) builds
    `RsGameTables` — the amp index above, the native amp chains
    (`NativeAmpTable`, see "Rocksmith's own amps") and every curve set in
    `RsGameCurveSet` — from the gear archive's manifests and banks plus
    `audio.psarc`'s `init.bnk` (the Game Parameter defaults the native blocks
    bake in). Each group is all-or-nothing: an amp that does not decode — or a
    missing `init.bnk`, or an RTPC without a default — drops the WHOLE amp table,
    index and native chains together (a partial one would shift every saved
    `model` int, and an index without chains would offer amps that pass audio
    through), a curve that does not decode drops that gear's set. A plugin's table loader (behind `IGameInstallReader`, Services `Plugins/`; #888) runs once per process on a pool thread, started by
    `App.axaml.cs` after the window is up: the install is `DOTCHARTER_RS2014_DIR`
    or the install the plugin's install locator finds, the archive opens with
    the player's key file (`RocksmithKeys`, #756) — without either the tables
    stay `RsGameTables.Empty` and the converter is generic. It loads the cache
    `%LOCALAPPDATA%/DotCharter/cache/rs-game-tables.json` (`RsGameTablesJson`,
    `FormatVersion` 2 + both archives' length and write time; the native blob
    rides along as base64) or builds and writes
    it, then `RsGameTables.Publish`es. The game folder is only ever read. Tests
    run on `RsGameTables.Empty` by default, on the SYNTHETIC install
    (`SyntheticRsGame`, invented numbers) when they exercise a game path, and
    `LocalRsGameTablesTest` (owner-only) compares a real build to the SHA-256 of
    the removed tables' canonical text (`RsGameTablesCanonical`), and the native
    blob, read back independently (`NativeAmpTableText`), to the SHA-256 of the
    header JuceSharp compiled in until #25; `TestRsGameTables` hands the real
    native table to the engine so real-table renders run the game's chains.
    `Infrastructure/RsGameDataLintTests` fails if a generated game table comes back.
    The RigLab has no loader: it always converts generically.

    The `amp` circuit voicings 24-26 (#70) and `bassamp`'s
    #72 controls stay for presets and the rig; RS conversion no longer lands on
    them (an amp key the game does not ship falls back to `amp` on the
    `GuessVoicing` substring guess, reported). **The wave's other 23 modules
    are level-neutral BY DECISION, not by omission** — they only attenuate, are
    wet blends against an untouched dry, or (`looper`) add a take whose level is
    unknowable statically; the comment at the bottom of the switch says which and
    why. `RsToneConverterGearWaveTests.EveryNewLevelChangingModule_MovesTheLoudnessEstimate`
    is the guard, because a module the walker does not know is level-neutral
    SILENTLY — the exact shape of this bug.
    - **Phase 2 (live switching, same folder)** — `DcRigApplier` diffs a
      `DcRigPreset` against `IDspChainService.GetNodes()` and applies the
      MINIMAL op set (reuse same-type nodes in place, only add/remove/move/param
      the delta — a same-multiset reorder never re-adds; an identical preset is a
      zero-native-call `NoOp`), so a mid-song tone switch never tears the chain
      down. It is throw-free and degrades on any native false; every failed
      native op is counted in `DcRigApplyResult.FailedOps` and logged (per-op
      for add/remove/move, one aggregated warning naming rejected param ids
      for param/bypass). **ABI-atomicity
      caveat lives in its doc comment:** the ABI has only per-node ops, so a
      *sequence* isn't atomic — new nodes are fully parameterized at the chain
      tail before being moved in-signal (no default-gain transient), and the
      native batch `dca_engine_dsp_load_preset` swap is the deferred escape hatch
      (NOT implemented; stays managed). `RsToneSchedule` (pure: base tone +
      ms-sorted boundaries → `ResolveAt`) + `RsToneScanner` (allocation-free
      change-detector, fires only on a boundary crossing / seek) are the pure
      switching brain; `RsToneConversionSummary` aggregates the per-tone
      `ConversionReport`s for the pre-play badge. The UI seam is
      `DotCharter.Services/Audio/Coordination/RsTonePlaybackCoordinator`. Phase 3 (the
      `AppConfig.UseInGameTones` toggle + Play Mode card) builds on these.
  - `Audio/Dsp/Presets/` — the built-in **factory tone catalogue**
    (`DspTonePresetLibrary.All`, **166 authored** `DspTonePreset`s across
    `DspToneCategory` Clean→Experimental, each an ordered `DcRigPreset` in
    catalogue range) + `IDspTonePresetService`/`DspTonePresetService` (DI
    singleton) to browse/apply them via `DcRigApplier` AND manage player-saved
    **user tone profiles**.
    **The catalogue is two halves, and the split is load-bearing (0.37).** The
    96 originals in `DspTonePresetLibrary.cs` (83 pre-0.9 + the 13 gear-wave
    showcase rigs below) are built on the algorithmic `amp` module or on the 0.9
    gear-wave modules; `DspTonePresetLibrary.Captures.cs` holds a **capture-backed twin of
    each of the 70 that had an amp** (`BuildCaptureBacked()`, concatenated FIRST
    into `All` so the neural version leads a category listing). Rules that must
    not drift:
    (a) **The twin takes the NAME, the original keeps its ID.** Twin ids are the
    original prefixed `nam-`; the original's display name gains " (Legacy)" and
    its id is untouched, so nothing that ever persisted a preset id resolves to
    a different tone than it did before. `CaptureBackedPresetTests` pins the
    pairing (name, category, and that the twin actually carries a `namamp`).
    (b) **A boost baked into the capture is never boosted twice.** Where the
    chosen capture already contains its screamer (`…-boosted`, `…-ts9`,
    `driftwood-nightmare-hm2`), the rig's `overdrive`/`distortion` module is
    DROPPED, not replaced. Everywhere else the dirt box becomes its OWN capture
    in front of the amp capture — a dirt/boost/preamp circuit is the one pedal
    class NAM models, and anything with an LFO or a clock (delay, reverb,
    chorus, flanger, phaser, tremolo, pitch) stays algorithmic by necessity.
    (c) **The `cab` module stays.** Every bundled amp capture is amp-only (the
    pack's `-Cab-` variants were deliberately not taken), so removing the cab is
    what makes a NAM rig sound like a fizzing DI.
    (d) **A capture-backed preset centres itself with `outputDb`.** The
    estimator cannot see a capture's absolute level, and the naive port landed
    the whole driven half 6–11 dB under its legacy twin — audible the moment a
    player A/Bs the pair. Every twin therefore carries a per-preset `outputDb`
    trim that puts its ESTIMATE on the −12 dBFS target, which is also what makes
    the apply-seam trailing gain ≈ 0 for these. Retune `outputDb`, never drive.
    (e) **Every `modelPath` is a bare bundled file name** and must exist in both
    `DotCharter.UI/NamCaptures/` and its `manifest.json` — a name that resolves
    to nothing is a MODELLESS namamp, i.e. silence, reported nowhere.
    `CaptureBackedPresetTests` fails the build on either half being missing.
    (f) **A capture-backed preset's cab sets `model` explicitly.** An omitted
    KNOB falls back to a neutral default by design, but the cab model is a
    voicing choice — a cab entry without it silently rides native default 0
    ("1x12 Combo") and the accident is indistinguishable from a decision.
    `CaptureBackedPresetTests.EveryCaptureBackedPreset_ChoosesItsCabModelExplicitly`
    lints it. (Measured 2026-08-11: the 63 capture-backed cab entries use only
    5 of the 12 roster models — 8/2/0/1/3 at 20/19/12/11/1 — none of the four
    0.16 synthesized guitar IRs, so roster fit per amp family is an open,
    listening-gated fidelity lever.)
    The 0.16 tone-catalogue pass added ~21 **artist/genre
    signature** soundalikes (`artist-*`/`genre-*` ids — Polyphia, Yorushika,
    Galneryus, AC/DC, Metallica MOP, Gilmour, EVH, SRV, Tool, Slipknot, city
    pop, shoegaze, surf, punk, …; soundalike names, never an endorsement) plus 4
    **capture-backed** presets (`nam-*`) that reference a bundled `.nam` via the
    `namamp` module's `modelPath` string param, plus 6 **vocal-synth** presets
    (`miku-*`, Experimental) across both singing modules — 5 on the Wave-6
    `mikustomp` (KORG recreation) and 1 on the v1 `miku`. Vocal-preset authoring
    rules (they behave unlike any other module) live in the block comment above
    them: `mikustomp` defaults to **mix = 1 (wet-only, like the hardware)** so a
    duet rig must pin `mix` explicitly; put a `gate` + `compressor` in FRONT (a
    clean monophonic signal is what makes the E1–E6 pitch/onset tracker fire one
    syllable per pick); the loudness estimator treats a vocal module as
    level-NEUTRAL (its output is the synth's, not a function of the input), so an
    amp-less vocal rig centres on the −12 dBFS target with compressor `makeupDb`
    + `eq` `outputTrimDb` — **never** with `mix`; lyrics ride the string params
    (`phrases`/`phrase2`/`phrase3` — a Phrase pattern 6/7/8 only sings when its
    slot is non-empty). Preset-carried lyrics reaching the real native node are
    pinned by `DspStringParamNativeRoundTripTests.ApplyingAMikuLyricPreset_*`.
    `Apply` prepares the rig through the SHARED pure pipeline
    `DcRigPresetPreparation.Prepare` (v0.27 extraction for the offline take
    re-amp, which was removed with the History screen on 2026-09-19; the AI
    tone designer's `ToneDesignService` uses it too, so both paths prepare
    identically): pin every module param the preset
    omits to its catalogue default (`DcRigTargetCatalog.FillDefaults` — the
    preset-apply seam), then — for FACTORY presets only — loudness-normalize
    via `DcRigLoudnessNormalizer.Normalize` (appends/replaces the trailing
    `gain` module so the estimated output hits the shared −12 dBFS target;
    user presets apply verbatim), then **resolve capture `modelPath`s**: a
    bare bundled file name is rewritten to an absolute path under the NAM
    library dir (the service's optional `namCaptureDirectory` ctor `Func` —
    DI passes `INamCaptureLibrary.RootDirectory`; null in tests leaves the
    bare name). `DcRigApplier` itself stays minimal-diff for live RS
    switching; `JuceDspChainService.ProcessOfflineBuffer` (over
    `JuceDspChain.ProcessOffline`, plumbed through
    `IDspNativeInterop.ProcessBuffer`) is the concrete-only offline render
    surface — deliberately NOT on `IDspChainService`. **The wrapper's offline
    call is INTERLEAVED both ways where this seam is planar and in-place**, so
    `DspNativeInterop.ProcessBuffer` interleaves in and de-interleaves out; it
    is offline-only, never a hot path. Offline also MEANS offline: the engine
    returns `Device` while a device is open, which surfaces here as `false`. The catalogue
    is additionally gated by `DspTonePresetLibraryTests` to keep every preset's
    ESTIMATED output within ±6 dB of the target (retouch
    `level`/`master`/`outputTrimDb` — or a `namamp` preset's `inputDb`/`outputDb`
    — never drive, when adding presets; the loudness estimator models `namamp` as
    a saturating amp). **Editorial ordering** is a separate pure seam:
    `PresetCuration` (`RankOf`/`IsFeatured`/`OrderFeaturedFirst`) — the
    preset-browser analogue of the gear rail's `GearCuration.RankOf`, keeping the
    library grouped clean→experimental while the browser surfaces the marquee
    tones first (`PresetCurationTests` gates that every featured id exists).

    **The 0.9 gear-wave showcase — 13 `gear-*` presets at the END of `Build()`.**
    The wave took the native catalogue from 57 modules to 100, and **most of
    those 43 have no Rocksmith gear to be converted FROM** (see the RsTone
    section), so a factory preset is the ONLY way a player meets them without
    building a board by hand. Ids: `gear-rodent-rhythm` `gear-chainsaw`
    `gear-optical-clean` `gear-bass-di` `gear-bass-synth`
    `gear-twelve-string-jangle` `gear-vocoder-lead` `gear-talk-box-funk`
    `gear-dotted-multitap` `gear-shoegaze-reverse` `gear-gated-snap`
    `gear-loop-station` `gear-tube-di-warm`; all 13 are on `PresetCuration`'s
    featured shelf, BELOW the marquee capture-backed tones and above the long
    tail. `FactoryPresetsAgainstLiveCatalogueTests.TheGearWaveShowcase_ReachesEveryNewDrawer`
    asserts the DRAWERS the block covers rather than the ids, so it still means
    something when a preset is rewritten. Authoring rules specific to this block
    (they are repeated as a comment above it):
    * `micpos` goes AFTER `cab`, never before it — it models moving a mic in
      front of a speaker, not a speaker.
    * `vocoder` at `carrier` = Sidechain needs a sidechain SOURCE, which a preset
      cannot carry; with none the engine falls back to the module's own input, so
      a preset must pick an INTERNAL carrier (saw/pulse) to be audible on load.
    * `pitchcorrect` and `basssynth` track a MONOPHONIC pitch — gate and compress
      in front of them, for the same reason the `miku` rigs do.
    * `looper` allocates both rings for the 60 s worst case at `prepare()`
      (43 MiB at 48 kHz, per instance per seat), so a preset caps `maxLoopS` well
      under the maximum instead of shipping the worst case as a default. It also
      ships `state` = Stop: a preset must never load mid-record.
    * `volume` ships at `position` 1 (toe-down) with `minDb` −60 rather than the
      module's −90 mute, so loading the preset is never silence.

    **A preset is validated against the LIVE ENGINE, not only the mirror
    (`FactoryPresetsAgainstLiveCatalogueTests`).** `DspTonePresetLibraryTests`
    checks presets against `DcRigTargetCatalog` and `DcRigTargetCatalogTests`
    checks the mirror against native — but the latter walks
    `DcRigTargetCatalog.AllModules`, so it can only catch a mirror entry that
    DRIFTED, never one that is MISSING. A preset on a module the engine does not
    have passes both and fails at apply time as a node that was never added.
    The same class also runs every shipped preset through `TonePresetJsonFile`
    (export → import: the import gate rejects an unknown module id WHOLESALE, so
    a tone the browser can show but nobody can send is half a feature) and
    through `DcRigPresetPreparation` (whose `FillDefaults` skips an unknown type
    SILENTLY). It skips cleanly where the native ABI cannot load.
    **Hi-gain authoring rules (0.16 rebuild — measured on the real dcdsp cores,
    not guessed):** a metal rig is `gate` → screamer boost (`overdrive`
    `model` 1, drive LOW 0.1–0.25 / level HIGH 0.8–0.9 — the mid hump + bass cut
    is the tightening trick, NOT a second distortion) → the amp `voicing` that
    matches the style (v24 roster: 15 Modern Djent, 16 PowerBall, 4/14 Recto, 2
    Five-Oh-One, 11 JMP-800) at `gain` 0.55–0.8 (never maxed) → `cab` →
    corrective `eq`. Two measured gotchas: (1) **cab `model` dominates the
    presence band** — 8 (4x12 V30 Modern) keeps ~21 % of the energy in 2–6 kHz
    while 3 (Vintage 4x12) and 7 (Greenback) keep ~3 % on the same chain, so a
    modern metal preset that ships on 3/7 reads as dark mush; (2) stacking a
    cranked `distortion` pedal into a near-maxed amp multiplies intermodulation
    (6–33 % vs ~2 % for boost-into-amp) — that is the "fizzy/mushy" sound. The
    cab `highCutHz` is nearly irrelevant next to the IR's own rolloff.
    Save-as-a-tone is `SaveUser(…, PedalboardViewModel.BuildCurrentRig())` — the
    CALLER snapshots the chain it is looking at; the service has no
    `CaptureCurrent` of its own (deleted 0.60: dead, and a drifting duplicate).
    **The snapshot LOOP is one pure type since 0.62: `DcRigSnapshot.FromNodes`
    / `.Modules`.** Every site that turns a live chain into a `DcRigPreset` goes
    through it — `BuildCurrentRig`, `ToneSessionService.SnapshotLiveChain`, and
    the two RESTORE snapshots that had drifted:
    `RsTonePlaybackCoordinator.SnapshotToPreset` (the manual chain a run gives
    back) and `ToneDesignerDialogViewModel`'s preview restore. Those two copied
    only type / params / bypass, so an RS run — which never converts to a
    `namamp` — removed the player's neural amp and re-added it MODELLESS, i.e.
    silent, and flattened a split board onto branch 0. **A new snapshot site
    calls the shared type; do not write the loop again.** Pinned by
    `RsTonePlaybackCoordinatorTests.EndRun_RestoresTheBoardsCaptureAndBranchTags_NotJustItsKnobs`.
    `SaveUser`(+overwrite)/`DeleteUser` persist over
    `AppConfig.UserTonePresets` (JSON `UserTonePresetDto` via
    `UserTonePresetStore`, same shape as `MatchPatternStore`). Factory presets
    are immutable (`DspToneOrigin.BuiltIn`, never overwritten/deleted);
    everything is throw-free (bad store read ⇒ empty user list).

---

## Attaching the rig to an engine (moved here from `DotCharter.Services/AGENTS.md`)

- **Detach the DSP tone rig BEFORE destroying an engine — `_dspChain.AttachEngine(null)` first, always.** Since 2026-09-11 the rig goes through JuceSharp's `JuceDspChain`, so an in-flight call PINS the engine handle and a knob write racing a dispose is a caught `ObjectDisposedException` rather than a use-after-free. That narrows the window; it does not remove the rule, because a call that has not started yet pins nothing — and `JuceDspChainService` still has its OWN gate, unrelated to `_gate`, which is what serializes the two. `Dispose` and `ReplaceEngineGated` both do this now; the failure path of a rebuild must detach too, or a failed engine create leaves the rig pointed at freed memory for the rest of the session.
  **`AttachEngine` writes the handle UNDER that gate, which is what makes the detach a real barrier** — it blocks until any knob write already inside `dca_engine_dsp_set_param` has returned. Hoist that write back out of the lock and the rule degrades to "no NEW writes", which never protected the in-flight one. **`AttachEngine` also CLEARS the managed `_nodes` mirror whenever the handle CHANGES** (a fresh engine's native chain is empty; a zero handle is no chain) and raises `NodesChanged`. Without it the survivors are phantoms: the pedalboard renders pedals that do not exist, `ChainHasNeuralAmp` fires the 48 kHz request against a ghost `namamp`, and `DcRigApplier` diffs the preset against the stale mirror, matches, and returns `NoOp` with ZERO native calls — so re-picking the tone cannot rebuild it. Re-attaching the SAME handle must NOT clear (`ApplyEngineConfig` re-asserts it after an in-place `Reconfigure`, which keeps its chain).
  **"The chain has no nodes" is NOT "no engine is attached", and `IDspChainService.HasEngine` is the only member that answers the second one.** They look identical through `GetNodes()`, and using the first as a proxy for the second is what shipped v0.44's "the app applies no tone rig at all": `ToneSessionService.Start()` restored the player's saved rig whenever the chain was empty, but the DSP chains are built during DI while the JUCE engine is built LAZILY (first audio load / first transport call), so `StartAll()` fired the whole rig at `EngineHandle == 0` on every launch. `DspNativeInterop.AddNode` returns `-1` before making a native call, so every module of every seat came back as a failed *native* op that no native code ever saw — measured on the owner's machine as 5.2 s between the chains existing and the engine existing, and the IDENTICAL rigs then applying with `FailedOps = 0` ten milliseconds after the attach. `Start()` now restores inline **only** when `HasEngine` (the case that call exists for: the engine attached before the service subscribed); otherwise it waits for `EngineAttached`. **The restore's re-entrancy guard DEFERS a concurrent attach, it does not drop it** — an attach landing mid-restore belongs to a different (or the first real) engine whose native chain is empty, and `EngineAttached` does not fire again until the next rebuild, so discarding it left the board empty for the whole session. `Audio/Dsp/ToneRestoreEngineAttachTests` pins both halves; `JuceDspChainService.AddNode` now LOGS which of the two managed refusals fired (unknown module type / no engine), because "AddNode('amp') failed" alone is indistinguishable from a native rejection and that is what made the diagnosis expensive.
  **`ProfileToneRigCoordinator.Start()` is the entry point the app actually calls, and it needs its own guard test** — those two files drive a hand-built single `ToneSessionService` and a profile SWITCH respectively, so both stayed green while the shipped startup path was broken. `ProfileToneRigTests.TheStartupRestore_FiresNothingBeforeAnEngineExists_AndEverySeatLandsWhenOneDoes` (+ `.TheFirstRunDefaultPresetAlsoWaitsForAnEngine` for the `DefaultTonePresetId` branch) covers `App.axaml.cs`'s background `Task.Run` → `Start()` → `StartAll()` over EVERY seat.
  **There is ONE premature-apply site in that log, not two, and the second cluster is a logging artefact.** A follow-up read of the same log found the identical `DcRigApplier: AddNode('namamp'/'gain'/'cab') failed` messages again at 12:40:50.124 — a pool thread, ~3.5 s before the engine attached, apparently never retried — and it was hunted as a separate caller. It is not one: those are `AppLogger`'s coalesced-repeat FLUSH SUMMARIES of the 12:40:48.44x failures, stamped at flush time (full mechanism in [infrastructure-update.md](infrastructure-update.md)). The giveaway is that all three share one millisecond while reporting internal spans of 0 ms and 2.8 ms, which no batch of real events can do; the seat-0-then-seat-1 fan-out of `StartAll` produced those two spans. An exhaustive sweep of every `DcRigApplier.Apply` reacher in `DotCharter.UI` + `DotCharter.Services` confirms `ToneSessionService.RestoreOnce` is the ONLY one reachable at startup without a user gesture — `RsTonePlaybackCoordinator` needs `BeginRun`/`BeginPreview`, `CoopRigApplier` needs a co-op run start, `PresetBrowserViewModel`/`PedalboardViewModel.ToggleCompare`/`ToneDesignerDialogViewModel` are all commands, and `TakeReampService` attaches its own offline engine first. **Do not go looking for a second one.**
  **A handle CHANGE to a LIVE engine also raises `EngineAttached` (0.37), and that event exists to be distinguishable from `NodesChanged`.** Both end with an empty chain, so anything that REPOPULATES the rig would otherwise have to guess whether the engine was rebuilt under it or the player just removed their last pedal — and guessing wrong means fighting someone deliberately clearing their board. `ToneSessionService` (`Audio/Dsp/ToneSessionService.cs`) is the one consumer: it restores the active profile's persisted rig for its seat, or the first-run `DefaultTonePresetId`, every time a live engine appears. That is what stops the pedalboard starting EMPTY on every launch **and** what stops a device change silently wiping the board (a new engine's native chain has no nodes — this was a real, unreported data loss). It is raised LAST and outside **`JuceDspChainService`'s OWN gate** because its handler calls back in through `AddNode`/`SetParam`, which take that same gate, and its handler exceptions are swallowed-and-logged: the raise happens inside `ReplaceEngineGated`, so a tone that fails to restore must never take the audio engine's rebuild down with it. **It is NOT outside `JuceAudioPlaybackService._gate`** — read that sentence as the playback gate and you will conclude something false. `ReplaceEngineGated` calls `AttachPerPlayerStateToEngine` from inside `lock (_gate)`, so the whole tone restore (N `dca_engine_dsp_add_node` calls plus every `SetParam`/`SetNodeString`) runs with the playback gate held. That is tolerable only because the rebuild block is the engine's exclusive owner by design and already holds `_gate` across engine destroy + create; it is not a licence to add more work there, and a slow restore lands on the same UI-thread wait Play/Pause/Stop/SeekTo take through `SnapshotStems()`. **Persistence is a POLL, not an event, and deliberately so** — `NodesChanged` covers composition only, and a knob turn fires at pointer rate during a drag, so hanging a config write off param changes would put file I/O on the UI thread 60×/second. `ToneSessionService` snapshots every 5 s and writes only when a cheap ordered signature differs, which is zero I/O for an idle board. **An empty chain is never persisted**: it is indistinguishable from "the engine is down", and overwriting a good saved rig with nothing is precisely the loss the service exists to prevent.

## What the live rig costs while nobody plays

- **Until the idle sleep below, a restored rig ran on EVERY device block from
  engine start, on every screen, whatever the input or the monitor level** —
  and while a player plays, or a non-sleeping pedal is on the board, it still
  does. The engine is built at
  startup (`ProjectSessionService.InitializeAudioEngineAsync` →
  `ApplyAudioConfiguration`) and `ToneSessionService` restores the profile's
  rig on its `EngineAttached` (~1 s after launch in the owner's log). In the
  callback (`AudioEngine.h`, `EngineCallback::renderBlock`) the seat loop calls
  the rig before the monitor mix (`processDspRigLive` since the idle sleep;
  `processDspRig`, whose only early-out is `nodeCountRt() == 0`, before it);
  the monitor gain gates the MIX, not the rig. So Home / Library paid the full
  rig, and a digital-silence input cost the same as a played note.
- **Measured** offline (null device, manual pump, 128 frames at 48 kHz, one
  core = 2,667 µs a block), the owner's saved rig — gate → `namamp`
  precision-drive (a feather WaveNet) → `namamp` mesa-markiv (a standard
  WaveNet) → `cab` → `eq` → `gain`: **~360 µs a block = 13.5 % of one core**,
  13.8 % with -70 dBFS hum in and 14.0 % with silence. Per module: the standard
  capture 9.5 %, the feather pedal capture 2.9 %, the cab 1.4 %, gate / eq /
  gain ≈ 0.3 % each. That matches the ~12–13 % the owner sampled on the ASIO
  driver's `TimeCritical` callback thread with nothing playing. It is the
  product doing what it was asked to — plug in and hear the amp anywhere — not
  a leak. The shipped first-run default (`DefaultTonePresetId`
  `nam-crunch-plexi`) is capture-backed too, so a fresh install pays roughly
  the one-capture share of that.
- **Do not "fix" it with a managed idle-park.** Parking a node from managed
  code (bypass) is a DRY passthrough, and waking it waits for a poll plus the
  10 ms fade, so the first pick after an idle stretch would be heard as a raw
  DI; `ToneSessionService` also persists bypass flags, so a parked rig can be
  saved as the player's board.
- **The native IDLE SLEEP is the fix (2026-09-24, JuceSharp; no ABI change,
  nothing managed to do).** On the device callback only, a seat's rig is
  skipped once what it puts on the device output has stayed under -90 dBFS for
  at least 5 s over a stationary input under -40 dBFS, and it wakes ON the
  block whose input rises sqrt(2) above the idle floor — that block is
  processed, so no attack is lost. Any edit, param write, input trim, monitor,
  input-EQ, input-VST or bridge change wakes it too; offline renders (the take
  re-amp, the tone designer) never sleep. Only module types on a measured
  allow-list sleep — the owner's whole rig qualifies (gate, namamp, cab, eq,
  gain); a delay, a reverb, a modulation pedal or a looper keeps its seat
  awake, which is the pre-sleep cost, not a fault. Design, thresholds and the
  proof that the sound does not change: `JuceSharp/docs/agents/dsp-tone-rig.md`
  § Idle sleep.
- **Measured after it**, same rig and block: the device callback idling on
  -70 dBFS hum costs **8-11 us (0.3-0.4 %) instead of 280-320 us**, and on
  digital silence 4-8 us — the engine's floor with no rig at all. The managed
  probe (paced null device, silence in, the engine's own callback timer) read
  285 us before and 7.8 us after. Playing is unchanged by the sleep.
- **NeuralAudio is compiled with `/arch:AVX2` since the same day**
  (`JUCESHARP_NEURAL_AVX2`, NeuralAudio + RTNeural only, the rest of the DLL
  stays SSE2): the rig while PLAYING drops **276 -> 176 us a block
  (10.3 % -> 6.6 %)**, mesa-markiv 188 -> 109 us, precision-drive 67 -> 41 us
  (offline best-of-3). **It makes an AVX2 CPU a requirement for loading
  `juce_sharp.dll`** (NeuralAudio's static registrations run at load): Intel
  Haswell 2013+, AMD Excavator 2015+ / every Zen; not the Atom-class Pentium /
  Celeron Silver line. `-DJUCESHARP_NEURAL_AVX2=OFF` builds the SSE2 DLL.

## Per-player rigs (ABI v28) — the full rules

- **The tone rig is PER PLAYER since ABI v28, and the detach rule now binds EVERY
  seat.** The native engine hosts `DspPlayers.MaxPlayers` independent rigs
  (own DSP chain, own VST3 input chain, own capture ring, own hardware input
  channel), all inside ONE open device — channel routing, never a second device
  open, because an ASIO driver is typically exclusive.
  `Audio/Dsp/IPlayerDspChains` (`PlayerDspChains`) owns one
  `JuceDspChainService` per seat and `AttachEngineToAll` fans the handle out;
  **`IDspChainService` stays registered in DI as `For(0)`**, so the ~500 existing
  call sites (pedalboard, RS in-game tones, tone presets) drive
  exactly the chain they always did. `IDspChainService.Player` reports the seat.
  Rules that fell out of building it:
  **(a) the detach-before-destroy rule above is now per seat.**
  `JuceAudioPlaybackService.AttachDspChainsToEngine` is the single choke point
  and every attach/detach site routes through it — a seat left pointing at a
  freed engine is the same use-after-free.
  **(b) node ids are ENGINE-global** (`DspPlayers.NodeIdStride`; seat `p` hands
  out `p * stride + local`), which is why only the CHAIN-scoped native ops are
  player-indexed. Seat 0's ids are unchanged, so nothing that ever persisted a
  node id changed meaning.
  **(c) the catalogue is loaded ONCE, on seat 0, and shared**
  (`JuceDspChainService.ShareCatalogFrom`) — it is static native data
  (`dca_dsp_module_*` takes no engine), and loading it per seat would walk the
  whole catalogue four times at startup for four identical answers. That was
  ~57 module types when this was written and is **100** since the JuceSharp 0.9
  gear wave (2026-09-12; 57 → 100 with NO ABI change, because every new module
  is a catalogue ROW and no export moved) — which makes the saving bigger, not
  smaller. Re-measure rather than carrying the number forward:
  `dca_dsp_module_count` is the answer.
  **(d) `IDspTonePresetService.ApplyToPlayer(p>0, …)` with no registry is a
  logged NO-OP, never a fall-back onto the single chain.** Applying there would
  be worse than doing nothing: it is how player 2's tone silently overwrites
  player 1's board, and how player 1's next persist then saves player 2's rig as
  if the player had built it. Pinned by
  `PerPlayerToneSessionTests.WithNoPerPlayerChains_ASecondSeatsToneIsANoOp_*`.
  **(e) persistence and restore are per seat** — `ToneSessionSet` builds one
  `ToneSessionService` per chain, and each keeps its OWN seat's slot in the
  active profile's rig store (`ProfileToneRigStore`; seat index as the key).
  A seat with no entry restores NOTHING rather than inheriting player 1's board,
  and the "an empty chain is never persisted" rule is enforced per seat — which
  is what stops player 2 never plugging in from wiping either board. (Through
  0.44 the seats lived in `AppConfig.ActiveToneRig` / `ActiveToneRigsByPlayer`;
  those two fields are migration input only now.)
  **(f) a VST slot declares its seat** (`VstChainEntry.Player`, default 0 and
  therefore back-compatible with every persisted `AppConfig.GuitarVstChain`).
  `ApplyGuitarVstChainCore` re-seats a moved slot UNLOCKED (the native
  attach-to-another-seat detaches first, and that detach is a wait-one-block
  barrier), and `_guitarSlotPlayer` mirrors the mapping managed-side because a
  BYPASSED slot is natively detached and would report seat -1 — the rehydration
  lists a device change and a sandbox mode switch build must carry a bypassed
  slot's seat too. **Every `new VstChainEntry` copy site must carry `Player`**;
  dropping it is the same shape of bug as a model field with no writer.
  **A slot's seat is AUTHORED since 0.58** — `VstChainSlotViewModel.SelectedSeat`
  behind a picker on both chain surfaces, fed the same `RigSeatOption` roster the
  Amp & FX rig's own seat picker gets. Until then the field was carried and never
  written, so every slot anyone added went on seat 0 and player 2 could not put a
  plugin on their own rig at all. A seat change re-pushes the WHOLE chain (that
  push is the reseat) and re-persists it; a roster merely ARRIVING must not.
  Rules in [../ui/pedalboard.md](../ui/pedalboard.md); pinned by
  `UI/VstChainSeatPickerTests` against the engine push and the persisted config.
  **(g) the out-of-process VST sandbox bridge remains SEAT 0 only** — the shared
  mapping carries one stereo stream per block, so a second seat needs a second
  child process. Seat 2's in-process rig is unaffected.
  **The DROP-TUNE pedal was the second half of this bullet until ABI v31 and is
  not any more.** It was one CSVD engine applied to the raw input ABOVE the
  per-player split, which made it unusable on a couch: dropping one guitar
  dropped both, and the seat that did not ask for it was detuned with nothing on
  screen saying so. Every seat owns a shifter now
  (`IPlayerInputRouting.SetPlayerInputPitchShift{,Mode}`, replayed across an
  engine rebuild like the channels and the trims) and the transport's pedal
  carries a seat picker over the same list the VST rows offer.
  Couch co-op consumed all of this (its rig applier and per-seat tone
  schedules, `RsTonePlaybackCoordinator.BeginCoopSeats`) until it was removed
  on 2026-09-19 — restore from tag `pre-cut-2026-09`. Seat 0 is the only seat
  that follows a chart's tone timeline now.

## Per-profile rigs — the outer axis

**The live rig is PER PLAYER PROFILE. `AppConfig.ActiveToneRig` and
`ActiveToneRigsByPlayer` are MIGRATION INPUT ONLY — do not add a reader.**
The board a player builds is their own settings, and one global rig meant two
people sharing a machine overwrote each other's pedals and knob positions.

- **Profile and seat are different axes, and this is how they compose.** A SEAT
  (ABI v28) is a native DSP chain — which guitar input is being processed. A
  PROFILE is whose settings those are, and the app has exactly one active profile
  at a time. So: **the active profile owns a rig PER SEAT**, keyed `"0"`…
  inside its own file. Switching profile brings that profile's whole set of seat
  rigs; it never renumbers, merges or shares seats, and every seat rule above is
  unchanged.
- **Where:** `Audio/Dsp/ProfileToneRigStore` → `tone-rig.json` in
  `profiles/<id>/`, the same mechanism `PracticeProfileService` and the
  replay/take stores use.
  Living inside the `profiles` tree means the existing directory rules already
  EXCLUDE it from LAN update packages and INCLUDE it in whole-install backups —
  no allow-list change — and deleting a profile takes its rigs with it.
- **The store PINS its scope; it does not resolve the active profile per access,
  and that is load-bearing.** `IPlayerProfileService.ActiveProfileChanged` is
  raised AFTER the switch, and the rig is persisted by a 5 s POLL (a knob turn
  raises no event — see above), so at the instant of a switch up to five seconds
  of the OUTGOING player's edits are still only in the live chain. A per-access
  store would already be pointing at the incoming profile, and no ordering could
  save it.
- **`ProfileToneRigCoordinator` owns the switch, and the order of its three steps
  IS the feature:** `FlushAll()` (store still scoped to the outgoing player) →
  `Rescope()` → `ReloadAll()`. Wrong order = a silent cross-profile leak.
- **Reload CLEARS a seat the incoming profile has nothing stored for.** On an
  engine attach "apply nothing" and "clear" are the same thing (the chain is
  already empty); on a profile switch they are not, and leaving the previous
  board up shows the wrong pedals *and* lets the next poll persist them here.
- **`ReloadForActiveProfile` waits for an engine** via
  `IDspChainService.HasEngine` — the same member v0.44.1's restore fix added, and
  the only one that answers "is an engine attached" as opposed to "does the chain
  have nodes".
- **The profile switch and the engine attach share ONE restore path
  (`Restore(bool clearWhenNothingStored)`), and therefore share the DEFERRAL
  LOOP.** That loop is v0.44.1's other half — a request landing while another
  restore runs is queued and re-run, never dropped. A deferred request must carry
  its OWN intent into the re-run (`_restorePendingClear`): re-running a deferred
  profile switch with the attach's flag leaves the outgoing player's board up and
  the next poll saves it as the incoming player's. Pinned by
  `ProfileToneRigTests.AProfileSwitchLandingMidRestore_IsDeferred_AndStillClears`,
  which drives the switch from inside the first `AddNode` of the attach restore.
- **The coordinator owns `ToneSessionSet.StartAll`, deliberately.** Nothing else
  starts the tone sessions, so a coordinator nobody resolves fails LOUDLY on the
  first launch (no rig restores at all) instead of invisibly until a machine has
  two profiles on it. `UI/ProfileToneRigReachabilityTests` lints that it is
  registered, resolved, `Start()`ed, that it is the only `StartAll` caller, that
  it flushes before it rescopes, and that no product code outside the migration
  touches the two legacy `AppConfig` fields.
- **Migration:** the pre-0.45 global rig is adopted into whichever profile is
  ACTIVE the first time the store resolves, then CLEARED (which is what makes it
  one-shot). Deliberately not the DEFAULT profile — where the pre-profile
  practice-store migration lands — because that rig demonstrably belonged to
  whoever was last playing; on a machine whose active profile is not the default,
  adopting into the default would greet that player with the factory tone. A
  degraded session (unreadable profile registry) never adopts. Behaviour pinned
  by `Audio/Dsp/ProfileToneRigTests`.
- **NOT profile-scoped, on purpose:** `DefaultTonePresetId` and
  `RestoreToneOnStartup` (app settings, not a rig), the `UserTonePresets` tone
  LIBRARY (a shareable catalogue, not a live board) and `AppConfig.GuitarVstChain`
  (the VST slot rows are a separate rig surface and still global).

### Rig control assignments

`RigControlAssignment` is managed-only, module-local metadata: parameter ID,
macro index (-1 or 0..3), finite endpoint values (reversed ranges allowed), and
paired MIDI channel/controller (-1/-1 or zero-based 0..15 / 0..127).
`RigControlAssignments` bounds each node to 64 distinct numeric parameter IDs;
`JuceDspChainService.SetControlAssignments` validates against its catalogue,
copies under its gate, then raises `NodesChanged` after releasing the gate.
No native/audio-thread work is added. Snapshots, user-tone DTOs, preparation,
normalization and applier reuse/re-add preserve mappings; omitted mappings clear
reused nodes. Invalid structural metadata imported in a user DTO is discarded
without discarding the tone. The application never persists native node IDs.

`PedalboardViewModel.Controls` owns one lazy `RigMacroBankViewModel` shared by
both rig screens. It rebinds to the selected seat and derives macro positions
from actual numeric values, with `HasMixedValues` when targets disagree. Macro
and MIDI writes clamp/snap and update visible parameters only after service
success. `Editor` edits a selected node's assignments; MIDI learn consumes the
first CC without moving a parameter. `LearningStarted` lets the UI MIDI bridge
discard messages queued before arming. The bank accepts zero-based channels and
7-bit CC/value integers on the UI thread and owns no MIDI device or audio pump.
`RigControlAssignmentTests` and the control-assignment case in
`JuceDspChainServiceTests` cover persistence, clearing, writes, learning, seat
isolation, rebuild and defensive copies.

### Optional native node meters

`IDspMeterSource` is implemented by `JuceDspChainService.Metering`: each visible
host acquires its own disposable lease, and only the last release disables
metering. Existing leases re-enable their seat after `AttachEngine`. Enable,
read, and release hold the same gate as engine attachment. `IsMeteringActive`
distinguishes unavailable/disabled from silence; `TryGetNodeMeter` rejects IDs
outside this chain and reports four real linear input/output peak/RMS values,
including valid amplitudes above one. These are node meters, not the song or
master output. No managed audio pump or native changes are involved.
`DspMeterSourceTests` covers independent leases, detach/rebuild, seat addressing,
stale IDs, unsupported interop and failed/nonfinite reads.

The pedalboard reads them in three places, all on `AmpFxRigView`'s 50 ms meter
clock (`AmpFxRigViewModel.PollMeters`) and each with its own lease while the view
is visible: `ModuleMeters` (the selected unit's editor), `OutputMeters` (the
footer's OUTPUT bar and the A/B level match) and `BoardMeters`
(`RigBoardMetersViewModel`, #299: every card's output peak, and the chain latency
summed from the cards' `LatencySamples` by `RigChainLatency` — serial units add,
a split costs its slowest lane, any unmeasured unit makes the total unknown).

**The node spectrum (#299) is OFFLINE, on purpose.** The engine reports no
per-node spectrum and the rule is no new native DSP, so `RigNodeSpectrumViewModel`
snapshots the chain, cuts it before and after the unit
(`Audio/Dsp/Spectrum/NodeSpectrumChain`; a unit inside a split is refused) and
renders `CalibrationPhrase` through each cut on `IOfflineToneRenderer` —
DotCharter passes the shared `IOfflineRigHost` (it IS one, `NullDeviceRigHost`);
RigLab has none and says so. `MagnitudeSpectrum` is managed analysis of that
render. A spectrum of the LIVE signal at a node needs a native per-node tap.
