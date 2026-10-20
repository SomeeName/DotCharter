# `Editing/` — the chart-model invariants (deep reference) — `ChartSpacingEnforcer`

## `ChartSpacingEnforcer` (runs on every save AND every committed edit)

- **Because it runs on the whole active level after EVERY committed edit, on the
  UI thread, anything it does per SOUND is done ~20,000 times per keystroke on a
  big chart.** `GetMinDistance` was one of those, and in the two FRACTION modes
  its answer depends only on which MEASURE the sound is in — a backwards walk to
  the nearest downbeat plus two GCD-reducing constructions, for one value that is
  the same for the whole level of a 4/4 chart. It goes through the one-slot
  `MinDistanceMemo` now (keyed on the beat; `Milliseconds` mode keys on the whole
  position, because it genuinely varies inside a beat), and the entry fill is
  indexed rather than a `foreach` over `IList`-typed `ObservableCollection`s.
  Measured on a 20,000-sound level: **80,346 B of post-pass allocation per
  committed edit → 266 B**. `ChartSpacingEnforcerTests
  .EnforceLevel_UsesEachMeasuresOwnMinDistance_WhenTheMeterChanges` is the guard,
  and it changes METER on purpose — a cache that assumed one answer per level is
  invisible on the 4/4 grid every other test in that class builds. **It is still
  an `Array.Sort` of every sound plus a full backward sweep per edit**; the
  gesture-cost table and what else was left is in
  [../ui/rendering-perf/editor-latency.md](../../ui/rendering-perf/editor-latency.md).
- **On SAVE it runs on the save WORKER over the cloned SNAPSHOT, not on the UI
  thread over the live project** (`ProjectSaver.SaveProjectCoreAsync`;
  pinned by `ProjectSessionSaveThreadingTests`). The file is enforced; the
  in-memory chart is not touched by a save — it is enforced on load, on the
  settings change and after every committed edit instead. Do not move it back
  above the clone: on a big chart that was hundreds of ms of Ctrl+S stall.
