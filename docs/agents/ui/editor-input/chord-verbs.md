# Editor keyboard input — what a verb means on a CHORD, and the chord-template panel

Part of the editor keyboard-input contract. Router: [../editor-input.md](../editor-input.md).

## What a keyboard verb means on a CHORD

`SelectedNoteMutationCommands` takes a `SelectedSoundMutationTargets` carrying
notes AND chords. Before v0.35 it was `ObservableCollection<GuitarNote>` end to
end, so every technique/fret/string keybind was a silent no-op on a chord.
A chord is not a note, so each verb has an assigned meaning:

| Verb | On a `GuitarNote` | On a `Chord` |
|---|---|---|
| fret ±1 / ±12 / `SetFret` | its `Fret` | **per-string via the mask** |
| mute, HOPO, harmonic, bass picking, vibrato, tremolo, link-next | its own field | **per-string via the mask** |
| accent, ignore, pass-other-notes | its own field | **whole chord** — `Chord.Accent`/`Ignore`/`PassOtherNotes` |
| string up / down | clamped ±1 lane | **whole shape** — every chord note moves, or none does |

Why those three are whole-chord: `Chord.Accent`/`Ignore` are the fields the
exporters read and the only ones the inspector's "Whole chord" row exposes
(`.eof` rolls per-string accent up anyway and GP drops it), and `ChordNote` has
no `PassOtherNotes` at all. Writing the per-string variants from a keybind would
create state no visible control can show.

Why string moves are all-or-nothing: clamping per string (what the note path
does) would stack two chord notes on one lane, which the model, the renderers
and the fixed-size SNG per-string arrays cannot express.

The mask also gates **which** sounds are targeted: a standalone note is skipped
when its string is not in the mask (a note lives on exactly one string — EOF's
per-lane semantics), and a chord is a target when it plays at least one masked
string.

**Group ("set all to the first one's next value") variants** read from the first
MASKED selected note, else the first masked `ChordNote` of the first targeted
chord — notes first, so a note-only selection behaves exactly as it did.

### Technique keys follow the "Apply to" ladder (#604)

A technique key on a chord reaches what a click on the same technique in the
Inspector would. Every technique verb above (per-string and whole-chord, and
each group variant) runs through `HighwayEditingService.RunTechniqueMutation`,
which asks `IHighwayEditingService.TechniqueReach` (the inspector's
`ChordTechniqueKeyboardReach`) for the chords; the fret and string verbs never
ask.

| Rung (the pinned picker) | A technique key writes |
|---|---|
| this chord | the chord the panel shows |
| selected (N) — default | the selection: the pre-#604 command, byte for byte |
| every use of this shape | `ChordApplyScope.TechniqueTargets`: every chord of the shape on every level (+ unbound strums under a selected bar) |
| + the same grip elsewhere | every use, plus the chords of the same-fret and moved grips |

- **Same set, same marks, one node.** The chords are the inspector's
  `LadderTargets()` — the set the ladder counts and the #485 preview marks —
  written by the inspector's own builder (`ChordTechniqueEdit.BuildPerLevel`,
  one `MutateChordsAction` per level with that level's spacing pass) in ONE
  history node with the selected notes. The mask still filters per string and
  still decides which chords are targets. The status rail says how far it went.
- **Never past a ladder nobody can see.** The rung is session state, so the key
  widens only while a ladder is on screen (`ChordApplyPreview.IsHostShown`: the
  Sound tab's pinned row over Shape / Technique / Bends, or the chord panel),
  only for the user's own selection (an implied hand-shape or pen target —
  `EditTarget.IsImplied` — is never widened) and only for the selection the
  ladder was resolved on.
- Pinned by `UI/ViewModels/Panels/ChordTechniqueKeyboardApplyToTests` and the
  headless `SoundTabRailUiPerfSmokeTests.TheMuteKey_FollowsTheLadderOnScreen_AndOnlyTheLadderOnScreen`.

## Chord-template panel: ONE "Apply to" ladder decides every edit's reach (#484)

