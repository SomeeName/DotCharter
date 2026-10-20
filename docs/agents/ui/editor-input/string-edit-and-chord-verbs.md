# Editor keyboard input (UI router)

**Read this before adding a keybind, changing a default chord, or touching
`SelectedNoteMutationCommands` / the string-edit mask / the pen / the target
ladder.** Landed in v0.35 as Stages 0–3 of the 2026-07-25 editing rework; this
file is the contract.

**This file is a ROUTER.** What stays here are the two rules that bite whatever
keyboard work you came for — the ONE string-edit mask, and the chord-template
repoint that a save silently discards if you skip it. Everything else is one
file per row of the table at the bottom, under [`editor-input/`](../editor-input/).

## The string-edit mask

`ProjectState.StringEditMask` (`DotCharter.Services/Editing/StringEditMask.cs`,
stored on `EditorUiState`) is the persistent "which strings am I editing"
selector — EOF's `eof_pro_guitar_fret_bitmask` and rscharter's
`getEditedStrings()`, promoted out of the chord inspector's per-string
checkboxes into editor state.

- **EMPTY MEANS EVERY STRING.** Both reference tools do it, the inspector's
  `ApplyChordNoteMutation` already did it, and it is the only reading under
  which the mask is a strict no-op until the user sets one. `Includes()` says
  yes to everything while empty; `IsExplicit()` does not — that split is what
  lets the lane headers show "no mask set" instead of "all nine lit".
- **It is an immutable value type.** It sits in shared state and is read on the
  render path; toggling produces a new value.
- **Not persisted.** Like the grid divisor it is a working mode. A mask silently
  restored at launch would make the first keyboard edit of a session act on a
  subset for no visible reason.
- **Bound to `Ctrl+1..9`** (`StringMaskBase`, digit derived by a loop in
  `AppKeybindRegistrar` exactly like `BookmarkBase`/`SetFretBase`) and
  **`Ctrl+0` clears it** (`StringMaskClear`). Toggling the last string off
  returns to "all".
- **`MaxStrings` is 9 because there are nine digit KEYS — it is not a claim
  about the arrangement.** Every digit-addressed string verb is bounded by
  `DotCharter.UI/Input/StringDigitTargeting` (the ACTIVE arrangement's
  `Tuning.Length`, which is what the renderer, the hit tester and the lane
  headers all lay out from — *not* `Arrangement.StringCount`, an independent
  field that can disagree). Three keyboard families used to be bounded by
  something else and each produced state nothing on screen can show: `Ctrl+7`
  on a 4-string bass set a mask bit with no lane header to draw it on, which
  silently made every per-string verb a no-op; a bare `6` created a
  `GuitarNote` on string index 5 (`ApplyNotePlacementCommand` never consults
  the string count, and the POINTER cannot do this —
  `ChartHitTester.GetLaneIndexAtY` is bounded by the track's string count);
  and `StringUp`'s ceiling came from `ChartEditor.CurrentChart`, the
  PROJECT-level `ChartData`, which carried a tuning vector of its own — so in a
  guitar+bass project editing the bass clamped to string 5. Pinned by
  `UI/Input/StringDigitTargetingTests` (pure cases 4/5/6/7-string plus a source
  lint that all three call sites route through it).
  **…and the bound has to be re-applied when the ARRANGEMENT changes, not only
  at the keypress (0.50.1).** `StringDigitTargeting` refused a `Ctrl+6` on a
  4-string bass, but a mask already set on the 6-string lead SURVIVED the switch
  with bit 5 still lit — `LaneLayer` draws four headers, so nothing on screen
  showed it, and every per-string verb silently no-opped because no bass sound
  plays a masked string. `ProjectState.ActiveArrangement`'s setter now narrows
  through `StringEditMask.NarrowTo(tuning.Length)`; narrowing the last bit away
  lands on `Empty`, i.e. "all strings", the same landing the last `Ctrl+n` has.
  An arrangement with no tuning narrows NOTHING — dropping a bit needs positive
  knowledge that the lane is gone. `Services/State/StringEditMaskArrangementScopeTests`.
  **That project-level vector is GONE as of 0.44.2** — it was wrong in every
  path that filled it and could not have been right for two arrangements at once
  — so `StringCountOf` is now the shared answer for `ChartRenderer`,
  `MinimapOverviewControl`, `CommandCatalog` and `InspectorViewModel` too, and
  `UI/Input/ProjectTuningSingleSourceTests` lints that no production file goes
  looking for a second source. Full reasoning:
  [../core/guitar-and-tuning.md](../../core/guitar-and-tuning.md).
- **It is DRAWN** — `Controls/Layers/LaneLayer` puts a pip on each masked lane
  header and dims the guide line + tuning label of the rest, and
  `ChartRenderer.OnProjectStateChanged` invalidates on
  `nameof(ProjectState.StringEditMask)` because `Ctrl+1..9` is keyboard-only and
  nothing else would produce a frame. **Never make the mask invisible modal
  state** — that is rscharter's `typingNumber` mistake, the one thing the audit
  says outright not to copy.
- **Since 0.36 it is also the INSPECTOR's only per-string selector**, drawn on
  the chord box's own rail (a pip column left of the nut; `Ctrl`+click or a pip
  click toggles) and quoted in words on every scope bar. The inspector used to
  own a second selector, `ChordStringTargets`, with the OPPOSITE default — so a
  mask set here did nothing to a technique chip. Deleted; see
  `docs/agents/ui/misc-surfaces/inspector-editing.md`. **Do not add a third.**

