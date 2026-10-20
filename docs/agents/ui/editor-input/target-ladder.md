# Editor keyboard input — the target ladder

Part of the editor keyboard-input contract. Router: [../editor-input.md](../editor-input.md).

## The target ladder — what a keyboard verb acts on

**`DotCharter.Services/Editing/Targets/`.** One resolution, in front of every
`IHighwayEditingService` verb and the pen, in the order **selection → hand
shape → pen**:

| Rung | Claims when | Target |
|---|---|---|
| **Selection** | any note or chord is selected | exactly those sounds |
| **Hand shape** | no sound selected but a hand shape is | the sounds that shape SPANS (`HandShape.Covers`, end EXCLUSIVE) |
| **Pen** | nothing selected | the sound at the pen: the chord there, else every note there |
| **None** | — | nothing; the verb is a strict no-op and records **no history node** |

Before stage 3 the **pen resolved its own** implied target while every other
keyboard verb read `SelectedNotes`/`SelectedChords` and nothing else — so the
same keystroke reached a different sound depending on which verb it was. That
is what this removes. It is rscharter's `GuitarModeHandler.toggleString`
(`:337-355`) with EOF's implied selection (`menu/note.c:11065-11112`) as the
bottom rung. **What each verb MEANS is unchanged** — the chord-semantics table
below is untouched; only *what it acts on* moved.

- **A rung claims the resolution as soon as its own selector is non-empty, even
  when it yields no sounds.** A selected hand shape over empty bars resolves to
  `HandShape` with nothing to edit rather than falling through to whatever sits
  under the playhead — rscharter's `HAND_SHAPE` branch returns unconditionally
  for the same reason, and it is the only reading under which the user's own
  selection cannot be silently overruled. `EditTarget.IsNone` (nothing
  resolved) and `HasNoSounds` (resolved, no sounds) are therefore different
  questions.
- **`EditTargetResolver` is PURE** — no services, no clock, no
  `ObservableCollection`; every input is a parameter, the same split as
  `ChartPen`/`GlobalChordGate`. `EditTargetService` is the thin wrapper that
  supplies the ambient state.
- **An implied target is PROMOTED to a real selection** (`ResolveAndPromote`)
  so the user sees what the keystroke rewrote — EOF's implied selection made
  drawable, not invisible modal state. The **pen** rung replaces the selection
  (nothing was selected, that is what put it on that rung); the **hand-shape**
  rung ADDS the sounds and keeps the bar selected, which also makes promotion
  idempotent — the next resolve takes the Selection rung and returns the same
  set.
- **The two implied rungs are gated on `AppConfig.UseFastChartingInput`**, read
  per call. With the setting off, resolution is byte-for-byte the pre-v0.35
  "act on the selection, else do nothing" — the whole of stages 0–3 sits behind
  that one switch, as the audit's §8 asks.
- **The selection is SNAPSHOTTED** on the way out. The actions capture the
  instances and the spacing enforcer running in their post-execute can mutate
  the live selection collections mid-flight.
- **The pen rung's position** is `ChartPenService.PenPosition` — the playhead
  snapped exactly the way the POINTER snaps (grid, with a nearer existing sound
  winning, `SoundMagnetSnap`). Verbs that have no grid divisor of their own get
  it through `SetPenPositionProvider`, installed by the composition root — the
  same idiom as `IEditingScope.SetResolver`. The pen itself is handed a divisor
  per keystroke and passes its position straight in (`ResolveAndPromoteAt`)
  rather than round-tripping through a global that has to have been wired.
- **`ChartPen.ToggleString` (bare `1..9`) deliberately does NOT go through the
  ladder.** It is note ENTRY — it always acts at the pen, because "press a
  string key to create a sound at the playhead" is the verb the editor did not
  have. (rscharter diverts its `toggleString` to a multi-sound selection; here
  that would silently turn note entry into a bulk edit. The pen selects what it
  places, so the two coincide anyway.)
- **Two more rows on the seek family (0.36, stage 5): `QueuePrevMatch` /
  `QueueNextMatch` on `Ctrl+PgUp`/`Ctrl+PgDn`.** The inspector work queue's
  `‹‹`/`››` — the same motion as `SeekPrev`/`SeekNext` but only over the sounds
  matching the queue's filter (missing fingering, unnamed chord, blocks export,
  …). The bare and `Shift` rows of those keys are already sound-by-sound seek
  and its extend variant, so the `Ctrl` row reads as "the same motion, over what
  needs work". They **select** as well as seek, so the panel's fields are live
  and the per-string verbs have a target. Both are NEW rows, so **no
  `NormalizeKeybinds` migration**.
- **Stage 5: `IEditTargetService.Resolve()` is the seam for the inspector's own
  target.** `ChordTemplateEditorViewModel` re-derives "selected chords, else
  selected hand shapes, else the shape enclosing the first selected note" in
  four copy-pasted blocks (`:95-101`, `:337-343`, `:391-397`, `:451-457`);
  that question is now answered once, and `EditTarget.Source` says which facet
  to show. A read-only consumer must use `Resolve()` — the promoting overloads
  write to the selection and belong to verbs.