- **`ChartSpacingEnforcer` runs on every save AND every committed highway edit —
  so whatever it changes, it must leave the chart self-consistent.** Two
  invariants it now upholds (0.34.1, both were owner-reported gaps):
  (1) **`EnforceSoundSpacing` shortens sustains, so it MUST re-clamp bends.** It
  zeroes/shortens a sound's `Sustain` for the min-note-distance gap; a bend point
  authored near the old (long) end then lay PAST the new end and rendered on the
  next sound. It now calls the single Core invariant
  `BendGeometry.ClampBendsToSustain` for every sound unconditionally — the SAME
  helper GP import (`GpPostProcessor.ClampBendsToSustain`) uses, so the two paths
  can't diverge. Never shorten a sustain anywhere without folding bends in.
  (2) **`EnforceHandShapeSpacing` leaves the min-distance gap before the next
  SOUND, not only the next hand shape.** A shape "held until the next sound"
  (`ChordHandShapeBuilder`/`GpHandShapeBuilder` park it ON that sound's onset)
  otherwise ignored the min-note-distance setting the note-sustain pass honours.
  Zero-width clamp leftovers are left alone (can't shorten below their start).
  (3) **What it changes on a NEIGHBOUR must be undoable.** Every
  `CollectionActionBase` runs the enforcer in its post-execute after `Execute`
  AND after `Undo`, and the pass only ever SHORTENS — so placing a note next to
  a 4-beat sustain truncated that neighbour (a sound the action does not hold)
  and undoing re-ran a pass that cannot lengthen anything back. The chart eroded
  one gesture at a time. The enforcer's write sites now report to the ambient,
  `using`-scoped `SpacingCollateralRecorder` (`Editing/Collisions/
  SpacingCollateral.cs`) — sustain, bend curve, hand-shape end — and
  `CollectionActionBase.ExecutePostPass`/`UndoPostPass` capture and replay it.
  **A new write site in `EnforceSoundSpacing`/`EnforceHandShapeSpacing` must
  capture before it writes**, or that field silently stops being undoable. The
  action's OWN items are excluded from the record (`Build(owned)`): a mutate
  target is captured holding the NEW state, so replaying it after `copyFn(old)`
  would undo the undo. Restore runs LAST in `Undo`, after the pass — the values
  being restored are the pre-edit ones, which the enforcer had already accepted.
  (4) **A `CompositeAction` runs the pass ONCE per gesture, and never on undo
  (2026-09-02, `State/DeferredPostPassScope`).** Every child used to run its
  own pass inside its own Execute AND Undo, and the composite just looped them:
  a drag-drop (up to six effective children — `CommitHighwayDragDropCommand`)
  paid the whole-level pass six times and Ctrl+Z six more, a plain Delete
  three or four — ~280 ms for one drop on a 20k-sound level against the 150 ms
  budget, re-deriving an answer the level already met. While a composite's
  scope is open (thread-static, nesting-aware: an inner composite defers into
  the outer one), `ExecutePostPass` hands its delegate + `owned` set to the
  scope instead of running; the composite runs each DISTINCT delegate once
  (delegate equality — `HighwayEditingService.GetLinkNextUpdater` is one
  cached instance per service now, so the six copies fold) under ONE recorder
  whose exclusion set is the union of the children's, and HOLDS that
  collateral (its children hold none; `Absorb` merges it like a child's).
  **On undo no pass runs at all**: the inverses + that one record restore the
  exact pre-edit chart, and the per-child flow's undo-time pass could only
  RE-SHORTEN that chart — whatever it took that the execute-time record had
  not happened to capture stayed short (`HandShapeTailTrimTests
  .OneUndoTakesTheBarAndTheTrimsBackTogether` went red under a
  pass-then-restore undo for exactly that reason: the trimmed tail is a mutate
  child with no pass of its own, and the restored 2-beat tail was capped to
  1 15/16). A STANDALONE action is untouched by all of this — it still runs its
  own pass in Execute and Undo. Pinned by `SpacingPostPassCoalescingTests`
  (once on execute/redo, zero on undo, distinct passes, nested, the real
  enforcer's collateral through the composite path, the restored-tail shape, a
  throwing child leaves no scope open).

### It is on the LOAD path too, and the win there is ALLOCATION

Opening a `.rscp` is `LoadChart` then `EnforceProject`, both inside one
`Task.Run` (`ProjectSessionService.LoadFile`), so the pass is on the wait for
every project open as well as on every save and every committed edit. On the
real 762 KB `TestData/RsCharter/Yorushika - Odorouze/project.rscp` (4
arrangements, 812 beats, 4,487 notes+chords), Debug, the parse is ~54 ms and
the whole enforce ~20 ms — **not a rounding error beside the XML.**

**But the 0.49 changes below did NOT make it measurably faster, and saying so is
the point.** Paired A/B on the same box, `EnforceProject` over a freshly cloned
project per iteration (it only ever SHORTENS, so re-enforcing one project
measures the already-clamped short circuit rather than the work), 21 reps:

| project | sounds | time before → after | **allocated before → after** |
|---|---|---|---|
| Odorouze | 4,487 | 23.3 → 17.6 ms (min) | **3,281,816 → 1,487,520 B (−55%)** |
| Robbery and Bouquet | 2,305 | 9.8 → 11.6 ms (min) | **1,672,704 → 780,440 B (−53%)** |
| Octagram Dance | 1,402 | 5.0 → 5.0 ms (min) | **1,001,952 → 440,384 B (−56%)** |

The time column disagrees with itself between projects and between runs (an
earlier pair read 20.7 → 21.3 ms on Odorouze) — it is contention, not signal.
The allocation column is **exact and reproducible**: five batches per arm
returned the identical byte count every time, because it is counting objects,
not racing a clock. Read the right column; quote neither as a speed-up.

That matters because of where the pass runs: on the UI thread after every
committed edit, where GC churn is a standing owner complaint ("GC kicks in way
too often while editing", v0.32.5). **These are not the same measurement as the
`80,346 B → 266 B` figure above** — that one is the post-pass on a 20,000-sound
level after a single edit; this one is a whole-project `EnforceProject`. Do not
compare them.

Four more output-identical changes in 0.49 — stacked ON TOP of the
`MinDistanceMemo` and indexed entry fill above, which came out of the
editor-latency work and already cover the per-sound `GetMinDistance`. **There is
one min-distance cache and it is `MinDistanceMemo`**; a second one keyed on the
measure length was written for this pass and dropped, because the memo's
`BeatId` compare is cheaper per lookup than the backwards walk to the downbeat
that a measure-length key needs, and `EnforceLevel_UsesEachMeasuresOwnMinDistance_WhenTheMeterChanges`
already pins the behaviour both were after. Verified by hashing the
enforce-twice-then-save output of **all 35 real projects in
`TestData/RsCharter/`** before and after: **byte-identical.** (An A/B against
the previous build, not a committed golden — the `.rscp` writer's format changes
too often for a checked-in hash to mean anything.)

- **`ApplyDerivedIgnore` takes the sound, not four accessor delegates.** The old
  form allocated a closure display-class plus FOUR delegates *per note and per
  chord* — ~22,000 allocations for a body that is two assignments.
  `GuitarNote` and `Chord` share no interface carrying
  `Ignore`/`IgnoreDerived`, so it is two overloads with the same body; **keep
  them identical.**
- **`DeriveIgnoreFlags` resolves templates through an id map built once per
  level**, not a `FirstOrDefault` per chord (which was O(chords × templates)
  *and* allocated a closure, a delegate and a boxed enumerator each time). The
  map is `TryAdd`, i.e. FIRST wins, because that is what `FirstOrDefault`
  answers when ids repeat — pinned by
  `ChartSpacingEnforcerTests.DerivedIgnore_ResolvesTheFIRSTTemplateCarryingTheId`.
  It is safe to build there only because that pass never ADDS a template;
  `RemoveMutedNotesFromPartiallyMutedChords`, which does, keeps its own linear
  lookup (now allocation-free).
- **The sweep skips a sound with no sustain before computing anything.**
  `maxSustain` is `Max(Zero, …)`, so a zero sustain can never exceed it — the
  two `FractionalPosition` subtractions and the `GetMinDistance` call were dead
  work for the majority of sounds on most charts. Every `FractionalPosition`
  operator runs several GCD loops, so this is the arithmetic win, and it is
  identical by construction. Pinned by
  `SoundSpacing_LeavesAZeroSustainAloneAndStillClampsARealOne`.
- **`RemoveMutedNotesFromPartiallyMutedChords` COUNTS the fully-muted notes
  before materializing them.** Almost no chord has one, and the
  `Where(...).ToList()` allocated an iterator, a `List` and a boxed enumerator
  for every chord only for the next line to throw the list away.
- Plus one deletion: the SECOND `SortByPosition(level.Anchors, …)` was a
  provable no-op — `RemoveRedundantAnchors` only ever REMOVES, which cannot
  un-sort what the line above sorted — and `SortByPosition`'s already-sorted
  check now carries the previous position forward instead of re-invoking the
  position delegate twice per element.

Still on the table, unmeasured (from a full read of the file): `FixLinkNexts` (now `LinkNextRepair.cs`)
builds a `PositionedSound` per sound and sorts per string even when the level
has no `LinkNext` at all, and its "next sound on this string" is a linear scan
over an already-sorted list (O(linkNexts × n/strings)); `MoveAnchorsNearSounds`
re-materializes the sound list with `OrderBy(...).ToList()` after building it
with an exact capacity, and `BuildSlideEndTimeSet` allocates a `Where` iterator
per chord; `PhraseMarkers.All(arrangement)` is recomputed per LEVEL though it
depends only on the arrangement (and sorts a result that goes straight into a
`HashSet`); `RemoveDuplicateBendValues` is five allocating LINQ operators over a
list of ≤ ~8 points; and `Array.Sort` at the top of the sweep is sorting two
already-sorted runs that could be MERGED — that last one is the only item whose
output-equivalence rests on an argument about tie ordering rather than on
inspection, so it needs the suite as its gate.

