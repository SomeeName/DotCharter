# Dialogs (`Views/Dialogs/`) — UI deep reference — save goes through undo

## Save goes through undo

**The settings-style dialogs' Save is ONE undo node.** Song Metadata,
Arrangement Settings and Vocal Track Settings used to write the domain
directly — a dialog Save was the one edit Ctrl+Z could not see. Each now
records through `IHistoryService` (field-record actions for metadata/vocal style —
`Services/Editing/SongMetadataEdit.cs` — `CloneArrangement` before/after +
`RestoreArrangement` in place for the arrangement dialog, add-actions for the
create-new paths that also restore the previous active pick). None of them is
`IMergeableAction`, so a Save can never fold into an in-flight gesture; a Save
with nothing changed records nothing (`HasEffect`). Pinned by
`UI/ViewModels/Dialogs/DialogSaveUndoTests`. Two the audit named were NOT
retrofitted, on purpose: **Level Generation** already records one
`ProjectSnapshotAction` inside `ChartMaintenanceService.GenerateLevels`, and the
**Pattern Builder** saves to the app-level pattern store, not the chart — chart
undo does not apply to it.

## Metadata fields: only what an export reads

**Song Metadata and New Project ask only for fields some export reads** (#218):
Rocksmith's manifest or the sheet export. The year is
the only date. `ChartData.ReleaseDate` is NOT on either contract
(`ISongMetadataScreen`, `INewProjectScreen`). A file's tag date still rides into
the project through `NewProjectViewModel`'s private field, and the year is
distilled from it by `AudioMetadataExtractor`. A Song Metadata Save keeps the
stored value because its edit is `before with { … }` and names no date.

Both dialogs show Year as its own labelled row with the same keys
(`Dialog_AlbumYear`, `Dialog_AlbumYearPlaceholder`). Pinned by
`UI/ViewModels/Dialogs/SongMetadataReleaseDateTests`. A new field on either
dialog needs a consumer, or the footer's "written into every export" is untrue.
