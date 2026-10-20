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

- **`RigConfig`** holds the settings this assembly reads (audio device, logging,
  the tone library, TONE3000, the rig UI's zoom/favourites/tuner pitch).
  `AppConfig` (Services) DERIVES from it, so `appconfig.json` is unchanged.
  **Add a property to `RigConfig` only if code in Tone or Tone.UI reads it.**
- **`IRigConfigService`** (`LoadRigConfig` / `UpdateRigConfig`) is what this
  assembly reads through. `IConfigurationService` extends it with default
  members; DotCharter's DI registers the SAME instance under both — never a
  second config. RigLab implements it over `riglab.json`.
- **`IToneRigProfileScope`**: what `ProfileToneRigStore` needs from the profile
  registry. `IPlayerProfileService` extends it; RigLab uses the store's
  fixed-directory constructor.
- **`WritablePaths.UseAppFolderName`**: a non-DotCharter host renames the
  per-user folder before anything resolves a path (it throws afterwards).

## Rules

- **References: Core, Localization, JuceSharp (+ `.Dsp`, `.AudioIo`) only.**
  Never Services, never Analysis. `AssemblyLayeringTests` has the row, and
  `Tone_references_none_of_the_charting_stack` fails on ONNX Runtime, the stem
  separators, AlphaTab, SQLite, the Wwise tools, the AI SDK, Avalonia or SkiaSharp.
- `InternalsVisibleTo`: Services (the AI matcher reads `DcRigTargetCatalog`),
  RigLab (`CoalescedFileWriter`), Tests.
- Everything the audio/DSP guides say still applies — they are
  [../docs/agents/services/audio-engine.md](../docs/agents/services/audio-engine.md)
  and [../docs/agents/services/dsp-tone-rig.md](../docs/agents/services/dsp-tone-rig.md);
  only the file paths moved.
