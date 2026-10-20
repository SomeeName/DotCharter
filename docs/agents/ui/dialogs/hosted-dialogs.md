# Dialogs on the dialog host (`IDialogs` + `DialogHost`) — the migration recipe

Wave 2 of the UI rewrite ([target-architecture.md](../target-architecture.md)
§§ 3.2, 3.6) moves the overlay dialogs off `AppDialogService` /
`DialogContainerView`'s `RegisterLazy` and onto the Presentation architecture.
**Moved so far (2026-09-22):** slice 1 — Unsaved changes, Set BPM, Tempo-map
operation, Update available, Fret-hand position, Generate levels, Beat; slice 2 —
Paste special, Arrangement sync, Keyboard shortcuts, Vocal track settings, Chart
from audio, Rocksmith XML import, MIDI import, Editor on Fire import; slice 3 —
History, Library doctor, Chart diff, Batch lint, Showlights, Checkpoints, Guitar
Pro import, About; slice 4 — Song metadata, Crash recovery, Tuner, Event, Arrangement settings, New project, Pattern builder, Latency calibration, Tone designer, Rocksmith export, Audio from YouTube (which replaced the deleted Songsterr import, #820); and the text-document dialog (`ITextDocumentScreen`), new on the host: Help ▸ Usage statistics and the licence (Help ▸ License & notices, About's link) are read in the app. Both handed a file (.txt, .md) to the system, which raised Windows' "choose an app" box where no viewer is set (2026-09-24); "Show the file" selects the copy on disk instead of opening it. The first-run wizard is still legacy and lives beside them (and the generic message dialog stays legacy, below); both halves share one Esc route, one
`HasOpenDialog` and one drawing order.

## How a hosted dialog is put together

| Piece | Where | Rule |
|---|---|---|
| Contract | `DotCharter.Presentation/Contracts/Dialogs/I<Name>Screen.cs` | `: IDialogScreen<TResult>`. Formatted strings, numbers, `ICommand` verbs. A dialog that opens WITH arguments adds a request record in the same file: `record FhpRequest(…) : IDialogRequest<FhpRequest, IFhpScreen, bool>`. A result enum puts its SAFE answer at 0 (`UnsavedChangesChoice.Cancel`): a dialog closed from outside answers `default`. |
| View model | `DotCharter.Presentation/Screens/Dialogs/<Name>ViewModel.cs` | Derives `DialogViewModel<TResult>` (`Result`, `Close(answer)`, `IsClosed`). Scoped: ONE INSTANCE PER OPEN, so "open" is the constructor (or `IDialogSetup<TRequest>.Configure` when there is a request) and there is no `IsOpen`, no `Open()`, no reset. Implements `ICancelableDialog` when Esc must do more than answer the default (log, stop a download) or must be REFUSED (a run in flight — Generate levels). |
| Services | existing seams only | Chart edits go through `IChartEditorSurface` / `IEditorTransport` / `IHistoryService`; the three chart dialogs share `ChartEditDialogServices` so each stays inside seven dependencies. |
| Fake | `DotCharter.Presentation.Fakes/Dialogs/Fake<Name>Screen.cs` | Derives `FakeDialog<TResult>`; a public static factory per variant (`FakeVariantsTests` counts them); `Default`, `Empty`, `LongText` (German + Japanese), `Busy` when the contract has `IsBusy`. The default button's command answers a NON-default result — the Enter test relies on it. |
| View | the existing `Views/Dialogs/<Name>DialogView.axaml`, rebound | `x:DataType` = the contract, `x:CompileBindings="True"`. The root is `Grid.DialogFrame` (the dialog grammar's centring box WITHOUT a backdrop) with the unchanged `Border.DialogCard` inside. No `IsVisible`, no `ZIndex`, no scrim — the host owns all three (`UI/DialogZOrderTests.HostedDialogs_LeaveStackingToTheHost`). Keep `IsDefault` / `IsCancel` and the `FocusOnOpen.IsTarget` marker. |
| Catalog | `Views/Shell/ScreenCatalog.cs` | `Dialog<I<Name>Screen, <Name>DialogView>(Dialogs, "<Name>")`. This one line gives the gallery page, the golden, the construction smoke in every variant and the grammar sweeps. |
| Registration | `DotCharter.Presentation/Dialogs/DialogsModule.cs` | `services.AddScoped<I<Name>Screen, <Name>ViewModel>()`. |
| Callers | wherever `Dialogs.<Name>.Open…()` was | `IDialogs.ShowAsync<TContract, TResult>()` or `ShowAsync(new <Name>Request(…))`. The shell's doors are `ViewModels/Shell/ShellDialogsViewModel.cs` (`MainWindowViewModel.ShellDialogs`; Tools ▸ Calibrate latency takes `ILatencyCalibrationPrompt` through its constructor, and `ShellDialogsViewModelTests` fails on any `App.Services` lookup under `ViewModels/Shell/`); a dialog nobody awaits goes through `Launch(…)`, which logs a fault (`DialogRequests.Observe`) and a missing host. |
| Delete | | the legacy view model, its `AppDialogService` property / ctor parameter / `AllDialogs` entry, its `RegisterLazy`, its `App.ViewModelServices` singleton. |
| State between opens | `Screens/Dialogs/DialogMemory` | The view model lives only while it is open, so anything the legacy SINGLETON remembered across opens goes here (Paste special's mode and mouse flag; Chart from audio's WAV, keyed to the project it was chosen for). Never a static. |
| Work BEFORE the dialog | `Screens/Dialogs/<Name>Launcher` | A dialog that used to do work before `IsOpen` went true (a file picker, a parse off the UI thread under a status-bar pill) gets a singleton launcher: `PickAsync()` for the menu, `OpenAsync(path)` for a dropped file, each ending in one `ShowAsync(new <Name>Request(…))`. Registered in `App.ViewModelServices`; the shell holds it and its verbs call it through `LaunchImport`. |
| Live state another surface shares | a Services singleton with LEASES | The Tuner's detection also drives the Tools popover's readout, so the poll, the detector and each string's memory live in `Services/Audio/Tuning/LiveTuner`: `Acquire(fresh)` returns a lease (null when there is nothing to tune), the first lease starts the poll (`IUiDispatcher.SchedulePeriodic`) and the last one stops it. The dialog takes a FRESH lease in its constructor and disposes it in `Dispose` (the host disposes the scope on close); the popover holds its own through `Presentation/Shared/TunerReadout`. |
| Work AFTER the dialog | Services | A dialog that used to keep working after it closed (the EOF import's folder picker, save and adopt) ANSWERS what it produced instead — `EofImportSelection` — and the launcher hands that to a Services class (`Import/Eof/EofProjectCreator`). A closed dialog's scope is gone; nothing may run on it. |

## The Rocksmith export dialog's findings (#184)

`RocksmithExportDialogView` opens with **Validation FIRST** — the findings decide
whether Export can run, and below the fold they were 25 clicks of menus away
from their fixes. **Review report** is an outlined `FireGhost` button on the
summary's line, and each finding a **Fix** beside
**Go to** (`IExportFinding.HasFix` / `FixDescription` / `FixCommand`). The rows
are `ExportFindingRow` + `ExportFindingFix` (the record stays inside seven
parameters); the verbs are `Presentation/Shared/RocksmithFixes`, shared with the
Inspector. Pinned by `Perf/RocksmithExportFixUiPerfSmokeTests` (Fix measures
where there is a verb and is absent where there is none, the findings sit above
the target section) and `RocksmithExportDialogViewModelTests`. Rules behind the
verbs: [dlc-builder-fixes.md](../../services/editing-invariants/dlc-builder-fixes.md).

Two layout rules (#214), pinned at 1280x720 and 1920x1080 on every fake variant by
`Perf/RocksmithExportLayoutUiPerfSmokeTests` (in the release's de-DE / ja-JP pass):

- **Each reason Export is grey is said ONCE**, in `ExportBlockedStrip` above the
  footer (and as Export's tooltip). `ExportBlockedReason` lists EVERY failing
  clause, one per line, in gate order (`RocksmithExportViewModel.BlockingReasons`);
  the body carries no strip that repeats one, and the contract has no per-clause
  flag left to draw one with.
- **ONE scroll region.** The findings `ListBox` has no `MaxHeight` and its own
  scroller is `Disabled`, so the list scrolls with `ExportBody` and takes the
  height the window has: 6 whole rows at 1280x720 in the Full variant, against 3
  in a 150 px nested scroller. Never cap it again.

## The song-package export (#707)

File ▸ Export ▸ DotCharter song (`IDcSongExportScreen`, `DcSongExportViewModel` +
`.Write.cs`, `Views/Dialogs/DcSongExportDialogView`) writes the open chart as a
`.dcsong` through `Services/Package/DcSong/IDcSongExporter`
([../../services/dcsong-format.md](../../services/dcsong-format.md)).

- **Nothing is offered that cannot be written.** The view model asks the exporter's
  off-thread `ProbeAsync` on open; until it answers, Export is grey with "Checking
  for ffmpeg and stems…", and without ffmpeg it stays grey with the reason and the
  fix in `ExportBlockedStrip` (every clause once, in order: check, ffmpeg, audio,
  nothing ticked, no file).
- **Stems are off by default and only live when the song has separated stems on
  disk** (`IncludeStems` coerces to false otherwise; the row's description names
  them and what they add, or says Tools ▸ Separate stems makes them).
- **The estimate sits in the footer's meta slot** (`SizeValue`), so it is in view at
  any scroll; once a package is written the slot holds Open output folder.
- The default file is `<Library folder>/<Artist> - <Title>.dcsong` (else the export
  folder); the output row says whether the file is in the Library folder.
- A refused write (`DcSongWriteException`) is an Error strip inside the dialog in the
  user's language; Close/Esc during a write cancel it and the dialog stays until the
  write unwinds. Pinned by `Presentation/Dialogs/DcSongExportViewModelTests` and
  `Perf/DcSongExportDialogUiPerfSmokeTests` (real view model in the swept culture,
  in the release's de-DE / ja-JP layout pass).

## A body that outgrows 1280x720 (#725)

The card is clamped to the window, so at 1280x720 with the status rail the body
row gets ~440 px. Whatever does not fit must scroll or give up height; it may
never paint on under the footer. Two shapes, both keeping the footer fixed:

- **One list grows: give it the STAR row (reflow).** The body is a `Grid` whose
  `*` row holds the list frame and whose `Auto` rows hold the rest, so the capped
  list (`MaxHeight` kept, so a big window looks as before) shrinks and scrolls
  itself while the options, progress and status stay put. Batch lint (results),
  Guitar Pro import (tracks), Showlights (existing positions) and the Tuner (the
  string rows, in a gutter `ScrollViewer.DialogBody`; the note, needle and the
  all-tuned verdict stay in view). Such a list shows its bar
  (`AllowAutoHide="False"`) so a cut-off row reads as "scrolls".
- **Several parts grow: the whole body is a `ScrollViewer.DialogBody`** with its
  bar in the card gutter (`Padding="0,0,16,0" Margin="0,0,-16,0"`). Chart from audio
  (#710), the Rocksmith export.

Pinned for EVERY hosted dialog in EVERY fake variant by
`Perf/DialogWindowFitUiPerfSmokeTests` (in the release's de-DE / ja-JP pass): every
visible descendant of the body that nothing clips (it stops at a `ClipToBounds`
visual, i.e. a `ScrollViewer`'s presenter) must end above the footer, read through
transforms so a `Viewbox` is graded by what it paints. Before #725 it flagged
Batch lint, Showlights and Guitar Pro import `Full`, the Tuner `Full` and
`LongText`, and the since-deleted Songsterr import's `LongText` and `Busy`.

## What the host does that the legacy path did per dialog

- **Stacking.** `DialogContainerView` adds the one `DialogHost` as a child of
  its panel, ABOVE every lazily built legacy dialog and BELOW the eager generic
  `MessageDialogView` — so a confirmation raised from any dialog is on top, and a
  hosted dialog is never drawn under a legacy one
  (`Perf/DialogConstructionUiPerfSmokeTests.TheDialogHost_SitsAboveEveryLegacyDialog_AndUnderTheAppWidePrompt`).
- **Raising never re-parents (#141).** A view is pooled as a hidden child of the
  host; opening it brings it to the top with `Children.Move` (`BringToTop`), and
  the scrim moves the same way. `Remove` + `Add` took the view out of the logical
  tree, which drops every style in it and re-applies them all on the re-attach:
  55-130 ms (Debug) on EVERY Tone designer open, and the context-free prewarm's
  work thrown away each time. Pinned by
  `Perf/SurfaceShowRewarmUiPerfSmokeTests.OpeningAPrewarmedDialog_KeepsItInTheTree_SoItsStylesSurvive`.
- **The scrim.** One shared `Border.FireOverlayScrim` under the top hosted
  dialog. **It is `FireScrimFill` (#D9111317), not the legacy `OverlayScrim`
  (#AA000000)** — a hosted dialog's backdrop is a shade lighter and bluer than a
  legacy one's. That is the architecture's one scrim (§ 3.2), not a restyle of
  the card; it wires no pointer handler (`UI/DialogScrimRuleTests`).
- **Styles.** A hosted view inherits `DialogStyles.axaml` from the
  `DialogContainerView` it sits in — still included ONCE in the app.
  `DialogHost.Preview` adds the same include for the gallery and the goldens.
- **Esc.** The window's one Esc route (`MainWindowViewModel.HandleEscapeKey`)
  asks `IAppDialogService.CloseTopDialog()`: the legacy message dialog first,
  then the host's top dialog (`ICancelableDialog.TryCancel`, then the default
  answer), then the first open legacy dialog. The host also handles an Esc that
  bubbles into it.
- **Enter.** Unchanged: the view's `IsDefault` button.
- **Focus.** The host calls `FocusOnOpen.FocusTargetIn(view)` at `Loaded`
  priority after it shows a dialog — there is no scrim `IsVisible` edge to watch.
- **`HasOpenDialog`.** `AppDialogService.HasOpenDialog` is legacy OR
  `IDialogs.HasOpenDialog`, and re-raises on `IDialogs.Changed`, so the 3D pane,
  `GlobalChordGate` and the keybind registry see a hosted dialog
  (`…A_hosted_dialog_is_an_open_dialog_to_the_shell`).
- **Usage statistics.** `IDialogs.Changed` carries the catalog entry's
  `UsageId` (the view's type name minus `View`, i.e. the legacy id), counted by
  `AppDialogService` exactly like an `IsOpen` edge.

## Seams the second slice needed

- **`IEditorTransport.GridDivisor`** (Paste special pastes on the editor's grid)
  and **`IChartEditorSurface.ExecuteSeek`** (the EOF project lands at 0). Both
  already existed on the implementing view models.
- **`Contracts/Shell/IShortcutCatalog`** — Help ▸ Keyboard shortcuts reads the
  palette catalog, which needs the whole shell; the shell implements the seam
  (`MainWindowViewModel.CurrentShortcuts`, same shape as `IShellDestinations`).
- **`Services/Import/Audio/IChartFromAudioAnalyzer`** — the analyzer lives in
  `DotCharter.Analysis`, which Presentation may not reference; the seam returns
  Core `GuitarNote`s. `ChartFromAudioOptions` moved beside it.
- **`Services/Audio/StemSeparation/ISongStemSeparation`** — chart from audio's
  **Separate stems** (#641) runs the app's ONE separation verb, which lives on the
  shell's `StemSeparationViewModel` (UI) and is registered as that same singleton.
  The outcome carries a reason; `GetAvailabilityAsync` greys the button with its
  reason (not the song's audio, no model). `IChartFromAudioStemLocator` is a DI
  singleton the analyzer and the dialog share. The run is the APP's, not the
  dialog's (#710): closing the dialog never cancels it (no dialog-owned token is
  cancelled, and the closed view model's progress target is closed, so a late
  report or the completion touches nothing), it runs on in the status bar and its
  stems land in the cache. While it runs the strip says so
  (`SeparationKeepsRunningNote`). A dialog opened while the app separates the
  same file FOLLOWS `ISongStemSeparation.Running` (a `SongStemSeparationRun`:
  progress, completion, cancel) instead of starting a second run; **Cancel
  separation** cancels whichever run the strip shows. The body is a
  `ScrollViewer.DialogBody` and the view scrolls the strip into view when the
  note appears: at 1280x720 the separating stack does not fit unscrolled
  (`ChartFromAudioStemStripUiPerfSmokeTests` checks nothing paints under the footer).
- A contract carries COLOURS as hex strings: `VocalTrackSettingsView` converts
  with `HexColorConverter` (picker) and `HexBrushConverter` (swatch fills).

## Guards that moved with the seven

`Perf/DialogConstructionUiPerfSmokeTests` builds every catalog dialog in every
fake variant inside a real `DialogHost` in the real `DialogContainerView`
(`.Hosted.cs`), runs the whole grammar sweep over hosted and legacy dialogs
alike (`ForEachConvertedDialog`), and presses Enter and Esc on each hosted
dialog. The view model rules are `Presentation/Dialogs/*Tests` (default gate),
each with a `Conforms_to_its_contract` fact; `Presentation/ContractConformanceTests`
also resolves every catalog dialog from the real `DialogsModule`.
`UI/PowerToolReachabilityTests.Each_hosted_dialog_power_tool_is_reachable_end_to_end`
is the menu → shell verb → catalog → module chain for a hosted power tool, and
`…The_hosted_midi_import_is_reachable_end_to_end` the same chain through a
launcher. Slice 2's view-model tests are `Presentation/Dialogs/<Name>ViewModelTests`
(the launchers are driven end to end through `ScriptedDialogs`, which opens the
real view model and plays the user), plus `Services/Import/EofProjectCreatorTests`.
`UI/ModalEscapeHatchTests` counts a hosted `Grid.DialogFrame` as a modal root,
so every hosted dialog still needs a focusable way out.

## Not moved, and why

**`MessageDialogView` stays legacy.** `IMessagePrompt` (Services) is called from
any thread, carries a severity icon and an expandable details block, queues one
message at a time with a stuck-dialog displacement (`DialogTurnstile`), must be
showable before the window exists, and a plain message answers TRUE when
dismissed. `IDialogs.MessageAsync` has none of that, and its view
(`Views/Prompts/MessagePromptView`) is a different card. Moving it means
widening the message contract (severity, details), an `IMessagePrompt` adapter
over `IDialogs` that marshals and queues, a decision on which of the two cards
survives, and a plan for messages raised before the host attaches — its own
slice, not a line in this one.
