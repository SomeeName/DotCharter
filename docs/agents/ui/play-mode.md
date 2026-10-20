# Play Mode — the game shell (UI router)

Extracted verbatim from `DotCharter.UI/AGENTS.md`. **Read this before touching
any Play Mode screen, overlay, backdrop, Song Select filter or the
gameplay-settings overlay.**

**This file is a ROUTER.** What stays here is the partial-file map (which
sibling a member belongs in), the screen/overlay distinction and the smoke-test
rule — the three things that apply whichever surface you came for. Everything
else is one file per row of the table at the bottom, under
[`play-mode/`](play-mode/).

## Play Mode (the game shell)

- **`PlayModeViewModel` is split into partials (0.41)
  — go to the sibling, not the root.** It was 5,504 lines / 251 KB (69 commands, 199 `[ObservableProperty]`,
  no partials, no regions), the largest file in the repo. The split is the same
  house pattern `JuceAudioPlaybackService` (six) and `ChartMaintenanceService`
  (four) use:

  | File | What is in it |
  |---|---|
  | `PlayModeViewModel.cs` (root) | screen enum plumbing + navigation, pause menu, rock-meter fail-out, pre-play setup, gameplay flow / count-in / end-watch, the in-game-tone state — **plus the ctor and every injected dependency and plain private field** |
  | `SongSelectViewModel.cs` (its own type, not a partial) | Song Select's list, exposed as `SongSelect`: the library scan (`EnsureLibrary`, shared in-flight task), the facets and record filter, search, sort, the filter chips and artist groups, the empty states and the counts. Built by Play Mode's constructor over the services only it reads (`ILibraryService`, `IChartGenreIndex`). `SongSelectView` binds `{Binding SongSelect.…}`; the chosen song, `SelectedSong`, stays on Play Mode (`.FrontDoor`) because Get Ready, Pause, setlists and Home act on it. Play Mode reads back `AllSongs`, `RefreshSongStats`, `LooksLikeProjectPath` and follows `LoadingChanged` for the launch screen |
  | `.Screens` | the screen enum, the `CurrentScreen` switch and the screen-navigation commands (they lived in `.Practice` until 2026-09-19). The practice queue, the adaptive plan and its planner, the global Stats tab, the calendar and goals were removed 2026-09-19 — restore from tag `pre-cut-2026-09` |
  | `.Results` | entering the results screen (`OnSessionCompleted`) and the run-exit verbs (`Retry`, `BackToSongSelect`, `EndRunSideEffects`, the editor bridges) — they unwind the run chain the root owns |
  | `RunResultsViewModel.cs` (its own type, not a partial) | the results BOARD, exposed as `RunResults`: score, grade, bests, the section breakdown, the technique line, the timing dashboard and the leaderboard, all filled by `Show(report)` from the `LivePlaySessionReport`. Takes only `ILocalizationService` and `ILeaderboardService`; reads no run state. `ResultsView` binds `{Binding RunResults.…}`, and its reveal reads `RunResults.ResultScoreValue` etc. |
  | `RunListenBackViewModel.cs` (its own type, not a partial) | listen-back to the run just played, exposed as `ListenBack`: borrows the one transport, mutes the stems for the take and hands the song back on every exit (`StopAsync`). Play Mode calls `Reset()` when a run starts, `OnTakeSaved(file, resultsShowing)` from the take archive, and `StopAsync()` when the screen leaves Results. `ResultsView` binds `{Binding ListenBack.…}` |
  | `RelativeTimeText.cs` | the one "4 days ago" formatter the song rows and the status bar share |
  | `.HomeNavigation` | `OpenSongAsync`: resolves Home's statistics song key in the unfiltered library, loads through `LoadFlowSong`, restores the recorded arrangement key, and opens Get ready |
  | `PlayModeToneRigViewModel.cs` (its own type, not a partial) | the Amp & FX rig overlay, exposed as `ToneRig`: `IsPedalboardOpen`, `Pedalboard`, the gated `AmpFxRig` screen, `OpenPedalboardCommand`, `EnsureRigScreen`, the AI panel's host half (`ConfigureMatch` / `ConfigureSections` / `PlaceToneSet`) and the test seam that hands it a chain. Constructor-injected; the chain, the per-screen rig parts and the AI lane are `Func<T?>` factories built in `App.ViewModelServices`. Bindings read `ToneRig.…`; Play Mode wires the song context, forwards `OpenSettingsRequested` and ends a song-tone edit on `Closed`. Nothing in Play Mode reads `App.Services` (`UI/PlayModeToneRigInjectionTests`) |
  | `PlayModeSettingsViewModel.cs` (its own type, not a partial; #658) | the settings modal, exposed as `Settings`: `IsGameplaySettingsOpen` and the open/close cycle (load on open, persist + mid-run re-arm on close), the AppConfig-backed values, the section rail, the search (`GameplaySettingsSearchPass`), the input meter (Get Ready's input row binds the same `Settings.GameplayInputLevelDb`; `SampleInputLevel` is the one read), the latency chip, Devices (`AudioDevices`), Keybinds and the per-section reset. A DI singleton (`App.ViewModelServices`); it takes the four services only it uses (`IThemeService`, `IVulkanRenderService`, `AudioSettingsViewModel`, `IKeybindRegistry`), so Play Mode's constructor no longer does. Its `ProjectState` reads are the sixteen graphics/appearance toggles, `Preview3DCameraFill` and the live `Keybinds` table — shared state, not Play Mode's. What it needs from Play Mode (`_runSideEffectsApplied`, the practice speed, the two tone rows, the input tap) goes through `IPlayModeSettingsHost`, which Play Mode implements and hands it with `AttachHost`. `Dispose` drops its one subscription on shared state (the keybind table, held only while open). The seven settings views bind `{Binding Settings.…}`; `PlayModeView` follows `Settings.PropertyChanged` for the meter clock. Tests: `UI/ViewModels/PlayMode/PlayModeSettingsViewModelTests` |
  | `PlaySetlistViewModel.cs` (its own type, not a partial; #646) | Play Mode SETLISTS (solo), exposed as `Setlists`: Song Select's panel (`Views/PlayMode/Screens/SetlistPanelView`, at the foot of `SongSelectDetailRailView` — its height is capped by `SongSelectDetailRailView.SetlistPanelMaxHeight` so the picked song's Play button stays in view), Results' "Next: <song>" + "End setlist", Pause's "Skip song" / "End setlist", and Get Ready's progress line and skipped-song note. It implements the four Presentation setlist contracts (`Contracts/PlayMode/ISetlistParts.cs`: `ISetlistPanel` for Song Select, `IGetReadySetlist`, `IPausedSetlist`, `IResultsSetlist`), and every shipped setlist binding goes through them: `SetlistPanelView` is typed `ISetlistPanel`, and Get Ready / Pause / Results set `DataContext="{Binding Setlists}"` with the part's `x:DataType` (`Presentation/SetlistContractTests` fails a `Setlists.` path or an untyped scope). The same `SetlistPanelView` sits in the redesigned Song Select rail on `FakeSetlistPanel`, so the gallery shows the markup the app ships. The pure run state is `Services/Gameplay/Setlists/SetlistRunner`; lists persist per player profile as `profiles/<id>/setlists.json` through `PlaySetlistStore` (`AtomicFile`; Unreadable/Corrupt reads never write over the file). It drives Play Mode only through `IPlaySetlistHost` (`OpenSongAsync`, the two run exits, the practice speed); Song Select reached any way but a setlist verb ends the setlist. Tests: `UI/ViewModels/PlayMode/PlaySetlistFlowTests`, `Services/Gameplay/SetlistRunnerTests`, `Services/Gameplay/PlaySetlistStoreTests`, `Perf/PlaySetlistUiPerfSmokeTests` |

  Home's hero and Jump back in use `IShellDestinations.PlaySongAsync`, carrying
  the song key and arrangement identity through `HomeSnapshot`. The generic
  Play card still opens Song Select. Remembered-song loading keeps Home visible
  until Get ready is prepared, then switches directly to it. A missing song opens Song Select with
  `FlowError`; it must not open the previous chart's setup.
  `EnsureLibrary` shares its in-flight scan task so a Home request can await
  an initial scan after backing out. Own-chart loading requires a new project
  reference; a cancelled or failed replacement must not reuse the old chart.
  Home requests await `_homeSongGate` so returning Home during a load and
  choosing another song cannot run two chart replacements concurrently.

  **The Progress hub — the History and Trends screens — was removed on
  2026-09-19; restore from tag `pre-cut-2026-09`.** With it went everything only
  it reached: `.Progress` / `.Trends`, the take library (star / rename / delete /
  re-amp, `TakeReampService`), the "loop your worst bars" drill
  (`SmartLoopPlanner`, `WeakSpotHeatmap`, `LivePlayViewModel.ArmSmartLoopLadder`)
  and the `PracticeTrends` / `PracticeTime` / `ProgressInsights` /
  `SessionHistory` read-models. Take RECORDING, the results screen's
  listen-back and per-song stats stay; stored takes are left on disk untouched,
  and the session record keeps its `TakeStarred` / `TakeLabel` / `ReampFiles`
  fields so an upgraded store round-trips them and the sweep still honours a
  star (`Services/Gameplay/RemovedPracticeDataCompatibilityTests`).

  **The field rule** (stated in full on the root, `PlayModeViewModel.cs`): injected
  dependencies and plain private fields live in the ROOT, in one block, grouped
  by which partial reads them. `[ObservableProperty]` backing fields travel WITH
  their responsibility — the generator turns each into a public property, so it
  declares that screen's bound surface rather than shared state. `const`s stay
  with the algorithm that reads them. Partials are compile-time only, so the
  split changed nothing about the type; **the reason it is worth keeping is that
  every screen's code is now findable by FILE.**
- `ViewModels/PlayMode/PlayModeViewModel` + `Views/PlayMode/PlayModeView.axaml` are
  a **single surface** (each screen its own view in `Views/PlayMode/Screens/`
  since #294, declared by `PlayModeView` under its gate) toggled by `IsVisible` off a
  `PlayModeScreen` enum (`Start → Menu → SongSelect → TuningCheck
  → Countdown → Playing → Paused → Failed → Results`). Couch co-op and its
  `CoopSetup` screen, and the `Stats`, `Calendar`, `History` and `Trends`
  screens, were removed on 2026-09-19; restore from tag `pre-cut-2026-09`. The view is a
  **deferred host** (`MainWindow.axaml` `PlayModeHost`, filled by
  `MainWindow.EnsurePlayModeViewCreated`): built synchronously in
  DataContextChanged when Play Mode is the boot surface
  (`AppConfig.StartInPlayMode`), otherwise on the `IsPlayModeVisible` flip or
  the Background-priority prewarm — never inside `InitializeComponent`. Add a screen = enum
  value + `[NotifyPropertyChangedFor(nameof(IsXScreen))]` on `_currentScreen` +
  `IsXScreen` getter + a `NavigateBack` case + the XAML block. `NavigateBack`
  (Esc) walks one screen up; the shell owns Esc routing in
  `MainWindowViewModel.HandleEscapeKey`.
  - **`Start` is the BOOT screen and behaves unlike every other one.**
    `_currentScreen` is INITIALIZED to it, so `OnCurrentScreenChanged` never
    fires for it and nothing there can fill it — the launch screen is warmed
    from `PlayModeView`'s ATTACH instead (`EnterLaunchScreen`, latched and
    `DispatcherPriority.Background`-deferred internally, so it is never work in
    front of the first frame). **`Esc` on it dismisses an open launch panel and
    does NOTHING else — it does not quit, and this paragraph said it did until
    2026-09-04.** Esc is the key you press to back out of things; answering it
    by closing the program turns a reflex into a way to lose your place. The
    footer's "ESC QUIT" hint went with that behaviour and
    `UI/LaunchScreenTests` fails if either comes back;
    `MainWindowViewModel.RequestApplicationExit`, the delegate this line named,
    was assigned by the window and invoked by NOTHING and is gone —
    `ExitRequested` (File ▸ Exit, the palette's `file.exit`, the caption button)
    is the one exit seam, pinned by
    `UI/ExitCommandWiringTests.TheShellHasExactlyOneApplicationExitSeam`. The screen itself is
    on the Fire layer — see
    [fire/play-mode-screens.md](fire/play-mode-screens.md) § "The LAUNCH screen".
- **Every screen you touch needs a headless construction + layout case in
  `DotCharter.Tests/Perf/PlayModeScreensUiPerfSmokeTests.cs`.** A Play Mode
  screen is an `IsVisible`-gated panel and Avalonia skips `MeasureCore` (hence
  `ApplyTemplate`) for an invisible element, so a screen nothing navigated to
  has ZERO visual descendants and a build proves nothing about it. Each case
  drives the real navigation command, lays the view out in a real `Window`, and
  asserts the screen's named subtrees measure non-zero. It is
  `*UiPerfSmokeTests` (real app session ⇒ its own `dotnet test` run, outside the
  default gate — a PROCESS split, not a hang; see
  [../testing/perf-harnesses.md](../testing/perf-harnesses.md)) and it caught a
  duplicate `Icons.axaml` key on its first run — a fault that builds clean and
  takes the whole app down at startup. **Run it after touching a screen**: the
  suite is not in the gate, so nothing else will tell you it went red.
- **Full screens vs. modal overlays.** Song Select and Results are full
  screens (like the enum above). Amp/FX and
  the gameplay-settings menu are
  **centered scrimmed overlays** (`IsXOpen` bool + a `Grid Background="#D9…"`
  card); the **Pedalboard** is the same scrimmed pattern but a LARGE
  near-fullscreen card (≈92% of the window, floored 1100×760) since it hosts the
  whole rig surface. `PlayMode.TryCloseOverlay()` closes the topmost overlay first on Esc.
  Prefer this centered-overlay pattern over `Flyout` for menu-triggered panels —
  a Flyout anchored to a menu button clips off-screen (that was the Amp/FX bug).

- **The results board is PER DETECTOR; the library's best is NOT (#642).**
  `RunResultsViewModel.Show` (called from `.Results`' `OnSessionCompleted`) ranks the run's own detector's board
  (`ILeaderboardService.GetSongLeaderboard(…, result.Detector)`, #329) and
  captions it through `ResultsBoardCaption` (`ResultLeaderboardCaption`, one
  `PlayMode_Results_Leaderboard_{Cqt,Classic,AnyDetector}` key per
  `LiveDetectorMode`). Song Select's "Your best" (`GameplayStatsService.GetBestForSong`
  → `PlaySongItem.ApplyStats`) stays the song's best on ANY arrangement and
  EITHER detector, and its tooltip (`PlayMode_SongSelect_YourBest_Tip`) says
  so. Why cross-detector: it is what a player means by "my best"; every
  pre-0.64.0 run has no recorded detector (`PracticeArrangementProfile.DetectorBests`
  is null there) and would vanish from a per-detector library; and Song
  Select's best-score sort and grade filter read that one best. The board is
  per detector because it ranks runs AGAINST each other, which only a shared
  judge makes fair. Pinned by `UI/ViewModels/PlayMode/ResultsBoardDetectorCaptionTests`.
- **The results' technique line counts hits played WITHOUT their technique (#645).**
  `ResultsTechniqueLine.For(report.Techniques, …)` → `ResultTechniqueLine`
  (`Screens/ResultsView`'s `ResultsTechniqueLine`; the redesign's
  `IResultsScreen.TechniqueLabel` / `HasTechniqueLine`): bends, slides, vibrato and
  palm mutes with a `WrongTechnique` count, as "played without / judged"; empty —
  hidden — on a clean run. The counts are `LivePlaySessionService.LastTechniqueTallies`,
  carried on `LivePlaySessionReport.Techniques`. The bend/slide/vibrato verdicts come
  from the contour judge behind the Gameplay switches "Judge bends" / "Judge slides" /
  "Judge vibrato" (`AppConfig.LivePlayJudgeBends` ON by default, `LivePlayJudgeSlides` and
  `LivePlayJudgeVibrato` OFF, #730; each switch is disabled while the CQT verifier is off). Pinned by `UI/ViewModels/PlayMode/ResultsTechniqueLineTests`
  and `Perf/TechniqueContourViewsUiPerfSmokeTests`.
- **Five of the screens are DEFERRED CONTENT, and adding another has rules.**
  `MainWindow` fills `PlayModeHost` synchronously before `Window.Show()` (it must,
  or the first frame is empty), so anything declared inline here is constructed
  in front of the app's first frame — the `bind=` phase of `StartupPhases:`, and
  the largest of them. The gameplay settings modal, Get Ready, Pause, Song
  Select and Results (#648) are the `ContentTemplate` of a `ContentControl` host
  instead
  (`PlayModeView.DeferredScreenHostNames`; `bind` median 5,113 → 1,363 ms,
  `r2r=yes`). The gate lives on the HOST and may not be on both; a template body
  generates no fields, so the code-behind reaches a deferred screen it drives
  through a lookup that builds the body on demand (`PlayModeView.SongSelect`,
  `Results`); the boot surfaces stay inline; and
  `HiddenSurfaces()` materializes the bodies before it lists anything. Full rules
  + the measured table:
  [rendering-perf/startup-and-prewarm.md](rendering-perf/startup-and-prewarm.md).

## Where the detail lives

| Read this when… | File |
|---|---|
| you are touching `MenuBackdropControl` (the one custom-drawn backdrop — no style animations), the profile chip / switcher flyout, or the guest-session fast path | [play-mode/backdrop-and-profile.md](play-mode/backdrop-and-profile.md) |
| you are touching a screen's CHROME or layout: the shared Play Mode chrome, the 0.35 content-screen vocabulary, the removed front door, the 0.37.1 sweep, or the gameplay-settings overlay | [play-mode/chrome-and-screens.md](play-mode/chrome-and-screens.md) |
| you are touching Song Select: the shared filter/sort engine (`LibraryQuery`, `LibrarySort`), the tuning chip's name-vs-value rule, the centered column, duplicate collapsing, or listen-back | [play-mode/song-select.md](play-mode/song-select.md) |
| you are touching what happens once a run STARTS: resume rewind, rock-meter fail-out, the shared editor surface, count-in + metronome, keyboard/controller focus, the grade medal | [play-mode/gameplay-flow.md](play-mode/gameplay-flow.md) |
