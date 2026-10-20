# Phrase catalog (the UI half)

The named, reusable phrase library — Editor-on-Fire's "fret catalog"
(`EOF_SRC/menu/song.c:122-136`, `menu/edit.c:35-36`), which is the single
biggest throughput multiplier for real songs because real songs repeat.

The **store, entry format, template rules and paste-compatibility gate** live in
`DotCharter.Services/AGENTS.md` (`Editing/Catalog/`). Read that first — this file
only covers the surface.

## Files

| What | Where |
|---|---|
| Pane VM | `ViewModels/Editor/PhraseCatalogViewModel.cs` |
| Pane view | `Views/Editor/PhraseCatalogView.axaml(.cs)` |
| Preview control | `Controls/PhrasePreviewControl.cs` |
| Capture / paste | `Services/Editing/EditingClipboardCoordinator.CaptureToCatalog` / `.PastePhrase`, fronted by `IGlobalEditingCoordinator.CapturePhrase` / `.PastePhrase` |
| Pane registration | `Views/MainWindow.axaml.cs` — `InitializeBottomDock` + the `IsPhraseCatalogVisible` `SetPaneVisible` case |
| Reachability guard | `DotCharter.Tests/UI/PhraseCatalogReachabilityTests.cs` |

## Rules

- **The library is NEVER read from the view model's constructor.**
  `PhraseCatalogViewModel` is a constructor argument of `MainWindowViewModel`, so
  its ctor runs on the pre-window path `StartupWorkDeferralTests` pins for
  everything else — and `RefreshEntries()` reads `IPhraseCatalogService.Entries`,
  which is a FULL `.rscp` project deserialize **per stored riff**, over a folder
  that grows with use, for a pane that is not on screen at boot and may never be
  opened. The load is posted at `DispatcherPriority.Background` (once the window
  is up) and `PhraseCatalogView`'s attach calls `EnsureLoaded()` too, so reaching
  the pane before that turn drains still fills it; the service caches
  (`_entries ??=`), so the second call is a list rebuild and not a folder read.
  Regression: `DotCharter.Tests/UI/PhraseCatalogDeferralTests.cs` (behavioural —
  a fake catalog COUNTS the reads).
- **Delete takes the TWO-CLICK CONFIRM LATCH** (`IsConfirmingDelete` +
  `DeleteCaption`, the idiom in [dialogs.md](dialogs.md)). A stored riff is the
  ONLY copy of itself: it is in the per-user library, not in the project, so
  neither undo nor reopening the `.rscp` reaches it — and the button sits in the
  same strip as Paste and Rename, one click from either. Arming reports what the
  second click will remove; picking another riff or a list refresh disarms; a
  `Remove` that returns FALSE now says so instead of leaving the row on screen
  with no word (it was silent in both directions). Pinned by
  `DotCharter.Tests/UI/ViewModels/Editor/PhraseCatalogDeleteConfirmTests.cs`.
- **It is its own surface, deliberately.** The inspector VMs are under a change
  freeze (`misc-surfaces.md`) and a browsable library is not a property sheet for
  the current selection. Nothing here touches `InspectorViewModel` or
  `ChordTemplateEditorViewModel`.
- **Capture and paste never open a second edit path.** Capture reuses the copy
  path's selection PROMOTION (`CollectHighwayContent` — hand shapes overlapping
  the span, the governing FHP clamped to the selection start), and paste goes
  through `ApplyPasteCommit` → `IHighwayEditingService.CommitHandShapeDragAndDrop`
  → `CommitHighwayDragDropCommand.Execute(IHistoryService)`. **One paste is one
  undo node.** Anything that needs a new verb here belongs in that command, not
  beside it.
- **A refused paste must say why.** `PastePhrase` returns a `PhrasePasteOutcome`
  carrying the compatibility findings; the VM renders them from resx keys the
  pure check supplies (`PhraseCatalog_Issue_*`). The verdict is also shown for
  the SELECTED entry before the user presses anything
  (`CompatibilityMessage` / `CompatibilityIsProblem`), so a mismatch is visible
  rather than discovered. `PhraseCatalogReachabilityTests` pins that every key
  the check can emit exists in the resx — a renamed key would otherwise show a
  raw lookup miss exactly when a paste is refused.
- **A phrase pastes WHOLE.** Unlike the clipboard it is not filtered by the
  `AppConfig.PasteInclude*` flags: a catalog entry is an explicit named object
  whose element counts the pane shows, and silently dropping half of it to a
  setting made for ad-hoc range copies would be a surprise, not a preference.
