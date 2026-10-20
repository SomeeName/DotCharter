# `Editing/` — the chart-model invariants (deep reference) — undo: a gesture is one node

## Undo: a gesture is one node

- **A bulk collection action goes through `CollectionActionBase.RemoveEach` /
  `.InsertEachSorted`, never a per-item loop (0.60).** `Collection.Remove(item)`
  hides an `IndexOf` scan and `InsertSorted` walks from index 0, so both halves
  of "delete 5,000 of 18,000 sounds, then Ctrl+Z" were O(k·n) — a measured
  **989 ms → 21.6 ms**, on the UI thread, for one keypress. The helpers are
  notification-for-notification identical to those loops (same items, same
  order, same index on each `Remove`) and they REFUSE, before mutating anything,
  on input the loop would answer differently: an absent item, a repeated one, a
  collection not ordered by position. `BulkCollectionEditEquivalenceTests` pins
  that against verbatim copies of the old bodies — **keep those copies dumb**, or
  the guard goes vacuous.
- **A mutate tick re-sorts with one `Move` per DISPLACED element, and a tick
  that moved nothing allocates nothing (0.49).** `MutateElementsAction.Execute`
  / `Undo` used to gnome-sort the UI-bound `ObservableCollection` — one
  `CollectionChanged(Move)` per SLOT travelled, on every tick of a drag or
  wheel gesture (`HistoryService.Do` executes before it merges): dragging one
  note across a 5,000-note level was 4,997 notifications per tick.
  `SortCollection` is now an O(n) sortedness check and, on an unsorted tick,
  the stable permutation plus the longest increasing subsequence of ranks so
  each displaced element gets exactly one `Move` (measured 4,997 → 1;
  379,105 → 598 for 600 scattered notes). The result is the stable sort the
  insertion sort produced, so equal-position runs keep their order.
  `MutateElementsActionSortTests` pins it against the old body verbatim and
  the sorted tick through `AllocationProbe` — which is what caught the sort's
  closure being hoisted above the early return (24 B per tick), hence the
  `RestoreOrder` split. Do not put a captured lambda back in `SortCollection`.
- **A GESTURE is one undo node, and an action opts in by naming the gesture —
  not by its type (0.40).** Typing a chord NAME is one of those since 0.49:
  `EditTemplateGlobalAction.NameMergeVerb` folds a keystroke run into one
  node — and the panel reloads the REAL template after a global edit, not the
  clone, which is what used to make every keystroke after the first mint a
  template (`ChordTemplateEditorViewModelTests.TypingAChordName_…`). `IMergeableAction.MergeVerb` + `MergeVerbs`;
  `HistoryService` folds a follow-up tick into the node the gesture already owns
  while the anchor is open (900 ms, injected clock) or until `EndGesture()`.
  Measured on one 32-minute session: **223 of 345 undo nodes were single
  gestures**, one hand-shape resize alone pushing **111 consecutive
  `MutateHandShapesAction` nodes** — and since the budget is 500, a long session
  spent it on wheel ticks and pruned real edits. Three rules that are load-bearing:
  **(a) key on the VERB.** Adjusting a sustain and re-fretting a chord are both a
  `MutateChordsAction` over the same chord; one shared verb would make one Ctrl+Z
  revert a separate, deliberate edit. Same reason anchor fret and anchor width
  have different verbs.
  **(b) `CanAbsorb` is PURE and separate from `Absorb`**, so a `CompositeAction`
  can poll every child before any commits. A half-merged composite describes a
  chart state that never existed, and redoing it produces one.
  **(c) targets compare by REFERENCE.** Two actions can hold equal-looking chords
  at one position and belong to different objects.
  **(d) `Absorb` must take over the incoming tick's `SpacingCollateral` too, and
  the head keeps the EARLIEST snapshot per element** (`CollectionActionBase
  .AbsorbCollateral` → `SpacingCollateral.MergeKeepingEarliest`). `HistoryService
  .Do` runs `Execute()` first, so each tick records ITS enforcer collateral on ITS
  own instance — and the merge then drops that instance. Copying only `_newStates`
  therefore threw away everything the enforcer changed on NEIGHBOURS from every
  tick but the first, i.e. reopened the erosion `SpacingCollateral` was added to
  close in 0.34.1. "Earliest" is the other half: the later snapshot of an element
  both ticks touched is already truncated, so overwriting with it would restore
  the wrong value.
  Not reproducible through a shipped keybind as of v0.43 — `EnforceSoundSpacing`'s
  shortening branch is a pure function of ONSETS, and none of the seven merge
  verbs moves a position — so it is a CONTRACT guard
  (`SpacingCollateralUndoTests.AMergedGestureRestoresTheCollateralOfEveryTick_…`),
  and a new verb on a position-moving gesture is exactly what would have made it
  user-visible.
  The merge anchor is cleared by every history navigation, so "drag, Ctrl+Z, drag
  again" cannot fold into the node just reverted.

- **…and the rule binds a COMPOUND verb even when nothing merges: build one
  action list, commit one composite.** A verb that calls another verb inherits
  its `HistoryService.Do`, and that second commit is a second node.
  `GlobalEditingCoordinator.DeleteSelectedRelated` — one keybind
  (`k.DeleteRelated`, plus the `edit.deleteRelated` palette command) — called
  `DeleteSelected` and then pushed the orphaned hand shapes separately, so one
  Ctrl+Z restored the hand-shape bars over sounds that were still gone (a chart
  state that never existed) and a second press was needed to finish. The delete
  half is now the private `BuildDeleteActions`, which builds and CLEARS but
  commits nothing; the orphan scan therefore runs on the PRE-delete chart and
  discounts the doomed elements BY REFERENCE. Pinned by
  `DeleteSelectedRelatedTests` (one `Undo` restores both halves and leaves
  `CanUndo` false).

