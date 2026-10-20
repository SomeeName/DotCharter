# DotCharter.Services Agent Guide

Application service layer. DI registration happens in `DotCharter.UI` (composition root).

**This guide is a ROUTER.** It carries the layering rule, the folder map, the
threading contracts and a one-line index of the hard rules; the full text is in
[docs/agents/services/](../docs/agents/services/). **Read the routed file before
you touch that area**, and update it in the same change. Every bare `*.md` name
below is a file in that folder (plain links, not `@import`s, so they cost nothing
until opened).

## Layout + routing — one row per folder

**The tone rig and the audio host live in `DotCharter.Tone`**, under the same
namespaces: `Audio/{Abstractions,Dsp,Engine,Diagnostics,Equalizer,Midi,Vst,Tuner}`,
`JuceAudioPlaybackService`, `Logging/`, `Online/Tone3000/` and some storage/shell
helpers. The rows below route those too; the files are under `../DotCharter.Tone/`.
What lives HERE from those areas: `Audio/Dsp/ProfileToneRigCoordinator` and the tone-from-audio trio in
`Audio/Dsp/Tones/` (they need profiles and `DotCharter.Analysis`), `Audio/Tuning/LiveTuner`,
and `Audio/ChartSynth/` — which instrument the chart synth plays: the player profile's or a
playing song tone's override (#66, `audio-engine.md` § Chart synth).
Read [../DotCharter.Tone/AGENTS.md](../DotCharter.Tone/AGENTS.md) for the seams
(`RigConfig`, `IRigConfigService`, `IToneRigProfileScope`).

