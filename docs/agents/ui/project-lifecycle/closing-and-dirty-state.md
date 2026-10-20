# Project lifecycle — open / replace / dirty / audio / shutdown (UI deep reference) — closing, replacing and dirty state

## Closing, replacing and dirty state

- `MainWindow.OnClosing` calls `App.TerminateNow()` deliberately — managed teardown while VST3 plugins are alive access-violates. Don't add shutdown work after it. The unsaved-changes gate (dirty project ⇒ Save/Discard/Cancel via `MainWindowViewModel.AskAboutUnsavedChangesAsync`, i.e. `IUnsavedChangesScreen` on the dialog host, where Esc and a close from outside answer Cancel) runs FIRST and cancels the close — it must stay ahead of the terminate path or the prompt can never show. Dirty state is `IProjectSessionService.IsProjectDirty`, derived from the undo history (`ProjectDirtyTracker`), baselined on save/load. **It is VISIBLE three ways since 2026-08-15, and each binding is pinned by `UI/ViewModels/Application/StatusBarDirtyStateTests`** — before that `WindowTitle`'s asterisk was bound by NOTHING and `StatusBarViewModel.IsProjectDirty` was hard-assigned `false`, so no surface showed unsaved work until the close prompt: the window `Title` binds `WindowTitle` (taskbar/alt-tab — BorderOnly decorations draw no caption), and a `●` marker sits beside the project name in both the title-bar context and the status rail, fed by `StatusBar.DirtyStateSource` (a shell-assigned delegate, same shape as `OpenChartIssues`) and re-READ — never reset — on a project/path switch, because attach-audio changes the path while the session stays dirty. Ctrl+S on a path-less session and Ctrl+Shift+S both route through the real Save As picker (`SaveProjectAsAsync`) — never a silent relative `project.rscp`.
- **The save's UI-thread stall is a CONTRACT, so it is INDICATED — and cut to
  the one step that has to be there (both 2026-08-15).** `ProjectSaver.SaveProjectCoreAsync`
  runs `AssembleProject` → `ChartProjectCloner.Clone` on the UI thread before
  its first worker hand-off, deliberately — the clone is what makes a torn save
  impossible — and THAT may not move. `ChartSpacingEnforcer.EnforceProject`
  used to sit in front of it, over the LIVE project, and it was the larger half
  of the stall on a big chart; it now runs on the save worker over the
  snapshot (the file is what needs it — the live project is enforced on load
  and after every committed edit). `ProjectSessionSaveThreadingTests` pins the
  shape, the `Project saved.` log line prints `uiThreadMs / workerEnforceMs /
  workerWriteMs`, and `docs/agents/MAP.md` has the order. What the remaining
  stall lacked was any sign it was happening:
  `IStatusBarService.BeginTask("project-save", …)` now wraps the whole call and
  is completed in a `finally`.
  **A pill that EXISTS is not a pill that is SEEN, and this is the trap:**
  `BeginTask` inserts inline on the UI thread, but `StatusBarViewModel` picks
  the change up through a Background-priority post, and the save then runs
  straight through with no yield — so the dispatcher's first chance to draw
  anything was the `await` on the worker, i.e. *after* the stall. The save
  therefore waits one `IUiDispatcher.Post` turn before starting
  (`DrainToFirstPaintAsync`). Background priority sits BELOW Render in Avalonia,
  so a continuation resumed from there runs after the frame that drew the pill;
  **a plain `Task.Yield()` would NOT do — its continuation goes to the
  dispatcher at Normal, above Render.** The pipeline order is untouched; the
  whole sequence just starts one dispatcher turn later, inside the existing
  `_saveInProgress` guard. A host with no dispatcher (tests, headless) skips it.
- **A FAILED save is the outcome that may not be a faint status line.** It leaves
  the session dirty while the user believes their work is on disk. The outer
  catch now also raises the app-wide `MessageDialogViewModel` at
  `MessageDialogKind.Error` with the exception as its details — an affordance
  this service already depended on for the replace-audio prompt, not a new one.
  It is deliberately NOT awaited (the save has already failed, and awaiting
  would hang a host with no dispatcher loop). All of this is pinned by
  `Services/Session/ProjectSessionSaveOutcomeTests`.