- **`PhrasePreviewControl` draws from `ChartTheme`**, the same runtime palette the
  timeline and highway use — no hard-coded colours, and a theme switch carries it
  along. It subscribes to nothing and overrides neither
  `OnAttachedToVisualTree` nor `OnDetachedFromVisualTree`; it repaints only when
  its `Entry` changes (`AffectsRender`).
- **The view DOES override both attach/detach**, because it subscribes to the
  VM's `FocusSearchRequested`. Pane views are re-created on float/redock, so a
  one-way subscription leaks one handler per dock cycle
  (`ViewSubscriptionLeakGuardTests`).
- **A backup import must drop the catalog cache.** `phrases/` is in the
  whole-install backup, so an import replaces the entry files behind the service
  that caches them — `SettingsViewModel.ImportBackupAsync` calls
  `IPhraseCatalogService.InvalidateCache()` alongside the other four. Skipping it
  leaves the pane showing the pre-import library.

## Keybinds

EOF's catalog keys, adopted where they do not collide with an existing DotCharter
binding. All four are rebindable (`KeybindConfig` → Settings ▸ Keybinds ▸
Navigation) and reachable from the command palette.

| Default | Action | EOF |
|---|---|---|
| `Q` | show / hide the catalog pane | `Q` |
| `Shift+W` | add the selection | `SHIFT+W` |
| `Shift+C` | paste the selected phrase at the playhead | `SHIFT+C` |
| `Shift+~` | show the catalog and focus its find box (paste by typed name) | `SHIFT+~` |

**EOF's `W` / `E` (previous / next entry) are deliberately NOT bound.** Bare
letter keys on the home row are what a keyboard note-entry layer needs, and the
pane's list already answers arrow keys.

The find box is one control doing EOF's two jobs: it filters the list as you
type, and Enter pastes the phrase whose name matches exactly (or the single
remaining entry if the filter has narrowed to one).

**Riff NAMES are not unique — only IDS are** (`PhraseCatalogService.UniqueIdLocked`,
pinned by `TwoPhrasesWithTheSameNameGetDistinctFiles`), so paste-by-typed-name
has to cope with two riffs answering to the name it was given. It did not:
`FindByName` returns whichever the newest-first order puts in front, so the
gesture pasted an arbitrary one into the chart, reported the name it had been
asked for, and left no way to tell which had landed. A paste is an EDIT and may
not guess — `PasteByName` now refuses and says so (`PhraseCatalog_NameAmbiguous`);
picking the riff from the list still pastes it. Regression:
`PhraseCatalogSearchFilterTests.PasteByName_*` (both directions).

**A filter STAYS where the user left it, so a verb that mints or re-titles a
riff has to check whether the filter would hide the result.** Capture and rename
both refilled `Entries` from the live filter, looked the new entry up by id,
found nothing and dropped the selection to null — reporting "Renamed 'Bridge
riff'" over an empty list, which reads exactly like a rename that ATE the riff
(and a stored riff is the only copy of itself). `PhraseCatalogViewModel.Reveal`
clears the filter in that case and only that case: the user has just NAMED the
thing they want to see, and a filter the entry still matches must be left alone.
Regression: `PhraseCatalogSearchFilterTests`, which pins both directions.

## Manual checklist

Dock drags are not testable headlessly, so after touching this pane:

- select a riff → Add selection → it appears in the list with a preview;
- move the playhead → Paste at playhead → one Ctrl+Z removes the whole phrase;
- paste into an arrangement with a different capo/tuning → the verdict line shows
  BEFORE the paste, and a blocked one changes nothing;
- float the pane, close it, re-enable it from the View menu → it comes back;
- restart → the phrase is still there (it is a file under `<ConfigRoot>/phrases`).

## The NAME: "Riff Library" to the user, `PhraseCatalog*` in code

**The pane the USER sees is "Riff Library"; every type, file,
  resx KEY and config folder is still `PhraseCatalog*`/`phrases/`, and that is
  deliberate.** The rename (0.39.2) is resx VALUES only: "phrase" already means a
  dynamic-difficulty region in Rocksmith and on this app's own timeline, so
  "Phrase Catalog" read as "the list of my song's phrase markers" — the owner
  reported it as making no sense. Renaming the identifiers too would have moved
  every user's stored riffs and rewritten five reachability hops for zero user
  benefit. **When you touch this area: read `PhraseCatalog*` in code, write "riff"
in anything a user reads.**

The pane in one line: the named reusable-phrase library (EOF's fret catalog) as
a BottomDock pane — capture reuses the copy path's selection promotion, paste
goes through the one history-backed commit (one paste = one undo node), and a
paste that would not fit its destination is refused WITH its reason instead of
silently landing.