| Folder | What lives there | Read this first |
|---|---|---|
| `Audio/` — `JuceAudioPlaybackService`, `Abstractions/`, `Engine/`, `Equalizer/`, `Diagnostics/`, `SilentTransport/`, `Calibration/`, `Tuning/` | the `IAudioPlaybackService` implementation over JUCE (engine, master + stem sources, VST chain, MIDI, the 4 ms latency-compensated position timer), the six-band parametric EQ (ABI v29), audio-health/dropout logging, the silent transport that drives audio-less charts | `audio-engine.md` |
| `Audio/Dsp/` | the in-house tone rig: reorderable `DspNode`s over the native `dca_engine_dsp_*` ABI, RS in-game-tone conversion, factory/user preset catalogue, the per-player seats (ABI v28) | `dsp-tone-rig.md` |
| `Audio/LivePlay/`, `Audio/Tuner/` | the pure Live Play brain (`LivePlaySessionService`, `LiveNoteMatcher`, `SpeedLadder`, `DynamicDifficultyEngine`, `PracticeProfileService`, `LiveTuningDriftMonitor`, take recorder), the detection pump + `CoalescedUiChannel<T>`, the managed tuner DSP (MPM/YIN, strobe, guided tuning) | `live-play.md` |
| `Audio/Lyrics/` | lyric auto-sync: the ONNX CTC acoustic models, the model catalogue + installer, the orchestration onto the beat grid (the alignment MATH is `DotCharter.Analysis/Lyrics/`) | `lyric-autosync.md` |
| `Audio/StemSeparation/`, `Audio/Waveform/` | htdemucs separation (ONNX preferred, native demucs.cpp fallback), the never-regenerated `.dcstems` cache + Opus encoder, the regenerable waveform pyramid, the DirectML GPU choice (`Gpu/`), the component manager | `stem-separation.md` |
| `Audio/Vst/`, `Audio/Vst/Sandbox/`, `Audio/Midi/` | VST3 slot lifetime + editor integration on top of `JuceAudioPlugin`; out-of-process hosting (`juce_vst_host.exe` bridge); MIDI note scheduling for VSTi preview | lock rules in `audio-engine.md`; seats in `dsp-tone-rig.md` |
| `Editing/` | every chart edit: chord templates, hand shapes, FHP anchors, selection + match patterns, `ChartSpacingEnforcer`, refret, the phrase catalog, structure generation, and the highway/pen/target/loop façades. Also the editing coordinators — at the top of the folder `GlobalEditingCoordinator`, `EditingClipboardCoordinator`, the hand-shape generator/splitter, `TempoScaleCoordinator`, `HistoryStepGuard`, `DraftArrangementFactory`, `AutoBackupGate`; `Editing/Tracks/` (`TrackEditorContext`, `TrackContextRegistry`, `RibbonVisibility`, `EditorFocusArea`); `Editing/HandShapes/` (`ChordHandShapePlanner`: THE hand-shape grouping rule, shared by the tab importer, Generate hand shapes (arrangement) and the D11 auto-fix — #478/#479; `HandShapeSuggester`: the dashed suggested bars over single-note grips, accepted through the same actions — #491); `Editing/DraftMerge/` (Chart from audio's merge into the current arrangement, #647: `DraftMergeReview.Plan` cuts the draft by phrase and offers only EMPTY phrases unless the charter ticks Replace existing notes — the draft's note F1 is ~12 %; `DraftPhraseDiff` counts added/changed/removed; `DraftMergeWriter` writes one `CompositeAction` per verb, never touches a chord template, and refuses a phrase that was empty and has notes now; `DraftMergeReviewHost` holds the ONE review the merge card decides); `Editing/Maintenance/` (`ChartMaintenanceService` and the collaborators it constructs itself: `ChordConversionCommands` notes↔chords/flags/fingering/muted strums, `PowerChordFrets`, `FhpGenerationCommands`, `ProjectSilencePadder`, `TempoMapBeats`); `Editing/Preparation/` (`ExportFindingFixer`: the export findings' Fix, one undo step each — `editing-invariants.md` § dlc-builder-fixes, which also covers the chart's "Apply DLC Builder fixes"); `Editing/Quality/` (`ChartQualityReporter`: ONE verdict over the rules, the export gate, a passed-in audio lint and the difficulty estimate, merging the faults two checks share — #290; it runs checks, never copies them; `CheckEofRoundTrip` is the Editor on Fire round trip, outside the verdict and a separate call the report runs lazily, off the UI thread — #638, #685; the report runs `Build` off the UI thread too, on a `QualityReportSnapshot` copy rebound to the live chart by position — #690 — taken after the busy row has painted, restored into the spare copy a landed check gave back, `QualityReportCopies` — #711 — skipping every arrangement whose revision (`Arrangement.GetRevision()`) has not moved since, #720); and `IChartEditorSurface`, the seam `ChartEditorViewModel` implements for them | `editing-invariants.md` |
| `Package/DcSong/` | the `.dcsong` song package (#705): `DcSongWriter` (Opus 192 kb/s or FLAC, preview, cover/banner, optional stems, the ticked arrangements, atomic write), `DcSongExporter` (the app's door: an off-thread probe for ffmpeg, the audio and the stems on disk, then the write; #707), `DcSongSizeEstimate`, `DcSongStemNames`, `DcSongProjectUnpacker` (open a package as a project in a NEW folder; #709), `Library/DcSongLibrarySource` (the Library and Play Mode read, index and play packages: the scan's rows, the audio and stems into the bounded `LibraryAudioCacheStore`, the no-ffmpeg block; #708) and `DcSongPackage`, the reader for UNTRUSTED packages (bounded `ZipCentralDirectory` reads, per-member SHA-256, seekable stored audio) | `dcsong-format.md` |
| `Library/` | the NEUTRAL song library (#886): `LibraryService` (walk, SQLite index `Index/`, parallel parse, dispatch) over every registered `ILibraryPackageSource` (the plugin's package source, `DcSongLibrarySource`; only `App.*.cs` names one — `LibrarySeamLintTests`), the row types (`LibrarySongEntry`, …), `LibraryDedupe`, `LibraryCoverCache`, the bounded audio cache `Cache/`, convert-on-select `Prefetch/`; and `LibrarySongIdentity`: the ONE spelling of a library song's identity (`lib:<SongKey>`), the read-compat for the `psarc:` one earlier builds saved, and the key migration every identity-keyed store runs (#884) | `parsing-psarc.md` |
| `Parsing/` | EOF read AND write, `.rscp` (the project format), RS XML, tone slots + authored tone definitions, the pure LRC/UltraStar codecs | `parsing-psarc.md` |
| `Parsing/GuitarPro/`, `Import/` | the tab importers (gpif, GP3-5 binary) and the post-pass they share — graces, swing, slides, hand shapes, FHP anchors — plus File ▸ Import ▸ Audio from YouTube (yt-dlp), audio metadata and silence baking | `import-tabs-audio.md` |
| `Export/` | Rocksmith package build (SNG/manifest/DDS/bnk/wem), DLC Builder's build steps on every export's copy (`Export/DynamicDifficulty/`: its fixes, its dynamic-difficulty generator and ML level-count model, ported from Rocksmith2014.NET), GuitarPro `.gp`, the save companions (`.eof` / `.rs2dlc` / `.psarc`), and the export-BOUNDARY fidelity rules. `Export/Sheet/` is the sheet music (alphaTex, the AlphaSkia renderer, the paged PDF) the UI's `SheetMusicView` draws | `export-companions.md`; sheet rules in `../ui/sheet-coordinators-theming.md` |
| `Session/` | `ProjectSessionService` (+ `IProjectSessionService`): open / save / new / recent projects, audio attach, the startup reload; it builds `ProjectSaver` (the save pipeline, over `IProjectSaveHost`), `ProjectAutoBackup` (timed zip backups), `SilentTransportSession` (the audio-less fallback), `DetachedCompanionBuilder` and `DcSongProjectOpener` (File ▸ Open of a `.dcsong`, #709). Reaches the editor and transport through `IChartEditorSurface` / `Audio/Coordination/IEditorTransport`, the UI thread through `IUiDispatcher`, prompts through `Diagnostics/IMessagePrompt` | `../ui/project-lifecycle.md` |
| `Audio/Coordination/` | the playback coordinators: `AudioPlaybackCoordinator` (click/clap scheduling, the editor loop, the 250 Hz → ≤ 60 Hz playhead publish), `RsTonePlaybackCoordinator` (+ `IGameplayToneHud`), `ChartTonePreviewController` (+ `IGuitarMonitorMixer`, and `EditorRocksmithTonePreview`, its Rocksmith target via tonelab), `StemWaveformCoordinator`, and `IEditorTransport`, the seam `TransportViewModel` implements | `audio-engine.md`; the tone-rig owner rule in `../ui/sheet-coordinators-theming.md` |
| `State/`, `Diagnostics/` | observable `ProjectState`, dirty tracking, `Checkpoints/`; the redacted diagnostics bundle, `LogTailReader`, `UsageStatistics` | `state-diagnostics.md` |
| `Gameplay/` | profiles, scoring, per-song stats, local leaderboards, crowd meter, the count-in plans (couch co-op, LAN multiplayer and the practice history were removed; tag `pre-cut-2026-09` holds them) | `gameplay.md` |
| `Online/` | The TONE3000 capture browser + NAM capture library, the MusicBrainz lookup (New project's Latin-name suggestion), and the shared HTTP/JSON helpers every client here uses (`OnlineJson` over `Infrastructure/Json/JsonFields` in `DotCharter.Tone`) | `online-tone3000-nam.md` |
| `Ai/` | AI tone design: the DPAPI key store, the Claude and OpenAI-compatible providers, the catalogue-grounded prompt, the reply validator and `ToneDesignService` | `ai.md` |
| `Ai/Match/` | Match a tone from audio: the contract (`MatchContracts.cs`), the rig sampler + dataset tool (`Match/RigSampler*.cs`, `tools/rig-dataset`), the ONNX rig estimator (`Match/Model/`, trainer in `tools/rig-model`), similarity/renderer/search lanes, and the per-section lane (`Match/Sections/**`: one rig per part of a song) | `ai.md` § Match from audio, `ai-match-model.md`, `ai-match-search.md`, `ai-match-sections.md`, `../testing/rig-dataset.md`, `../testing/audio-eval.md` |
| `Ai/Generate/` | Generate ROCKSMITH tones from placed tone changes + stems (W6): the measure-lead window planner, the named DSP window features, the catalogue-grounded prompt + strict reply reader, the closed-catalog gate, and the writer into the project's own `ToneDefinition`s | `ai-tone-generate.md` |
| `Ai/Chart/` | The AI chart pass (#497): FHP + chord fingering together, FHP only (free fingerings follow via `ChordFingeringFollower`), fingering only (anchors fixed, E04 rejects), or chord names in the EOF or musical style, on any arrangement, over the selection or the whole arrangement — phrase chunks with context, a strict echo-checked reply (reject, never repair), the inspector's own rules on a scratch copy, one retry with the rejection, the proposals HELD for review and the accepted sections written as one undo step through `FindIdenticalTemplate` (shared templates never edited; #506); and the AI chat's sections & phrases job (`AiStructure*`, #561: one request, a closed add/rename/move/remove set on the beat grid, no new chart-quality-report finding) | `ai-chart-pass.md`, `ai-chat-instructions.md` |
| `Ai/Chat/` | The AI chat history (#562): conversations saved per chart under `<project folder>/ai-chats/<ChartProject.ProjectId>/`, listed, loaded, renamed and deleted for the OPEN chart only — another chart's conversation is refused (`ChartMismatch`), never loaded or overwritten; and the chat's chart actions (#560): `IAiChatChartActions` runs an instruction through `AiChartPassRun` on an `AiChatScope` as HELD FHP / fingering or, with the pane's Task picker on "Sections & phrases", section and phrase proposals (#561), or on "Ask / explain" a TEXT answer from the scope and its quality-report findings, nothing held or written (#563, `AiExplain*` in `Ai/Chart/`), or on "Tones" Rocksmith tone bodies for the scope's tone definitions through the W6 generator, held per tone and applied as one undo step (#614, `Ai/Generate/AiTonePass`, `Ai/Chart/AiToneReview`). Both are registered in `App.AiServices.cs` and driven by the chat pane (#559) | `ai-chat-history.md`, `ai-chat-instructions.md` |
| `Ai/Match/Prior/` | Match by SECTION, prior lane: the language model's guess at a section's rig (`ToneDesignPriorSource`), the narrator that puts a matched rig back into words (`ToneNarrator`), and the one rig→text renderer both use (`DcRigPresetDescriber`) | `ai-match-prior.md` |
| `Infrastructure/`, `Update/`, `Logging/` | `AppConfig`, `WritablePaths`, recovery (`Recovery/CrashRecoveryService` beside the snapshot store), crash reports, whole-install backup, library doctor, GitHub auto-update, the log severity/category filter; the interfaces the UI reaches the OS through — `Shell/` (`IBundledDocuments`; `IShellLauncher` and `ISystemClipboard` live in `DotCharter.Tone/Infrastructure/Shell/` under the same namespace, so the rig can reach them), `Input/` (`ScreenKey`, `IKeyGestureSource`), `Storage/` (`IFileStorageService`, `IExportTargetWriter`, `IArtworkSource`) — with their adapters in `DotCharter.UI/Platform/` | `infrastructure-update.md` |

**Task routing** (the same answer, keyed by what you are doing):

| Doing this | Read this |
|---|---|
| adding a field the project must survive a save with | `parsing-psarc.md` (write BOTH halves) |
| touching any writer/exporter, or a chart that "exports wrong" | `export-companions.md` |
| any edit to chords, shapes, anchors, selection or undo | `editing-invariants.md` + [../docs/agents/ui/editor-input.md](../docs/agents/ui/editor-input.md) for the verb semantics |
| a tab import that lost or misplaced something | `import-tabs-audio.md` |
| adding a DSP module, param, branch tag or tone preset | `dsp-tone-rig.md` |
| anything that takes `_gate`, changes the device, the sample rate or rebuilds the engine | `audio-engine.md` |
| removing the YouTube audio download from a build, or adding a feature that must be removable | `contested-features.md` (`-p:DotCharterContestedFeatures=false`, `ContestedFeatures.All`) |
| anything that READS a game package or the game install, or a plugin module (`Plugins/`: `IPluginModule`, `PluginLoader`, `IPackageFeatures`, the seams a plugin fills: `IGameInstallReader`, `IPackageToneImporter`) | `contested-features.md` § Optional plugins and package features — every reader entry point asks `IPackageFeatures` and is ABSENT when it says no; `-p:DotCharterPlugins=false` builds without `plugins/` |
| opening a socket or an inbound listener | the port range in the root `AGENTS.md` (`Services/Online/InboundListenerPortTests`) |
| writing a new file under `WritablePaths.ConfigRoot` | `infrastructure-update.md` (payload receipt filter + backup allowlist) |
| judging, scoring or recording a run | `gameplay.md` + `live-play.md` |
| adding an AI provider, or anything the tone designer says about the module catalogue | `ai.md` |
| feeding the tone matcher a model's guess, or narrating a matched rig | `ai-match-prior.md` |

## Dependency direction

`Core` → { `Analysis`, `Tone` } → `Services` → `Presentation` → `UI`. `Services` may
reference `Core`, `Localization`, `Analysis` (the audio-lint wrapper) and `Tone`
(the rig and the audio host); none of them may reference it back. It does not
reference `Rendering` — do not add it without a consumer. **The direction is a
TEST, not a convention** — `DotCharter.Tests/Infrastructure/AssemblyLayeringTests.cs`
carries each project's exact allowed set, so an upward leak fails the default
gate instead of compiling. Declare a dependency you use: a reference that
arrives transitively breaks the moment the middle project's own references
change.

## Threading contracts (these bite from anywhere)

- **`PositionChanged` fires on a thread-pool timer thread (a `System.Threading.Timer`; it allocates nothing per tick) at up to 250 Hz.**
  Anything UI-visible must coalesce and go through the UI dispatcher —
  `IUiDispatcher` from Services, `Dispatcher.UIThread.Post` in the UI (see
  `Audio/Coordination/AudioPlaybackCoordinator`, which posts one coalesced
  publish at a time with `IUiDispatcher.PostNormal`).
  `ProjectState.CurrentTimeMs` has an allocation-free hot-path setter — keep it
  that way.
- **`PositionChanged`/`CurrentTimeMs` are LATENCY-COMPENSATED: they report what
  the user is HEARING, so never schedule audio against them.** Anything that
  must land on a point of the SONG (metronome, claps, count-in, the editor's
  loop wrap) aims at `RawPlayheadMs` / `TryScheduleClick`.
- **No managed audio pump on the hot path, ever.** All real-time audio lives in
  the native engine; the managed wrapper stays marshalling + lifetime only. New
  audio features go native-first (JuceSharp ABI) with the service as a thin
  coordinator.
- **Never block the UI thread on an apply gate.** `_gate` (engine/source/plugin
  mutation), `_vstGate` (VST load/unload) and `_engineConfigGate` are held off
  the UI thread and never across a media open, a `Reconfigure` or a P/Invoke
  that waits a block — six UI-thread members take `_gate`, and Play/Pause/Stop/
  SeekTo all reach it. Read state + detach under the lock, do the slow thing
  UNLOCKED, publish under the lock. Linted by `PlaybackGateHoldGuardTests`.
- **Detach the DSP tone rig before destroying an engine**, for every seat
  (`JuceAudioPlaybackService.AttachDspChainsToEngine(null)`) — the rig has its
  OWN gate, unrelated to `_gate`. JuceSharp's `JuceDspChain` pins the handle
  for an in-flight call, but a call that has not started yet pins nothing. **The app binds no part of the audio ABI itself** — linted by
  `Services/Audio/DspNativeBindingTests`; add a missing export to the submodule.
- Dispose order in `JuceAudioPlaybackService.Dispose` is deliberate; don't
  reorder.

Full text, the measured numbers and the lints: `audio-engine.md`
and `dsp-tone-rig.md`.

## Hard-rule index — one line each, full text one file away

Each rule has cost a shipped bug. If your change is near one, open the file it
points at (all under `docs/agents/services/`).

- **A progress line the user reads is a resx string, never a literal** — every
  `status?.Report(` / `progress?.Report(` in this project; a count goes through
  `Plural`. Log lines and run reports stay English.
  → `ServiceProgressLocalizationTests` (Tests/Localization)
- **`ChartProjectCloner` is the single snapshot source of truth** (save, crash
  recovery, checkpoints); its one sanctioned sibling is `ChartProjectRestorer`.
  → `state-diagnostics.md`
- **A `ChordTemplate` is SHARED — never mutate one in place.** Resolve-or-create
  by shape and repoint `TemplateId`; the find-or-create KEY is FRETS within one
  ARPEGGIO flavour; `0` is a real template id and `-1` is the sentinel; an
  unused string is ABSENT from `Frets`, never `-1`.
  → `editing-invariants.md`
- **Two flags spell ARPEGGIO** (`Arpeggio`, `ForceArpeggioInRs`) and each import
  path sets only one — ask about both.
  → `editing-invariants.md`
- **A GESTURE is one undo node**, and an action opts in by naming the VERB.
  → `editing-invariants.md`
- **A mutate action must SEE exactly what it can TRANSFER** — `HasEffect` IS the
  `ChartActionStateComparer` entry, so a field the comparer forgets turns an edit
  that touches only that field into a dropped action with no undo step.
  Reflection-pinned per property by `ChartActionFieldCoverageTests`.
  → `editing-invariants.md`
- **A chord, the hand shape over it and the anchor under it are ONE authored
  decision** (`Editing/Selection/SoundCompanions`), and a `ChartLevel`'s anchors
  are NOT sorted mid-session — read them through `ArrangementValidationContext`.
  → `editing-invariants.md`
- **`ChartSpacingEnforcer` runs on every save AND every committed highway edit**,
  it only ever SHORTENS, and whatever it changes on a NEIGHBOUR must be
  undoable (`SpacingCollateral`).
  → `editing-invariants.md`
- **Capo: frets are ABSOLUTE, fret `0` is the capo, and every fret consumer folds
  `Arrangement.Capo` in**; the lowest legal FHP anchor is `capo + 1`. Guitar Pro
  is the RELATIVE format at both ends — convert at the parse point, invert at
  the write.
  → `editing-invariants.md` ·
  `import-tabs-audio.md` ·
  `export-companions.md`
- **The `.rscp` IS the project, so a model field with no writer is silently
  DELETED on every save** — write parser AND writer in the same change, under
  the same condition, and pin it with a field-level assertion.
  → `parsing-psarc.md`
- **An export transform must never invent an error the editor never showed**, and
  a linked chord is legal to AUTHOR — the landing is fixed at the export
  boundary (`LinkLandingBreakup`), never in the chart and never by auto-fix.
  **BOTH export paths consume it** — the RS-XML writer and `SngFromDomainMapper`,
  because the SNG is what the game reads.
  → `export-companions.md`
- **A repeated chord ships with NO `<chordNote>` children — `highDensity="1"`
  does nothing at XML version 8.** The game reads `NoteMask.ChordPanel` off
  `chord.HasChordNotes`; DLC Builder only translates the attribute when
  `xml.Version < 8`, and we write 8. One shared decision
  (`Export/Rocksmith/RsChordDensity`, EOF's `eof_note_has_high_chord_density`)
  drives BOTH writers, and the reader rebuilds the notes from the template.
  The editor's 2D/3D repeat box reads that same decision as data
  (`Export/Rocksmith/RepeatChords` → `ChordFlags.Repeat`), never a copy.
  → `export-companions.md`
- **The `.rs2dlc` and the `.eof` are somebody ELSE'S project file, so a save may
  only overwrite the one it wrote itself** (`Export/CompanionOwnership`) — we
  write the tone fields EMPTY, so overwriting theirs blanks tones assigned in DLC
  Builder. An absent or
  unreadable stamp means DON'T write — a stale derived file is deletable, lost
  hand-authored data is not. The bundle and the `.psarc` stay unconditional.
  → `export-companions.md`
- **RS-XML's `chordId` is a positional INDEX into `<chordTemplates>`, not a
  template id** — `RsXmlSongReader` assigns `Id = id++` while walking the list,
  and ids are contiguous only by coincidence. Both the `<chord>` and the
  `<handShape>` sites resolve through `Export/Rocksmith/RsChordTemplateExport`,
  the ONE table both writers build their list from (the SNG's
  `SngFromDomainMapper.BuildChordTemplates` too), unresolvable cases included
  (synthesize / drop / `-1`, never `0`), and a template only split chords play
  left out (#621).
  → `export-companions.md`
- **Every writer that emits a TUNING comes from `TuningOffsetResolution`** —
  the rule is "writes a tuning", not "lives under `Export/`" (`EofExporter` and
  `RscpWriter` included); and no
  exporter reads `Arrangement.Properties` — it resolves through
  `ArrangementPropertiesDeriver.Derive`.
  → `export-companions.md`
- **`songLength` is `min(chart length, audio length) - 1 ms`, and the audio
  length has to be PASSED IN** — never resolved from ambient state.
  → `export-companions.md`
- **The decoded-PSARC-audio cache is BOUNDED and `LibraryAudioCacheStore` is its
  only owner** — orphans (an archive updated/moved/deleted: its last-write ticks
  are IN the folder name) reclaimed first, then LRU down to 8 GiB. Automatic
  eviction is opt-in and ONLY the composition root arms it, because
  the plugin's package source's fallback store points at the user's real cache.
  Contrast the stem cache below — that one may never auto-evict. A song's
  `<id>.wav` is converted ONCE to the device rate IN PLACE (#435,
  `LibraryDeviceRateAudio`): still one file per song, re-derived from its `.wem`
  when the device rate changes or the file is damaged.
  → `parsing-psarc.md`
- **The stem cache is NEVER regenerated** (one durable `.dcstems` per song, keyed
  by identity + audio content signature — a library song's source `.wem`, never
  its decoded WAV (`StemCacheSource`, #426) — check before separating, save after),
  and **Opus stems are playback-only**, never fed to live note detection —
  linted by `LiveDetectionAudioSourceTests` over the whole `Audio/LivePlay/`
  folder.
  → `stem-separation.md`
- **A NAM capture's file EXTENSION is load-bearing** — resolve it through
  `NamCaptureIndex.ExtensionFor`; never write `.bin`. Since W8 the same library
  is also the AI designer's CLOSED capture catalogue
  (`Ai/NamCaptureCatalog`), so a capture's file name is prompt-visible and
  `ModelId < 0` is what marks a bundled one.
  → `online-tone3000-nam.md` · `ai.md`
- **Config saves are COALESCED and both exit paths flush.** `SaveAppConfig`
  updates the cache synchronously but only ARMS a debounced background write
  (`Infrastructure/Configuration/CoalescedFileWriter.cs`, 250 ms, last write
  wins) so the atomic write + fsync never runs on the UI thread per setter.
  `App.TerminateNow` and `App.RunCleanShutdown` both `Flush()`; so must
  anything that reads `appconfig.json` FROM DISK, and `InvalidateCache()`
  DISCARDS the pending write so a backup import is not clobbered.
  → `infrastructure-update.md`
- **A degraded READ is never authoritative, and never a licence to DELETE.**
  Every JSON store tells three outcomes apart: ABSENT starts fresh, CORRUPT
  (`JsonException`) is quarantined aside as `<name>.corrupt-<stamp>.json`, and
  UNREADABLE (an `IOException`, a sharing violation, an AV/cloud-sync hold) is a
  file presumed INTACT — the empty stand-in is served but never cached, and every
  save is a no-op until a read succeeds. A bare `catch (Exception)` that caches an
  empty store collapses all three into the worst one, and every caller is
  read-modify-write. Anything that deletes on the strength of what a store does
  not say asks `IPracticeProfileService.IsStoreAuthoritative` first.
  → `infrastructure-update.md` · `gameplay.md`
- **A backup restore is atomic per FILE and per SET** — the promote journals the
  previous bytes of every destination and rolls back on any failure, and the
  cache invalidations run in a `finally`, because a part-way import still
  replaced files on disk.
  → `infrastructure-update.md`
- **A `CompositeAction` is all-or-nothing, and an apply that THREW marks the
  project dirty** — the undo node is pushed only after `Execute` returns, and
  dirty is derived from the node id. `IHistoryService.ActionFailed` is the
  seam; `ProjectDirtyTracker.DirtyMarked` is the one crash recovery listens to.
  → `state-diagnostics.md`
- **A new project's folder goes through `Infrastructure/Storage/NewProjectFolder`**,
  never `Directory.CreateDirectory` (which is a no-op on an existing folder and
  destroyed the earlier import).
  → `infrastructure-update.md`
- **An untrusted zip is expanded ONLY through
  `Infrastructure/Storage/BoundedZipExtractor`** — five paths take archives from
  outside.
  → `infrastructure-update.md`
- **The self-updater must keep a CONSOLE and stay PIPELINE-FREE, its wait must be
  BOUNDED, and a failed launch must never close the app.** `DETACHED_PROCESS`
  deadlocks `cmd.exe` on its first pipeline. A staged payload is resumable
  (`GetPendingUpdateApply` / `ResumePendingUpdateApply`) and every phase is
  written to `<install>\dotcharter-update.log`.
  → `infrastructure-update.md`
- **`PayloadReceiptFilter.IsLocalState` is a data-loss footgun**: on a portable
  install `ConfigRoot` IS the install dir, so anything it fails to refuse is
  overwritten by the next update payload. It DERIVES from `ConfigBackupRules`
  rather than restating it — keep the derivation.
  → `infrastructure-update.md`
- **Network listeners must stay inside ports 47810–47829.** The TONE3000 OAuth
  loopback catcher is the only inbound listener; a new one takes a fixed
  in-range port and passes `Services/Online/InboundListenerPortTests`.
  → root `AGENTS.md`
- **The engine is built LAZILY, so "the chain is empty" never means "an engine is
  attached" — ask `IDspChainService.HasEngine`.** Before the engine exists every
  `AddNode` returns −1 without a native call, and the restore's re-entrancy guard
  DEFERS a concurrent `EngineAttached` rather than dropping it.
  → `dsp-tone-rig.md`
- **A new service gets a lint that it is REACHED, not that it is registered** —
  and a service the app resolves but never DRIVES is the same fault one level in.
  → root `AGENTS.md`
- **Accuracy comes from `Gameplay/Scoring/AccuracyMath` and `0/0` is 0, never
  1** (which renders as 100% for a player who has done nothing). Gate a readout on
  `IsMeasured`; never add a private zero convention (source-linted by
  `Services/Gameplay/AccuracyMathTests`).
  → `gameplay.md`
- **The live tone rig is PER PLAYER PROFILE, and `AppConfig.ActiveToneRig`
  / `ActiveToneRigsByPlayer` are migration input only** — do not add a reader.
  Profile and SEAT are different axes: a profile owns a rig per seat, in
  `tone-rig.json` under `profiles/<id>/`. The store PINS its scope (a per-access
  store leaks the outgoing player's last 5 s into the incoming one), and
  `ProfileToneRigCoordinator`'s flush → rescope → reload order is the feature.
  → `dsp-tone-rig.md`
- **A match PERCENTAGE needs TWO measured anchors, and one of them is a render.**
  `Similarity/MatchScale` scores `1 - distance / referenceDistance`, where the 0 %
  end is an information-free `overdrive -> amp -> cab` rendered over the caller's
  own DI on the same run. The target's SELF-distance is a reported CAVEAT, never
  the scale (a one-anchor scale saturates at 100 % on real material). **A view model
  may not derive a score from a distance**, a headline percentage always ships with
  its per-block breakdown, and a run that cannot justify a number reports UNRATED —
  a third state, never 0.
  → `ai-match-search.md`

- **A GENERATED Rocksmith tone is REJECTED, never repaired.** Rocksmith's gear is
  a closed list shipped with the game, so an invented gear key or an out-of-range
  knob is a tone the game refuses — not one that sounds slightly wrong.
  `Ai/Generate/RsToneCatalogValidation` gates every write and every reply;
  `RsToneAuthoring.ClampKnobValue` is the tone EDITOR's helper and is banned in
  that folder (a model answering `Gain 250` on a 0–100 knob misread the unit, it
  did not mean 100). Source-linted by `RsToneWriterLintTests`.
  → `ai-tone-generate.md`

- **The 48 kHz-for-neural-amp request must stay
  `ApplyEngineConfig(cfg, allowReplaceFallback: false)`** — the default fallback
  rebuilds the whole engine on a 44.1 kHz-pinned device.
  → `audio-engine.md`

## Validation

```powershell
dotnet build DotCharter.sln --nologo -v:m
dotnet test --project DotCharter.Tests\DotCharter.Tests.csproj --no-build --filter-not-class "*UiPerfSmokeTests"
```

The `UiPerfSmokeTests` filter is a PROCESS split, not a hang: the headless-UI
suite is a second `dotnet test` run with `~UiPerfSmokeTests` (needed after view
changes, rarely relevant to this project). Sharing one testhost fails almost
every headless case on `Avalonia.Headless`'s process-global dispatcher — see
`DotCharter.Tests/AGENTS.md` § Running.

Kill stray `testhost.exe` / `DotCharter.Tests.exe` / `JuceSharp.Tests.exe` before
rebuilding — a hung testhost holds the output DLLs and, if it created an audio
engine, the ASIO device exclusively. Native rebuild instructions and the ABI
rules are in the root `AGENTS.md`.

## Keeping this current

**When you change a subsystem in a way that would make its guide misleading — a
new/removed subfolder, a changed invariant, a new gotcha, a moved entry point —
update the routed `docs/agents/services/*.md` file in the same change**, and add
a row here only if a whole FOLDER appeared or moved. Terse, accurate bullets
with real file/type names beat prose. This file is auto-loaded for every agent
in the project: anything that is not routing or a cross-cutting contract belongs
in the topic file, not here.