- **CLOSING is not the only way to lose unsaved work — REPLACING the project is
  the other one, and there are six of those paths.** `ApplyProject(resetHistory:
  true)` clears the undo history, so the previous project's edits are unreachable
  the instant it runs (File → Open is the worst: it also `NotifyProjectSwitched`s,
  which DELETES the crash snapshot, so there is no recovery copy either). Every
  such path must `await IProjectSessionService.MayReplaceOpenProjectAsync()` and
  abandon on false: File → Open, New Project, the song browser's `LoadSongItem`
  (guarded IN that method — **Play Mode's song select calls it directly**, so a
  guard on the browser command would leave the game shell's path open), EOF
  import and checkpoint restore. Ask BEFORE the folder picker /
  any file write, and **close the owning dialog first** — the prompt is another
  overlay in the same `DialogHost` panel, where z-order follows materialization
  order, so a dialog left open can cover it and the awaited answer never arrives.
  The prompt itself is a delegate `ProjectMenuViewModel` assigns on construction
  (`ConfirmDiscardOpenProjectAsync`; pinned by `ProjectMenuViewModelTests`) rather than an injected
  dialog service: `IAppDialogService` is built from thirty view models that reach
  back into the session service, so injecting it would close a cycle. Null means
  no shell is listening and everything proceeds exactly as before, which is what
  keeps tests and headless hosts working. A seventh path cannot skip the guard
  silently — the source lint
  `ProjectReplaceGuardWiringTests.EveryReplacePathInTheUi_AsksTheGuard` fails,
  and its two exemptions each carry their reason.
- **A replace path's SPACING pass belongs on a worker, and the reason it is safe
  there is that the project is not applied yet.** `ChartSpacingEnforcer
  .EnforceProject` sweeps every level of every arrangement;
  `ProjectSessionService.LoadFile` has always run it inside the same `Task.Run`
  as the deserialize, but `SongBrowserViewModel`'s PSARC/library load ran the
  identical pass INLINE between the click and the first frame of the chart it
  had just opened — hundreds of milliseconds on a dense multi-arrangement song.
  It is on the worker now.
  `ProjectReplaceGuardWiringTests.EveryReplacePathsSpacingEnforce_RunsOnAWorker`
  is the lint, and it is **deliberately scoped to methods that also call
  `ApplyProject(`**: the other six `EnforceProject` call sites in
  `DotCharter.UI` (the beat drag, the event tool, the beat/BPM dialogs' undo
  callbacks, the settings write-back) run over the LIVE
  `ProjectState.ActiveProject` and MUST stay on the UI thread — moving one of
  those off would race the renderer's snapshot.
- **…and every replace path must DECLARE its dirty state on EVERY exit, not at
  the end of the `try`.** `IsDirty` is `CurrentNodeId != _baselineNodeId` over ids
  that only increase, so once `ApplyProject(resetHistory: true)` has minted a new
  root the baseline can NEVER match again — a path that returns without saying
  `MarkProjectClean`/`MarkProjectDirty` leaves the session dirty forever. The
  chart is applied first and the AUDIO is loaded after it, so both declarations
  used to sit behind fallible I/O: open a project (or, through
  `SongBrowserViewModel.LoadSongItem`, load a library song — the method Play
  Mode's song select calls directly, where `preferMemoryPlayback` reads the whole
  song into a `NativeFileBuffer` first) whose audio will not decode and the chart
  rendered correctly while the title wore a `*` no undo could clear, closing
  raised "unsaved changes", and `CrashRecoveryService` — same flag — started
  snapshotting it, so the restore prompt returned on every launch. Both are in a
  `finally` now, gated on a `sessionReplaced` flag set immediately after
  `ApplyProject`: **a failure BEFORE the swap must declare nothing**, because the
  previous project is still open and its dirty state is still the truth.
  **The crash-recovery dialog is the third case and the opposite sign**:
  `RecoveryViewModel.RecoverAsync` loads the SNAPSHOT (whose `LoadFile` ends
  clean, correctly) and then re-points `CurrentProjectPath` at the original
  `.rscp`, which does not contain the recovered work — so it must
  `MarkProjectDirty()` right after the repoint, exactly as
  `CheckpointsViewModel.RestoreAsync` does. Without it: recover a crash, close
  the app, `MainWindow.OnClosing` sees a clean session and `TerminateNow()`s with
  no prompt. The `EveryReplacePathInTheUi_DeclaresItsDirtyState` lint cannot see
  any of this — it greps the enclosing method for the two spellings — so the four
  behavioural cases beside it in that file are what hold the rule.
- **Save As moves the session onto the new path only AFTER the write lands
  (0.61).** `CurrentProjectPath` and `AppConfig.LastRscpDir` were assigned the
  moment the picker returned, so a Save As that then FAILED — no permission, disk
  full, the path taken by something else — repointed the whole session at a file
  that does not exist: the title bar named it, the next plain Ctrl+S wrote there
  without asking, the recent list learned it, and the project the user actually
  had open was no longer reachable from anywhere in the app. The failure dialog
  said the save failed; nothing said the session had moved. The tail is
  `ProjectSaver.SaveToPickedPathAsync` (internal so a test can drive it —
  the public entry hops through `Dispatcher.UIThread.InvokeAsync`, which never
  completes from a plain xunit thread), pinned in both directions by
  `ProjectSessionSaveOutcomeTests.SaveAs_ThatFailsToWrite_LeavesTheSessionOnTheProjectItHadOpen`
  and `.SaveAs_ThatSucceeds_MovesTheSessionOntoTheNewPath`.
- **…and Save As out of the audio's FOLDER rebases the audio reference
  (2026-09-04).** `musicFileName` is a bare name resolved against the project's
  own folder by `ProjectAudioLocator`, and Save As is the one operation that
  changes that folder — so the new `.rscp` named an audio file that had stayed
  behind, and the copy opened with no waveform, no playback, no stems and no
  message. `ProjectSaver.RebaseAudioReferenceForSaveAs` runs BEFORE the
  write (the reference is read by `AssembleProject` inside
  `SaveProjectCoreAsync`) and promotes the name to the absolute file it resolves
  to — but only when it would otherwise break: saving next to the audio keeps
  its PORTABLE relative name, and a reference that resolves from neither path is
  left alone rather than given an invented one. The promotion is not a one-way
  door, because `ProjectAudioLocator.TryResolve` now falls back to a stale
  ROOTED path's bare name beside the project (which also fixes a moved
  PSARC-derived session). Three cases in `ProjectSessionSaveOutcomeTests`, plus
  `ProjectAudioLocatorTests.AStaleRootedReference_FallsBackToTheNameBesideTheProject`
  and `.ARootedReferenceThatExists_BeatsASameNamedFileBesideTheProject`.
