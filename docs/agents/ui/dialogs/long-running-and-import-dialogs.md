# Dialogs (`Views/Dialogs/`) — UI deep reference — what a long-running dialog owes the user; the four import dialects

## A long-running dialog owes the user three things

**Batch Lint is the worked example, because it was missing all three.** It walks
a folder tree with `SearchOption.AllDirectories`, which on a real library is
MINUTES of I/O, and in its saving mode it REWRITES `.rscp` files the user never
opened. It shipped with an indeterminate bar, no token, and a `Cancel()` that
early-returned while busy — so the footer's Close **and Escape** (both route
through `RequestClose` → `CancelCommand`) were dead for the whole run.

- **PROGRESS is per unit of work, not a spinner.**
  `IBatchChartLintService.Run` takes `IProgress<ChartLintProgress>` and reports
  BEFORE each file (so the label names the file being worked on, not the one
  just finished) plus once after the loop. The `Progress<T>` is constructed on
  the UI thread, so the worker never raises `PropertyChanged` itself.
- **CANCEL means the run, not the dialog, and it stops on a WHOLE-UNIT
  boundary.** The token is checked between files and never inside one — the save
  half stages a temp file and then swaps it in, so a mid-file stop could abandon
  a project between the two. The partial result carries
  `ChartLintBatchResult.Canceled` and the dialog says so: a partial sweep
  reported as a plain summary reads as "your library is clean".
- **A bulk write with NO UNDO takes a pre-flight confirmation that names the
  COUNT, and reports what it did afterwards.** `ConfirmOverwriteAsync` /
  `ReportOverwriteSummaryAsync` are delegates the shell assigns in
  `AppDialogService` (the message dialog lives on `IAppDialogService`, which is
  built FROM this view model — injecting back would close a cycle, same reason
  as `IProjectSessionService.ConfirmDiscardAsync`). **Unlike that one, null does
  NOT mean "proceed": an unconfirmable overwrite is REFUSED.** Counted strings
  go through `LocalizationService.Plural` and their pairs are in
  `UI/PluralStringTests.PairList`. Pinned by
  `Presentation/Dialogs/BatchLintViewModelTests` (cancel-while-busy, the
  three gate outcomes, the report) and `Services/Editing/Linting/BatchChartLintServiceTests`
  (per-file progress, the whole-file cancel boundary, the untouched remainder).

**A dialog that closes and THEN works is worse than one that stays.** Level
Generation used to set `IsOpen = false` and run `GenerateLevels` synchronously —
up to twenty levels off a full project snapshot, on the UI thread, behind a
window that had just stopped showing anything about it. It stays open now, with
per-level progress and a `StatusBarService.BeginTask` pill, and closes when the
work is done (`Presentation/Dialogs/LevelGenerationViewModelTests`; on the dialog host, Esc is refused mid-run through `ICancelableDialog`). **Staying open is
load-bearing, not cosmetic**: the scrim and `GlobalChordGate.HasOpenDialog` are
what stop the live project being edited between the coordinator's snapshot and
its history node. Its footer actions disable against `IsBusy` and there is
deliberately NO cancel — generation is one transaction that cannot be
half-applied, so the honest thing is a visibly unavailable gesture, not a live
one that does nothing. (Batch lint is the other case: minutes long and genuinely
stoppable, so it gets a real cancel.)

