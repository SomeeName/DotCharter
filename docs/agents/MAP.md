# Agent navigation map

A symbol→file index for DotCharter. It exists so an agent can answer "where is X"
without reading 200–600 KB of source first. **Read this before grepping the tree.**

It is deliberately NOT auto-loaded — root `AGENTS.md` links here by name. Keep it
terse; if a section grows past a screen, move the detail into the relevant
`docs/agents/<area>/` file and leave a pointer.

Line numbers drift. Each entry carries a **grep anchor** — trust the anchor, use
the line number as a hint. Sizes below were measured **2026-10-11** and are
rounded on purpose: they tell you how expensive a Read is, nothing more. **Never
carry a number forward — re-measure** (`wc -c`, `grep -c '<data name='`). The
history of how a size got here is in `git log -p` on this file, not in the table.
`DocReferenceLintTests` fails the gate on a dead file path or a dead `*Tests` name
anywhere in `docs/agents/**`, and `SourceFileSizeBudgetTests` fails it when a
source file outgrows its budget; the prose still needs a human.

---

## Never Read these whole

A single `Read` of any of these costs more context than the entire task usually
needs. Grep them, or read a slice.

| File | Size | Instead |
|---|---|---|
| `DotCharter.Localization/Resources.resx` | ~605 KB | 5,003 keys. Grep for the key: `grep -n 'name="Menu_Export' Resources.resx`. Never Read. |
| `DotCharter.Localization/Resources.de-DE.resx` | ~635 KB | Same. |
| `DotCharter.Localization/Resources.ja-JP.resx` | ~695 KB | Same. All THREE files must stay in sync — grep all three, edit all three. ja-JP is the biggest on disk because kana and kanji cost three UTF-8 bytes each; size order is never the check, the check is that all three carry the same key NAMES. |
| `JuceSharp/.../dsp/cores/dcdsp_cab_irs.h` | 846 KB | Generated impulse-response float tables. **Zero logic.** Never open. |
| `JuceSharp/.../dsp/cores/dcdsp_acoustic_irs.h` | 98 KB | Same. |
| `DotCharter.UI/Views/PlayMode/PlayModeView.axaml` | ~14 KB (14,069 bytes, LF) — the SURFACE the Play Mode screens share: the backdrop, each screen declared under its gate, the pause/fail frozen frames, the rig overlay (`RigScreenRoot`, hosting `AmpFxRigView.axaml`) and the deferred hosts. Its style grammar is `Views/PlayMode/PlayModeStyles.axaml`; the screens' ControlThemes are `PlayModeThemes.axaml`, merged by each view that wears one | **Every screen is its own view in `Views/PlayMode/Screens/`** (#294): `LaunchScreenView` (`StartScreenRoot`), `SongSelectView` + `SongSelectDetailRailView`, `GetReadyView` (`TuningCheckScreenRoot`) + `GetReadyCentreView`, `CountdownView`, `PlayingView`, `PauseMenuView`, `FailedView`, `ResultsView` (owns the reveal), `GameplaySettingsView` + one `GameSettings*View` per section pane. Grep the `x:Name` across `Views/PlayMode/`, then read ±100 lines. A screen's names are in its view's namescope — `FindControl` on `PlayModeView` reaches none. **Five screens are `ContentTemplate` bodies under a `ContentControl` host** (`PlayModeView.DeferredScreenHostNames`: settings modal, Get Ready, Pause, Song Select, Results — #648), not constructed before the first frame; the code-behind reaches Song Select and Results through `PlayModeView.SongSelect` / `Results`, lookups that build the body on demand; the rig VIEW is one too (`RigScreenHost`, #194). Features removed 2026-09-19 (History, Trends, Progress hub, Statistics, Calendar, LAN Scoreboard) restore from tag `pre-cut-2026-09`. |
| `DotCharter.Tone.UI/Views/PlayMode/AmpFxRigView.axaml` | ~133 KiB / 1 622 lines (135 847 bytes) — the gear tile and the split region's lane are `RigGearTileTemplate.axaml` / `RigBoardLaneTemplate.axaml`; the rig's own style rules (descendant selectors, Path / Panel marks) are `AmpFxRigStyles.axaml`, `StyleInclude`d first in its Styles (#242). `RigTonesView`'s NAM capture rows are `RigNamRowTemplates.axaml` | Same. The Amp & FX rig, its OWN view since v0.43 so the chart editor's overlay can host it too. **Two top-level bodies share row 1 of `RigScreenRoot` and are mutually exclusive by `IsTonesMode`: `RigFlatBody` (the stage + gear bar in `RigWorkspace`, and the routing map — `RigSignalStrip` wraps its routing rail and `RigBoard`) and `RigTonesMode`, which is ONE `RigTonesView` element (`Views/PlayMode/RigTonesView.axaml`). Its names live in THAT file and its own name scope: `FindControl` from the rig view does not reach them. Grep the `x:Name` and know which file you are in.** |
| Any other `.axaml` view or style under DotCharter.UI, and the tone-rig HTML prototype outside the repo (E:\…\amp-rig-texture-pack\tone-rig\) | 5–160 KB each | **Never `cat` or Read one whole.** Grep the `x:Name`, the style class or the CSS selector first, then Read with offset/limit around the hit. |
| `DotCharter.Tone.UI/Views/PlayMode/RigModuleEditorView.axaml` | ~1 KB host (the family views are `Views/PlayMode/RigEditors/*.axaml`, picked by `RigEditorTemplateSelector`) | Selected module controls extracted from the rig screen. Root data type `RigDockUnit`; primary/secondary knob projections, model/file/capture/lyrics surfaces, cabinet slots, and hosted `RigPerformanceControlsView`, `RigControlAssignmentView`, `RigMidiControlsView` and `RigNodeMetersView` (which hosts `RigNodeSpectrumView` when its view model has a `Spectrum`). Source guards must follow their own data contexts. |
| `DotCharter.Tone/Audio/Dsp/Tones/tone_gear_data.json` | 147 KB | Gear catalogue snapshot, **on ONE line** — a slice read is useless. Query it in code. |
| `DotCharter.UI/NamCaptures/*.nam` | 64–805 KiB | 63 binary neural captures. Never open. |

## Split types: open the SIBLING, not the root

v0.44 split the nine most-opened types into partial files (seven then, two more since). **Every
partial opens with a one-line `//` header saying what it holds** — `head -1` the candidates,
or just `ls`, and open the ONE you need. Do not read the root to find your way.

**`Infrastructure/SplitTypePartialRatchetTests` is the source of the numbers.** Its
table lists EVERY product type split across three or more partials (the rows here
plus `ChartMaintenanceService`, `TransportViewModel`, `AmpFxRigViewModel`,
`RsTonePlaybackCoordinator`, `FrameSnapshot`, `ToneDesignViewModel`, `RigDockUnit`,
`RigSampler`, `JuceDspChainService`), each with its exact file set, a ceiling on the
class total and the reason it is still split. The set may only shrink, the ceiling
may only go down, every listed file must open with its `//` header, and a type that
reaches three partials without an entry fails the gate.

The "class total" column is the real size of the type — the summed size of the
files that declare it, CR bytes not counted, as that test measures it: partials
make a file small, they do not make a class small. If you are about to add to one
of these, that total is what you are adding to.

| Type | Root | Files | Class total (2026-10-11; ceiling in `SplitTypePartialRatchetTests`) | Where to look |
|---|---|---|---|---|
| `ViewModels/PlayMode/PlayModeViewModel` | 14 KB | 14 | 148 KiB (151 469 bytes) | `.HomeNavigation` (Home song loading) `.Keys` (the Get Ready screen's key presses) `.Flow` `.Gameplay` `.Screens` `.Tones` `.RunControl` `.Overlays` `.FrontDoor` `.Launch` `.GetReady` `.Pause`, `.Results` (entering Results + the run-exit verbs). **The results board and listen-back are their own view models**, `RunResultsViewModel` (`RunResults`: score, bests, sections, timing, leaderboard, filled by `Show(report)` over localization + leaderboards only) and `RunListenBackViewModel` (`ListenBack`: the borrowed-transport take player); `ResultsView` binds `RunResults.…` / `ListenBack.…`. **Song Select's list is its own view model**, `SongSelectViewModel` (33 KiB, one file), built by the root's constructor and exposed as `SongSelect`: the library scan, facets, filters, search, sort, chips and empty states; `SongSelectView` binds `{Binding SongSelect.…}`, while `SelectedSong` stays on the root (`.FrontDoor`). The types the screens bind to have their own files beside it: `PlaySongItem`, `PlayModeRows` (`SectionRow`, `MyTonePresetOption`, `ArrangementOption`), `PlayModeScreen`, `PausedRunFacts`, `CountInPip`. The History + Trends hub (`.Progress` `.Trends`) was removed 2026-09-19 — restore from tag `pre-cut-2026-09`. **Screen NAVIGATION lives in `.Screens`** (`OpenSongSelect` `Back` `ExitToEditor` `NavigateBack`; it was in `.Practice` until that partial was retired on 2026-09-19). Root keeps the ctor and **every injected dependency and plain private field**. Field rule: [ui/play-mode.md](ui/play-mode.md). **The Amp & FX rig overlay is its own view model**, `PlayModeToneRigViewModel` (16 KB), injected and exposed as `ToneRig` (`IsPedalboardOpen`, `Pedalboard`, `AmpFxRig`, `OpenPedalboardCommand`, the AI panel's host half, `PlaceToneSet`); bindings go through `ToneRig.`. **So is the settings modal** (#658), `PlayModeSettingsViewModel` (43 KiB, one file), a DI singleton exposed as `Settings`: the open flag and the open/close cycle, the AppConfig-backed values, the section rail, the search, the input meter (Get Ready's input row binds the same `Settings.GameplayInputLevelDb`), the latency chip, Devices, Keybinds and the per-section reset; the seven settings views bind `{Binding Settings.…}`. What it reads back from Play Mode — `_runSideEffectsApplied`, the practice speed, the two tone rows, the input tap — goes through `IPlayModeSettingsHost`, which Play Mode implements and attaches; its `OpenFullSettingsRequested` is forwarded as Play Mode's `OpenSettingsRequested`. Tests: `UI/ViewModels/PlayMode/PlayModeSettingsViewModelTests`. Nothing in Play Mode reads `App.Services` (`UI/PlayModeToneRigInjectionTests`, which globs both collaborators). |
| `DotCharter.Tone/Audio/JuceAudioPlaybackService` (moved from Services 2026-09-26) | 26 KB | 18 | 295 KiB (302 096 bytes, 2026-10-15) | `.EngineLifecycle` (⚠ `ReplaceEngineGated` — all rebuild/replay state lives here, keep it whole), `.PlayerRouting` (ABI v28 per-player state + its replay), `.EngineConfig` `.Transport` `.Media` `.Devices` `.Mixer` `.Playhead` `.Eq`, plus the older `.Sandbox` `.VstChain` `.Midi` `.Stems` `.DeviceWatchdog`, `.RigMidi` (generation-checked controller input), `.Reamp` (the DI recorder's capture read + the `IReampEngine` forwarders), `.ChartSynth` (the `IChartSynthEngine` forwarders, the availability event and the song clock). **Three clusters are collaborators now (#838)**, each owned by the root and handed the engine as a `() => _engine` getter, none taking a service gate: `Audio/Diagnostics/AudioHealthMonitor` (the dropout/heartbeat log thread), `Audio/ChartSynth/ChartSynthEngineDriver` (the chart synth's request, its push chain and its rebuild replay `ReplayTo`) and `Audio/Reamp/ReampTakes` (the per-seat take and `ReplayTakesTo`); the source lints read them with the partials through `DotCharter.Tests/Services/Audio/PlaybackServiceSourceFiles.cs`. `.Sandbox` / `.VstChain` stay partials: each reads 24–32 of the root's fields and holds `_vstGate`, `_gate` and `_midiGate` together. The position timer stays in the ROOT: grep `_positionTimer = new Timer`. Shared engine fields live in the root; MIDI connection lifetime state lives in `.RigMidi`. |
| `ViewModels/Editor/LivePlayViewModel` | 24 KB | 9 | 107 KiB (109 163 bytes) | `.Tick` `.Judging` `.Session` `.Loops` `.Meters` `.UiState` `.Tuning` `.Persistence`. **Split into collaborators** where a cluster shared little: the tuning-drift watchdog is `DotCharter.Services/Audio/LivePlay/LiveTuningDriftMonitor` (pure, injected; `.Tuning` keeps the hint, the two-call seam, the pass summary and the speed request); the rock + crowd meters, their HUD state, the fail latch and `RockMeterFailed` are `ViewModels/Editor/LivePlayMetersViewModel` (`LivePlayViewModel.Meters`, DI-injected; it takes no lock — `.Meters` keeps only the gate-taking arm/disarm/fail-mark door; the HUD binds `LivePlay.Meters.RockEnergy`); the take and replay persistence, both stores and `TakeSaved` are `ViewModels/Editor/LivePlayRunArchive` (`LivePlayViewModel.RunArchive`, DI-injected; `.Persistence` keeps the practice-profile half); which pre-first-note count-in click is due is `DotCharter.Services/Gameplay/FirstNoteCountInSchedule` (pure). **Left in place, on purpose:** `.Tick` (the worker loop — reads ~46 members of its siblings: the gate, the session, the analyzer, the capture buffer, calibration, the UI channel; hot path), `.Judging` (~42: it composes the tick snapshot and publishes stats), `.Session` (~64: StartCore/StopCore arm and tear down everything), `.UiState` (the snapshot payload — all 31 of its members are read by siblings), `.Loops` (~13: every loop command mutates the session and five bound properties under the gate). |
| `Views/MainWindow` (code-behind) | 35 KB | 4 | 75 KiB (77 040 bytes) | `.Input` `.Docking` `.WindowChrome` (what `WindowState` drives: fullscreen, the maximize glyph, UI scale). **The window CHROME is its own view since #654**: `Views/Shell/WindowChromeView` holds the title bar (`TitleBarRow`, with `MainMenuBarHost`), the back bar and the window caption, their move-drag strips and caption-button handlers; the window hosts it as `WindowChrome` in `TitleContentGrid` row 0 and reaches its rows as `WindowChrome.TitleBarRow` (its own namescope — `FindControl` on the window no longer finds them). Shell-MARKUP lints read `MainWindow.axaml` and the chrome view together through `DotCharter.Tests/Infrastructure/MainWindowSource.cs`, which throws if either half goes missing. **Four of its partials are no longer partials**: each shared ZERO or ONE private field with its siblings and each is now a collaborator the window constructs in its ctor — `Views/Shell/MainWindowPrewarm.cs` (⚠ `PrewarmTopLevelViews` — load-bearing for the responsiveness budget; `DeferredChrome<T>` reaches the menu bar, toolbar and status rail, which since #194 are `ContentTemplate` bodies of `MainMenuBarHost` / `EditorToolbarHost` / `StatusBarHost`, like the rig's `ShellRigScreenHost`); `Views/Shell/MainWindowShellLayout.cs` (sheet rows, gameplay-presentation collapse, `ApplyShellChrome` — NOT the same thing as `Layout/MainWindowLayoutController`, which owns the PERSISTED geometry); `Views/Shell/MainWindowCapture.cs` (the `PrintWindow` self-capture behind the pause backdrop, the screenshot key and the pedalboard freeze-frame); `Views/Shell/MainWindowDiagnostics.cs` (the startup popup-transparency log line and the `DOTCHARTER_UI_AUTOPILOT` runs, among them `Views/Shell/ShellSwitchProbe.cs`, the shell-switch timing probe). Lints that read the shell source glob `Views/**/MainWindow*.cs` RECURSIVELY for that reason. |
| `ViewModels/Shell/MainWindowViewModel` | 34 KB | 4 | 71 KiB (72 506 bytes) | `.Layout` `.Editing` `.Destinations`. Grep `[RelayCommand]` + the verb if unsure. **Being split into per-surface view models**, one cluster per change: stem separation is `ViewModels/Shell/StemSeparationViewModel` (exposed as `MainWindowViewModel.StemSeparation`, its folder work in `Services/Audio/StemSeparation/StemFolderBackup`); the Help menu is `ViewModels/Shell/HelpMenuViewModel` (`MainWindowViewModel.Help`); File ▸ New project and the imports (EOF, RS XML, MIDI, Guitar Pro, Audio from YouTube, and the drop routes) are `ViewModels/Shell/ImportMenuViewModel` (`MainWindowViewModel.Imports`); the Amp & FX rig overlay, Preview tones, Tools ▸ Generate tones with AI (a one-line verb over `Services/Ai/Generate/AiToneGenerationRun`) and the Tools popover's tuner readout are `ViewModels/Shell/ToneRigViewModel` (`MainWindowViewModel.ToneRig`, 17 956 bytes; the tone-rig and AI services arrive as `Func<T>` factories so nothing builds the DSP chain during shell DI — `UI/ToneRigInjectionTests`); File ▸ Export (Guitar Pro, EOF, sheet PDF) and View ▸ Screenshot are `ViewModels/Shell/ExportMenuViewModel` (`MainWindowViewModel.Export`); open / save / attach audio, Open Recent (`RecentProjectItem`), the file-drop funnel `LoadFile`, the unsaved-changes prompt and Arrangement/Vocals ▸ New/Delete are `ViewModels/Shell/ProjectMenuViewModel` (`MainWindowViewModel.Project`; it assigns the session's `ConfirmDiscardAsync` on construction). The two share no state: each takes the storage services `IFileStorageService` and `IExportTargetWriter` itself. File ▸ Exit (`ExitRequested`) and `IsProjectDirty` stay on the shell — they are the window's. Every verb that opens a hosted dialog (Edit, File, Tools, Tempo map, Arrangement, Vocals, and the editor tools' `OpenFhp` / `OpenEvent` / `OpenBeat`) is `ViewModels/Shell/ShellDialogsViewModel` (`MainWindowViewModel.ShellDialogs`), which takes `ILatencyCalibrationPrompt` through its constructor — no file under `ViewModels/Shell/` resolves through `App.Services` (`ShellDialogsViewModelTests`); `IShortcutCatalog.CurrentShortcuts` stays on the shell because it builds the palette catalog from it. `.Layout` stays a partial on purpose: the shell's own tab, chrome and gameplay logic reads and writes its panel flags (the pre-run snapshot, `Is3DPreviewActive`, `IsMinimapShown`, stems-applied, the issue chips), the window's code-behind reads both halves off the shell's ONE `PropertyChanged` stream (`MainWindow.axaml.cs`'s pane switch mixes `IsPreviewVisible` with `IsPlayModeVisible` and `IsMinimapShown`; `Layout/MainWindowLayoutController` and `Views/Shell/MainWindowShellLayout` subscribe there too), so a moved flag would leave a `nameof` case that compiles and never fires, and a layout view model would add a constructor parameter the budget forbids. Views bind through the property (`{Binding Help.ShowAboutCommand}`). `UI/MainWindowViewModelDependencyBudgetTests` pins the constructor's parameter count; it only goes down. |
| `DotCharter.Tone/Audio/Dsp/RsToneConversion/RsToneConverter` (split 2026-10-04) | 55 KiB (56 360 bytes) | 7 | 112 KiB (114 791 bytes) | `.RsAmp` turns every amp the game ships into `rsamp`, its OWN soundbank chain (#88: the row, each knob's position, the per-knob caveats `RsAmpKnobCaveats`) (its per-amp level trims are the `RsAmpLevelTrims` collaborator, by gear key); the table it indexes (`RsAmpChains`) is read from the player's install (`RsGameTables`, #755). `.Amp` holds the fallback for an amp key the game does not ship (`amp` on the `GuessVoicing` roster pick) and `AmpVoicingTrimDb`. `.GearModels` holds the dedicated circuit-model builders (the 0.9 gear-wave targets, the 2026-10 treble booster / bit cruncher). `.GameCurveAcoustic` runs Pedal_AcousticEmulator's mid band through its knob curves read from the install (#73). `.GameCurveChorus` maps the chorus pedals through the curves read from the install (`RsFlangerChorus`, #76). `.GameCurveGear` holds the gear mapped through the GAME'S OWN knob curves (`RsGameCurves`, read from the player's install at run time: Rack_StudioEQ, Pedal_Compression, Pedal_LoFiFilter, Pedal_BassEmulator) and `BuildCompanion`, the second node a gear may add (StudioEQ's HiMid bell, the Lo-Fi's high cut, the Bass Emulator's voice filter). The root keeps the entry points (`Convert`, `ConvertFilled`, `ConvertGear`), the slot routing (amp, cab + `BuildCabMic`, effects by category), the EQ helpers, the knob math and `ParamMap`. What every RS key becomes: [services/rs-tone-coverage.md](services/rs-tone-coverage.md) (generated). |
| `App` (composition root) | 36 KB | 11 | 140 KiB (143 093 bytes) | See the DI section below. |
| `ViewModels/Editor/InspectorViewModel` | 26 KB | 8 | 107 KiB (109 811 bytes) | `.SelectionIssues` (the selection's field-level findings) `.Notes` `.Chords` `.StringMask` `.SelectionState` `.BulkEdits` `.Bends`. The two history write funnels `ApplyNoteMutation`/`ApplyChordMutation` stay in the ROOT — every other partial goes through them. **Being split into collaborators**, one cluster per change, each a DI singleton under `ViewModels/Editor/Inspector/` exposed as a property and bound through it: the Notes tab's annotations are `AnnotationsInspectorViewModel` (`Inspector.Annotations`); the FEHLER tab's export readiness is `ExportReadinessViewModel` (`Inspector.ExportReadiness`); the Span card and the rebind warning are `HandShapeEditorViewModel` (`Inspector.HandShapeEditor`, tied back by its `Synced` event); the mask verbs and keyboard readouts are `StringMaskViewModel` (`Inspector.StringMask`); the Issues tab — validation, severity groups, jump / auto-fix, the easier-levels toggle, its fix menu `IssueFixViewModel` (#618) and its AI row `IssueAiViewModel` (#803) — is `InspectorIssuesViewModel` (`Inspector.Issues`, #659, 17 KiB, tied back by the narrow `IInspectorIssuesHost` the inspector implements); the Sound tab's section rail (#601) is `SoundSectionsViewModel` (`Inspector.Sections`) and its next task with nothing selected `SoundNextTaskViewModel` (`Inspector.NextTask`), both plain sub-VMs, not DI. The partials that remain are entangled with the selection state and stay — the survey is in misc-surfaces.md. Map, what stayed and why, scope ceiling: [ui/misc-surfaces.md](ui/misc-surfaces.md). |
| `DotCharter.Tone.UI/ViewModels/PlayMode/PedalboardViewModel` (moved from UI 2026-09-26) | 37 KB | 10 (+3 files of other types) | 102 KiB (104 370 bytes; with `.Node` `.Params` `.BoardColumns` 186 KiB, 190 936) | `.Node` (one pedal card) `.Params` `.BoardColumns` (other types), `.ChainEdits` `.Routing` `.NeuralAmp` `.Presets` `.Seats` (selected seat) `.MidiControls` (connection/UI bridge + the class's `Dispose`) `.History` `.Scenes` `.Spillover`. **Owned collaborators are declared in the ROOT**, not in partials: `RigTuner`, `Workspace` (`RigWorkspaceViewModel`, the persisted workspace height — bind `Rig.Workspace.Height`), `Setlists`, `Controls` (shared macro bank), `Tempo` (live delay BPM/tap), `ModuleMeters` / `OutputMeters`. The partial set is pinned by `Infrastructure/SplitTypePartialRatchetTests` (may only shrink); why each remaining partial stays is the shared-member table in [ui/pedalboard.md](ui/pedalboard.md). |
| `DotCharter.Services/Audio/LivePlay/LiveNoteMatcher` (split 2026-10-11, #650) | 44 KiB (45 568 bytes) | 1 | 44 KiB (45 568 bytes) | **Not a partial: three collaborators beside it.** `LiveTechniqueChecks` holds the credited hits whose bend/slide/vibrato the pitch-contour judge (`TechniqueContourJudge`, pure; the contour is `LiveCqtVerifier.JudgeTechnique`) has still to rule on (#645). `LiveExpectedTimeline` BUILDS the expected sounds (`Build` from one level or a per-phrase selector, `BuildPhraseSpans`, `LevelIndexFor`, `AppendLevelRegion`, the template-resolved `ChordShape`); the static `LivePitchAcceptance` holds the pure pitch rules (`Accepts`, `SoundingMidi`, `HarmonicOvertoneSemis`, `MaxBendSemis`, `NeedsTimbreEvidence`, `PitchIsUnreliable`). The matcher keeps run-time judging: `Load`/`Reload` and the reload carry-over, `Rewind*`, `AdvanceTo`, `Offer*`, `Verifies`, grading. Detail: [services/live-play.md](services/live-play.md). |

| `Controls/Sheet/SheetMusicView` (split #595, #655) | 54 KiB (55 332 bytes) | 3 | 54 KiB (55 332 bytes; the three files 75 KiB, 77 203) | **Not a partial: collaborators beside it.** `SheetMusicViewLayers.cs` holds the paint layers — `SheetCanvas` (the chunk canvas), the abstract `SheetOverlayLayer` (the overlays' page-mode state, `SetPageMode` / `SetMetrics`, `Scale` and the ONE `MeasureOverride`) with `CursorOverlay`, `SelectionOverlay` and `HoverOverlay` on it (each keeps only its own state and `Render`), and `SheetDrawOp`. `SheetMusicViewInput.cs` holds the gestures: the pointer handlers on the scroll content (press seeks + selects, drag scrubs at ~60 Hz, double-click inserts, move hovers) and `TryHandleKey` (digits type a fret through `FretDigitAccumulator`, +/− step the note value); it calls back into the view's `internal` `SeekToPointer` / `SelectAtPointer` / `UpdateHover` / `TryAddAtPointer`, which keep the hit-testing beside the render state. The pure lookups are `SheetHitIndex.cs`. Source lints read the set through `DotCharter.Tests/Infrastructure/SheetMusicViewSource.cs`, which throws on a missing or unlisted `SheetMusicView*.cs`. Rules: [ui/sheet-coordinators-theming.md](ui/sheet-coordinators-theming.md). |
| `Controls/Preview3DRenderDriver` (split #656) | 55 KiB (56 543 bytes) | 1 | 55 KiB (56 543 bytes; with the drain 62 KiB, 63 612) | **Not a partial: one collaborator beside it.** `LivePlayFeedbackDrain.cs` is the Live Play half of the snapshot staging: it drains `LivePlayViewModel.FeedbackEvents` into the staging `FrameSnapshot` once per frame and owns what outlives that frame — the `CrowdMeter`, the sticky EARLY/LATE direction and PERFECT/GREAT/GOOD tier (`TimingFlashMath.NextSticky`), the feedback `Seq` — and the `IsGrade` guard (a timing grade moves the cues only: no crowd verdict, no marker). Pure and allocation-free, pinned by `DotCharter.Tests/UI/LivePlayFeedbackDrainTests.cs`; the driver calls it once from `RunSnapshotUpdate` on the render thread (`Preview3DDriverWiringTests`). Source lints read the set through `DotCharter.Tests/Infrastructure/Preview3DRenderDriverSource.cs`, which throws on a missing or unlisted `Preview3DRenderDriver*.cs`. Rules: [ui/rendering-perf-detail.md](ui/rendering-perf-detail.md). |

**Splitting a file silently blinds any lint that locates source BY PATH.**
Thirty-three guards needed repointing in v0.44 and one exclusion would have
inverted. After any split, grep the test project for the old filename. Root
[AGENTS.md](../../AGENTS.md) carries the full rule.

## Style dictionaries: a CONTAINER plus a folder of parts

Five dictionaries became a thin container that `StyleInclude`s an ordered list of
parts in a folder named after it. **Edit the part; the container is only an
include list.**

| Container | Was | Now | Parts in |
|---|---|---|---|
| `Styles/Fire.axaml` | 240 KB | 56 KB | `Styles/Fire/` — 15 (the rig's six `FireRig*` parts hold ControlThemes only since #242; the rig's descendant and Path / Panel rules are view-scoped: `Views/PlayMode/AmpFxRigStyles.axaml`, `RigModuleEditorStyles.axaml`) |
| `Styles/Resources.axaml` | 87 KB | 2 KB | `Styles/Resources/` — 4 |
| `Views/Editor/InspectorStyles.axaml` | 85 KB | 7 KB | `Views/Editor/InspectorStyles/` — 5 |
| `Styles/FireTokens.axaml` | 73 KB | 8 KB | `Styles/FireTokens/` — 4 |
| `Views/Dialogs/DialogStyles.axaml` | 51 KB | 8 KB | `Views/Dialogs/DialogStyles/` — 3 |

**Include ORDER is load-bearing** — a later dictionary wins, so reordering the
include list silently changes which definition applies. `Perf/SplitStyleDictionaryUiPerfSmokeTests`
pins folder ↔ include-list agreement both ways and that every `x:Key` resolves
from a live control.

**A lint that parses one of these with `XDocument.Load` reads an empty
container and passes.** Three did. Use `Tests/Infrastructure/StyleFileText`
(`ReadWithIncludes` / `ElementsWithIncludes`), which follows the includes.

**The COLOUR tokens are a sixth dictionary and live in
`Styles/ColorTokens.axaml`**, merged into `App.axaml`'s `Application.Resources`
(and into `DotCharter.UI.Gallery/App.axaml`, so the gallery renders the
production palette). `ThemeService` writes `Application.Current.Resources[key]`,
which SHADOWS the merged entry — that is what keeps runtime theme switching
working across the split. Pinned by `UI/ThemeTokenDictionaryTests` and
`Perf/ThemeTokenOverrideUiPerfSmokeTests`.

**The screen gallery** — `DotCharter.UI.Gallery/` (assembly `DotCharter.Gallery`),
a standalone Avalonia host for UI screens on fake view models. Screen pages are
DERIVED from the screen catalog (`Pages/Screens/ScreenPages.cs`); only
non-screen pages are listed by hand in `Pages/<Area>/<Area>Pages.cs`. See
`DotCharter.UI.Gallery/AGENTS.md`.

**Screen and dialog machinery (the UI rewrite)** — one line per screen in
`DotCharter.UI/Views/Shell/ScreenCatalog.cs`; hosts `Views/Shell/ScreenHost.cs`
and `Views/Shell/DialogHost.cs`, registered by `Views/Shell/ScreenShellModule.cs`;
`INavigator` / `Navigator` / `BackStack` in `DotCharter.Presentation/Navigation/`,
`IDialogs` / `DialogStack` / `DialogsSlot` in `DotCharter.Presentation/Dialogs/`.
Golden screenshots: `DotCharter.Tests/Perf/ScreenGoldenUiPerfSmokeTests.cs`
(`Invoke-Gate.ps1 -Goldens`), store `DotCharter.Tests/Goldens/ui/`, tool
`tools/agent/Update-Goldens.ps1`. Design and deviations:
[ui/target-architecture.md](ui/target-architecture.md) §§ 3.4, 3.5, 5.1.

**Settings (the first surface on the rewrite)** — the shell
`DotCharter.UI/Views/Settings/SettingsShellView.axaml` over
`DotCharter.Presentation/Screens/Settings/SettingsShellViewModel.cs`, built into
`MainWindow`'s `SettingsHost` by `DotCharter.UI/Views/Settings/SettingsSurface.cs`;
its tabs shown by `DotCharter.UI/Views/Shell/ScreenTabHost.cs`. Every tab is a
screen: `<Tab>SettingsView.axaml` beside the shell, view models in
`DotCharter.Presentation/Screens/Settings/` (the old page and its adapter are
gone since 2026-09-22). Appearance drives the theme and the interface scale
through `DotCharter.Services/Infrastructure/Appearance/` (`IAppearanceTheme`,
`IInterfaceScale`; implemented in `DotCharter.UI/Platform/`). The one rail order:
`DotCharter.Presentation/Screens/Settings/SettingsRail.cs`; the keybind rows:
`KeybindRowCatalog.cs` beside it; the update verbs (manual check, Help-menu door,
startup check, staged-payload recovery): `DotCharter.Services/Update/UpdateCheckFlow.cs`.
`IAppUpdateService` is `Update/GitHubUpdateService.cs` (the release check and the
downloads), which forwards the install half (apply-block reasons, the archive
expansion, the staged-payload record, the updater launch) to `Update/UpdateInstaller.cs`.
The add-a-tab checklist: [ui/settings.md](ui/settings.md).

**The Library** — `DotCharter.UI/Views/Library/LibraryView.axaml` (+ `LibraryRowView`,
the rows' container template) bound to `Contracts/Library/ILibraryScreen` through
`DotCharter.UI/ViewModels/Panels/LibraryScreen.cs` (+ `LibraryScreenParts.cs`: rows,
columns, chips), an adapter over `SongBrowserViewModel`, which keeps the scan, the
filter pass, the covers and the load. Its filter, facet options, counts and orders
are the engine it shares with Song Select, `DotCharter.Presentation/Shared/Library/`
(`LibraryQuery`, `LibraryFacets`, `LibraryFilter`, `LibraryFacetOptions`,
`LibrarySort`, over `ILibrarySong`; table in [ui/song-browser.md](ui/song-browser.md)). Built into
`MainWindow`'s `SongBrowserHost` by `Views/Shell/MainWindowPrewarm.EnsureLibraryCreated`,
not a `ScreenHost`. Fake: `Presentation.Fakes/Library/FakeLibraryScreen.cs`. Rules:
[ui/song-browser.md](ui/song-browser.md). A library song's identity (`lib:<SongKey>`,
read-compatible with the older `psarc:`) is spelled only by
`Services/Library/LibrarySongIdentity.cs`; the index and its rows are
`Services/Library/Index/ILibraryIndex.cs` and `Services/Library/LibrarySongEntry.cs`; the scan is
`Services/Library/LibraryService.cs` over each `ILibraryPackageSource` (`.psarc`, `.dcsong`; #886).

**Screen contracts and fakes (the UI rewrite)** — contracts in
`DotCharter.Presentation/Contracts/<Area>/` (namespace
`DotCharter.Presentation.Contracts.<Area>`, e.g. `ISongSelectScreen`), fakes in
`DotCharter.Presentation.Fakes/<Area>/` (`FakeSongSelectScreen`, each with a
static `Variants` list of `FakeVariant<T>`); the views that bind them stay in
`DotCharter.UI/Views/PlayMode/Redesign/`. The UI never references the fakes.
Rules and their tests: `DotCharter.Presentation/AGENTS.md`; the design:
[ui/target-architecture.md](ui/target-architecture.md).

## Test harnesses

`Perf/PlayModeScreensUiPerfSmokeTests` (10 KB root, 8 files, 132 KB total) and
`Perf/DialogConstructionUiPerfSmokeTests` (16 KB root, 11 files, 145 KB total)
are each ONE partial class over topic files — open the sibling matching your
screen.

`Perf/UiSweep.cs` (104 KB) is deliberately NOT split: `UI/UiSweepCoverageTests`
reads it by path and asserts the text never writes `IsVisible`, so moving the
runner out would leave that guard passing vacuously over the remainder. Splitting
it requires widening that read to `Perf/UiSweep*.cs` in the SAME commit — and
reading [testing/ui-sweep.md](testing/ui-sweep.md) first.

## Standalone tools under `tools/`

Console projects in the solution that reference `DotCharter.Services` the way
the UI does. `AssemblyLayeringTests` is a hand-written table over the five
LIBRARY assemblies and does not enumerate the solution, so a tool is not a
subject of it — do not add a row there for one.

| Tool | Entry point | What it is |
|---|---|---|
| `tools/rig-dataset/DotCharter.Tools.RigDataset/` | `Program.cs` | Renders the rig-model training corpus. Referenced by `DotCharter.Tests` so `Ai/Dataset/*` can drive a real render in-process. |
| `tools/lyric-sync/DotCharter.LyricSync/` | `Program.cs` → `LyricSyncCommand` | **`dotcharter-lyricsync`**: the lyric aligner for people who chart in Editor on Fire or RSCharter and do not have DotCharter. Stands on `ILyricSyncCore.AlignAsync` (`Services/Audio/Lyrics/LyricSyncCore.cs`), which `LyricAutoSyncService` implements — **a seam, not a fork**, pinned by `LyricSyncSeamTests`. Writes seconds, never beats. Users: `tools/lyric-sync/README.md`. Agents: [services/lyric-autosync.md](services/lyric-autosync.md) § the seam. Published by `tools/lyric-sync/Publish-LyricSync.ps1`. |

## Still one big file

| File | Size | How to navigate |
|---|---|---|
| `JuceSharp/.../AudioEngine.cpp` | 176 KB | 13 `// ---` section banners; grep the banner text. |
| `JuceSharp/.../dotcharter_audio.h` | 156 KB | The C ABI (`DCA_ABI_VERSION` 43). Section banners too — `grep -n '^// ---' dotcharter_audio.h` lists them. |

---

## Save / load / export pipeline

The one an agent asks about most.

**Save** — `Ctrl+S` / menu / palette / window-close all funnel to one place:

- `DotCharter.Services/Session/ProjectSaver.cs` → `SaveProjectCoreAsync`
  (grep `internal async Task<bool> SaveProjectCoreAsync`). `ProjectSessionService`
  builds the saver and forwards `SaveProjectAsync` / `SaveProjectAsAsync` to
  `SaveWithGuardAsync`; the session-side bookkeeping a saved path triggers
  (last project, MRU, auto-backup reset) comes back through `IProjectSaveHost`.
  Order is deliberate:
  1. `AssembleProject` + `ChartProjectCloner.Clone` — deep snapshot on the UI
     thread, so the worker serializes an immutable copy (a torn save is
     impossible). **This clone is the ONLY heavy UI-thread step of a save.**
  2. `Task.Run`: `ChartSpacingEnforcer.EnforceProject` over the SNAPSHOT (it
     used to run on the UI thread over the live project — hundreds of ms of
     stall on a big chart; the live project is re-enforced on load and after
     every committed edit, so the file is what needs it) → write `<path>.saving`
     → `AtomicFile.ReplaceWithRetry` (atomic) → `SaveDlcBuilderXmlBundle` →
     delete legacy xml
  3. `MarkClean()` on the dirty tracker; the `Project saved.` log line carries
     `uiThreadMs / workerEnforceMs / workerWriteMs`, and the `SaveTimed` seam +
     `Services/Session/ProjectSessionSaveThreadingTests` pin the thread shape
- **The player's tone edits and instruments follow a SAVED tone rename** (#196):
  `Session/SongToneEditKeyFollow` captures the live project's
  `Editing/Tones/ToneKeyRenameJournal` beside the snapshot, moves the
  `SongToneEditStore` entries in the worker after the swap, and settles the
  journal on the UI thread only when the store agreed. Detail in
  [services/editing-invariants/undo.md](services/editing-invariants/undo.md).
- **Any exception inside that `Task.Run` is reported as "project save failed"** —
  so companion-artifact writers must carry their own try/catch, or a failed
  side-artifact will claim the `.dcproj` didn't save when it did.

**Project format**: `.dcproj`, XML (not a zip), `chartFormatVersion` 3 — the
RsCharter layout plus DotCharter's `dc*` extensions, so the code keeps the
`Rscp` name. Serializer `DotCharter.Services/Parsing/Rscp/RscpChartSerializer.cs`, resolved by
extension through a **keyed** DI registration (see DI table below). Both
extensions are spelled ONLY in `Parsing/Rscp/ProjectFiles.cs` (`ProjectFiles` for
`.dcproj`, `CharterRscp` for Charter's `.rscp`). A `.rscp` (Charter, or an older
DotCharter build) still opens; its first save writes the `.dcproj` beside it
(`ProjectSaver.SaveFromRscpAsync`; an older DotCharter file is renamed to
`.rscp.bak`). A `.rscp` beside its `.dcproj` is the Charter companion and never
opens in the project's place (`ProjectFiles.PreferProject`). Guarantees:
[../DCPROJ-FORMAT-GUARANTEES.md](../DCPROJ-FORMAT-GUARANTEES.md).
A project's stable identity is `ChartProject.ProjectId` (`<dcProjectId>`, minted
at creation, derived from the bytes of an older file on open); the AI chat
history keeps its conversations BESIDE the project, in
`<project folder>/ai-chats/<id>/` — not in the `.dcproj`, not in any snapshot
([services/ai-chat-history.md](services/ai-chat-history.md)).

**Companion artifacts written on every save** (`SaveCompanionService`, plus the
pre-existing XML bundle). All are DERIVED — none may fail a save:

| Artifact | When | Notes |
|---|---|---|
| `<name>.rscp` | inside the save worker, FIRST, right after the `.dcproj` swap | `Export/Charter/` — the Charter-compatible companion: the project XML cut down to `CharterProjectSchema` (an allow-list read from Charter V0.22.44's model), no `dc*` nodes. Charter's own project format, so it goes through `CompanionOwnership` and a Charter project at that name is never overwritten |
| `DTRS XML/` bundle | inside the save worker | `ProjectDocumentService.SaveDlcBuilderXmlBundle` — staged into `DTRS XML.saving`, then `Directory.Move`, so it is never half-populated |
| `<project>.eof` | inside the save worker | Cheap. Also transcodes a sibling `.ogg` — **EOF resolves its audio relative to the folder the `.eof` is in** and prompts without it |
| `<DlcKey>_p.psarc` | **detached, after** the save | Slow (SNG + audio copy). Coalesced (holding Ctrl+S does not queue builds) and staged-then-swapped; `Session/DetachedCompanionBuilder` owns the queue |
| `<Artist>_<Title>.rs2dlc` (the PROJECT ROOT, beside the `.dcproj` — NOT inside `DTRS XML/`, which is renamed aside on the next save) | inside the save worker, AFTER the bundle | `Export/DlcBuilder/` — the DLC Builder PROJECT that names the bundle's XML through the `DTRS XML\` folder prefix (`Rs2DlcProjectBuilder.InBundle`). Refuses when the bundle is absent; a project file naming missing XML reads as an EMPTY project. **Its `Tones` list carries the project's tone BODIES** (`ChartProject.ToneDefinitions`, each definition's JSON emitted verbatim — it already is DLC Builder's own tone object), and each arrangement carries `BaseTone` + its four slots. Empty ONLY when the project owns no tone definitions; it was empty for every earlier release because the app had no tone bodies to put there, and a `BaseTone` with `Tones: []` is a project DLC Builder cannot build |

**Exporters** (none are DI-registered except `ISaveCompanionService`; the rest are
`new`'d or static):

| Format | Entry point | Invoked from |
|---|---|---|
| RS2014 `.psarc` | `Services/Export/RocksmithPackageBuilder.cs` → `BuildPsarcAsync` | export dialog + `SaveCompanionService` |
| `.sng`, manifests, xblock, aggregategraph | `Services/Export/{Sng,Package}/`; a 7-string guitar goes through `Services/Export/Rocksmith/Rs7Projection.cs` first, and its strings past the sixth go into the SNG's RS7X block (`Rs7BlockBuilder` → `Sng/Model/RsExtensionBlock`, bytes by `Sng/RsExtensionBlockWriter`, #777). No game file is vendored (#894): the two `rsenumerable_*.flat` entries and the vocals glyph table are generated (`Package/RsEnumerableModels`, `Sng/DefaultVocalSymbols`), SHA-256-pinned by `GeneratedGameTemplatesTests` | the builder |
| `.wem` | `Services/Export/Audio/WemWriter.cs` (Vorbis through JuceSharp's `JuceWemEncoder`, or PCM; **no Wwise**, never ADPCM: the game does not play it, #717) | the builder, for non-`.wem` audio |
| `.bnk` | `Services/Export/Audio/SoundBankWriter.cs` | the builder — **its codec plugin id must match the wem's codec** |
| Editor on Fire `.eof` | `Services/Parsing/Eof/EofExporter.cs` → `ExportToFile` | menu + `SaveCompanionService` |
| Guitar Pro `.gp` | `Services/Export/GuitarPro/GpExporter.cs` | `ExportMenuViewModel.ExportGuitarPro` (`MainWindowViewModel.Export`) |
| DLC Builder `.rs2dlc` | `Services/Export/DlcBuilder/Rs2DlcProjectBuilder.cs` + `Rs2DlcWriter.cs`, the naming/path references in `DlcBuilderProjectFile.cs` | `SaveCompanionService.WriteDlcBuilderProject` (every save, when its setting is on), `DlcBuilderProjectWriter.Write` (the Rocksmith export dialog's "Export DLC Builder project", on request) |
| Sheet PDF | `DotCharter.Services/Export/Sheet/SheetPdfExporter.cs` | `ExportMenuViewModel.ExportSheetPdf` (`MainWindowViewModel.Export`) |
| DotCharter song `.dcsong` | `Services/Package/DcSong/DcSongWriter.cs` behind `DcSongExporter` (`IDcSongExporter`, the one DI-registered exporter here besides the companions) | `ShellDialogsViewModel.OpenDcSongExport` → `DcSongExportViewModel`; format in [services/dcsong-format.md](services/dcsong-format.md). The way back in (File ▸ Open / drop of a `.dcsong`) is `Session/DcSongProjectOpener` → `Package/DcSong/DcSongProjectUnpacker`, routed by `ProjectSessionService.LoadFile` |

**Every exporter above refuses play-only content** through
`Export/PlayOnlyExportGuard` (#822); the list it must stay complete against is
`Services/Export/PlayOnlyExportGuardCoverageTests`. Detail in
[services/export-companions.md](services/export-companions.md).

Audio-encoding rules and the two ways to silently ship a silent CDLC:
[docs/agents/services/export-companions.md](services/export-companions.md).

**PSARC write**: `Services/Infrastructure/Psarc/` — `RocksmithKeys` (the user's keys) and
`Write/PsarcWriter` + `Write/PsarcTocEncryptor` (AES-CFB TOC). Big-endian header + TOC; the
SNG/bnk bodies inside are little-endian. **Nothing in this repository reads a
package (#889)**: the reader is an optional plugin (`Services/Plugins/`: it registers an
`ILibraryPackageSource`, an `IPackageToneImporter`, an `IGameInstallReader` and an `IPackageSongConverter`;
File ▸ Import ▸ Convert PSARC to DotCharter song drives the last through
`Presentation/Screens/Dialogs/PackageConversionLauncher`, and the plugin's drag-and-drop exe runs the same converter).

**Writer / reader split (#885, epic #882)**: the writer side is `Services/Export/**` and
`Services/Infrastructure/Psarc/Write/**` and names NO reader type
(`Infrastructure/WriterSideReaderReferenceLintTests`). Shared data lives on the writer side:
the SNG model (`Export/Sng/Model/`: `RocksmithSng`, the `Rs*` records, `RsNoteMask`,
`SngFlags`, `RsExtensionBlock`). Writer halves: `Export/Sng/RocksmithSngWriter`,
`Export/Sng/SngPacker` (zlib + AES-CTR ENCRYPT), `Export/Sng/RsExtensionBlockWriter`,
`Export/Rocksmith/Rs7ChordSidecar` (names + removes old sidecars). The reader halves are the
plugin's (#889), and so are the tests that read writer output back; the writer's evidence here
is byte/SHA-256 pins (`SngWriterBytePinTests`, `RocksmithPackageBytePinTests`). A test class
whose read-back half moved keeps what both halves share in a partial file named after the class
with a `.Fixtures` suffix, which the plugin's test project links.

**Importers**: `.dcproj`, Charter's `.rscp`, `.xml`, RS2014 XML, Guitar Pro (`.gp*`), `.eof`, `.dcsong` (and a package
through the optional plugin),
YouTube audio (File ▸ Import ▸ Audio from YouTube, `YoutubeAudioLauncher`; contested), WAV/OGG/MP3/FLAC, LRC, UltraStar, `.bnk`/`.wem`,
`.dds`, MIDI (`.mid`/`.midi` — `Services/Parsing/Midi/` behind
the hosted `MidiImportViewModel` + `MidiImportLauncher`; lands as a new draft arrangement, never overwrites).

---

## Adding a menu item / command

Five files, in this order. Miss one and the command exists but is unreachable.

1. `DotCharter.UI/Views/Shell/Menus/<Name>Menu.axaml` — the `<MenuItem>`, in the
   file of the top-level menu it belongs to (`FileMenu`, `EditMenu`, `TempoMapMenu`,
   `ArrangementsMenu`, `VocalsMenu`, `ToolsMenu`, `ViewMenu`, `HelpMenu`; #653).
   `Views/Shell/MainMenuView.axaml` only lists those menus on the bar; a NEW
   top-level menu is a new `EditorTopMenu` subclass there plus its element in the
   bar. The menu lints read the bar and every menu file through
   `DotCharter.Tests/Infrastructure/MainMenuSource`, which throws on a menu file
   the bar does not list. **Put it in
   the right GROUP**: since 0.52 every long menu (File, Edit, Tempo Map, View,
   Tools) is one level of submenus, capped at 14 direct entries and with no
   submenu holding fewer than three rows — `UI/MainMenuLocalizationTests` fails
   if a new item breaks either. A new group header needs its own resx key.
2. `DotCharter.UI/ViewModels/Shell/MainWindowViewModel.cs` — a `[RelayCommand]`
   method (source-generated into `XxxCommand`).
3. `DotCharter.UI/ViewModels/CommandPalette/CommandCatalog.cs` — palette entry
   (grep `CommandCatalog.Build`; ids look like `file.saveProject`, `tools.*`).
   **Not optional for a keybound action**: `CommandPaletteCoverageTests` reflects
   over `KeybindConfig` and fails on any `KeyChord` property with no palette row
   unless it is excluded there with a reason.
4. `DotCharter.UI/Input/AppKeybindRegistrar.cs` — only if it needs a keybind.
   Register a `[RelayCommand]` on the shell rather than writing the action
   inline, or the key and the palette end up with two implementations.
   **A keybound row then SHOWS its chord**: `InputGesture="{Binding
   ProjectState.Keybinds.X, Converter={x:Static
   converters:KeyChordToGestureConverter.Instance}}"` (a digit family adds
   `ConverterParameter=<digit>`), never a literal — the binding follows a rebind
   and a replaced table. A toolbar button with a bound verb carries the chord in
   its tooltip (`StringFormatMultiConverter` + a `Toolbar_*_Hint` key).
   `UI/MenuGestureCoverageTests` derives command → chord from the registrar's
   source and fails a row or button that shows none, or the wrong one.
5. **All three** resx files (`Resources.resx` + `Resources.de-DE.resx` +
   `Resources.ja-JP.resx`) — the
   `Menu_*` key. Grep, don't Read.

A dialog additionally needs a VM registered in `App.axaml.cs`, a property on
`DotCharter.UI/ViewModels/Shell/AppDialogService.cs` (which also fixes Esc priority
and `HasOpenDialog`) **and a `RegisterLazy` line in
`DotCharter.UI/Views/Shell/DialogContainerView.axaml.cs`**. Dialog views are built
lazily on the first `IsOpen = true`; without the `RegisterLazy` the view model opens
and NOTHING renders. `PowerToolReachabilityTests` gates all of these hops.

---

## DI composition root

`App` is **one class over eleven partial files** (re-counted 2026-09-13: `ls
DotCharter.UI/App*.cs` — it was nine at the v0.44 split, and the AI and match
slices arrived after). `ConfigureServices(IServiceCollection,
JsonConfigurationService)` still lives in `App.axaml.cs` (grep `internal static
void ConfigureServices`) but now mostly dispatches to **nine** topical slices —
grep `Services(services);` in that method for the live list rather than trusting
this sentence. ~250 registrations in total.

**Add a registration to the slice that owns it, not to `App.axaml.cs`:**

| Slice file | Holds | Anchor |
|---|---|---|
| `App.AudioServices.cs` | JUCE playback, per-player DSP rigs, tones, stems, Guitar roles (`IGuitarRoleStems`), waveform cache | `AddAudioServices` |
| `App.ViewModelServices.cs` | every shell/panel/editor/dialog view model (~40), keybind handler, and the forwarding seams onto the one VM instance (`IEditorTransport` → `TransportViewModel`, `IChartEditorSurface` → `ChartEditorViewModel`, `IMessagePrompt` → `MessageDialogViewModel`, `ISongStemSeparation` → `StemSeparationViewModel`) | `AddViewModelServices` |
| `App.EditingServices.cs` | editing/history/power tools, recovery, project session, library | `AddEditingServices` |
| `App.OnlineServices.cs` | TONE3000 + NAM captures, GitHub sign-in, app update | `AddOnlineServices` |
| `App.GameplayServices.cs` | profiles, takes, replays, practice | `AddGameplayServices` |
| `App.ValidationServices.cs` | validation rules + auto-fix, lints, lyrics | `AddValidationServices` |
| `App.AiServices.cs` | the tone-design provider slice (secret store, model factory, design service) | `AddAiServices` |
| `App.MatchServices.cs` | "match a tone from audio": extractor, similarity, offline rig host + renderer, stem isolator, rig sampler, library, matcher, the prior/narrator lane, the sections lane — **and the rig estimator's model DOWNLOADER** (`IRigEstimatorModelDownloadService` → `RigEstimatorModelCatalog` / `RigEstimatorModelInstaller`, installing into `%LOCALAPPDATA%\DotCharter\ai-match\rig-estimator\`, which is `RigEstimatorOnnx.DefaultModelDirectory`). The estimator is handed the INSTALLER's directory, so write-path and read-path cannot drift — [services/ai-match-model.md](services/ai-match-model.md) § Getting the model | `AddMatchServices` |
| `App.StateServices.cs` | **keyed chart serializers**, `ProjectState`, the audio coordinators (types in `DotCharter.Services/Audio/Coordination/`, + `IGameplayToneHud`, + `IToneLoudnessMeter` → `RenderedToneLoudnessMeter` over the Match slice's `IOfflineRigHost`, #67), editor tools | `AddKeyedTransient<IChartSerializer, RscpChartSerializer>("rscp")` — **keyed TRANSIENT, not singleton**; resolve with `GetRequiredKeyedService<IChartSerializer>("rscp")` |
| `App.axaml.cs` (tail) | the Presentation AREA modules — `SettingsModule.Register`, `HomeModule.Register` and `OnboardingModule.Register` (the first-run wizard's system check; its two probes, `IGpuRenderProbe` and `INativeEngineProbe`, are registered beside `IGraphicsDeviceSettings` at the top) — plus `IShellDestinations` → `MainWindowViewModel` (Home's cards navigate through it; `ViewModels/Shell/MainWindowViewModel.Destinations.cs` implements it) | `ConfigureServices`, last block |
| `App.axaml.cs` | rendering/infra/status-bar registrations inline — including the `Platform/` adapters (`IShellLauncher`, `ISystemClipboard`, `IBundledDocuments`, `IExportTargetWriter`, `IArtworkSource`) — plus the seven calls | `ConfigureServices` |
| `App.Plugins.cs` | `AppPlugins`, a static class beside `App` (not a partial: `SplitTypePartialRatchetTests`) — the plugins (#887): `PluginLoader` loads the plugins the build declares on this assembly (`PluginLoader.Declared`; a plugin can register a package library source, the tone importer, the AI tone anchors and the game-install reader), the merge of every module's `IPackageFeatures` into the one registration (none by default), `Activate` (hands the plugin's `IGameInstallReader` to `GameInstall` and its `IPackageToneImporter` to `PackageTones`), and the startup warning for a refused plugin. Called FIRST in `ConfigureServices` so a package source precedes `.dcsong` in the scan order. [services/contested-features.md](services/contested-features.md) § Optional plugins and package features | `AppPlugins.AddPlugins` |
| `App.Shutdown.cs` | `IsExiting`, `TerminateNow`, clean shutdown, kernel32 imports | `TerminateNow` |

**Nothing outside the composition root reads `App.Services`** except the
XAML-constructed controls and render-path code on the shrink-only allowance list
in `Infrastructure/ServiceLocatorRatchetTests` (view models: none, ever). A class
DI or an owner constructs takes its dependencies through its constructor, as
REQUIRED parameters (`Infrastructure/OptionalDependencyRatchetTests` ratchets
the remaining `IFoo? foo = null` ones per project, #661) — a
`Func<T>` factory when it must not build the service early (`IDiagnosticsDialogService`
is registered with one for its transient `DiagnosticsViewModel`).

**A registration is not a use.** The reachability lints
(`PowerToolReachabilityTests`, `ServiceRegistrationReachabilityTests`) exclude *every*
`App.*.cs` file for exactly this reason — a service registered here and resolved
by nobody is a dead feature that still compiles and still passes its own unit
tests. If you add a slice file, it must match that `App.` exclusion prefix or
those guards start counting your registration as the use they exist to discount.

A new project-file format needs **both** a keyed `IChartSerializer` registration
here **and** a case in the `ext switch` in `ProjectDocumentService.ResolveFormat`
(grep `private static string ResolveFormat`) — the ONE helper both `LoadChart`
and `SaveChart` go through, and it throws `NotSupportedException` on anything
that is not `.dcproj`, `.rscp` or `.xml`.

---

## Where the per-area detail lives

Root [AGENTS.md](../../AGENTS.md) carries the cross-cutting invariants. Per-area
deep reference (read on demand, not auto-loaded):

**Every project `AGENTS.md` is now a ROUTER**, not a reference: it auto-loads for
every agent that opens a file in that project, so it carries the layout table,
the task table, the cross-cutting contracts and a ONE-LINE index of every hard
rule — and each rule's full text sits in a topic file it names. Start at the
router; it tells you the ONE extra file to open for what you are touching.

| Area | Router | Topic files |
|---|---|---|
| UI | `DotCharter.UI/AGENTS.md` | [docs/agents/ui/](ui/) |
| Services | `DotCharter.Services/AGENTS.md` (~29 KB) | [docs/agents/services/](services/) — 23 files, plus 6 sub-folders |
| Rendering | `DotCharter.Rendering/AGENTS.md` (v0.56) | [docs/agents/rendering/](rendering/) — 5 files |
| Core | `DotCharter.Core/AGENTS.md` (v0.56) | [docs/agents/core/](core/) — 3 files |
| Tests | `DotCharter.Tests/AGENTS.md` (v0.56) | [docs/agents/testing/](testing/) — 6 files, plus the shared [testing-headless-rendering.md](testing-headless-rendering.md) |
| Analysis | `DotCharter.Analysis/AGENTS.md` (v0.56) | [docs/agents/analysis/](analysis/) — 1 file |
| JuceSharp | `JuceSharp/AGENTS.md` (v0.56) | `JuceSharp/docs/agents/` — 6 files, **inside the submodule** so a standalone clone keeps them |
| Localization | its own `AGENTS.md` | none — it is small and single-purpose |