- **`MarkDirty` now ARMS a crash-recovery snapshot (0.61).**
  `CrashRecoveryService` listened to `HistoryChanged` alone, so every
  `MarkProjectDirty` caller above — attach/replace audio, the checkpoint restore,
  the recovery restore, the new-project wizard — set the flag this file spends so
  much space on and was then never snapshotted: the app knew the project was
  dirty and had nothing to recover. `ProjectSessionService` wires
  `ProjectDirtyTracker.DirtyMarked` (which fires on EVERY `MarkDirty`, unlike the
  edge-triggered `IsDirtyChanged`) to `ICrashRecoveryService.NotifyProjectChanged`.
- **…and `MarkClean` now RETIRES one, because arming was the only half wired
  (0.62).** The owner: "sometimes it still asks to restore the project from
  loaded PSARCs even though there were no changes." A change SIGNAL is not a
  change — `ApplyProject(resetHistory: true)` raises `HistoryChanged` on **every**
  replace path, so merely opening a song armed the scheduler and 10 s later the
  app wrote a full crash snapshot for a chart nobody had edited. Only `LoadFile`
  disarmed it (`_crashRecovery.NotifyProjectSwitched()` in its finally);
  `SongBrowserViewModel.LoadSongItem` — the method **Play Mode's song select**
  calls — declared `MarkProjectClean()` and nothing else, so a library load left
  the scheduler armed and the OUTGOING project's snapshot on disk. A PSARC
  session has `CurrentProjectPath == ""`, so its snapshot lands under
  `RecoverySnapshotStore.UntitledKey` and `IsRecoverable` has no original file to
  date it against — **untitled is recoverable unconditionally**, which is why it
  came back every launch. Fixed at the DECLARATION, not the call site, so a new
  replace path is covered for free: `ProjectDirtyTracker.CleanMarked` (every
  `MarkClean`, not the edge — browsing one library song after another never flips
  it) drives `NotifyProjectSwitched`, and `ICrashRecoveryService.HasUnsavedChanges`
  — handed `ProjectDirtyTracker.IsDirty` from the same ctor — is the
  did-anything-change gate `CrashRecoveryService.RunTickAsync` consults before
  writing anything. `ProjectSessionRecoveryWiringTests` pins all three wirings;
  `CrashRecoverySnapshotGateTests` pins the behaviour, including that an EDITED
  PSARC song still snapshots and is still recoverable after a crash.
