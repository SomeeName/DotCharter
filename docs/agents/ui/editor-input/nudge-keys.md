# Editor keyboard input — the nudge keys, and where a gesture ENDS

Part of the editor keyboard-input contract. Router: [../editor-input.md](../editor-input.md).

## Where a gesture deterministically ENDS

**`HistoryService.MergeWindow` (900 ms) is the FALLBACK, not the mechanism.**
Its own comment says erring long is the cheap direction *because* "gestures that
know their own end call `EndGesture()` and never depend on this at all" — and
until 0.58 **nothing outside `HistoryService` called it**. `Clear`/`Undo`/`Redo`
call it internally; there was no production call site at all, so every gesture in
the editor was left to the timer.

- **A pointer PRESS and a pointer RELEASE both close the open gesture**
  (`Input/Tools/ToolCoordinator` — the single funnel for all four timeline
  tools). Only-on-press would leave a drag's own commit open for the next wheel
  run; only-on-release would leave a keyboard run open across the click that
  starts a drag.
- The regression it closes: wheel a hand shape longer, **click it**, wheel again
  inside the window ⇒ same collection, same instances, same verb, and nothing in
  between pushed a node to move the merge anchor, so both runs are ONE undo node.
  `UI/Input/PointerGestureBoundaryTests` asserts the two-node outcome AND carries
  its own inverse (`WithoutTheBoundaryTheTwoRunsCollapseIntoOne`) so the
  assertion cannot go vacuous; the call site itself is a source lint, because
  `ToolCoordinator` takes a `MainWindowViewModel`.
- **The other boundaries are already covered by cheaper means — check before
  adding a call.** `_mergeAnchor != _current` blocks anything that pushed a node
  in between; `CanAbsorb` compares targets by REFERENCE, so a changed selection,
  a different sound under the pen or a different anchor cannot merge; and every
  history navigation clears the anchor. What is left is exactly "two deliberate
  gestures over the same targets with no node in between", which is what a
  pointer press marks.

## Undo granularity of the nudge keys

**Every repeating keyboard verb names its gesture, and the octave shift is ONE
command.** Until 0.41 only the sustain nudge and the FHP anchor nudge did — so
two nudge verbs over the same concept behaved differently, and the fret/string
keys spent one of the 500 history nodes per auto-repeat.

| Verb | `MergeVerbs` | Notes |
|---|---|---|
| fret ±1 **and** ±12 | `Fret` | one verb: both are "I moved the fret", the way `Sustain` covers a wheel tick and a held key |
| string ±1 | `String` | **separate from `Fret`** — rule (a) |
| lyric wheel resize | `LyricLength` | `ResizeLyricAction` was not `IMergeableAction` at all |
| pen typed fret (`SetFret`, `TransposeShapeTo`) | *(none)* | it owns its own run granularity — see the pen section |

- **`Shift+Alt+↑`/`↓` (octave) is `IHighwayEditingService.MoveSelectedFretByOctave`,
  not a loop.** `AppKeybindRegistrar` used to spell it
  `for (i < 12) MoveSelectedFretUp()`: one keystroke on a barre chord cost twelve
  history nodes, twelve `ChartSpacingEnforcer` passes and **up to eleven
  intermediate `ChordTemplate`s** — nothing garbage-collects templates, so those
  stayed in the arrangement forever. The one-pass form is behaviour-identical
  because the per-string clamp is idempotent: twelve `min(24, f+1)` steps and one
  `clamp(f+12, 0, 24)` agree for every `f`. Pinned per starting fret by
  `FretStringGestureMergeTests.TheOctaveShiftLandsWhereTwelveSingleStepsWould`.
- **A held fret run over a chord that MINTS a template still records one node per
  repeat, and that is correct.** The minted `AddChordTemplatesAction` goes first
  inside the composite (the template-trap rule above), and `CompositeAction` only
  reports a verb when every child carries the same one — so the composite is
  unmergeable. It has to be: a merged head would hold the FIRST tick's minted
  template while its stored new-states point at the LAST tick's, so redo would
  bind a chord to a template that was never added. That is exactly the
  "half-merged composite describes a state that never existed" failure `CanAbsorb`
  and `Absorb` are split to prevent. Notes and UNTEMPLATED chords coalesce
  normally.
- **The verb is opt-in per CALL SITE**, so making an action mergeable is only half
  the change — `ResizeLyricGestureMergeTests.TheWheelCallSitePassesTheMergeVerb`
  is a source lint over `TimelineWheelAdjuster` for that reason (it takes a
  `MainWindowViewModel`, so it has no behavioural test at all).

## FHP nudge keys

`Alt`+`+`/`-` nudges the selected anchor's FRET, `Shift`+`Alt`+`+`/`-` its hand
SPAN; `Shift+F` still opens the dialog. Added because the dialog was the only
route to an existing anchor — 16 modal round trips in one 32-minute session, 12
of them a ±1 fret change, two of them consecutive on the same anchor a second
apart.

- **Fret and width are SEPARATE merge verbs** (`MergeVerbs.AnchorFret` /
  `AnchorWidth`). Both are a `MutateAnchorsAction` over the same anchor, so one
  shared verb would let a width tweak fold into the preceding fret run and make
  one Ctrl+Z revert both. Within a verb a run coalesces, so holding the key to
  walk an anchor down four frets stays one undo step.
- **The capo is read at PRESS time** from the arrangement being edited
  (`AppKeybindRegistrar.NudgeAnchor`), never captured at registration: the fret
  floor is `capo + 1`, and switching arrangements must not leave the nudge
  clamping against the previous one's capo.
- An anchor already at a clamp contributes nothing, so a mixed selection still
  moves its movable members and records exactly one node rather than an empty one.

