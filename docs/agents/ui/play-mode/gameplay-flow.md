# Play Mode — the gameplay run itself

Part of the Play Mode reference. Router: [../play-mode.md](../play-mode.md).

- **EVERY way out of a run unwinds it, and `EndRunSideEffects` is the only
  place that does.** A run leaves six things applied — the practice playback
  speed, the per-playthrough highway scroll speed, the drop-tuner pitch shift,
  the tuning-match SONG shift (#312), the backing-track mutes and the in-game
  rig — so an exit that does not call it carries all six into the chart editor
  and into every later song. Four exits
  did not, and all four are fixed (2026-09-05, `UI/ViewModels/PlayMode/PlayModeRunUnwindTests`):
  **Esc on the Results screen** (`NavigateBack` was a bare screen assignment
  beside a "Song select" BUTTON that unwound; it calls `BackToSongSelect` now,
  which also refreshes the song stats the keyboard exit used to skip), the
  **section breakdown's "Practice" link** (`OpenSectionInEditor`, the one editor
  bridge whose sibling `OpenInEditor` already unwound), and **abandoning a
  countdown** by either route (`CancelCountdown` for Esc, `NavigateBack` for
  Back) — a countdown is reached from Retry / Restart / retry-from-section
  INSIDE a run chain, with the previous run's effects live.
  **The drop-tuner restore is keyed on `_runAppliedPitchShift`, never on
  `DropTuner.IsOn`** — that switch is a pre-play control `PreparePrePlay` reloads on
  every song pick, so a refused run (which lands the player straight back on the
  pre-play screen) stranded the shift the moment they picked another song.
  The tuning-match song shift follows the same shape on its own flag,
  `_runAppliedSongTranspose`, with `_preRunSongPitchShift` captured once per run
  chain (`UI/ViewModels/PlayMode/PlayModeTuningMatchTests`).