- **…and a replace path must assign the PROJECT-SCOPED STATE beside the chart
  UNCONDITIONALLY — clearing is a declaration, silence is inheritance.** Two
  fields sit outside `ChartProject` and are read as if they described the open
  project: `ProjectState.CurrentAudioFileName` and
  `ProjectState.CurrentSongIdentity`. The first is the one that reaches DISK —
  `ProjectDocumentService.AssembleProject` writes it into
  `project.AudioFileName` unconditionally and is the entry point for the manual
  save, the auto-backup, the crash snapshot, checkpoint create, the Rocksmith
  export and the diagnostics bundle. `LoadFile` used to assign it only INSIDE
  `if (!string.IsNullOrEmpty(project.AudioFileName))`, the lone conditional
  among the replace paths, so an audio-less chart (an RS1 chart-only import)
  opened after a song with audio inherited that song's name and the first save
  stamped the wrong `<musicFileName>` into it — `AttachAudioAsync` also read the
  stale value and asked "replace the song audio?" for a project with none.
  `CheckpointsViewModel.RestoreAsync` had the same hole from the other side: a
  checkpoint CARRIES the reference (`Create` snapshots it through
  `AssembleProject`) and the restore never rolled it back, so a checkpoint taken
  before an attach came back with the post-attach name. The identity is
  cache-facing rather than disk-facing: `SongBrowserViewModel.LoadSongItem` is
  its only writer (`psarc:<SongKey>`) and nothing cleared it, while
  `ProjectSessionService.BuildWaveformCacheIdentity` reads it BEFORE the project
  path — so after one library song every own-chart project keyed its waveform
  pyramid under that song, overwriting the library entry and never hitting its
  own. All three are pinned behaviourally by
  `Services/Session/ProjectSessionLoadFileStateTests` and
  `Presentation/Dialogs/CheckpointsViewModelTests`;
  `InitializeDefaultProject` declares all three fields by inspection only (its
  caller `Initialize()` boots the audio engine, so it has no default-gate test).
- **A load SPEAKS for itself, at both ends.** Until 0.61 `LoadFile` said nothing
  until it was finished: the deserialize, the spacing enforce and the audio
  decode all ran under whatever message the PREVIOUS project had left on the
  rail, which on a dense chart is seconds of silent wait after a click. It now
  begins the `project-load` pill (`LoadTaskId`) before the work and completes it
  in the `finally`, so it clears on the throwing path too — the same shape as
  `SaveTaskId`. **And a project whose declared audio is GONE now says so**: that
  case fell through to the silent-transport fallback and reported
  `Status_LoadedChart`, so the chart opened looking normal, played silence, and
  the only record of why was a log line. It reports
  `Status_ProjectAudioMissingOnOpen` at `StatusSeverity.Warning`, naming the
  missing file and the route that repairs it (File ▸ Attach audio…). An
  audio-less chart that declares NO audio is a legitimate state and is
  deliberately NOT warned about — `ProjectSessionLoadFileStateTests` pins both
  arms plus the pill's two ends.