Five mechanisms used to decide how far a panel edit spread — the this/all
switch, Save fingering's propagation to same-fret templates (only under "all"),
the "Finger N alike shapes" button, `AppConfig.SyncHandShapeToChordTemplate`
and the hand-shape rebind of unbound enclosed chords. They are ONE decision now:
`ChordApplyScope` (pure; `ChordTemplateEditPlanner.ResolveReach` builds it)
answers `Resolve(rung, kind, newState)` → `ChordApplyTargets`, and the planner
writes exactly that set. The ladder (`ChordApplyLadder`, `ChordTemplateEditorViewModel.Ladder`)
prints each rung's count off the SAME scope — `ChordApplyScope.Reach` is read off
`Resolve`, so it counts the unbound strums under a selected bar too — and the
words cannot drift from the write. The ladder resolves once per refresh (and
the Save count once per draft change), never per property read. Every edit uses it: fret, finger, name, arpeggio, and the template
dropdown / containing-shape pills / New (`ChordTemplateEditKind.Switch`).

| Rung | Reaches | Write path |
|---|---|---|
| this chord | the chord the panel shows (+ its hand shape when the sync is on — the label says "+ its hand shape") | rebind onto a find-or-created template |
| selected (N) — **the default** | the narrowed selection, sync hand shapes included | rebind; in place when it IS every use |
| every use of this shape | every chord and hand shape on the template, every level, plus unbound selected sounds of that shape and the unbound strums under a selected bar (they JOIN it) | in place (`EditTemplateGlobalAction`) |
| + the same grip elsewhere | every use, plus the other templates with the same frets or the grip moved along the neck (`ChordShapeTransposition.FindTransposed`) | in place, and those templates IN PLACE (`SetTemplateFingersAction` / `SetTemplateNameAction`, no fret moves) |

- **What the grip rung carries depends on the kind:** a fingering reaches every
  grip template and OVERWRITES what is there; a name only same-fret templates; a
  shape, arpeggio or template switch stays on its own shape and the status line
  says so (`ChordTemplate_Applied_GripSkipped`). Every grip template has the
  loaded shape's arpeggio flavour, BOTH flags (`ArpeggioFlavour.Matches`).
- **Only a COMPLETE fingering travels** (`ChordApplyScope.IsFullyFingered`: a
  finger on every string fretted above the capo). The copy overwrites, so a shape
  fingered on one string of three — the finger keybind, a half-typed row — would
  erase a complete grip's other fingers; it stays on its own shape and the status
  line says why (`ChordTemplate_Applied_GripNeedsEveryFinger`).
- **The status rail counts what CHANGED:** the planner keeps only the grip
  templates whose action has an effect (`ChordApplyScope.WithGrips`), with their
  chords and hand shapes.
- **A rung with nothing to reach is disabled** with its reason in the tip, and the
  edit takes the next rung UP (`ChordApplyScope.Effective`): with nothing
  selected that is "every use" — what browse mode always edited. The chosen rung
  is session state (a DI singleton field, never persisted), like the mask.
- **One gesture is one undo node** — the mint, the anchor sync and every grip
  template ride in one `CompositeAction`. After the commit the status rail says
  what changed (`ChordApplyLadder.Describe` → `ProjectState.SetMessage`).
- **Save on "+ same grip" works with nothing typed** when the shape is fully
  fingered and a grip template is fingered differently
  (`ChordApplyScope.GripFingeringDiffers`, `CanSaveFingering`) — that is the old
  alike button's one-click verb, now a mode. "Save & next missing" saves only
  what was TYPED (`HasPendingFingerEdits`); walking on is never a copy.
- **Carry-overs from #483, decided:** with nothing selected, Save fingering
  follows the ladder like any edit (default: the shape itself; "+ same grip":
  the other grips too), and picking a template in the dropdown BROWSES to it
  (it used to do nothing). With a selection the dropdown moves the rung's sounds.

