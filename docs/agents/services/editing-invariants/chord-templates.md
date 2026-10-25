# `Editing/` — the chart-model invariants (deep reference)

Extracted verbatim from `DotCharter.Services/AGENTS.md`. **Read this before
touching chord templates, hand shapes, FHP anchors, the undo/gesture merge,
select-alike, `ChartSpacingEnforcer`, refret, the phrase catalog or structure
generation.** Every rule here was paid for by a shipped bug; several are pinned
by a test named in the bullet.

See also: `DotCharter.Core/AGENTS.md` (the domain model, the cloner rule, the
capo invariant) · [../ui/editor-input.md](../../ui/editor-input.md) (the keyboard
and pointer verbs built on top of these) ·
[export-companions.md](../export-companions.md) (what the export boundary fixes
instead of the chart) · [import-tabs-audio.md](../import-tabs-audio.md) (the
generators — hand shapes, anchors — as measured on the import path)

---

## Chord templates, shapes and arpeggios

- **A `ChordTemplate` is SHARED — never mutate one in place.** Templates are
  find-or-create/deduped by shape, so a single instance backs every chord AND
  hand shape with that shape; editing one silently rewrites all of them. To
  change a chord's shape, resolve-or-create a template that already has the new
  shape and repoint that chord's `TemplateId`. `RefretService` and (since 0.33.2)
  `ChartSpacingEnforcer.RemoveMutedNotesFromPartiallyMutedChords` both do this —
  the latter used to strip strings from the resolved template in place, and since
  the enforcer runs on EVERY save the damage accumulated across a chart.
  `RscpTemplateManager.SynchronizeHandShapeAndTemplateName` was the third
  offender (it copied a hand shape's name onto the shared template) and is GONE
  in 0.35. The same pass no longer touches an AUTHORED hand-shape `TemplateId`
  either: a shape with no chord inside its span used to be repointed at template
  `0`, which discarded every let-ring/single-note run's find-or-created template
  (0.34.1) on the first save, and a shape over a chord used to be forced onto
  that chord's REDUCED template, undoing the enforcer's "the hand shape keeps its
  own full-shape template" contract two passes later. **`0` is a real template
  id and is never an "unset" fallback — `-1` is the sentinel.**
  **That rule binds the WRITER too (0.41):** `RscpWriter.BuildHandShapesNode`
  coerced `-1` to `0` on the way out ("rscharter always serializes templateId"),
  so the sentinel the pass above had just protected was destroyed one method
  later and the bar reopened wearing an unrelated chord's frets, fingering and
  name — reachable from the primary authoring gesture, since right-clicking an
  EMPTY stretch of the hand-shape lane leaves `HandShapeLaneSpan.BuildBar`
  with no sounds to resolve. The sentinel is now
  spelled as an ABSENT attribute, which both readers already treat as "no
  template": our parser defaults to `-1`, and rscharter's `HandShapeConverter
  .readTemplateId` returns its null `Integer`. Do NOT write it as `"null"` (that
  is what rscharter's own `marshal` emits, and its `Integer.valueOf` throws on
  it) or as `-1` (`HandShape.likeGenerator` would do `chordTemplates.get(-1)`).
  **A chord and the hand shape over it stay on ONE template through every edit
  path, unless `AppConfig.SyncHandShapeToChordTemplate` is off.**
  `Editing/HandShapes/ChordHandShapeSync` is the one rule. A fret or string verb
  (`SelectedNoteMutationCommand.AddChordMutation`), a placement or right-click
  string edit (`ChordReshapeAction.Build`) and a drag-drop merge
  (`CommitHighwayDragDropCommand`) put a `MutateHandShapesAction` in the SAME
  undo node as the chord edit (the caller passes the level's hand shapes only
  while the sync is on: `SelectedSoundMutationTargets.SyncedHandShapes`). A bar
  follows only when it was in step (its template is the one the chord left) and
  every templated chord under it ends on the same template; otherwise it stays.
  The two hand-shape verbs go the other way: a split's tail
  (`HandShapeSplitter.ResolveTemplateId`) and a wheel-resized bar that owns its
  template (`TimelineWheelAdjuster.RebuildGrips`) take the template of the chord
  whose frets they play (`ChordHandShapeSync.TemplateOfChordPlaying`) instead of a
  private copy or the first equal-fret template. Pinned by
  `ChordHandShapeSyncTests`, `HandShapeSplitSyncTests`, `HandShapeResizeSyncTests`.
  **An edit that leaves a template with no user removes it in the SAME undo
  node.** `Editing/ChordShapes/StrandedShapes.Wrap(arrangement, action)` is the one
  rule: every action reports the chords and hand shapes it repoints, adds or
  removes (`ITemplateUseReporter` into a `TemplateUseDelta`), and the templates that
  go from some users to none are appended as a `RemoveStrandedShapesAction`, which
  is `DeleteChordShapesAction` (survivors renumbered while ids are positions, every
  chord, hand shape and notation-voice chord repointed, undo restores the exact list
  and ids). A user is a chord, hand shape or notation-voice chord on ANY level; a
  template nobody used before the edit stays, and so does one that still has a user
  when the removal runs. Call `Wrap` on the FINISHED action at the commit site: the
  fret and string verbs (`SelectedNoteMutationCommand`), placement and right-click
  string edits (`ApplyNotePlacementCommand`, `EditChordStringCommand`), drag-drop
  (`CommitHighwayDragDropCommand`, which hand shape split and paste go through),
  `HighwayEditingService.RemoveHandShape`, `GlobalEditingCoordinator.DeleteSelected`
  and `DeleteSelectedRelated`, the wheel resize (`TimelineWheelAdjuster.BuildResize`),
  the chord panel (`ChordTemplateEditPlanner`, Create new), chord shape placement and
  the apply-hand-shape-template command. The shape list's merge and delete build their
  own `DeleteChordShapesAction`. **A new action that repoints, adds or removes chords
  or hand shapes must implement `ITemplateUseReporter`, or the template it strands
  stays.** The removal renumbers ids that later ticks would have captured, so a
  wrapped edit never merges into a held-key or wheel run (the tick that strands a
  template is a node of its own). Pinned by `StrandedShapesTests`,
  `StrandedShapePathTests`, `StrandedShapeResizeTests`,
  `ChordTemplateStrandedShapeTests`.
  **The find-or-create KEY is FRETS within one ARPEGGIO flavour** —
  `ChordTemplateResolver.Resolve` (editing), `ChordHandShapeBuilder`/
  `GpHandShapeBuilder` (import) and `RscpTemplateManager` (save) all use exactly
  that; the hand-shape planner's editing grip source
  (`Editing/HandShapes/ResolvedGripSource`, #478) goes through the resolver in
  the chord's OWN flavour, and keeps a chord's own template when it already
  describes the chord's frets. `Arpeggio` is part of the key because it is load-bearing on export
  (`ChordMask.Arpeggio`, the `.eof` arpeggio section type, purple brackets
  instead of a hand-shape bar), and leaving it out let the save path repoint a
  plain chord at an arpeggio template that merely shared its frets.
  **Which is why an UNUSED string must be ABSENT from `Frets`, never stored as
  `-1` (0.44).** `Rocksmith2014.XML`'s `ChordTemplate` writes `fret{i}="-1"` for
  every unused string whenever `UseAbridgedXml` is false — DLC Builder's normal
  output — and `RsXmlSongReader.ReadChordTemplates` stored them verbatim, so a
  template came back with `Frets.Count == 6` and four sentinels in it. The
  frets-keyed find-or-create can then never match one, so **every edit minted a
  duplicate**; `GetLowestFret()` returned −1 into the anchor and fingering
  paths; and the E05 rule's `Frets.Count == 0` "declares nothing" guard could
  never fire. The reader filters them now (`fv >= 0`), symmetric with the
  `Fingers` branch that always has. EOF omits the attributes entirely, which is
  why the corpus never showed it.

- **Rocksmith's Nop chord flag is `ChordTemplate.NopInRs`, never name text
  (#489).** Interchange formats spell it as a `-nop` display-name suffix; every
  reader folds it into the field through `Core/Domain/Guitar/RsChordDisplaySuffix`
  (`RsXmlSongReader` from `displayName` AND `chordName`, the package reader's SNG mapper from
  mask 0x2, the EOF track parser from the note name, and
  `ChartSpacingEnforcer.StripRsDisplaySuffixes` on every save for names typed
  before the field existed — it may leave the name EMPTY, as Rocksmith's own Nop
  double stops are unnamed), and every writer derives it back
  (`RsChordDisplaySuffix.ForExport`: `RsXmlSongWriter`'s displayName, the SNG
  mask, the `.eof` note name, `.rscp`'s `nop="T"`). One XML displayName holds one
  suffix and the game's converters test `-arp` first, so an arpeggio wins there;
  the SNG carries both bits. **Identity decision: Nop rides with the NAME** — it
  is part of how the game DISPLAYS the shape, not a different kind of chord. So
  it IS in value equality (`Equals`/`GetHashCode`), in
  `FindIdenticalTemplate` (else a Nop-only panel edit resolves back onto its own
  template and is dropped — `ChordTemplateNopToggleTests`) and in
  `RefretService.FindOrCreateTemplate` (name + Nop + flavour + frets: a refret
  keeps the authored display), and it is NOT in the frets-only shape keys
  (`ChordTemplateResolver`, `FindEquivalentTemplate`, `RscpTemplateManager`,
  `ChartSpacingEnforcer.SameShape`, `ResolveReduced`): there a reshape adopts the
  destination shape's authored name, fingering and Nop, exactly as for the name.
  The panel's toggle is its own edit kind (`ChordTemplateEditKind.Nop`) and, like
  the arpeggio box, never crosses to another grip on "+ the same grip". A
  further flag spelled as a display suffix follows the same pair (a `Fold…` for
  readers, `ForExport` for writers) and the same field checklist: `Clone`,
  `Equals`/hash, `ChartProjectRestorer`, `EditTemplateGlobalAction`, the `.rscp`
  writer AND parser — the reflection tests (`DomainCloneCoverageTests`,
  `ChartProjectRestorerCoverageTests`, `RscpFixtureCoverageTests`) demand all but
  the action.

- **`ChordTemplate.FollowedFingering` says WHO made the fingering, not what the
  grip is (#508).** `ChordFingeringFollower` sets it on the variants it mints (the
  suggestion shifted up the hand); it rides `Clone`, `ChartProjectRestorer`,
  `EditTemplateGlobalAction` and `.rscp`'s `followed="T"` (writer AND parser,
  absent when false); no exporter writes it. **Identity decision: provenance
  joins NO key** — not `Equals`/hash, not `FindIdenticalTemplate`, not the
  frets-only keys — so the follower reuses an existing template with the same
  fingering and a typed fingering can land on a minted one. **A hand edit makes
  the fingering authored:** `ChordTemplateEditPlanner.ApplyEdit` clears it for a
  Fingering or Shape edit (a name, arpeggio or Nop edit keeps it), a typed
  fingering that resolves onto a minted template marks THAT template authored in
  the same undo node (`SetTemplateFingersAction.MarkAuthored`), and
  `SetTemplateFingersAction` — the "same grip" rung, the 1·3 / 1·4 switches,
  Generate fingerings — clears it and undo restores it
  (`ChordTemplateFollowedFingeringTests`, `FollowedFingeringPersistenceTests`).
  **False is the safe default:** an old project or an imported chart has every
  shifted fingering authored, as before #508.

- **`ChordTemplate.Fingerless` says the grip has no fingering ON PURPOSE (#609)** —
  EOF's Fingerless eflag (`song.h:137`; per NOTE in EOF, per template here). An
  empty `Fingers` map already exports `-1`; the flag adds the intent: L06/L07/L08
  skip the template, Generate fingering does not fill it, and the EOF import and
  export carry it. Unlike `FollowedFingering` it IS part of `Equals`/hash (it
  changes validation and the EOF eflag) and of the EOF importer's template key. It
  rides `Clone`, `ChartProjectRestorer`, `EditTemplateGlobalAction` and `.rscp`'s
  `fingerless="T"` (writer AND parser, absent when false — an older project reads
  as not fingerless). **Kept with an empty `Fingers` map:** a fingering someone
  sets clears it (`SetTemplateFingersAction` when it sets any finger, and
  `ChordTemplateEditPlanner.ApplyEdit` for a state with fingers), and undo
  restores it. **The UI toggle is the Fingerings header's icon toggle in the
  chord template panel (#622)**, `ChordTemplateEditorViewModel.IsFingerless`: its
  own `ChordTemplateEditKind.Fingerless` (read off the flag BEFORE the fingers,
  since turning it on clears them), so like Nop it takes the ladder's rung but
  never crosses to another grip; `FindIdenticalTemplate` compares the flag, or a
  "this chord" toggle on an unfingered shared shape resolved back onto its source.
  While it is on, the per-string finger strips are disabled with the reason as
  their tooltip (`ChordTemplateFingerlessToggleTests`).

- **Two flags spell ARPEGGIO and each import path sets only one of them.**
  `RsXmlSongReader.ReadChordTemplates` sets `ForceArpeggioInRs` (from a `-arp`
  displayName) and never `Arpeggio`; the package reader's SNG mapper
  sets `Arpeggio` (from chord mask 0x1) and never the other. **Anything asking
  "is this an arpeggio?" must test BOTH**, or it works on psarc imports and is
  silently dead on every DLC-Builder / EOF chart (or the reverse).
  **There are exactly two questions and Core owns both answers (0.44):**
  `ChordTemplate.IsArpeggio` ("is this an arpeggio?" — either flag) and
  `ArpeggioFlavour.Matches` ("is this the SAME flavour?" — both flags exactly,
  the non-fret half of every find-or-create key; a false negative mints one
  extra template, a false positive repoints a chord onto a template that exports
  under a different name). **Every editing site read one flag** and each was a
  real defect on RS-XML/EOF charts: `ChordTemplateResolver` broke BOTH ways
  (re-shaping an arpeggio chord resolved with a PLAIN key and dropped its `-arp`
  export name; re-shaping a plain chord onto those frets adopted the arpeggio
  template), `ChartSpacingEnforcer.FindOrCreateReducedTemplate` repointed a
  partially-muted arpeggio at a plain reduced template **on every save**,
  `RefretService.FindOrCreateTemplate` could hand the refretted chord a template
  whose `ForceArpeggioInRs` disagreed, `FingeringGenerationService`'s two
  resolve keys ignored it, and `ChordShapeTransposition` /
  `ChordShapeSuperset` / `PatternMatcher`'s arpeggio facet made every RS-XML
  arpeggio "alike" to the plain shape beside it. `ArpeggioFlavourTests
  .NoEditingCode_AsksAboutOneArpeggioFlag` is the mechanical rule: nothing under
  `Services/Editing` may READ `.Arpeggio`/`.ForceArpeggioInRs` on its own
  (assignments are exempt — `ChartSpacingEnforcer.StripRsDisplaySuffixes`
  deliberately WRITES the RS-specific flag when it strips a `-arp` suffix).
  **The lint is scoped to that folder, so a find-or-create OUTSIDE it is not
  covered and one was wrong for exactly this reason:**
  `ChartMaintenanceService.MergeSelectedNotesIntoChords` (in `DotCharter.UI`)
  open-coded its own frets comparison filtered by `!t.Arpeggio`, so merging
  plain notes on any RS-XML/EOF chart adopted a `ForceArpeggioInRs` template
  that merely shared their frets and the new chord exported as `-arp`. It routes
  through `ChordTemplateResolver` with `ArpeggioFlavour.Plain` now
  (`ChartChordEditingRegressionTests`). **A find-or-create anywhere is the
  resolver's job — never a local copy.**
  Outside that folder the two consumers ask the fold too: `DotCharter.Rendering`'s
  `FrameSnapshot` projects `ChordTemplate.IsArpeggio` into
  `RenderChordTemplate.Arpeggio` (the 3D brackets) and `RenderHandShape.IsArpeggio`
  (the 2D hand-shape bar, #480) — `Rendering/ArpeggioFlagsTests` lints that nothing
  there reads `ForceArpeggioInRs` — and `EofExporter` writes its arpeggio sections
  from `IsArpeggio`.
  `HandShapeContentRule` (inspector E05) is the worked example: it skips
  arpeggios and templates declaring fewer than three strings, because official
  Rocksmith content routinely plays sounds under a shape whose template does not
  describe them — 4,443 disagreements over 642,429 (sound, string) pairs in 691
  official arrangements, arpeggios at 2.54% against 0.59% for plain shapes, and
  64% of the findings under RS's one/two-string hand-POSITION bar. The rule's
  own summary carries both corpora; note the `TestData/EOF` corpus contains
  **zero** arpeggio templates, so it cannot measure that half at all.
  **FOUR stricter comparisons exist ON PURPOSE and must not be "unified" with
  it** — they answer a different question, "is this byte-identical to something
  I already have", and their failure modes are asymmetric: a false negative
  mints one extra template, a false positive silently re-fingers or renames a
  chord.
  `ChartSpacingEnforcer.SameShape` (frets **+ fingers**, used only by
  `FindOrCreateReducedTemplate`) would otherwise hand a partially-muted chord a
  same-shape template carrying someone else's authored fingering, on every save.
  `ChordTemplateResolver.ResolveReduced` (frets **+ the source's fingers** +
  flavour, #490) is the same key for the same reason, for the reduced grips of
  generated difficulty levels: the hand must stay where the full chord puts it.
  `RefretService.FindOrCreateTemplate` (**name** + arpeggio + frets) keeps the
  refretted chord's musical NAME — refretting preserves pitch, so the name is
  unchanged, and matching frets-only would adopt whatever name an existing
  same-shape template happened to have (very often the empty one from an
  import). The audit's "it mints duplicates behind the user's back" was true
  only while the enforcer renamed templates (`min`→`m`), which is gone.
  `IFingeringGenerationService.FindIdenticalTemplate` (arpeggio + frets +
  fingers + **name**, 0.37) is the inspector's resolve key for an edit that
  moved **no fret** — a fingering, name or arpeggio change. The frets-only key
  cannot serve those: it returns the very template the edit came from, so the
  edit degrades to a `SwitchTemplateAction` onto the same shape and is silently
  discarded (the owner's "I have to click save fingering TWICE"). A SHAPE change
  still resolves frets-only, because after a reshape the destination shape's
  authored name + fingering are the trustworthy ones.
  `ChordTemplate.Equals` (every field but `Id` and the provenance flag
  `FollowedFingering`, incl. `ForceArpeggioInRs`, `NopInRs` and `Fingerless`) is value
  equality for the diff engine and the power-chord builder, not identity.
  **…and BECAUSE it is value equality, no default-comparer collection call may
  stand in for identity (0.41).** `Remove`/`Contains`/`IndexOf` on an
  `ObservableCollection<ChordTemplate>` hit the first VALUE-EQUAL template, not
  the instance the action owns — and two value-equal templates with different ids
  are ordinary chart state. Three sites did
  (`AddChordTemplatesAction.Execute`/`Undo`,
  `CreateAndSwitchTemplateAction.Undo`), so undoing a template-creating edit
  removed a DIFFERENT, still-referenced template and left its chords with a
  dangling `TemplateId` — which `SngFromDomainMapper` filters on
  (`TemplateIndexById.ContainsKey`), dropping those chords from the exported
  psarc with no error anywhere. `State/ChordTemplateCollectionOps`
  (`ContainsInstance`/`RemoveInstance`) is the identity answer. Do NOT weaken
  `ChordTemplate.Equals` to fix this.

- **Saving renumbers templates to POSITIONS, and every user follows (#547).**
  The `.rscp` is positional — a `chordTemplate` element carries no id and
  `RscpArrangementParser` numbers templates by index — so
  `RscpTemplateManager.RegenerateTemplates` (run by `RscpWriter` on a COPY of
  each arrangement, #304) renumbers them, and `RemapUsers` moves every chord and
  hand shape on every level AND every notation-voice chord from the template its
  OLD id resolved to (first template with that id wins) to that template's new
  position. An id that resolved to nothing becomes `-1`: left alone it could
  name a renumbered or freshly minted template. `RscpWriter` writes the
  `dcNotationVoices` node from the same regenerated copies, so a voice chord's
  id follows its template too. Before this, only a chord survived a gapped or
  twinned chart (its stale id failed validation and it re-resolved by frets);
  a let-ring bar and a voice chord kept the old NUMBER and reopened on whichever
  template sat at that position. On a chart whose ids already are positions the
  remap is the identity, so its bytes do not change. Writing ids as they are was
  not an option: the format has no id attribute, and rscharter indexes the list
  by position too (`chordTemplates.get(templateId)`). RS-XML and SNG export never needed this — they map ids to
  indices themselves (`RsChordTemplateExport`, `SngFromDomainMapper
  .TemplateIndexById`). Pinned by `Services/Parsing/Rscp/RscpTemplateRenumberTests`
  (gapped + twinned ids, let-ring bars on two levels, a bar over another chord,
  a voice chord, a dangling bar id).

- **Only Squash levels and Generate levels DELETE templates, and only the ones
  they orphaned (#520)** — plus the PSARC open's own squash under "Hardest
  difficulty level only" (`Library/LibraryLevelImport`, #634), which applies
  the same rule with no undo node. Everything else keeps an unused template
  (`RscpTemplateManager`, `ArrangementAutoFixService`). The Chord shapes list's Delete, Remove unused and merge
  delete by their own undoable `DeleteChordShapesAction`. **Clean up shapes
  (#272) is the same action once per undo node**: `ShapeCleanupPlanner` repoints
  every copy's chords, hand shapes and notation-voice chords with ONE
  `SwitchTemplateAction`, then removes every merged-away and unused shape with ONE
  `DeleteChordShapesAction` — never one merge per copy, because the removal
  renumbers positional ids and a later repoint planned before it would hold stale
  ids. `ShapeCleanupFinder` decides what is a duplicate (same frets, fingers and
  flags; two different names are never one group), what is unused, and what only
  differs by a finger, a name or a flag (review, nothing happens until chosen).
  `Editing/Improvement/LevelTemplateOrphans` is the rule: `Capture` before the
  operation, `Find` after it; a template goes only when NOTHING uses it afterwards
  (a chord or hand shape on any level, or a notation-voice chord, resolved through
  `ChordTemplateResolver.IndexById`) AND either the operation minted it, or it had
  a user before, lost it to the operation, and has NO name. A named template, and
  any template that had no user before (an import's spare, a shape not placed
  yet), always survives. **No persisted "minted" marker:** `ResolveReduced` mints
  reduced grips unnamed, which is what "unnamed and orphaned by the operation"
  relies on; a reduced grip the frets-only fallback minted carries a suggested
  name and so survives a LATER squash (the run that mints it drops it if unused).
  **The ids are renumbered to positions and every user remapped** (chords and
  hand shapes on every level, notation-voice chords), so the live chart stays
  positional like the `.rscp`. `LevelTemplateOrphans.Apply` does nothing on a
  chart whose ids were not positional before, and that guard STAYS after #547
  fixed the save path: its own remap is keyed by OLD id (two templates sharing
  an id would collide in it) and it leaves a dangling id alone because, on a
  positional chart, a dangling id cannot meet a renumbered one. Pruning a
  non-positional chart needs an instance-keyed remap like
  `RscpTemplateManager.RemapUsers` first.
  `ChartMaintenanceService.LevelOperationAction` puts all of it in the
  operation's ONE undo node: a mint leaves the AFTER snapshot (it sits at the
  tail); a declared orphan is PARKED in its slot there (id `-1`) and its live
  instance removed after the restore, by instance
  (`State/RemoveChordTemplatesAction`, back in its own slot on undo) — never
  pulled out of the snapshot, whose index-wise reconcile would shift every later
  template's values into another live instance. Pinned by
  `Services/Editing/Maintenance/LevelTemplatePruningTests` and
  `Services/State/RemoveChordTemplatesActionTests`.


- **Suggested hand shapes (#491) never write a template until accepted, and then only
  through the #478 path.** `Editing/HandShapes/HandShapeSuggester` is PURE: a run of
  uncovered sounds with at least one single note, no string at two frets, a fretted
  spread `ChordTemplateFingerSetter` can finger (≤ 3), no slide leaving it, no gap
  over 2 beats, no anchor or bar starting inside it, ONE chord shape (nested strums
  count as one), ≥ 3 sounds, and ≥ 4 strings — or ≥ 3 when a declared PLAIN template
  equals or contains the grip (`ChordShapeSuperset`). Bass gets none (charters never
  bar single notes there). `HandShapeSuggestionAcceptor` reuses that declared template
  (plain) or mints the grip through `ChordTemplateResolver`; arpeggio resolves the
  same frets with `HandShapeFromSelectionGenerator.ResolveArpeggio`, never by flagging
  a shared template. The run's chords keep their own templates. Limits tuned and
  scored with `tools/chart-eval/HandShapeEval.cs` (README there); the gate's floor is
  `HandShapeSuggesterCorpusFloorTests`.

- **The Chord shapes list's verbs are `Editing/ChordShapes/ChordShapeEdits`, each ONE
  undo node and none of them reshaping a template (#486).** `PlanMerge` repoints every
  chord and hand shape of every level onto another template through
  `SwitchTemplateAction` (notation-voice chords too), then takes the emptied source out
  with `DeleteChordShapesAction` in the same node, so a merge leaves no duplicate and no
  unused shape behind. It is REFUSED unless both have the same frets in the same
  `ArpeggioFlavour` (the resolver's own key); the target keeps its own fingers and name.
  `PlanSplit` gives the selected uses a `Clone()` under a fresh id through
  `CreateAndSwitchTemplateAction` (refused when nothing selected uses it, or the
  selection is every use). `PlanRename` is a `SetTemplateNameAction`; `PlanSetName`
  is the same action with the No name flag stated (the Shapes panel's name box and its
  No name switch), and `PlanSuggestFingers` is a `SetTemplateFingersAction` of the
  arrangement-aware suggestion, refused for a pinned template. A use is the
  template its id resolves to through `ChordTemplateResolver.IndexById`, so a twin
  sharing an earlier template's id counts nothing. `ChordShapeCatalog` is the pure row
  model (counts on the level on screen and on every level, duplicates, finger state:
  Missing = a fretted string with no finger, Suggested = exactly the suggester's).
  `ChordShapeIssues` is the panel's pure verdict on top of it: the ONE issue a used shape
  names (a copy of the same frets with more uses, else no fingering) and where a copy's
  uses go (the other copy with the most uses, then list order). Pinned by
  `Services/Editing/ChordShapes/ChordShapeIssuesTests`, `Services/Editing/ChordShapes/ChordShapeEditsTests`,
  `ChordShapeMergeDuplicatesTests` and `ChordShapeCatalogTests`.

- **Placing a whole shape is `Editing/ChordShapes/ChordShapePlacement` — ONE composite
  node, no find-or-create for a palette shape (#477).** At each position it does what a
  string toggle does there for every string at once: over nothing it adds the chord, over
  notes it promotes them (each kept string carries its note's techniques at the shape's
  fret, `SoundConversion.ToChordNote`), over a chord it reshapes that chord in place
  (`MutateChordsAction`). The chord's `TemplateId` IS the row's template — so a
  duplicate's row places the duplicate and an arpeggio row places an arpeggio, which the
  frets-only key would get wrong both ways. Refused whole, never snapped: a fret strictly
  between nut and capo (`SoundingPitch.SnapToNeck` disagrees ⇒ an `L01` fret), a fret past
  24, a string the arrangement has not got, an empty shape, or a target already on this
  template with these frets. **"Repeat previous chord"** takes the nearest chord STRICTLY
  before the pen through `HandShapes/ResolvedGripSource.GripFor` (its own template when it
  describes its frets, else the resolver in its OWN flavour), minting inside the same node.
  `IChordShapePlacementService` picks the target (selection wins, else
  `IChartPenService.PenPosition`) and keeps the recent shapes. Pinned by
  `ChordShapePlacementTests` and `ChordShapePlacementServiceTests`.

- **"Muted strums → muted chord" is `Editing/ChordShapes/MutedStrumChords` — ONE composite
  node, the chord's OWN template (#566).** Tools ▸ Chords, `Ctrl+Shift+M`, palette
  `tools.mutedStrumsToChord`; `ChartMaintenanceService.ConvertMutedStrumsToChords` scopes it to
  the selected notes, else every note of the phrase iteration under the playhead (the whole
  level when there are none). A run starts at a chord and takes, position by position, a group
  of single notes that are all in scope, all `MuteType.Full`, on strings the chord plays and
  free of per-string techniques (each group becomes ONE fret-hand-muted copy: the grip's
  strings, uniform accent/ignore/sustain, so `RepeatChords` ships it as a repeat box), and later
  chords the grip `Holds`; anything else ends it, as do `ChordHandShapePlanner`'s breaks (merge
  gap, anchor, another bar, a sustain-drawn technique on the chord). The grip is
  `ResolvedGripSource.GripFor` — so a mint happens only when the chord's own template does not
  describe it, and a run that converted nothing takes its mint back. The chord's covering bar is
  EXTENDED (never shortened), else a new bar on the grip is added; it ends by the planner's
  `CoverEveryChord` rule (the last sound's span, clipped at the next sound, bar or anchor), so it
  never runs over another bar. Pinned by `Services/Editing/ChordShapes/MutedStrumChordsTests` and
  `Services/Editing/Maintenance/MutedStrumsToChordCommandTests`.
