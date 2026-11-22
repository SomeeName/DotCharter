# DotCharter.Tone Agent Guide

The tone rig and the audio host, split out of `DotCharter.Services` (2026-09-26)
so the standalone rig (`DotCharter.RigLab`) can ship without the charting stack.
**Namespaces are `DotCharter.Tone.*` and follow the folders**
(`Audio/Dsp/Presets/` is `DotCharter.Tone.Audio.Dsp.Presets`), so a namespace
names its assembly: `DotCharter.Tone.Audio.Dsp` is THIS assembly,
`DotCharter.Services.Audio.StemSeparation` is Services.
`Infrastructure/NamespaceMatchesAssemblyTests` fails a type declared outside its
project's namespace. Services files that sit in a parallel namespace
(`DotCharter.Services.Audio.Dsp.Tones`, `…Audio.ChartSynth`) import this
assembly's half with a `using DotCharter.Tone.…;`.

## What lives here

| Folder | What |
|---|---|
| `Audio/` | `JuceAudioPlaybackService` (all partials), `NullAudioPlaybackService`, `Abstractions/` (the `IAudioPlaybackService` seams), `Engine/`, `Diagnostics/`, `Equalizer/`, `Midi/`, `Vst/` (+ `Sandbox/`), `Tuner/`, `Tuning/ArrangementTunerLogic` |
| `Audio/ChartSynth/` | the built-in chart synth's managed half (ABI v38): `IChartSynthEngine` (implemented by `JuceAudioPlaybackService.ChartSynth.cs`, which forwards to `ChartSynthEngineDriver` — the request, its push chain and its rebuild replay), `ChartNoteEventBuilder` (arrangement → `JuceChartNote[]`, pure), `ChartSynthFeeder` (one upload per chart change), `ChartSynthVoiceSelection` (built-in guitar vs bass per upload, pure) and `ChartSynthInstrumentSelection` (the instrument models' defaults, name parsing and the one-time move out of the legacy config fields, pure — #50/#66). WHICH instrument plays (the player profile's, or a song tone's override) is decided in DotCharter.Services (`Audio/ChartSynth/ChartSynthInstrumentCoordinator`); nothing here reads it from the config. See [../docs/agents/services/audio-engine.md](../docs/agents/services/audio-engine.md) § Chart synth |
| `Audio/Dsp/` | the in-house tone rig — chains, presets, `Loudness/` (the pure half of levelling converted tones by RENDERED loudness, #67: `IntegratedLoudness`, `CalibrationPhrase`, `ToneLoudnessKey`, `RsToneLoudnessPolicy`; the render and the coordinator's half are in Services, `Audio/Coordination/`), `RsToneConversion/` (what each Rocksmith gear key becomes is GENERATED in [../docs/agents/services/rs-tone-coverage.md](../docs/agents/services/rs-tone-coverage.md); measured against renders by `RsToneFidelityHarnessTests`; seven gears and six choruses map through the GAME'S OWN knob curves and every amp the game ships runs ITS OWN soundbank chain on the native `rsamp` module — all read from the PLAYER'S install at run time (`RsGameTables`, built and loaded by the optional plugin that reads the game install through Services' `IGameInstallReader`, #888; the repo holds NO game numbers, #755; without it the converter maps generically; `RsMbCompModel`, `RsGameTablesJson` and `IRsGameFileSource` stay here) — see dsp-tone-rig.md § "The game's own knob curves", "Rocksmith's own amps" and "Rocksmith's game tables"; `RsInputGate` is the input gate put in front of every song tone by the playback coordinator (#155); `RigToRsToneConverter` is the INVERSE — rig → closest catalogue-valid `RsTone`, built on `ConvertGear` rather than a second mapping, see [../docs/agents/ui/tone-designer.md](../docs/agents/ui/tone-designer.md) § From the tone rig), `Tones/` (RS tone JSON, gear catalogue), `Spectrum/` (the tone lab's offline node spectrum, #299: `NodeSpectrumChain`, `MagnitudeSpectrum`, the `IOfflineToneRenderer` seam), `Loudness/CompareLevelTrim` (the A/B level match's gain math) — minus the two song-bound pieces that stayed in Services (`Tones/ToneFromAudioBuilder`, `ToneGenerationService`, `ToneEvidence`: they need `DotCharter.Analysis`; `ProfileToneRigCoordinator`: it follows player profiles) |
| `Audio/Tonelab/` | the host side of the in-game Rocksmith tone preview: `TonelabPipeClient` (the `\\.\pipe\tonelab-<pid>` command pipe), `ITonelabSession`/`TonelabSession` (discovery, state, the commands), `TonelabTonePreview` (the in-game `ITonePreviewTarget`: validate, debounce, `apply-file` or `tone knob`, `tone restore`), `TonelabToneFiles`, `TonelabToneDiff`, `TonelabDebouncer`, and the launcher: `IGameToneLauncher` / `TonelabGameLauncher` (start or attach, the 32-bit `tonelab-launch.exe --inject <pid>` helper via `TonelabInjectHelper`, the quit sequence; registered only when the install carries `tools/tonelab/`). The default start is the mirror (`TonelabGameMirror` + `Win32GameMirrorFileSystem`: hardlinks and junctions in `%LOCALAPPDATA%\tonelab\game`, never a write through a link) and `MirrorRocksmithStarter` (`CreateProcessW`, hidden, Steam's app id, refused without Steam); Steam + `RsAsioIniGuard` on the live folder is only the cross-volume fallback. The game side's source is `native/tonelab/`. The audio exchange is a GENERIC native insert behind `Audio/Abstractions/IExternalProcessInsert` (phase 2). The AI tone check renders through the game OFFLINE: `IGameToneRenderer` / `TonelabOfflineRenderer` over `TonelabAudioRing` (the host side of the exchange, a worker thread, never the audio callback), only under the session's `TryLeaseAudio` (refused while a live preview routes audio). See [../docs/agents/services/tonelab-bridge.md](../docs/agents/services/tonelab-bridge.md) |
| `Online/` | TONE3000 + the NAM capture library (`Tone3000/`), `OnlineHttp`, `OnlineJson`, `OnlineModels` |
| `Logging/` | `IAppLogger`, `AppLogger` |
| `Infrastructure/Configuration/` | `RigConfig`, `IRigConfigService`, `CoalescedFileWriter` |
| `Infrastructure/Storage/`, `Infrastructure/Shell/` | `AtomicFile`, `BestEffortDelete`, `WindowsPosixRename`, `WritablePaths`, `UserDataFolders` (the ONLY route to `%APPDATA%` / `%LOCALAPPDATA%`; `DOTCHARTER_DATA_ROOT` redirects both, #439), `InstallLocation`, `SafeFileName`, `IFileStorageService` + `FilePickerFilters`, `IExportTargetWriter`, `IShellLauncher`, `ISystemClipboard` (moved from Services 2026-09-28 for the AI designer's copy & paste route; one Avalonia adapter, `DotCharter.Tone.UI/Platform/AvaloniaSystemClipboard`) |
| `Diagnostics/StatusBarService.cs`, `Parsing/Psarc/PsarcToneDefinition.cs`, `Presentation/Shared/CoalescedPass.cs` | small types the rig needs whose siblings stayed behind (the package READER, an optional plugin now; the Presentation screens) |

## The seams that made the split possible

- Rig writes report success; a tone session advances its saved signature only
  after success. Unreadable rig stores are retried without caching defaults;
  corrupt JSON is moved aside before replacement. Queued presets carry the
  shared store's scope generation and cannot apply after a profile rescope.
- The AI secret store refuses writes and removals after a failed read or
  decryption, preserving every provider's existing keys.

- **`RigConfig`** holds the settings this assembly reads (audio device, logging,
  the tone library, TONE3000, the rig UI's zoom/favourites/tuner pitch).
  `AppConfig` (Services) DERIVES from it, so `appconfig.json` is unchanged.
  **Add a property to `RigConfig` only if code in Tone or Tone.UI reads it.**
  Tone pickers remember their folder through `PickerFolders` (`StartFolder` /
  `RememberFileFolder`, the `Last*Dir` settings); detail in
  [infrastructure-update/config-and-json-stores.md](../docs/agents/services/infrastructure-update/config-and-json-stores.md).
- **`IRigConfigService`** (`LoadRigConfig` / `UpdateRigConfig`) is what this
  assembly reads through. `IConfigurationService` extends it with default
  members; DotCharter's DI registers the SAME instance under both — never a
  second config. RigLab implements it over `riglab.json`.
- **`IToneRigProfileScope`**: what `ProfileToneRigStore` needs from the profile
  registry. `IPlayerProfileService` extends it; RigLab uses the store's
  fixed-directory constructor.
- **`WritablePaths.UseAppFolderName`**: a non-DotCharter host renames the
  per-user folder before anything resolves a path (it throws afterwards).

Session-wide preview ownership and I/O ordering live in `Audio/Tonelab/TonelabPreviewOwnership`;
pending applies prevent restores, failed owners leave, and the last owner restores once.
`Audio/Tonelab/TonelabTonePreview` reserves its local restore turn before
accepting a new generation. A debounce firing during an old reply hands pending work
to the current generation after that restore (`TonelabTonePreviewTests`, #530).
Tests inject a session `TimeProvider` for pipe reply deadlines and await the launcher's
`ExitWatch` through INI restoration; see tonelab-bridge.md for the load-safe mock harness (#647).

Rebuild media publication is isolated in `Audio/Engine/MediaRebuildReplay`; VST
capture/merge/restore in `Audio/Vst/VstRebuildState`. Their
injected I/O and `Audio/ChartSynth/IChartSynthBackend` let `RebuildPublicationTests`
exercise the actual decision paths without a device. VST state capture calls the wrapper's `SaveState()` once. Native audio imports belong only in JuceSharp.
Keep the rebuild lock order
in [../docs/agents/services/audio-engine.md](../docs/agents/services/audio-engine.md).

`Audio/Engine/MediaRebuildRecovery` retains song and full stem-mix intent until
rebuild replay succeeds; current mixer edits merge into retained missing-stem intent.
Explicit stem clear discards that intent and invalidates a restoration in flight (a clear
generation checked before replay and before publication); logical media changes invalidate
obsolete recovery, but a take's hand-back keeps the song's stems (`_mediaLineage`).
`IRigConfigService.UpdateRigConfigAndWrite` performs the write before returning, so
`UserTonePresetStore.Upsert` answers null when a preset did not reach the disk and
`SaveUser(…, out written)` reports it; the preset browser uses `StageUser` (in memory now, the write on its worker) and posts the answer to its status line.
Guitar-chain workers reject older request generations after taking `_vstGate`.
`Audio/Vst/VstInputOrder` detaches all effects before attaching active slots in
requested order, including after bypass changes; plugin objects and state survive.

## Sandbox ownership

`Audio/JuceAudioPlaybackService.Sandbox.cs` owns four `VstSandboxSeat` contexts
under `Audio/Vst/Sandbox/`: one child per seat, started with its first plugin
and stopped after its last unload. The instrument belongs to seat 0 and counts
toward that child's lifetime. Each context owns its slots, desired state,
restart budget, heartbeat and state replay. Slot controls use the owner's
channel; moves await the old unload before the new load. A crash recovers only
that seat. Engine rebuilds re-arm every surviving child's own bridge and queue
prepare for all of them; the instrument's MIDI sink is rebound on seat 0.
Chain edits visit changed seats and retry missing desired slots except remembered
rejections or missing files (cleared when that entry changes), complete unrelated
edits before reporting aggregate failures, and hand captured state to a move's
destination before unloading its source. A source that dies during unload cannot
retain ownership. Health capture prunes state by desired ownership, preserving
copies awaiting a destination load or retry. Unchanged seats in backoff or after
giving up stay untouched. Pending restarts retain their host
identity; an explicit edit cancels obsolete work before replacing a dead host.
A failed rebuild re-arm kills and recovers only that seat. Mode switch off and
dispose request every child's quit before one shared 10-second bound, then kill
unfinished stops (`VstSandboxShutdown`, `IVstSandboxStopWait`). Host and
engine interfaces let `VstSandboxPerSeatTests` exercise the real orchestration
without launching a process.

## Rules

- `StemLoadRequest.OffsetMs` is applied once during worker-side opening by
  `Audio/StemPlaybackOffset`: positive offsets pad with silence, negative offsets
  trim. Zero keeps the direct file open. Rebuild snapshots retain the offset;
  no managed timing pump participates in playback.
- Untrusted file references use `Infrastructure/Storage/FileReferencePolicy`
  before any probe. NAM manifest targets require a bare filename; imported tone
  file params accept local absolute or confined library-relative paths.

- **References: Core, Localization, JuceSharp (+ `.Dsp`, `.AudioIo`) only.**
  Never Services, never Analysis. `AssemblyLayeringTests` has the row, and
  `Tone_references_none_of_the_charting_stack` fails on ONNX Runtime, the stem
  separators, AlphaTab, SQLite, the Wwise tools, the AI SDK, Avalonia or SkiaSharp.
- `InternalsVisibleTo`: Services (the AI matcher reads `DcRigTargetCatalog`),
  RigLab (`CoalescedFileWriter`), Tests.
- Portable preset file params are `modelPath`, `irPath`, `irPathB` and `irFile`;
  keep resolution, export rewriting and non-portable warnings in
  `DcRigPresetPreparation` using the same key set.
- Everything the audio/DSP guides say still applies — they are
  [../docs/agents/services/audio-engine.md](../docs/agents/services/audio-engine.md)
  and [../docs/agents/services/dsp-tone-rig.md](../docs/agents/services/dsp-tone-rig.md);
  only the file paths moved.

Preserving relocation of original files uses `AtomicFile.MoveAside(..., overwrite: false)`.
It never replaces a competing destination or sweeps the live source. The two-argument
quarantine overload retains replacement semantics; `PublishNew` owns and sweeps its stage.