## Splitting a hand shape (`Ctrl+Shift+K`)

`HandShapeSplitter` cuts the bar covering each selected sound AT that sound —
the head keeps the original template, each tail piece gets one built from what
actually sounds inside it (`IFingeringGenerationService.BuildTemplateForRange`,
deduped through `FindEquivalentTemplate` exactly like
`HandShapeFromSelectionGenerator`). Owner request: a bar drawn across a fret
change declares the wrong fret for everything after it (inspector E05), and
fixing it by hand meant delete-and-redraw-two.

**Do NOT compute the gap between the halves here.** The head ends exactly ON the
split position and the tail starts there; `ChartSpacingEnforcer` runs on every
committed highway edit and opens the same minimum distance every other shape
gets. A second, local answer to that question is how two of them come apart.

A position at a shape's own start or end is a NO-OP, not an error — either would
leave a zero-width piece and no visible change, and a verb that silently pushes
an empty undo node is worse than one that does nothing. `HandShapeSplitterTests`
pins both edges plus the tiling invariant.

## The chord-template trap — any verb that moves a fret or a string

An `.rscp` `<chordNote>` carries **no `fret` attribute**; the reader takes the
fret from the chord's template (`02-format-capability-matrix.md` §6.5). So a
gesture that changes a chord note's fret **without repointing `TemplateId` is
discarded on the next save**. And a `ChordTemplate` is SHARED, so mutating one
in place rewrites every chord and hand shape with that shape (the v0.33.2
corruption).

Every shape-changing verb therefore:

1. builds old/new `Chord` clones,
2. resolves-or-creates through **`ChordTemplateResolver`** — never a tenth
   find-or-create — and repoints the CLONE's `TemplateId`,
3. emits `AddChordTemplatesAction` **first** inside the composite, because
   `MutateChordsAction`'s post-execute is the spacing enforcer and that resolves
   template ids.

An untemplated chord (`TemplateId -1`) stays untemplated: it has nothing to
disagree with. This is the same shape `EditChordStringCommand.BuildReshapeAction`
and `CommitHighwayDragDropCommand` already use — follow it, don't invent a
fourth.

Frets stay **ABSOLUTE**. **They are NOT capo-agnostic, and this file said they
were until 0.50.1.** Fret `0` is the open (capo'd) string, but a NON-ZERO fret
below the capo is malformed — inspector rule `L01` (`NoteFretBelowCapo`), which
`ArrangementAutoFixService.FixCapoFrets` silently raises on the next auto-fix.
A plain `Math.Clamp(fret, 0, 24)` manufactured exactly that finding: on a capo-5
chart a typed `3`, a `FretDown` off the capo and a `FretDownOctave` each wrote a
fret the neck has not got. Every fret write goes through
`SoundingPitch.SnapToNeck`, whose legal set is `{0} ∪ [capo, 24]`; a DESCENT off
the back of the capo lands open (fret 0) so a fret-down run stays monotone,
while an ascent or an absolute write lands on the capo — the same answer
`FixCapoFrets` gives. **Capo `0` is a strict no-op**, so no un-capo'd chart
changed. `Services/Editing/Highway/CapoFretVerbTests`.

**A minted template must be undoable.** `HandShapeInteractionTool` used to
`arr.ChordTemplates.Add(...)` directly, so a right-click hand-shape create was
half-undoable — the shape came back on Ctrl+Z and the template did not, forever
(nothing garbage-collects templates). Resolve without adding and hand the
created list to `IHighwayEditingService.AddHandShape` /
`CommitHandShapeDragAndDrop`'s `mintedTemplates`, which puts it in the same
history node. Guarded by a source lint over `DotCharter.UI/Input/`
(`HandShapeTemplateMintTests`).

