# `Editing/` — the chart-model invariants (deep reference) — the editing façades: Highway, Playback, Targets, Pen

## The editing façades — `Editing/Highway|Playback|Targets|Pen`

- `Editing/Highway/` — the command/undo façade for every highway edit
  (`IHighwayEditCommand.Execute(IHistoryService)` is its ONLY member, which is
  what makes highway editing history-backed by construction — never add an edit
  path around it). Since 0.35 `SelectedNoteMutationCommands` operates on
  `IGuitarSound`, so the keyboard verbs work on CHORDS as well as notes, filtered
  by the persistent `ProjectState.StringEditMask` (`Editing/StringEditMask.cs`;
  EMPTY = every string). The TECHNIQUE verbs also ask
  `IHighwayEditingService.TechniqueReach` (`IChordTechniqueReach`, set by the
  inspector) and, on a rung of the "Apply to" ladder past "selected", write the
  rung's chords on every level through `ChordTechniqueEdit` — the inspector's own
  builder, which lives here so the two doors cannot drift (#604;
  [chord-verbs.md](../../ui/editor-input/chord-verbs.md)). **Read
  [../ui/editor-input.md](../../ui/editor-input.md) before
  touching either** — it carries the per-verb chord semantics (per-string vs
  whole-chord vs whole-shape) and the rule that any verb moving a fret or a
  string must resolve-or-create through `ChordTemplateResolver` and repoint
  `TemplateId`, because an rscp `<chordNote>` has no `fret` attribute and an
  unrepointed fret change is discarded on the next save. That repoint is the
  shared `ChordReshapeAction` — `EditChordStringCommand` and
  `ApplyNotePlacementCommand`'s chord branch both go through it (the latter did
  NOT until 0.35, so the same gesture was correct through the right-click route
  and lossy through the placement route).
  - **Nothing in this façade may do work proportional to the SELECTION for a
    log line, and nothing may write the selection back one item at a time
    (0.60).** `_logger.Info` takes an already-interpolated `string`, so
    `RunMutation`'s per-sound dump was built before the logger was asked whether
    it wanted it — 9.16 MB of garbage per keypress at 5,000 selected, measured,
    on the UI thread; `LogDetailCap` (20) plus an "…and N more" tail is the shape
    every one of these lines takes now, including the drag commit's one-line-per-
    sound results. And the post-drag reselection went through
    `SelectionBulkOps.ReplaceWith` for the reason that helper's own summary
    gives: every `Selected*` collection has a handler that re-reads the WHOLE
    selection, so N `Add`s is quadratic — 20,004 notifications for a 5,000-sound
    drag became 4. `LargeSelectionEditCostTests` pins both, structurally.
- `Editing/Playback/` — the **editor's A/B loop region + per-selection
  audition** (0.35, editing-rework stage 3). It REUSES Live Play's loop
  plumbing rather than duplicating it: the region IS a `LivePlayLoop` and its
  `TryCreate`/`ShouldWrap` are now the single implementation that
  `LivePlaySessionService` also goes through. `EditorLoopDecision` is PURE
  (it is the one thing that runs at 250 Hz on the audio timer thread) and
  carries the one-seek-per-pass latch; `AuditionSpan` is pure too.
  **The wrap aims at `RawPlayheadMs`, never the compensated playhead** — the
  loop end is a point of the SONG, so the same rule as `TryScheduleClick`
  applies; SETTING a punch point reads the compensated one, because marking is
  not scheduling. Rules in
  [../ui/editor-input/loop-region-and-audition.md](../../ui/editor-input/loop-region-and-audition.md).
- `Editing/Targets/` — the **one keyboard target ladder** (0.35, editing-rework
  stage 3): `EditTargetResolver` (PURE — selection → hand shape → pen, every
  input a parameter) plus the thin `EditTargetService` that supplies the
  ambient state and promotes an implied target to a real selection. It sits in
  front of every `IHighwayEditingService` verb AND the pen's typed fret, which
  is the point: the pen used to resolve its own implied target while every
  other keyboard verb was selection-only. **A rung claims as soon as its own
  selector is non-empty, even with no sounds to show for it** (a selected hand
  shape over empty bars must not fall through to the playhead), and the two
  IMPLIED rungs are gated on `AppConfig.UseFastChartingInput`. `Resolve()` is
  the read-only seam Stage 5's inspector target should be built on. Contract in
  [../ui/editor-input/target-ladder.md](../../ui/editor-input/target-ladder.md).
- `Editing/Pen/` — the **keyboard note-entry pen** (0.35, editing-rework stage
  2), behind `AppConfig.UseFastChartingInput` (OFF by default) so it runs
  alongside the pointer placement path rather than replacing it. Three types
  with a deliberate split: `FretDigitAccumulator` (the multi-digit fret typist,
  pure, lifted out of `SheetMusicView` so there is ONE implementation),
  `ChartPen` (the pure state machine — no clock, no chart, no services;
  decides but never edits) and `ChartPenService` (the driver, which reuses
  `ApplyNotePlacementCommand` and `SelectedNoteMutationCommands` rather than
  adding a second set of verbs). **Read
  [../ui/editor-input/keyboard-pen.md](../../ui/editor-input/keyboard-pen.md)** for the
  state table, the undo granularity (one string toggle = one node; one typing
  RUN = one node, a continuation digit UNDOES the run's own node first) and why
  bare digits toggle strings while `Alt`+digits type frets.