- **Audio: ATTACH ≠ audition.** `IProjectSessionService.LoadAudioFile` only pushes a
  file through the engine — it does NOT write `ChartProject.AudioFileName`, so a save
  emits the `song.wav` fallback and the reopened project finds nothing.
  `AttachAudioAsync` is the one that makes audio stick: it frees the current source
  (or the silent transport), copies the file into the project folder via
  `ImportAudioPreparer` (0 s silence — an attach must never shift a chart that
  already has beats), sets BOTH `project.AudioFileName` and
  `ProjectState.CurrentAudioFileName`, and marks the session dirty
  (`ProjectDirtyTracker.MarkDirty`, for state the undo history can't represent). A
  session with no project path stores the ABSOLUTE source path — both resolvers
  (`ProjectSessionService.ResolveProjectAudioPath`,
  `ChartMaintenanceService.ResolveCurrentAudioPath`) accept a rooted name. Reachable
  as File → Attach audio (`MainWindowViewModel.AttachAudio`), the palette
  (`file.attachAudio`) and any audio drag-drop.
- **Open Recent (0.58): the MRU list is `AppConfig.RecentProjects`, written by
  exactly ONE funnel.** `ProjectSessionService.RecordRecentProject` is the only
  writer, called from the two success points — `LoadFile` after the session swap
  (a failed parse or a declined discard prompt never enters the list) and
  `SaveProjectCoreAsync` after the atomic swap (a save-as is how a new project
  gains a path). The pure rules (cap 10, case-insensitive dedupe, front
  insertion, missing-file prune on read) are `RecentProjectsList` in
  `DotCharter.Services/Infrastructure/Configuration/RecentProjects.cs`, pinned
  by `RecentProjectsListTests` + two behavioural cases in
  `ProjectSessionSaveOutcomeTests`. Opening a recent goes through
  `ProjectMenuViewModel.OpenRecentProjectAsync` → `LoadFile` — the SAME funnel as
  File ▸ Open, discard guard included; a file deleted since the menu rendered
  gets a status message and a list refresh instead of a load error. The menu
  rows are dynamic (`MenuItem.ItemsSource` over `RecentProjectItem`, each
  carrying its OWN open command — no ancestor-DataContext lookup inside the
  popup's namescope), and the palette mints `file.openRecent.N` entries on each
  open. **MRU paths are user data — never log them at Info level.**
- **`AudioFileTypes` is the single "is this song audio?" list** (`.wav .ogg .mp3
  .flac .m4a .aac`). It used to be re-declared in three places that disagreed, so a
  dropped `.mp3` fell through to the PROJECT loader and reported "error loading
  project". Never re-inline the extensions — extend `AudioFileTypes`.
- **`ViewModels/Shell/DroppedFileRouter.Route` is the ONE extension→subsystem
  table, and `ProjectMenuViewModel.LoadFile` may not test an extension itself.**
  The window's file DROP (`MainWindow.axaml.cs`, `DragDrop.DropEvent` → first
  file only) and File ▸ Open Recent both funnel through `LoadFile`. It tested
  `EndsWith(".eof")` INLINE for several releases, which is exactly why every
  other importable format stayed unrouted: a dropped `.gp5`/`.mid`/`.psarc`
  reached `ProjectDocumentService.ResolveFormat`, threw and came back as "Error
  loading project" — the `AudioFileTypes` bug one level up. Current table:
  `.rscp`→project · audio→`AudioFileTypes` · `.eof`→`OpenEofImport` (the
  shell's door onto `EofImportLauncher.OpenAsync`) · `.mid/.midi`→`OpenMidiImport`
  (the launcher refuses, and says so, with no project open) ·
  RS `.xml`→`OpenRsXmlImport`, MERGING into the open project and
  falling back to the project loader when none is open · `.gp/.gpx/.gp3-7/.gpa`
  → `Dialogs.GpImport.OpenWithFile` (until 0.44.5 this arm opened the GP
  dialog's PICKER at the file's folder, because the GP dialog had no path-taking
  seam; `OpenWithFileAsync` is that seam now — its `GetAvailableTracks` read
  runs OFF the UI thread behind `IsBusy`, since for `.gp`/`.gpx` it inflates
  the zip and loads the whole score as an `XDocument`, not a header) · `.psarc` → a status
  message naming the Song Browser, because `SongBrowserViewModel.LoadSongItem`
  takes a scanned `PsarcSongListItem`, not a path · anything else → a message
  naming the file TYPE. Router + lint are `UI/DroppedFileRoutingTests`.