- **A tone's IDENTITY is renamed and deleted by ONE verb, as ONE node (#181):
  `Editing/Tones/ToneKeyEdits` (`PlanRename`, `BuildDelete`).** A tone key is
  stored in five places — the `ToneDefinition` (its `Key`, plus the `Key` inside
  the manifest body), the arrangement's `ToneBase`/`ToneA`–`ToneD`, its
  `StartingTone`, its derived `ToneNames`, and every `ToneChange.ToneName` — and
  the Song tab and the Tone designer each used to rewrite a different subset, so
  a rename "worked" where it was made and the export then found no definition.
  Both callers (`SongOverviewViewModel`, `ToneDesignerViewModel.CommitPendingEdit`
  / `DeleteTone`) go through the verb; matching is trim- and case-insensitive
  like `UpsertToneDefinitionAction`, and undo gives each tone change back ITS OWN
  spelling (`RetargetToneChangesAction`). The definition's display NAME follows
  the key only when it was a mirror of it. **A collision is REFUSED, never
  merged** (`ToneKeyRenameOutcome.Taken`, printed under the Song-tab row; the
  designer already refuses a key another definition holds): the target is taken
  when another tone DEFINES it, or when it is placed/declared and the renamed
  tone has no definition either. The one in-use target that is allowed is a key
  that is referenced but UNDEFINED while the renamed tone IS defined — that gives
  the dangling references their body, and is how a project broken by the old
  partial renames is repaired (the Song tab marks such a key "no definition").
  Delete takes the definition, the tone changes and every slot, starting tone
  and declared name with it. `Services/Editing/Tones/ToneKeyEditsTests` seeds
  every string property named `*Tone*` on `Arrangement`/`ToneChange` by
  REFLECTION, so a sixth place added later fails it until the verb learns it
  (proven by adding a `ToneE`). **The store OUTSIDE the project follows at SAVE
  time, never inside the edit (#196):** the player's per-song tone edits and
  chart-synth instrument overrides (`SongToneEditStore`) are keyed by the SAVED
  song's identity (an own chart's project path) plus the tone key. A rename's
  node ends with a `ToneKeyRenameJournalStep`, which records `old → new` on do
  and redo and `new → old` on undo into the live project INSTANCE's
  `ToneKeyRenameJournal` (a weak table, so a discarded project takes its unsaved
  renames with it). `ProjectSaver` reads the net renames beside its snapshot
  (`Session/SongToneEditKeyFollow`; a move whose new key the snapshot does not
  carry is dropped), moves them with `SongToneEditStore.MoveKeys` after the
  `.rscp` swap, and settles the journal only once the store agrees — an
  unwritable store keeps them for the next save. Save As COPIES the old path's
  entries (renamed) to the new path and leaves the old path's alone; a first save
  and a library (`psarc:`) session move nothing, and `MoveKeys` refuses to write a
  `psarc:` identity. Pinned by `Services/Session/SongToneEditKeyFollowTests`.
- **The repair of an undefined key is PROPOSED only without doubt (#195):
  `Editing/Tones/ToneKeyRepair.Propose`.** Exactly one referenced key with no
  definition, and exactly one defined tone that nothing PLAYS (no tone change
  names it, it is no arrangement's starting tone — the base slot stands in for an
  empty one) and that shares no arrangement's Base/A–D slots with the undefined
  key. Those are the two shapes the old partial renames left (the Song tab's
  definition left behind in the slots, the designer's definition moved alone).
  The defined tone takes the chart's key, through `PlanRename` — one undo node —
  because that is the one in-use target the verb allows. Anything else stays a
  report. `ChartMaintenanceService.RepairUndefinedToneKey` applies it for the
  export Fix; the package builder's "declares tone … has no definition" warning
  names the same rename. Pinned by `Services/Editing/Tones/ToneKeyRepairTests`
  and `Services/Editing/Maintenance/UndefinedToneKeyFixTests`.

- **A mutate action must SEE exactly what it can TRANSFER, and that is now a
  reflection guard (0.46).** Every `Mutate*Action` pairs a transfer function
  (`t.CopyFrom(s)`, or an inline field-by-field lambda) with a comparer from
  `ChartActionStateComparer`, and `MutateElementsAction.HasEffect` *is* that
  comparer — so a field the comparer forgets makes an edit that touches only
  that field read as a no-op: `HistoryService.Do` drops the action, the edit
  never happens, and no undo step is recorded. The reverse is a phantom undo
  node that changes nothing. Both halves are asserted per property by
  `DotCharter.Tests/Services/State/ChartActionFieldCoverageTests` — it populates
  the element by reflection, alternates ONE property, and checks that
  `HasEffect` fires and that `Execute()` actually moved it — for
  `MutateNotesAction`, `MutateChordsAction` (including nested `ChordNote`),
  `MutateHandShapesAction`, `MutateAnchorsAction`, `MutateEventPointsAction`,
  `MutateBeatsAction` and `BendValue`. **This had already shipped four times**:
  the sheet duration hint, per-string `Accent`/`Ignore`, and — found by the
  guard itself — `GuitarNote.GraceSlide` plus `GuitarNote`/`Chord`
  `IgnoreDerived`, the last of which meant an `Ignore`-provenance change could
  be dropped and `ChartSpacingEnforcer` would then retract a charter's own
  authored `Ignore` on the next save. A property that genuinely must not
  participate goes in that test's `Exclusions` with a reason; the only current
  entries are `EventPoint.Events` and `EventPoint.Status`, which
  `MutateEventPointsAction` deliberately does not carry because the drag builds
  new states from the Position/Phrase/Section triple alone and transferring the
  other two would DELETE them.