**…and a CANCEL that only hides the dialog is worse than no cancel at all.**
The Songsterr import (deleted in #820) shipped a token that reached one of its
awaits and a `Cancel()` that only closed the overlay, so a dismissed import went
on downloading, raised the replace guard and wrote a project behind a dialog the
user had backed out of. Three rules came out of it, and they generalize:

- **The token reaches EVERY await of the run**, not the one that was easiest to
  wire.
- **Cancel cancels the RUN while one is in flight and only closes the dialog
  when nothing is running** (the Batch Lint shape): `YoutubeAudioViewModel.TryCancel`
  returns false while a download runs, and the dialog answers null from the run's
  own `OperationCanceledException` path once yt-dlp's process tree is killed.
- **…and work that has already ARRIVED is refused by no token.** A run whose last
  await has returned must re-check `ct.IsCancellationRequested` (and, for a
  write, that the dialog is still wanted) before it builds or writes anything.

**The one phase that may NOT stay in the dialog is the one that raises another
overlay.** A replace prompt, a folder picker or the unsaved-changes gate is
another dialog on the same host (see [project-lifecycle.md](../project-lifecycle.md)).
So the dialog answers what it fetched and closes first, and a LAUNCHER acts on
the answer: `Presentation/Screens/Dialogs/YoutubeAudioLauncher` hands the
downloaded mp3 to `IProjectSessionService.AttachAudioAsync`, whose "replace the
song audio?" question would otherwise open under the YouTube dialog, and deletes
the download again when the attach does not keep it
(`Presentation/Dialogs/YoutubeAudioLauncherTests`).

**A failure has two homes and the dialog's own visibility picks between them.**
While the dialog is open it is a `Border.DlgStrip Error` inside it — distinct
from the `Notice` strip that carries an OUTCOME (no sync, a locked beat map, a
count mismatch), which is a different thing from a failure. Once the dialog is
closed the same failure goes through the app-wide `MessageDialogViewModel`
(`ShowAsync` is `virtual`, so a test asserts the call without a dispatcher
loop), because a strip on a dismissed overlay is a message nobody will read.
Both used to be one `DlgHint` line, and the video load's failures were a LOG
LINE and nothing else — the dialog just sat there with an empty recording picker
and Resync silently unavailable.

**A prompt raised FROM an open dialog needs the z-order to allow it.**
`MessageDialogView`'s own comment said "topmost, hence ZIndex 120" and 120 is
NOT topmost — nine dialogs sit at 200, so a confirm raised from one of them
would have rendered behind its own caller's scrim and the awaited answer could
never arrive. It is `ZIndex="300"` now and `UI/DialogZOrderTests` fails the gate
if anything reaches it. (The unsaved-changes gate at 115 is deliberately NOT in
that tier: its callers close the owning dialog first — see
[project-lifecycle.md](../project-lifecycle.md).)
### …and what the awaited half must re-check before it applies

**Anything read BEFORE the await and used AFTER it has to be re-checked, or the
dialog applies a result to something that moved.** The chart-from-audio wizard
(`Presentation/Screens/Dialogs/ChartFromAudioViewModel` since wave 2)
had both halves of this wrong: `Services/Import/Audio/ChartFromAudioOptions` spells the draft's frets
from the active arrangement's tuning/capo/string count and `Apply` stamps those
same three onto the new arrangement, but the two reads sat on opposite sides of
the awaited analysis — switch track while it runs and the draft is spelled for
one tuning and stamped with another, silently. It records what the draft was
made for and discards it with a reason now. The MIDI dialog's `_draftGeneration`
counter is the same idea for a result still in flight.

**State a dialog keeps between opens has to be scoped to the PROJECT.** The same
wizard pre-filled `AudioPath` only while it was empty, so one song's WAV was
offered — pre-filled and plausible — for the next song opened. Key such state to
`ProjectState.ActiveProject` and clear it when that changes. A hosted dialog's
view model lives only while it is open, so the path lives in the
`Presentation/Screens/Dialogs/DialogMemory` singleton beside the project it was
chosen for.

**And an `Arrangements.Add` goes through `IHistoryService`
(an add-arrangement action), never straight into the collection.** The
unsaved-changes state is DERIVED from the undo history (`ProjectDirtyTracker`
compares node ids), so a bare Add leaves the project reading CLEAN with a new
arrangement in it: closing the app never prompts. Pinned by
`Presentation/Dialogs/ChartFromAudioViewModelTests`.

## The four import dialects: off-thread work, busy, in-dialog errors

**Guitar Pro, EOF, MIDI and RS-XML all parsed on the UI thread with no busy
state and reported every failure to the STATUS BAR — behind their own still-open
modal.** All four now follow the chart-from-audio wizard's split, and the
split is the rule, not the pattern: **parse and convert OFF the UI thread, apply
ON it.** What mutates the live project stays on the thread that owns it —
`GpImportApplier.ApplyTempoAndKey`, `ChartSpacingEnforcer` against
`_projectState.Beats`, every `Arrangements.Add` — and it all still happens inside
ONE `RunAsSingleUndoStep`, so **an import remains one undo node**.

- **`IsBusy` gates the command (`CanExecute`), disables the body and drives a
  `StackPanel.DlgBusy`; `ErrorMessage` fills a `Border.DlgStrip Error`.** A
  failure that only reaches `ProjectState.SetMessage` is invisible while a modal
  is up; the status bar is for what happens after the dialog closes.
- **Cancel/Esc during a run REQUESTS cancellation — it does not close over a
  live background task.** None of these parsers takes a token, so what the token
  actually buys is that the APPLY half is skipped: a cancelled run changes
  nothing and the dialog closes when the parse unwinds. The MIDI dialog is the
  exception and says why in code: its conversion is a preview that writes
  nothing, so Cancel closes immediately and a `_draftGeneration` counter
  discards the result that is still in flight.
- **A completion must be honest about the half that failed.** EOF's
  `Status_EofCreatedProjectNoAudio` exists because "Created project X by Y" was
  printed even when `LoadAudioFile` had thrown — a project that cannot play a
  note read as a finished import. GP's add-new path finished in total silence
  and now names what it added.
- **A CRASH is not an empty result.** The MIDI dialog swallowed a
  converter exception into a null draft, which the UI rendered as "No note in
  this selection maps onto the arrangement's tuning" — a claim about the music,
  made because the code had thrown. `DraftAttempt.Error` separates the two, and
  the converter is an optional ctor delegate (the seam `NewProjectCreator`
  uses for its silence bake) so both halves are testable.
- **The MIDI preview converts once per DISTINCT selection.** `DraftKey`
  (track, drum channel, tuning) keys a cached attempt; Import reuses the
  preview's draft instead of running the converter a fourth time.
- **RS-XML has a view now** — `Views/Dialogs/RsXmlImportDialogView`, on the
  dialog host since wave 2, closing the "ViewModel but no View"
  debt note that used to sit in `DialogContainerView`. It is READ-ONLY (the file is already
  chosen when it appears), so per the CLOSE TIER its one footer action is the
  Primary carrying `IsCancel` + `IsDefault`. The importer split into
  `RsXmlImporter.Prepare` (off-thread; takes a SNAPSHOT of the host beats, never
  the live collection) and `Apply`; `ImportInto` is the two back to back.
- **The pre-dialog READ is off-thread too, in all three track-picking dialogs.**
  `EofImportLauncher.OpenAsync` and `MidiImportLauncher.OpenAsync`
  (`Presentation/Screens/Dialogs/`, since wave 2 — the hosted dialog's view
  model only exists once it opens, so the read lives in the door that opens it)
  and (since 0.44.5) `GpImportApplier
  .OpenWithFileAsync` each `Task.Run` the parse that fills the track list before
  the dialog appears. GP's stayed inline one release longer under the comment
  "a header read, not the score" — true for GP3–5 (`headersOnly: true`), false
  for `.gp`/`.gpx`, where `GetAvailableTracks` inflates the zip and loads the
  WHOLE score `XDocument`. `OpenAsync(path)` (GP: the `OpenWithFile(path)`
  fire-and-forget twin) is the drop route's seam; `PickAsync()` (GP: `Open()`)
  is picker + the same async. **Its busy
  state is the status-bar task pill** (`IStatusBarService.BeginTask` /
  `CompleteTask`, ids `import.{gp,eof,midi}.read`, label `Status_ImportingFile`),
  because the dialog — and its `DlgBusy` panel — is not on screen until the
  read has landed; the pill is completed in a `finally`, so a failed read
  never leaves one behind.
  Pinned per dialog by the `…ShowsAStatusBarPillFor…` / `…shows_a_pill…` cases in
  the three test classes (`Tests/Infrastructure/RecordingStatusBarService`).
- **After the EOF dialog closes**, `Services/Import/Eof/EofProjectCreator`
  asks the replace guard, picks the folder, writes and adopts the project —
  the dialog answers an `EofImportSelection` (the parsed tracks and the audio
  found beside the file) rather than doing that work after it is gone.
- Pinned by `Presentation/Dialogs/GpImportViewModelTests`,
  `Presentation/Dialogs/EofImportViewModelTests`, `MidiImportViewModelTests`,
  `RsXmlImportViewModelTests` and `Services/Import/EofProjectCreatorTests` in the
  default gate, plus every fake variant of the three hosted views in
  `Perf/DialogConstructionUiPerfSmokeTests.Hosted.cs`.