- **Play in my tuning (#312) shifts the SONG, never the frets.**
  `TuningMatchViewModel` (Get Ready's row, `GetReadyTuningMatch`) asks the pure
  `DotCharter.Tone/Audio/Tuning/TuningMatchTranspose` whether EVERY string of the
  path sits the same distance from the player's own tuning (`PlayerProfile.MyTuningId`,
  a `TunerTunings` id; empty = E standard) within `MaxShiftSemitones`. Only then
  is the switch live; anything else (Drop D against E standard, too far, an
  unreadable tuning, a 5/7-string path) shows it OFF and disabled with the reason
  printed, while the stored opt-in (`PlayerProfile.PlayInMyTuning`) survives.
  `EffectiveSemitones` is the ONE number a run reads: `BeginGameplay` sets
  `TransportViewModel.PitchShift` (the engine's Rubber Band, master source and
  stem group alike, replayed on a rebuild) and `LivePlayViewModel.TransposeSemitones`
  (judging expects every charted fret that much higher, `LiveNoteMatcher.TransposeSemitones`),
  the guided tuning check aims at the player's strings, the tuning lockup prints
  the player's tuning, and `LastVerifiedTuning` is not written for a shifted run.
  The drop tuner and the song shift never stack (the drop tuner wins). The shift
  is LIVE, not an offline render, so there is no shifted-stem cache.
- **The drop tuner (#635) shifts the INPUT, per SONG.** `DropTunerViewModel` (Get
  Ready's row `GetReadyDropTuner`, above "Play in my tuning") answers a verdict
  per path off `ArrangementOption.UniformTuningStep`: `Shift` for a uniform step
  between -1 and -`MaxDropSemitones` (12, the transport's clamp); `NotUniform`,
  `AboveStandard` (the shifter only tunes down) and `TooFar` show the switch OFF
  and disabled with the reason printed; a path in standard hides the row. The
  opt-in is stored per song in `AppConfig.DropTunerSongs` (song identity,
  case-insensitive; off is never written) and survives a refused path.
  `EffectiveSemitones` is what `BeginGameplay` writes to
  `TransportViewModel.InputPitchShiftSemis`. While it is on,
  `TuningMatchViewModel.SetDropTunerOn` shows "Play in my tuning" off and
  disabled with `PlayMode_TuningMatch_DropTunerWins` (the profile's opt-in
  survives), the tuning lockup and the guided check aim at standard, and
  `LastVerifiedTuning` is not written. Tests: `UI/ViewModels/PlayMode/PlayModeDropTunerTests`,
  `Perf/GetReadyDropTunerUiPerfSmokeTests`, `UI/DropTunerExperimentalLabelTests`.
- **`PracticeSpeedPercent` belongs to the PLAYER** — a run's unwind restores
  the TRANSPORT's speed, never the picker. The two run-scoped overrides that
  once wrote it (the smart-practice drill and a LAN host's speed) and their
  `OverridePracticeSpeed` stash-and-restore were removed on 2026-09-19; restore
  from tag `pre-cut-2026-09`, and bring the stash back with any new override.
  And the gameplay-settings overlay's Speed row applies to the run it was opened
  over, through `PlayModeSettingsViewModel.ApplyGameplaySettingsToLiveRun` (it
  reads the speed back through `IPlayModeSettingsHost`) — the same seam the metronome
  and claps rows use, and for the same reason: the overlay opens from the PAUSE
  menu so the song can be slowed where it is hard, and until 2026-09-05 only
  `BeginGameplay` reached the transport, so a mid-run change did nothing at all.
  (`UI/ViewModels/PlayMode/PracticeSpeedOwnershipTests`.)
- **Resume rewind (Rocksmith pause semantics).** `ResumeGameplay` seeks BACKWARDS by
  `AppConfig.GameplayResumeRewindMs` (default 1500, 0 = off; Off/1/1.5/2 s in the
  gameplay-settings overlay) BEFORE starting the transport, via
  `LivePlayViewModel.BeginResumeRewind` → `LivePlaySessionService.BeginResumeRewind`.
  The session arms it as its OWN seek, so nothing already judged is rewound: the
  replayed notes cannot score or miss again and render DIMMED on the 3D highway
  (`FrameSnapshot.LivePlayResolvedThroughMs` + `DeadNoteMath`). **Only the resume path
  gets it** — `RestartRun`/`AbortRun`/`EndRunFromPause`/`OpenRiffRepeater` must not —
  and it is SKIPPED on a LAN sync run (`_isSyncRun`, set for the run's lifetime in
  `BeginGameplay`), where rewinding one machine desyncs the match. Invariants live in
  [docs/agents/services/live-play.md](../../services/live-play.md) — read them before touching it.
- **Rock-meter fail-out (0.24 performance stakes).** Opt-in
  (`AppConfig.GameplayRockMeterEnabled`, default OFF; mirrored toggle in the
  gameplay-settings overlay's Gameplay section + Settings → General → Gameplay).
  The pure `RockMeter` (see [services/gameplay.md](../../services/gameplay.md)) is
  fed by `LivePlayViewModel` — the single judgment funnel + 30 Hz tick — and
  held by its `Meters` (`ViewModels/Editor/LivePlayMetersViewModel.cs`, which
  takes no lock: every call arrives under `_sessionGate`); armed ONLY in
  `BeginGameplay` through `LivePlayViewModel.ArmRockMeter` (the gate-taking door). Hard exclusions: a LAN sync run (`_isSyncRun`)
  never arms, `OpenRiffRepeater` disarms mid-run, editor practice / free play /
  listen-back never arm. **`RockMeterFailed` is posted from ONE helper
  (`LivePlayMetersViewModel.PostRunFailed`), driven by the meter's own `Failed` EVENT
  — subscribed in `ArmRockMeter`, dropped in `DisarmRockMeter` — plus sudden
  death; never from a `Tick` return value alone.** That was the bug: the only
  fail detector was `PumpRockMeter`'s `Tick`, and `OnJudged` discards what
  `OnVerdict` did — so a debounce crossed INSIDE a verdict (reachable, because
  `DetectedAtMs` is a raw detection timestamp while the pump's playhead is
  latency-compensated and trails it) latched `HasFailed`, raised `Failed` into
  the void, and left every later `Tick` returning false forever. The run then
  hung UNFAILABLE at zero energy. The helper's one-shot is what keeps a failure
  seen by both routes to one fail. Pinned by
  `UI/ViewModels/Editor/LivePlayRunFailRoutingTests` and
  `UI/ViewModels/Editor/LivePlayMetersViewModelTests`. On `Meters.RockMeterFailed`
  `PlayModeViewModel.OnRockMeterFailed` ends the run EXACTLY like `AbortRun` —
  screen FIRST (`PlayModeScreen.Failed`), so `OnSessionCompleted` ignores the
  stop and no results flash — but captures a pause-style freeze-frame and lands
  on the fail overlay via `ShowPauseSurfaceRequested` (never a Flyout). The
  session still records, marked failed (`PracticeSessionRecord.Failed`,
  schema-tolerant nullable; History run rows show a FAILED tag; bests only
  improve, invariant untouched). Three actions: **retry-from-section**
  (`RetryFromFailedSection` → `BeginCountdown(sectionStartMs)`; `BeginGameplay`
  folds the override through `GameplayStartPlan.ResolveStartMs` with the same
  lead-in + count-in, floored at the ordinary run start — a NEW run, scoring
  restarts), **restart** (existing `RestartRun`) and **quit** (existing
  `AbortToSongSelect`). Esc = quit, routed in `NavigateBack` — and
  `MainWindowViewModel.HandleEscapeKey` special-cases the Failed screen because
  `IsGameplayPresentation` is still true on the pause surface (a plain fallthrough
  would call `PauseGameplay`, a no-op, and Esc would read dead). Every countdown
  states its start target (`BeginCountdown(null)` clears the override), so an
  abandoned retry can never leak into a later run; `LaunchMultiplayerRun` clears
  it explicitly.
- Gameplay runs on the **editor surface** (transport + Live Play + 3D highway),
  not inside PlayModeView. The pause menu (a Play Mode SCREEN, i.e. another tab)
  floats over a `PrintWindow` self-capture of the frozen highway
  (`Views/Shell/MainWindowCapture.CaptureSelfBitmap`) — the editor surface is not
  visible behind that tab, so the still IS the backdrop. Immersive layout hides all chrome
  (`Views/Shell/MainWindowShellLayout.ApplyGameplayLayout`).
  **Nothing above the highway means no row and no splitter.** During a run the
  only view that can sit above it is the notation band (the HUD's notation
  toggle, `IsSheetMusicVisible`); `MainWindowShellLayout.ApplyGameplayRows` gives
  it the top row and makes `DockRowSplitter` the one seam between band and run,
  and collapses both rows when it is off. `Layout/EditorRowSplitterPolicy` decides
  which splitter shows (`IsDockRowSplitterVisible` / `IsSheetSplitterVisible`), and
  `MainWindowLayoutController.ApplyBottomDockRowSizing`, which runs POSTED after a
  run starts, leaves the splitter row alone during a run. Pinned by
  `UI/EditorRowSplitterPolicyTests` and `Perf/GameplaySplitterUiPerfSmokeTests`.
  **Every gameplay override is SAVED and RESTORED, never absorbed.**
  `ApplyGameplayLayout` snapshots the grid rows/columns it collapses; the
  pre-run panel snapshot does the same for the panes; the minimap keeps its own
  preference. The sheet strip was the one that leaked (fixed 2026-09-04): a run
  caps the notation band at 240 px, and CLOSING the strip mid-run wrote that cap
  back to `AppConfig.LayoutSheetHeight` as the user's editor preference, so one
  run permanently shrank a 480 px score view. The cap and the write-back rule
  are one pure type now, `Views/Shell/SheetStripHeightPolicy`
  (`SheetStripHeightPolicyTests`) — a new gameplay override belongs there or in
  the save/restore pair, never in a value the editor reads back.
  The 3D preview is composed-only, so there is no airspace: dialogs and the rig
  render live above the highway, with no panel-collapse and no freeze-frame
  (`UI/Preview3DComposedOnlyTests`). The PAUSE/FAIL backdrop is not an airspace
  workaround and stays — a frozen still behind the menu is the intended look.
  - **In-game HUD (0.20 — the strip is game chrome, not editor chrome).** The
    Row-0 bar of `Preview3DView` is mode-branched: two Borders share ONE Grid
    cell with mutually-exclusive `IsVisible` off `IsGameplayPresentation` — the
    editor keeps its practice / live-play toolbar VERBATIM (parity is by
    construction, not by re-styling), and a game run swaps in a `PanelGlass` HUD
    where the score is the dominant figure, the multiplier a compact `×N` chip,
    with accuracy + hits/misses and QUIET Pause / Quit / Notation icon buttons
    (`Classes="Subtle GameIcon"`, ToolTip only, no text). The MainWindow 3D-pane
    header (`Menu_Panel3DPreview` title + pop-out) is
    `IsVisible="{Binding !IsGameplayPresentation}"` so it returns in the editor.
    **Diagnostics are gated OFF the HUD** and keyed on the detection monitor
    (`LivePlay.ShowDetectionMonitor`, the "Monitor" toggle): the detection-load
    chip collapses to a discoverable ⚠ glyph (tooltip) unless the monitor is
    open, and the bottom-left GPU render-stats line is suppressed ON THE HIGHWAY
    during a run unless the monitor is open — wired through the
    `GameplayPresentation` styled property on
    `Preview3DComposedCanvas` → `Preview3DRenderDriver.SetGameplayPresentation`
    (editor mode always shows the stats line, unchanged). Dead-input and
    tuning-drift alerts stay HUD-visible (player-facing, not diagnostics).
    The opt-in **rock meter** renders as a compact energy bar LEFT of the score
    cluster (`LivePlay.Meters.IsRockMeterActive` gates the whole panel — disabled or
    excluded runs collapse it): token zone colors via `Classes.Warn`/`.Danger`
    on a themed `ProgressBar` plus a STATIC critical frame
    (`Border.RockMeterFrame.Critical`), all state-driven from the same
    coalesced 30 Hz tick as the score — no style animations on this hideable
    surface, and never the raw 250 Hz playhead.
- **Count-in + metronome (0.14.4 C).** `BeginGameplay` optionally leads in with an
  audible count-in (`AppConfig.GameplayCountInBeats` off/4/8) via
  `RunCountInThenStartAsync` (awaits the pure `CountInPlan` schedule, plays
  `TransportViewModel.PlayMetronomeClick`, THEN starts the transport; skipped on
  LAN sync runs). The gameplay metronome (`AppConfig.GameplayMetronomeEnabled`)
  arms `TransportViewModel.GameplayMetronomeActive`, which `AudioPlaybackCoordinator`
  ORs with the editor toggle so the existing beat-crossing tick clicks every beat
  — cleared in `EndRunSideEffects`. Settings → General → "Gameplay".
  **Pre-first-note count-in (0.24.x).** When the count-in is on AND the first
  expected sound sits ≥ N+2 beats past the run start, `BeginGameplay` ALSO arms a
  count-in on the N beats immediately before the first note (`FirstExpectedSoundMs`
  → the pure `FirstNoteCountInPlan`), so a long noteless intro is announced instead
  of dumping the player into silence. Unlike the run-start pre-roll (trigger-now,
  before the transport starts), these land ON chart beats, so `LivePlayViewModel`
  schedules them sample-accurately over the render clock (`TryScheduleClick`) as
  the playhead reaches them — NEVER trigger-now (one output latency late). Skipped
  on LAN sync runs; see [services/gameplay.md](../../services/gameplay.md).
- **A REFUSED run unwinds to the PRE-PLAY screen, and that screen must be able
  to say why.** `_livePlay.IsActive = true` can fail (no input device, no
  chart); `StartRun` then unwinds. It used to set `SongSelect` — two screens
  back, after the user had already sat through Get Ready and the count-in, with
  the reason as a small `FlowError` string in the browse bar at the far corner
  and their whole run setup (path, scroll speed, stem mutes, drop tuner) off
  screen. Worse, Get Ready is the screen that DIAGNOSES the refusal: the input
  chip and the live signal probe are both on it. It now lands on the screen
  `PlaySong` routed through — `TuningCheck` — and it renders `FlowError`
  (`GetReadyFlowError`). `UI/RefusedRunLandingTests` pins both halves: the
  landing is a pre-play screen, and every screen the refusal block names binds
  `FlowError`. **The second half is the load-bearing one** — `FlowError` is
  rendered by only a few screen roots, so almost any reroute would
  silently drop the explanation. Headless:
  `PlayModeScreensUiPerfSmokeTests.GetReadyScreen_showsWhyARunWasRefused`.
- Play Mode's root Panel sets `XYFocus.NavigationModes="Keyboard"` (inherited)
  so arrow keys walk the screens' buttons; ListBox/ComboBox/TextBox keep their
  native arrow handling. The Results screen runs a staged reveal
  (`Screens/ResultsView.RunRevealAsync`, started and skipped by `PlayModeView`:
  score count-up → grade stamp → **FC badge** → record flash; any key/click skips) — the score TextBlock is deliberately
  UNBOUND, the reveal writes it from `RunResults.ResultScoreValue`/`ResultScore`.
- **Grade "medal" + FC badge (0.24.x — presentation only).** The services already
  ship the letter grade (`GradeCalculator`) and the sticky full-combo flag
  (`SongPlayStats.FullCombo` / `LivePlaySessionResult.IsFullCombo`); these ARE the
  "medals". Surfaced two ways: on Song Select rows the grade chip
  (`GradeLabel`, `HasBest`) is joined by a small outlined `SuccessColor` FC badge
  (`PlaySongItem.IsFullCombo`, stamped in `ApplyStats` from `stats.FullCombo`) —
  both shown ONLY when earned, no empty placeholder; and on Results the `FcStamp`
  (`ResultIsFullCombo` from `report.IsFullCombo`) stamps in a beat after the grade
  in the same staged reveal. Read-only projection over the existing profile/stats
  read paths — no scoring change. There is deliberately NO "Score Attack preset"
  concept: it would require new game rules, so it is out of scope.

A first countdown can be cancelled before `BeginGameplay` captures any baseline.
`EndRunSideEffects` restores transport and highway settings only if the run had
applied them; repeated cleanup likewise leaves subsequent editor settings alone.
`PlayModeRunUnwindTests.CancellingTheFirstCountdownPreservesTheEditorsSettings`
covers both dismissal paths.
