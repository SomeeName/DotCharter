# `Editing/` — the chart-model invariants (deep reference) — linked measures

Linked measures (#76; phase 1a #88 is the backend, phase 1b #134 the editor UI). A **link group**
(`Core/Domain/Project/LinkGroup`, on `ChartProject.LinkGroups`) keeps runs of
whole measures equal across their **placements** — in one arrangement or several.

## The model

- **Every placement keeps its own real copy** in its arrangement's HARDEST level
  (`ChartLevel.Notes/Chords/Anchors/HandShapes/FretHandMutes`). The group is only
  the rule that keeps the copies equal. Nothing that reads a level needs to know
  about links; the exporters write plain copies; an older build reads the chart.
- `LinkPlacement` holds its arrangement by **reference** (saved as an index) and
  its run as two ABSOLUTE barline positions `[Position, EndPosition)`. A
  placement whose arrangement left the project is dead and is dropped
  (`LinkRuns.IsLive`); the restorer maps each placement to the restored
  project's arrangement at the same index.
- `LinkGroup.Rule` is the comparison rule (`MeasureContentOptions.Header()`, the
  detection role at the gap the group was created with). Every check of a group
  — the history, the save, the load check — compares under THAT rule.
- Not linked: notation voices (the rule does not compare them), tones,
  sections, phrases, lyrics, levels other than the hardest.

## Propagation lives in `HistoryService`, nowhere else

- `State/LinkReconciler` runs inside `HistoryService.PushAction` (after the
  action, before the node is pushed or merged), compares each placement with
  its key from before the action (gated by `Arrangement.GetRevision`), and
  applies the action's `LinkPolicy`. What it changes is ONE
  `LinkFollowUpAction` stored IN the step's node: undo reverts it first, redo
  re-applies it last, a merged gesture tick folds its follow-up into the node's
  (`Absorb`). **Undo/redo never reconcile** — they only re-read
  (`LinkReconciler.Rebuild`).
- Order inside one commit: drop dead placements → unlink placements whose
  arrangement can no longer share (`LinkRuns.DropIncompatible`: tuning, capo,
  strings) → unlink placements whose measures changed (`LinkRuns.Geometry`: a
  beat inserted or removed inside, a time signature) → compare → propagate or
  split → re-read every target and unlink any copy that still does not match.
  **A copy is never left linked and different.**
- A propagated copy is `Core/Linking/LinkedRunCopier.Plan` (notes, chords,
  anchors, hand shapes, fret-hand mutes that START in the run, moved by whole
  beats; templates resolved by GRIP in the target, minted there, never mutated;
  held sustains and shapes lengthened to the target's own bound; a link-next
  whose continuation is outside the run keeps the flag only where the target has
  the continuation) followed by the editor's spacing post-pass over the target
  level, its collateral recorded (`LinkCopyStep`).
- A fault in the link layer never costs the edit: the reconciler reverts its own
  copies, restores the groups and logs.

## Every action declares a link policy

`State/LinkPolicy` + `ILinkPolicyAction`, one per action type:

| Policy | Means | Who |
|---|---|---|
| `Propagate` | the changed copies agree: copy to the others in the same step; they disagree: split by content (the untouched copies keep the group) | editing gestures (collection actions, template edits, hand-shape resizes) |
| `ShareFirstChanged` | a generator's result is kept once per group: the first placement (group order) the action changed is copied to every other placement in the same step, so every link survives; content outside placements keeps the generator's own output | composites wrapped with `CompositeAction.LinkPolicyOverride`: Generate FHP (arrangement and selection), Generate hand shapes, the AI chart pass, Generate fingerings (chord panel), Generate levels |
| `UnlinkOnDivergence` | never propagated; a placement that comes out different is unlinked | tools that apply outside truth: `ProjectSnapshotAction` / `ArrangementSnapshotAction` (import, sync, refret, squash levels, tempo-map tools, `RunAsSingleUndoStep`), `BeatScaleAction` |
| `Retain` | cannot change linked content in beats; nothing propagated or unlinked by content | tempo (`MutateBeatsAction`), metadata, tones, lyrics, vocals, arrangement settings, the link actions themselves |

Geometry and compatibility unlink under EVERY policy, so a tempo-only change
never unlinks and a time-signature change inside a run always does. An action
that declares nothing is treated as `UnlinkOnDivergence`; a composite without an
override takes its strictest child (unlink, share, propagate, retain).
`LinkPolicyReachabilityTests` holds the registry: a new product action fails the
gate until it is classified there, and each whole-chart tool file
(`ToolFlows`) must name its policy and have its behaviour test
(`LinkedMeasuresGeneratorTests` for the generators).

Generators and difficulty levels: links live on the hardest level only. Generate
levels keeps the hardest level verbatim, so its links survive; the lower levels
are built per arrangement and are never linked or copied. The source is the
first CHANGED placement, not the first placement, so a range tool (Generate FHP
for a selection, an AI chunk) never has its own change reverted by an untouched
earlier copy.

## Creating, saving, loading

- `Editing/Linking/LinkGroupCreator.TryBuild` checks the strict rules (two runs
  or more, whole measures with one time-signature sequence, `LinkCompatibility`,
  no overlap, not empty, equal under the rule) and returns ONE undo step that
  also splits hand shapes at the run edges and pins the anchor in effect at each
  run start. The pin is not protected from `RemoveRedundantAnchors`, on purpose:
  the rule compares the position IN EFFECT, and propagation re-pins it where a
  target would inherit a different one.
- `.rscp`: root node `dcLinkGroups` (`Parsing/Rscp/RscpLinkGroups`), checked on
  load by `Core/Linking/LinkGroupVerifier`; see `docs/RSCP-FORMAT-GUARANTEES.md`
  § Linked measures. `.dcsong` carries it inside its `chart.rscp`.

## Group edits, Only here, Link identical measures

- `HistoryService` raises `LinksPropagated` with one `LinkPropagationReport` per
  group an edit was copied into (source placement, targets, policy). Only
  `Propagate` runs produce reports.
- `IHistoryService.OnlyHere(report)` keeps the edit in the source placement,
  restores every target and unlinks the source — ONE new undo step; false when the
  report's step is not the current one.
- `Editing/Linking/LinkGroupEdits` — `Unlink`, `NewGroupFrom`,
  `SplitByArrangement`, `Rename`, `Delete`: each returns one undo step
  (`LinkPolicy.Retain`) that swaps the project's whole group list, so a group
  object held across the step is stale — look it up again by `Id`.
- `LinkGroupEdits.AddPlacement(project, groupId, run)` adds one run to a group (one undo
  step, `AddLinkPlacementAction`, `Retain`, the run's edges normalised like a new group's
  through `LinkRunEdges`); null unless `LinkRelinkSuggestions.Qualifies` holds against the
  chart as it is now.
- `Editing/Linking/LinkRelinkSuggestions.Find(project, group)`: the re-link suggestions —
  runs in no group, in an arrangement that may share with the group, with the group's measure
  geometry and equal under the group's rule. One content index per compatible arrangement:
  the link panel calls it on open / re-read only (a closed panel forgets its group,
  `LinkPanelViewModel.Close`, so edits made while it is closed re-read nothing).
- `Editing/Linking/LinkIdenticalMeasures.Plan(project, arrangement, first, last, rule)`
  widens the span to whole measures, finds every equal unlinked run in every
  compatible arrangement and returns a `LinkGroupCreator` action or the
  `LinkCreateRefusal`.

## The editor UI

All under `DotCharter.UI`; nothing changes in 3D or Play Mode.

- **LINKS row**: `RibbonLanes.Links` (deliberately NOT in `RibbonLanes.All`, so
  the headless goldens do not move). `RibbonLaneRules.Visible` drops it whenever
  the track has no group (`RibbonContent.HasLinks`), whatever auto-hide says.
  Toggle: `RibbonVisibility.ShowLinks` / `AppConfig.RibbonShowLinks`, the lane
  menu and the palette's `view.toggleRowLinks`.
- **Drawing**: `Controls/Layers/LinkLayer` (chip + `×N` + span line; for the
  emphasised group an outline over its measures; a fading flash on the targets of
  the last propagation). Spans come from the pure `Editing/Linking/LinkLaneModel`.
  `Editing/Linking/LinkHighlight` (on `ChartEditorViewModel.LinkHighlight`) holds
  hovered / selected / flashing group for every timeline and the song map.
- **Song map**: `MinimapOverviewControl` draws `Controls/SongMapLinkMarks` (pure) for the
  emphasised group only — an outline per placement of the arrangement on screen with the
  letter chip at its bottom-left — and nothing otherwise (no permanent link lines). It
  repaints when the emphasised group CHANGES, not on every hover-point move.
- **Input**: `EventInteractionTool` — hovering a LINKS chip sets the hover, a
  click calls `LinkHighlight.RequestPanel`.
- **View models**: `ViewModels/Editor/Linking/LinkedMeasuresViewModel` (DI
  singleton; `InspectorViewModel` takes it, so it is built with the shell and
  registers itself on `ChartEditorViewModel.LinkedMeasures`) owns the hover card, the toast ("Linked · changed N places [Only
  here]"; Only here → `IHistoryService.OnlyHere`), the Inspector banner and
  `LinkIdentical`; `LinkPanelViewModel` is the panel (placements, the three group edits, and the re-link
  suggestions with a Link button each).
- **Views** (`Views/Editor/Linking/`): `LinkOverlayView` sits over the main
  timeline in `MainWindow.axaml` (hover card, toast, and the panel as its attached
  `PrewarmableFlyout`, bound to `ChartEditor.LinkedMeasures`); `LinkBannerView`
  sits in `SoundInspectorView` above the apply bar, bound to
  `InspectorViewModel.LinkedMeasures`, and the host owns its `IsVisible`
  (`IsBannerVisible`). Neither view reads `App.Services`.
- **Link identical measures**: Tools ▸ Structure & levels (the Edit menu is at
  its 14-entry cap), the palette's `edit.linkIdentical`, and the note context menu.
  A refusal shows `LinkCreate_<LinkCreateRefusal>` in the toast.
- Tests: `UI/Linking/*` (model, lane rules, layer, song-map marks, view models),
  `Services/State/Linking/LinkRelinkSuggestionsTests` (suggestions, Link, undo),
  `Perf/LinkedMeasuresConstructionUiPerfSmokeTests` (headless views),
  `Perf/LinkedMeasuresSongMapUiPerfSmokeTests` (the real song map),
  `Rendering/Headless/HeadlessLinkTimelineTests` (GPU frame).

## The known limit

"Content in beats" meets the spacing gap: in milliseconds mode, when the minimum
note distance is coarser than the chart's rhythm at ONE copy's tempo (80 ms above
about 187 BPM against quarter-beat spacing), the clean-up pass must shorten a
sustain in that copy only. The copies then sound different and the rule calls
them different, so that copy is unlinked (with the edit applied).
`LinkedMeasuresReleaseBlockerTests.A_clamp_only_one_copys_tempo_forces_unlinks_that_copy_and_keeps_the_edit`
pins it; the release-blocker property tests skip those tempo maps. Keeping such
copies linked needs a comparison that treats "at its clamp bound" as compatible
with a longer sustain elsewhere: a change to `MeasureContentIndex` (#82).
