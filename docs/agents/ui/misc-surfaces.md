# Inspector, diagnostics, RTL, UI scale, command palette (UI router)

Extracted verbatim from `DotCharter.UI/AGENTS.md`. **Read the relevant section
before touching the Inspector pane, the Session Health window, RTL flow, the
global UI-scale transform, or the command palette.**

**This file is a ROUTER.** What stays here is the inspector VM SCOPE ceiling —
the rule that decides whether your change is allowed at all, and the one other
guides cite this file for. The rest is one file per row of the table at the
bottom, under [`misc-surfaces/`](misc-surfaces/).

## `InspectorViewModel`: partial files plus real collaborators

Open the one that owns your change. The partials are the v0.44 split (every
member moved verbatim). The clusters that shared little with the rest have since
left the class: each is a sealed `*ViewModel` under `ViewModels/Editor/Inspector/`,
a DI singleton injected into the inspector's constructor, exposed as a property
on it, and bound THROUGH that property (`{Binding Annotations.AddAnnotationAtPlayheadCommand}`).

| File | What lives there |
|---|---|
| `InspectorViewModel.cs` | Every field, the tab strip, the constructor + composed sub-view-models, `Dispose`, the service event handlers, the 150 ms issue debounce (it calls `Issues.RecomputeIssues()`), and `ApplyNoteMutation`/`ApplyChordMutation` — the two history write funnels every other partial goes through |
| `.SelectionIssues.cs` | The CURRENT SELECTION's field-level findings (the memo, the fold, the pips — bound by the Sound tab), the two "open the Issues tab" commands, and the `IInspectorIssuesHost` implementation the Issues tab calls back through |
| `.Notes.cs` | `SyncNoteProperties` + `AllSameNullable`, the per-note field handlers, the placement target, the editable length, the provenance badges |
| `.Chords.cs` | `SyncChordProperties`, the chord-template resolve, the per-string technique handlers, the chord slide field (`SyncChordSlide`, its mixed readout and refusal over the pure `Inspector/ChordSlideScope`), the chord-level flags |
| `.StringMask.cs` | What the mask TARGETS in the selection: `MaskedChordNotes`, `ActiveTechniqueSource` + `SyncChordNoteTechniques`, the scope sentences (`ChordStringScopeLabel`, `NoteStringIsOutsideMask`) |
| `.SelectionState.cs` | `SelectionKind` and the card-visibility matrix derived from it, the summaries; it forwards the kind to `Sections` |
| `.BulkEdits.cs` | The multi-selection verbs: fret/string steps, reset flags, apply-to-all, set finger |
| `.Bends.cs` | The two `BendEditorViewModel` factories + the chord-bend target rebuild, and `SelectionHasBends` (the Sound rail's Bends dot, #601) |

| Collaborator (property) | What lives there |
|---|---|
| `AnnotationsInspectorViewModel` (`Annotations`) | The Notes tab's charting annotations. Add/remove go through `IHistoryService` (`AddAnnotationAction` / `RemoveAnnotationAction` in `Services/State/AnnotationActions.cs`, pinned by `AnnotationActionsTests`) — annotations are persisted chart content, and until 2026-08-15 both verbs mutated the collection directly, invisible to Ctrl+Z and to the dirty tracker. Behaviour: `AnnotationsInspectorViewModelTests` |
| `ExportReadinessViewModel` (`ExportReadiness`) | The FEHLER tab's pre-export check (`ChartExportValidator` over the whole project), its findings, the three-state verdict and the switch-then-seek jump. Two ties back, both plain calls: `RefreshIfChecked()` from `InspectorIssuesViewModel.RecomputeIssues` (a result once asked for follows edits on the issue debounce) and `Reset()` from the inspector on `ActiveProject`. Behaviour: `ExportReadinessViewModelTests` |
| `InspectorIssuesViewModel` (`Issues`, #659) | The Issues (FEHLER) tab: `RecomputeIssues` (validation, the easier-level filter, the hand-shape findings published to the timeline), the list (`Items`) and its severity groups (`IssueGroups`, deferred while the tab is hidden), the count/summary/badge tip and the header dot (`TopIssueSeverity`), the refresh / auto-fix / jump commands with the pure `LevelOwning` and `OnEasierLevels`, the easier-levels toggle, the fix menu (`IssueFix`, `IssueFixViewModel`, #618), and the AI row for the same finding (`IssueAi`, `IssueAiViewModel`, #803: Fix with AI only for a finding an AI chart-pass job can fix — the phrase findings D03 I10 I25 I26 through the sections & phrases job on the whole arrangement, #808 — any other one prints why — Fix all N in this phrase (only the findings one job fixes with it), Explain with AI; each is sent to the AI chat through `IAiChatFindingsDoor`, a lazy `Func` so the inspector never builds the chat; see `docs/agents/services/ai-chat-instructions.md` § Findings). The inspector drives it — its debounce calls `RecomputeIssues`, a switch to the tab `EnsureIssueGroupsCurrent` — and it reaches back only through `Inspector/IInspectorIssuesHost` (`IsIssuesTabSelected`, `OnIssuesRecomputed` → the selection memo + the work queue, `SelectProgrammatically` → the jump does not switch tabs). Behaviour: `InspectorIssuesViewModelTests`; layout: `Perf/IssueFixRowUiPerfSmokeTests`, `Perf/IssueAiRowUiPerfSmokeTests`, `Perf/InspectorConstructionUiPerfSmokeTests`; the AI row: `IssueAiViewModelTests` |
| `HandShapeEditorViewModel` (`HandShapeEditor`) | The Span card (end EXCLUSIVE, typed start/end, fit-to-sounds — each commit one `SetHandShapeSpanAction` undo node), the enclosed/rebind labels (folded once per sync, `UI/InspectorHandShapeCostTests`), and the pure `ResolveHandShapeSpan` / `CountRebindableChords`. Its own `_isSyncing` guards the two span boxes. The inspector routes `SelectedHandShapes.CollectionChanged` into its `SyncHandShapeProperties` and handles its `Synced` event (selection summary, selection-issue memo, card matrix) — a span commit re-syncs, so the inspector hears about that too. Behaviour: `HandShapeEditorViewModelTests` |
| `StringMaskViewModel` (`StringMask`) | The two mask verbs (`ToggleStringMaskCommand` for the chord box's rail pips and the Technique section's string chips, `ClearStringMaskCommand` for the scope lines), the chips themselves (`Chips`, #601, synced from `NotifyMaskScopeChanged`) and the keyboard readouts (`PenFretReadout`, `MaskReadout`). The mask itself stays `ProjectState.StringEditMask`; the inspector forwards the two `ProjectState` changes these follow (`NotifyMaskChanged` from `NotifyMaskScopeChanged`, `NotifyPenFretChanged` on `PreviewNoteFret`). Behaviour: `StringMaskViewModelTests` |
| `SoundSectionsViewModel` (`Sections`, plain sub-VM, not DI) | The Sound tab's rail (#601): which sections each `SelectionKind` offers, the open one remembered per kind, `ShowApplyBar`, `ApplyFamily` (forwarded to the chord panel's ladder) and `HasChordScope` (whether the ladder reaches a technique edit at all). Replaced the #572 `SoundSegmentsViewModel`. Behaviour: `SoundSectionsViewModelTests` |
| `SoundNextTaskViewModel` (`NextTask`, plain sub-VM over `Queue`) | The Task section with nothing selected (#601): the next task in the shipped order (fingerings, names, export blockers), its sentence and "Fix the next one" (= `Queue.TriageFilter`). Behaviour: `SoundSectionsViewModelTests.TheNextTask_ComesInTheShippedOrder`, `Perf/SoundTabRailUiPerfSmokeTests` |

**What stayed, and why.** Each remaining partial was surveyed for the members it
shares with the root and its siblings (2026-10-03, comments stripped): `.Notes`
36, `.Chords` 28, `.BulkEdits` 24, `.StringMask` 18 (after the mask
verbs left), `.SelectionState` 9 outbound but read by every other partial, and
`.Bends` 5. They are all entangled with the SELECTION state, which is the part
that has to stay one object:

- `.Notes`, `.Chords`, `.BulkEdits` project the selection into ~40
  `[ObservableProperty]` fields under the shared `_isSyncing` guard and write
  back through `ApplyNoteMutation`/`ApplyChordMutation`.
- `.SelectionState` IS the selection state (`SelectionKind`, the card matrix,
  `HasSingleNoteSelection`, …) that the others read.
- `.StringMask`'s remainder writes the ten `ChordNote*` readouts under
  `_isSyncing` from `BendTargetChordNote`/`ChordBendStrings`.
- `.SelectionIssues` is what stayed of the old `.Issues` when the issue LIST
  left (#659): it reads the selection collections and the tuning, and the Sound
  tab binds it. The list's four ties to the root — the tab state that defers
  the groups, the memo and `Queue` after a pass, `_programmaticSelection` for
  the jump — became `IInspectorIssuesHost`.
- `.Bends` is small but its whole job is binding `NoteBendEditor`/
  `ChordBendEditor` to `SelectedChord`/`BendTargetChordNote` and the two write
  funnels.

**A source lint that reads this view model must read the whole
`InspectorViewModel*.cs` set** — `UI/IssueDotSeverityTests`,
`UI/PhraseCatalogReachabilityTests`, `InspectorStringMaskTests` and
`SoundQueueViewModelTests` each do, and each was proved to still fail on a real
break. A lint pointed at one partial passes vacuously. A lint about an
EXTRACTED cluster reads that collaborator's own file instead
(`AnnotationActionsTests` reads `Inspector/AnnotationsInspectorViewModel.cs`;
`InspectorJumpToIssueLevelTests` reads `Inspector/InspectorIssuesViewModel.cs`),
and a lint over BOTH sides of a tie-back reads the set plus that file
(`UI/InspectorIssueRebuildTests`, `UI/IssueDotSeverityTests`).

## The inspector's issue list — what a recompute costs

- `InspectorIssuesViewModel.RecomputeIssues` runs on the inspector's 150 ms
  debounce after every history step.
  `Items` and `IssueGroups` are `BulkObservableCollection`s replaced in ONE
  Reset (they were Clear + N Adds into nested non-virtualizing
  `ItemsControl`s), and the severity GROUPS — bound only by the FEHLER tab —
  go stale while that tab is hidden (the host's `IsIssuesTabSelected`) and are
  rebuilt from the inspector's `OnSelectedTabIndexChanged` when it opens
  (`Issues.EnsureIssueGroupsCurrent`). The
  header dot reads `IssueGrouping.TopSeverity(IEnumerable<ArrangementIssue>)`
  straight off the issues so it never waits for the groups; the badge, the
  queue and the selection findings were already derived from `Issues.Items`.
- The five selection-finding properties (`SelectionIssueCount`,
  `HasSelectionIssues`, `SelectionBlocksExport`, `SelectionFieldIssues`,
  `SelectionIssuePips`) read ONE materialized list per notification, built
  over a reference set of the selection — they used to re-run an
  Issues × Selection iterator each, with `List.Contains`. Pinned by
  `UI/InspectorIssueRebuildTests`.
  - **Invalidation has THREE points, not two, and the third is the one that was
    missing.** A fresh issue list clears the memo, and so does
    `ISelectionService.PropertyChanged` — but that service raises
    `PropertyChanged` for the SCALARS `SelectedNote`/`SelectedChord` ONLY; every
    other `Selected*` collection raises `SelectionChanged`, an event, and
    nothing else (`SelectionService`'s ctor). The memo folds
    `SelectedHandShapes` in as well, so selecting a hand shape left it holding
    the previous selection's answer: an issue back-referencing that shape read
    as `SelectionIssueCount == 0` and stayed 0.
    The hand-shape sync (`HandShapeEditorViewModel.SyncHandShapeProperties`)
    raises `Synced`, and the inspector's `OnHandShapeEditorSynced` calls
    `NotifySelectionIssuesChanged()`.
    `AHandShapeSelection_InvalidatesTheSelectionFindings` pins all four links —
    the memo reads the collection, the ctor subscribes it to the sync, the sync
    raises `Synced`, the inspector's handler invalidates.
  - **The same shape survives, narrowly, for NOTES and CHORDS**, and it is left
    alone deliberately. `SelectedNotes.CollectionChanged` sets
    `SelectedNote = FirstOrDefault()`, which raises `PropertyChanged` only when
    the scalar actually MOVES — so adding a second note to a selection whose
    first note is unchanged does not invalidate the memo. The fix is the same
    one line, and it is not taken because `SyncNoteProperties` is on the hot
    edit path (it runs TWICE per committed edit, see
    [rendering-perf/editor-latency.md](rendering-perf/editor-latency.md)) and
    `NotifySelectionIssuesChanged` raises five bound properties. A hand-shape
    selection is never thousands of items; a note selection is.

## The inspector VM scope ceiling

- Inspector VMs are otherwise off-limits to visual passes — no binding/command
  restructuring; layout-only trivial props (like the two `Has*Selection`) are
  the ceiling. **The 0.16 owner-granted VM exception (which added `IssueGroups`/
  `IssueSummary`/`RebuildIssueGroups` for the FEHLER rework) is SPENT.** The
  **0.33 owner-granted exception** added the note-placement fretboard seams —
  `NotePreviewChord`, `NoteStringCount`, `NoteTuningNames`, `NoteSustainLabel`,
  `HasSingleNoteSelection`, `SetNoteTargetCommand` — all routing through the
  existing `ApplyNoteMutation` / `MutateNotesAction` history path (the tri-state
  `AllSameNullable` projection and every chip binding are untouched). That
  exception is SPENT too.
- **The 0.36 owner-granted exception (editing-rework stage 5) is the THIRD, and
  it is now SPENT — the ceiling is back in force.** Its scope was the build
  order of the 2026-07-25 editing-rework audit §15
  PLUS the visual overhaul (layout, spacing, grouping and control design) across
  the inspector, the note editor and the chord/hand-shape surfaces. Concretely
  it covered:
  - **deleting** `ChordStringTargets` / `ChordStringTarget` /
    `SelectedStringPickIndices` / `ToggleStringPickCommand` and the
    `MiniFretboardControl` string-pick properties;
  - **adding** `SelectionKind` (+ the `Show*Card` projections), `AppliesToLabel`,
    `ChordStringScopeLabel`, `NoteStringIsOutsideMask`, `BendSummary`,
    `SelectionShapeSummary`, the hand-shape span read-models, the mask commands,
    the chord-box hover-gesture commands, one editable `Sustain` property, and
    the standalone `SoundQueueViewModel`;
  - **renaming** `EditSelectedOnly` → `ScopeIsThisChordOnly` and flipping its
    default.

  Everything else held: every write still funnels through
  `ApplyNoteMutation`/`ApplyChordMutation` → `MutateNotesAction`/
  `MutateChordsAction` and `ApplyTemplateEdit`, the tri-state `AllSameNullable`
  projection is untouched, and every chip binding is as it was. New user-facing
  strings need EN + de-DE + ja-JP resx keys (`Inspector_*` / `ChordTemplate_*`).
- **The v0.60 polish is the FOURTH exception, and it is SPENT.** The owner's
  v0.60 direction (2026-10-04) asked for the inspector to be improved and
  streamlined; the analysis that followed filed #117, #118, #119 under epic #111. It added `BendsExpanded`,
  `NoteStringLabels`, `ChordBendStringLabels`/`ChordBendStringPosition`,
  `HasProvenanceBadges`, `IssueBadgeTip`, `ShowExportReadinessCommand`, the
  queue's `BlocksExportFindingCount`/`BlocksExportTileTip` and an injected
  `ILocalizationService`, folded `SelectionFieldIssues`, and made
  `SelectionIssueCount` count the folded rows. The logic lives in pure types
  under `Inspector/` (`GuitarStringLabel`, `BendDisclosurePolicy`,
  `InspectorFieldIssue.Fold`, `ProblemCountText`); every write still goes
  through the two mutation funnels.
- **#483 (2026-10-09) is a BUG FIX the owner filed, not a visual pass, so it is
  not counted against the ceiling.** The chord panel's scope switch said "this
  chord" while the edit reached every use; making it honest needed one
  read-model: `ChordTemplateEditorViewModel.Scope` (`ChordTemplateScopeBar`,
  fed by `ChordTemplateEditPlanner.ResolveReach`) replaced `ScopeUsageCount` /
  `ScopeAllLabel` / `ScopeAffectsOthers`. No write path moved: every edit still
  goes through `ApplyTemplateEdit` → `ChordTemplateEditPlanner.ApplyEdit`.
- **#484 (2026-10-09) is an owner-filed redesign of that same decision, not a
  visual pass.** The five mechanisms that spread an edit became one "Apply to"
  ladder: `ApplyRung` + `ChooseApplyRungCommand` + `Ladder` (`ChordApplyLadder`)
  replaced `ScopeIsThisChordOnly`, `Scope` and the alike button's
  `ApplyFingeringToTransposedCommand` / `TransposedShapeCount`; `CanSaveFingering`
  joined `HasPendingFingerEdits`. Every write still goes through
  `ApplyTemplateEdit` → `ChordTemplateEditPlanner.ApplyEdit`, which now executes
  the target set of the pure `ChordApplyScope`
  ([editor-input/chord-verbs.md](editor-input/chord-verbs.md)).
- **#530 (2026-10-09) is an owner-filed bug fix, not a visual pass.** The chord
  slide field read the lowest string's target and wrote one ABSOLUTE fret to
  every masked string. It added `ChordSlideIsMixed`, `ChordSlideSummary` and
  `ChordSlideRefusal` / `HasChordSlideRefusal`; the rule is the pure
  `ChordSlideScope` ([misc-surfaces/inspector-editing.md](misc-surfaces/inspector-editing.md)).
  Every write still goes through `ApplyChordMutation`.


- **#601 (2026-10-10) is the FIFTH owner-granted exception: the Inspector
  redesign the owner chose from the design study ("Rail (dense)").** It added
  `SoundSectionsViewModel` (replacing `SoundSegmentsViewModel`),
  `SoundNextTaskViewModel`, `StringMaskViewModel.Chips` and
  `SelectionHasBends`; it removed `BendsExpanded` + `BendDisclosurePolicy`,
  `SelectionIssuesExpanded` and `SelectionIssueHeadline` (the disclosures the rail
  replaced); and `ChordStringScopeLabel` names strings the gutter's way. Every
  write still goes through `ApplyNoteMutation` / `ApplyChordMutation` and
  `ApplyTemplateEdit`; the tri-state projection and every technique binding are
  untouched. The queue's counter following the selection is the #611 BUG FIX
  (`SoundQueueViewModel`), not part of the exception. Spent; the ceiling is back.

## Where the detail lives

| Read this when… | File |
|---|---|
| you are touching the inspector PANE itself: its Dock `Context`, the shared Fire tab strip, the "not a `TabControl`" rule, or the chrome classes in `InspectorStyles.axaml` | [misc-surfaces/inspector-pane.md](misc-surfaces/inspector-pane.md) |
| you are touching the MIXER tab (incl. its EQ module) or the FEHLER tab — the 0.51 rebuild and the severity-grouped triage surface | [misc-surfaces/inspector-mixer-and-fehler.md](misc-surfaces/inspector-mixer-and-fehler.md) |
| you are touching what the inspector EDITS: the Sound tab, `ChordBoxControl`, the ONE `StringEditMask`, the work queue, the length editor, reshape scope, technique chips, the chord-template editor, or the Lyrics/vocals tab | [misc-surfaces/inspector-editing.md](misc-surfaces/inspector-editing.md) |
| you are touching Session Health / the diagnostics window, **accessibility (accessible names, modal escape hatches, colour-only signals)**, RTL, the global UI scale, or adding a command to the palette (**`CommandCatalog` registration is tested**) | [misc-surfaces/diagnostics-rtl-scale-palette.md](misc-surfaces/diagnostics-rtl-scale-palette.md) |
| you are editing `Views/Editor/InspectorStyles.axaml` — the FOURTH converted Fire grammar, its Cursor-vs-Transitions difference, and the Mixer EQ's own view | [misc-surfaces/inspector-styles-grammar.md](misc-surfaces/inspector-styles-grammar.md) |

## The command palette at the WIDE breakpoint

`PaletteCard` was `Width="680" MaxHeight="500"` — its own header calls it "a
680 px centred overlay" — which is 35 % of a 1920 window and 17.7 % of a 3840
one, i.e. the same box with twice as much scrim around it. Both numbers are
TOKENS now (`FirePaletteWidth` 680/920, `FirePaletteMaxHeight` 500/760) rather
than a selector, because a `Width` written on the element is a LOCAL value and
outranks every style setter.

**The HEIGHT is the half that matters.** The result row is a fixed 38 px, so the
card's ceiling IS the number of commands on screen: measured ~10 rows → ~17.

The type steps with it, through `:is(Window).Wide` selectors reaching existing
rungs: `TextBox.PaletteSearch` → `FireSizeCardTitle` 17, `TextBlock.PaletteName`
→ `FireSizeLg` 15, `TextBlock.PaletteLegend` → `FireSizeSm` 12. Two numbers
deliberately do NOT move — the row's 38 px floor (that is what the taller
ceiling spends on MORE ROWS) and `TextBlock.PaletteGroup`'s 152 px category
column, which `Perf/CommandPaletteUiPerfSmokeTests` derives from the longest
localized `Keybinds_*` string and not from the window.

`Perf/UiResolutionAuditUiPerfSmokeTests.TheCommandPalette_GrowsAtTheWideBreakpoint`
pins the sizes, the derived row capacity, and that the keyboard route survives
the switch — the query keeps its text and its focus and the list keeps its
selection, which the size numbers alone could not see.