- **Technique and playback edits take the same rung (#581)** — on the chords
  only, never the template; "selected" there is the whole selection. See
  [../misc-surfaces/inspector-editing.md](../misc-surfaces/inspector-editing.md)
  ("Techniques on the ladder"). **So do a chord's bend curve and the technique
  KEYS (#604)** — see "Technique keys follow the ladder" above.

### The bulk-edit preview (#485)

While the lit rung reaches MORE than the panel's selection (companion bars
included), every chord and hand shape the next edit would change is marked:
corner brackets on the 2D timeline and a mark per sound on the song map, with a
"N changes marked · + M on other levels" row and ‹ › steppers under the ladder
(keys `SeekPrevEditTarget` / `SeekNextEditTarget`, default Alt+Left / Alt+Right).

- **One resolver.** The set is `ChordApplyScope.Preview(rung)` =
  `Members(...)` of the very target set `Reach` counts (a complete fingering, the
  edit that reaches furthest) — never a second computation.
  `ChordApplyPreviewParityTests` fingerprints every sound of every level before
  and after `ApplyEdit` and asserts preview == changed, per rung.
- **The path:** `ChordApplyPreview` (the panel's collaborator) publishes a
  `ViewModels/Editor/ChordUseHighlight` on `ChartEditorViewModel.UseHighlight`
  (`PublishUseHighlight`, change-only, raises `UseHighlightChanged`);
  `ChartRenderer` hands its sets to `SnapshotUpdateContext.EditTargetChords` /
  `EditTargetHandShapes`; the song map draws `UseHighlight.On(PlacedChords)`.
- **A second publisher, which wins while it is open:** the AI chart pass review
  (#506) publishes through `ChartEditorViewModel.PublishReviewHighlight`
  (`ChordUseHighlight.ForLevel`, with the anchors it replaces and adds);
  `UseHighlight` is the review's set while it is non-empty, else the panel's. It
  hands the marks back on apply or discard (`AiChartReviewViewModelTests`).
- **Only while a panel is on screen:** each `ChordTemplateEditorView` reports its
  `EffectiveVisibilityWatcher` state (`ChordApplyPreview.SetHostShown`). The rung
  is sticky for the session, and marks for a hidden panel would be noise.
- Pinned by `ChordApplyPreviewTests`, `EditTargetRenderDataTests`,
  `EditTargetLayerTests`, `UI/Controls/SongMapMarksTests`,
  `Perf/MinimapUseMarksUiPerfSmokeTests` and the preview case of
  `Perf/ChordTemplateEditorConstructionUiPerfSmokeTests`.

Pinned by `UI/ViewModels/Panels/ChordApplyScopeTests` (the resolver per rung),
`ChordApplyLadderTests` (the words), `ChordApplyLadderPanelTests` (through the
panel: "this chord" never touches another template; "+ same grip" is one undo
node; the carry-overs; #515), `SameGripFingeringTests`, and
`Perf/ChordTemplateEditorConstructionUiPerfSmokeTests`.

### Shape edits need a selection

The panel's write path used to compute
`selectionBound = templateIsReal && selectedChords.All(…) && selectedHandShapes.All(…)`,
and `All` on an EMPTY list is vacuously **true** — so with nothing selected it
took the GLOBAL branch and rewrote every chord bound to the browsed template,
**once per keystroke**.

A **shape** change now requires a selection (`ChordApplyScope.Resolve` answers
`Refused`). Name, fingering and arpeggio edits deliberately still work with
nothing selected — that is what browse mode is for, and the fingering triage
loop depends on it: `SelectNextMissingFingering` SEEKS to a template's first
usage without selecting anything. None of those three reshapes a chord.

The refusal records no history node, and it is NOT silent (#483): the rows go
back to the frets the shape really holds and the panel shows
`ChordTemplate_SelectToReshape` (`ChordApplyLadder.ShowsReshapeHint`).
A refused hover-typed digit also drops the fret-typing run's anchor
(`_fretRunNodeId = -1`) — it pushed nothing, so anchoring on the current node
let the next digit UNDO somebody else's work.

### The name box (#515)

The Chord name box is a `ComboBox` whose `SelectedItem` is bound to
`ChordName`. `LoadTemplate` used to set the name and THEN rebuild
`KnownChordNames` with `Clear()`, which dropped the selected item; the binding
wrote that null back, so selecting a chord showed an empty box.
`ChordTemplateNameOffers.UpdateKnownChordNames` (the panel's `Names`, which also
holds the suggested names and containing shapes) now syncs the list in place, always offers the shown
template's name, and runs before the name is set; a null written into
`ChordName` is never a rename. Only a bound view shows the fault:
`Perf/ChordTemplateEditorConstructionUiPerfSmokeTests.SelectingAChord_ShowsItsTemplatesNameInTheNameBox`.

### …and every edit is NARROWED to the shape the panel is showing

Every write path in `ChordTemplateEditPlanner` projects its targets onto the
loaded shape (`ProjectNotesOntoTemplate`), and the selection is **not
necessarily homogeneous** — `Ctrl+Shift+A` selects every chord and hand shape in
the track and the panel then displays the FIRST one's shape. So
`ApplyEdit` first narrows `SelectedChords`/`SelectedHandShapes` to the
sounds that already carry the displayed shape (`ChordsCarryingDisplayedShape` /
`HandShapesCarryingDisplayedShape`). Without it, one keystroke in the fret
column — or one Save of a fingering, which lands in the same reconcile path —
flattened every selected chord onto chord[0]'s shape.

The narrowing is also what makes **"apply one fingering to every selected
chord"** a verb: select-like a chord, press Save once, and every sound of that
shape is re-fingered in **one history node** (one `CreateAndSwitch`/
`SwitchTemplateAction` over the whole list). `FingeringTargetCount` /
`SaveFingeringLabel` put the live count of the lit rung on the Save button, because a bulk edit
with no visible count is the same mistake as an invisible mask.

Which template that fingering lands on is decided by the resolve key, and the
key depends on whether the FRETS moved (`DotCharter.Services/AGENTS.md`, the
`ChordTemplate` bullet): a shape change resolves frets-only, a fingering / name /
arpeggio change resolves on the whole authored state
(`FindIdenticalTemplate`). Reusing the frets-only key for a fingering edit is
what silently threw the fingering away and made the owner press Save twice. The
rebind path ("this chord" / "selected" on a template someone else uses) goes through
the same resolve, so re-fingering N siblings one at a time **converges on one
minted template** instead of leaving N duplicates. A RESHAPE takes the
destination template's name when one already has the new frets, and the
`ChordNameSuggester` name when it mints — never `<old> (Isolated)`, which showed
on the highway and in the export (#483). A fingering or rename keeps the name
it carries.

**"This chord" needs a THIS CHORD.** With nothing selected the isolated
branch once bound the new state to an EMPTY list — it minted an ORPHAN template
and left the browsed one untouched, so "Save & next missing" (which seeks
WITHOUT selecting) kept returning to it. With no selection the two selection
rungs are unavailable and the edit takes "every use", which is what browse mode
means.


## Two-fret octave and power chord fingering (Tools ▸ Chords, #498, #511)

`ToggleOctaveFingering` → `ChartMaintenanceService.ToggleTwoFretOctaveFingering`, and
`TogglePowerChordFingering` → `ChartMaintenanceService.TogglePowerChordFingering`, both over
`Services/Editing/Fixing/TwoFretPairFingering`. Each switches every template of its pair in the
ACTIVE arrangement between `1·4` and `1·3`: the first press makes a mixed arrangement all the
fingering the suggester does NOT give (`1·3` for an octave, `1·4` for a power chord), and the
next press goes back. One undo node of `SetTemplateFingersAction`s.

- **A pair is found by its SOUNDING interval** (`SoundingPitch.MidiFor`: tuning offsets and
  capo included), two fretted strings with the upper one two frets higher: an octave has one
  string skipped and twelve semitones, a power chord adjacent strings and seven. In drop D the
  low pair two frets apart is a sixth, not a power chord.
- **Never touched, because the owner's verified charts never vary them:** three-fret octaves
  (across the G–B third, `1·4` in all 1,455 chords), the G–B power chord (three frets, `1·4` in
  258 of 260) and root·fifth·octave (`1·3·4` in all 2,940).
- **A deliberate other fingering** (a thumb, `2·4`) is not a target.
- **Why a command and not a rule:** the choice is per SONG. Two-fret octaves: 20 of 21 songs
  never mix, 13 play `1·3`, 8 `1·4`. Two-string power chords: `1·3` in 33 songs, `1·4` in 9.
  The automatic fingering follows the arrangement's own majority
  (`Services/Editing/Fingering/ArrangementFingeringStyle`, `core/guitar-and-tuning.md`); the
  command flips a whole song at once.
- **It edits templates IN PLACE,** for the same reason as the "+ same grip" rung: no fret moves,
  and the index finger stays on the root, so anchors and E04 are untouched.
- **Whole arrangement only.** A selection scope would have to split shared templates; that is
  not built. `Services/Editing/Fixing/TwoFretPairFingeringTests`.
