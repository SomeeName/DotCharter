# Positions, cloning and the exhaustive traversals

Deep reference for `DotCharter.Core/AGENTS.md`. The invariants over TIME and over
the project GRAPH: the value-type primitives, the position↔ms conversions, the
one snapshot source of truth, and the three walks that must each know about every
position-bearing collection. The guitar-side invariants (frets, capo, tuning,
chord naming) are in [guitar-and-tuning.md](guitar-and-tuning.md).


- **Primitives are immutable, always-canonical value types.** `Fraction` reduces
  via GCD with a positive denominator; `FractionalPosition` normalizes so
  `Fraction ∈ [0,1)` and whole/negative beats carry into `BeatId`. Equal
  positions therefore have identical `(BeatId, Fraction)` — safe as dictionary
  keys / for direct comparison. (No `double` ctor — see the Live Play gotcha.)
- **`Fraction`'s constructor takes `long`s and EVERY operator widens before it
  multiplies — do not "simplify" a cast away.** The range guard at the end of the
  ctor reduces first and refuses anything that will not fit an `int`, but it can
  only see what it is handed: while `operator+` computed
  `a.Denominator * b.Denominator` in unchecked `int`, the wrap had already
  happened one step earlier and the guard never fired. `536870912 * 8` is 2^32,
  which wraps to **0**, so adding `1/536870912` to `1/8` threw
  `DivideByZeroException` out of a fraction with no zero in it — and a product
  that wrapped to a merely-wrong value produced a silently wrong POSITION with no
  error at all. One `.rscp` attribute reaches both (`<vocal p="0 1/536870912"/>`
  with no `ep`, where the parser adds a hard-coded `1/8`), and that file arrives
  from disk **and** over the unauthenticated LAN session transfer. Widening is
  always safe here: every product of two ints fits a `long`, and `operator+`'s
  sum of two of them is bounded by `2 * (2^31-1)^2 < long.MaxValue`.
- **A `Try*` on a primitive NEVER throws — that is the whole contract, and both
  `Fraction.TryParse` and `FractionalPosition.TryParse` broke it.** The range
  guard above lives in the CONSTRUCTOR, so a string that parses as two `int`s
  but does not fit once REDUCED — `"1/-2147483648"` reduces to
  `-1/2147483648`, `"-2147483648/-1"` to `2147483648/1` — raised
  `ArgumentOutOfRangeException` straight out of a `bool`-returning method.
  `Fraction.TryCreate(long, long, out Fraction)` is the non-throwing
  counterpart (`false` for a zero denominator or an out-of-range reduced value)
  and `TryParse` goes through it; the constructor and `Parse` still throw, and
  must — this narrows nothing about the range invariant. **Both parses are
  reachable from a `.rscp` `p=`/`ep=` attribute** (`RscpParsingUtilities.ParseFraction`,
  `RscpArrangementParser`, `RscpVocalsParser`), which arrives off disk AND over
  the unauthenticated LAN session transfer, where one attribute aborted the
  whole load. Pinned by `FractionTests`, `FractionalPositionTests` and the
  end-to-end `RscpHostilePositionTests`. **If you add another `Try*` here, route
  it through a non-throwing factory — never a bare constructor call.**
- **Timing is grid-relative, not absolute.** A `FractionalPosition` is meaningless
  without the `Beats` timeline; convert only via `TimelineMath` /
  `GetPositionMs(beats)`. Don't store ms on domain notes — derive it.
  `BeatMarker.Time` (ms) is the one absolute anchor.
  **`TimelineMath.PositionFromMs` is a true inverse of `CalculateTimeMs`,
  including OUTSIDE the grid**: before the first beat it extrapolates backwards
  off the first interval and past the last beat off the LAST interval, both
  exactly as `CalculateTimeMs` does, bounded to ±1e6 beats. It used to clamp the
  fraction into `[0, 0.999]`, so every pre-roll time collapsed onto beat 0 and
  everything past the grid stacked on one position — reachable from any ms-keyed
  import (MIDI, EOF) whose audio outruns the authored beats.
  A one-beat grid uses `BeatMarker.Bpm`, not a hardcoded 120.
  **`TryBeatOffsetFromMs` is that method's un-rounded half, and it is public so
  a caller snapping to its OWN resolution does not re-derive the arithmetic.**
  It answers "which beat anchors this time, and how many beats past it does it
  lie" with no rounding and no clamp; `PositionFromMs` is `Round(offset * 192)`
  over it. `AudioToChartAnalyzer` (grid denominator 12) is the caller it was
  extracted for — its private copy clamped the fraction and collapsed the whole
  post-grid tail of every auto-generated chart onto one position, at maximum
  confidence, which is the same defect `PositionFromMs` itself was fixed for.
- **A file that stores MILLISECONDS imports through
  `TimelineMath.PositionFromImportedMs` and exports through
  `TimelineMath.ToWholeMs` — the pair round-trips every whole ms exactly
  (#325).** `ToWholeMs` rounds to the nearest ms, a half to even (what the SNG exporter always wrote); every
  ms-keyed writer uses it (`RsXmlSongWriter`, `RsXmlVocalsWriter`,
  `RsXmlShowlightsWriter`, `SngFromDomainMapper.MsInt`). A bare `(int)` cast
  truncates and biased every RS-XML time up to 1 ms early. `PositionFromImportedMs`
  keeps the 1/192 grid point when it writes back as the source's ms (all
  1/2…1/64 and triplet content, DotCharter's own exports included) and only
  otherwise tries 1/1920, then 1/19200 — each divides the next, so sums of
  imported positions never outgrow 1/19200. The readers that use it:
  `RsXmlSongReader`, `RsXmlVocalsReader`, `RsXmlShowlightsReader`,
  the package reader's SNG mapper, `EofProGuitarTrackParser`, `EofImporter`. Plain
  `PositionFromMs` snapped those off-grid notes by up to 1/384 beat (2.6 ms at
  60 BPM); it stays the call for editor gestures and retiming, which have no
  authored ms to keep. Exact for any beat shorter than 19.2 s (above 3.2 BPM).
  Pinned by `TimelineMathTests` (every ms sweep) and
  `RsXmlOffGridTimingRoundtripTests` (read → write through the real XML).
- **`GetVisibleRange` requires an ascending-by-time list and does NOT check it**
  (it is on the per-frame path). An out-of-order element is simply not found, so
  it vanishes from rendering AND hit-testing with no error. Anything that
  reorders a chart collection must re-sort it. The range covers exactly ONE
  element before the window plus `bufferMs` — size the buffer for the longest
  sustain you must draw.
- **"the next / previous sound from here" is `TimelineMath.FirstAfter` /
  `LastBefore` (0.60), under that same ascending precondition** — never
  `list.Where(…).OrderBy(x => x.Position).FirstOrDefault()`, which is an
  O(n log n) sort of an already-sorted list plus an n-element buffer. The editor's
  four seek/select verbs each spelled it that way, per arrow-key press, on the UI
  thread: 2.19 ms and 184 B on an 18,000-note chart against 0.002 ms and 0 B.
  **`LastBefore` walks back over the equal-time run and that is BEHAVIOUR** —
  `OrderByDescending` is stable, so among sounds sharing the greatest passing
  position it returned the FIRST in list order (a chord's low string, not its
  high one). `TimelineScanEquivalenceTests` sweeps both against the LINQ shapes at
  every element's own time and states each tie-break separately.
- **"the sound NEAREST here" is `TimelineMath.NearestTo` / `NearestIndexTo`
  (0.61), same precondition** — the sheet strip's click-to-select
  (`ChartEditorViewModel.SelectNearestToTime`) ran two full passes with a
  `GetPositionMs` per element over lists the spacing enforcer keeps sorted.
  Sortedness is what makes |t(x) − target| unimodal and the minimum therefore a
  BOUNDARY: the last element at or before the target, or the first one after it.
  It carries BOTH of the linear scan's tie-breaks, because a strict `<` over a
  forward pass has two — the EARLIER of two equidistant sounds wins, and among
  sounds sharing a time the first in LIST order does (a chord's low string).
  Dropping the walk-back fails 5 of the 13 cases in
  `TimelineScanEquivalenceTests`, which sweeps it against the scan verbatim,
  midpoints included.
  **Its sweep stops at ±1e9 ms while the seek sweep runs to ±double.MaxValue/2,
  and that asymmetry is measured, not timid:** at 1e308 every |t(x) − target|
  rounds to the same double, so "nearest" stops being a property of the data and
  the two shapes tie-break an arbitrary answer differently. `FirstAfter` /
  `LastBefore` are unaffected — they compare TIMES, not differences of them.
- **Domain objects are mutable reference types** with `ObservableCollection<T>`
  props (built for MVVM binding). Each has a `Clone()` + `CopyFrom(other)` pair —
  follow that pattern for new types.
  **Both halves are MECHANICAL — `DotCharter.Tests/Core/DomainCloneCoverageTests`
  DISCOVERS every Core class with such a pair by reflection** (no list to keep),
  builds an instance whose every public settable property differs from its
  default, round-trips it through `Clone()` and through `CopyFrom` onto both an
  empty and an already-populated destination, and compares property-by-property.
  A field missing from a copy path fails the day it compiles. A property that
  genuinely must not be copied goes in that test's `Exclusions` array with a
  REASON, and the test asserts the property still EXISTS so a rename breaks the
  exclusion instead of silently widening it. (Audited at 0.46: all seven
  `CopyFrom`s and all ten `Clone`s in Core were already complete — the guard is
  there so the next field is too.) The value generator is
  `Tests/Infrastructure/DomainFieldProbe`; the comparison is
  `Tests/Infrastructure/DeepCompare`. Reuse those rather than writing a third.
- **`ChartProjectCloner` is the single source of truth for snapshotting**
  (undo/redo + torn-save protection). Any new project-level collection MUST be
  added there or it silently vanishes from history. Same for new fields → the
  type's `CopyFrom`/`Clone`.
  **Since 0.35 the cloner is a THIN WRAPPER over `ChartProjectRestorer`** —
  a clone is "restore into a brand-new empty object" — so "added there" means
  added to the restorer. Do NOT re-inline field copying into the cloner: undo's
  in-place snapshot restore needs the identical field knowledge, and a second
  hand-written copier would silently stop carrying newly added model fields
  (the same failure mode as an `.rscp` field with a reader but no writer).
  **That obligation is MECHANICAL since 0.44 —
  `ChartProjectRestorerCoverageTests` walks the whole graph by reflection**,
  gives every public settable property a value distinguishable from its default
  (recursing into nested objects, seeding every collection with two elements),
  and reports every property PATH that did not survive a `Clone` or a `Restore`
  onto an already-populated graph. A new field on any domain type is covered the
  day it compiles, and a third test asserts the clone shares no mutable
  reference with its source. `ChartProjectClonerTests` stays: it pins the ~15
  fields the 2026-07 audit found missing, by name, so their history is
  readable. **The walk THROWS on a property type it has no value for** rather
  than skipping it — a silent skip is exactly how this guard would stop
  guarding.
  (Audited at 0.44: the restorer was already complete — every settable property
  of every type in the graph survives both paths. The guard is there so the
  next field does too.)
- **`Utilities/ChartProjectPositions` is the one exhaustive walk over a
  project's ABSOLUTE positions, and a new position-bearing element class MUST
  be added there.** The tempo-map tools (Insert Beat / Remove Beat / Add Beats
  At Start) hand-wrote that traversal TWICE in
  `ChartMaintenanceService.TempoMap`, and both copies omitted
  `ChartLevel.FretHandMutes` and `Arrangement.NotationVoices` — so every fret-hand
  mute and every imported GP voice 2-4 silently drifted a beat away from the
  notes it belongs to, with no error anywhere. Same obligation as the restorer
  and `ChartDiffEngine.DiffStructure`; those answer different questions and stay
  separate types, so what is shared is the rule, not the code.
  `ChartProjectPositionsTests.EveryPositionBearingCollection_IsAccountedFor`
  makes it executable — a new collection whose element carries a settable
  `Position` fails the gate until it is listed. Link placements
  (`ChartProject.LinkGroups[].Placements`) are walked too: both barlines move,
  so a beat inserted INSIDE a run widens only that run (and the history then
  unlinks it).
  **`Services/Editing/Tempo/BeatScaleService` is the one sanctioned hand-written
  copy of that walk, and it is held to the same inventory by
  `TheBeatScaleTraversal_MentionsEveryPositionBearingCollection`.** It cannot
  route through `Remap`: that helper takes a pure `Func<>` with nowhere to record
  the per-element undo snapshots a scale action needs. It had skipped
  `Arrangement.NotationVoices` and `Arrangement.PhraseIterations` — and
  `PhraseIterations` is where every phrase marker on an imported chart lives
  (0 vs 2,596 over `TestData/EOF`), so Double Time moved all of them. **A scale
  is not a shift**: it also has to re-derive SUSTAINS by ms, because a beat is
  worth a different number of milliseconds afterwards. That is the one place
  `ChartProjectPositions`' "relative spans are excluded" rule does not carry
  over.
  Two exclusions are deliberate: **relative** spans (`GuitarNote.Sustain`,
  `ChordNote.Sustain` — durations from their own onset; adding the delta again
  would stretch every sustain by the shift, and `GuitarNote.EndPosition` is
  derived from them, which is why it has no setter) and **ms-keyed** markers
  (`Bookmark.Time`, `ChartAnnotation.TimeMs` — whether they move is a per-tool
  decision about the AUDIO, so it stays with the caller). Remove Beat's
  `RemoveContentAtBeat` is a separate traversal on purpose (a remove is not a
  remap — it deliberately drops lyrics but not their phrases) and carries the
  same inventory obligation.
- **`ChartProjectRestorer.Restore(live, snapshot)` reconciles a snapshot INTO an
  existing graph** instead of swapping a clone in: index-wise `CopyFrom` onto the
  live instances, then add/remove only at the tail. That is what keeps undo
  history attached across a whole-project tool — `AddNotesAction` captures the
  `ObservableCollection`, `MutateNotesAction` captures the individual
  `GuitarNote` INSTANCES, so preserving the containers alone is not enough.
  Only the tail is freshly minted, so an action recorded after a GROWING tool
  can still detach if that tool is undone and redone. `RestoreArrangement` bumps
  the target's revision (`Arrangement.MarkEdited`/`GetRevision()`, a content stamp, never copied or saved —
  #720), and `Restore(live, snapshot, isCurrent)` leaves alone an arrangement the
  caller KNOWS already matches (only the quality report's spare copy does).
- **Two parallel note stores** — don't confuse: legacy flat
  `ChartData.SingleNotes`/`Chords` vs. per-difficulty
  `Arrangement → ChartLevel.Notes/Chords/Anchors/HandShapes/FretHandMutes`.
  `ChartData.EditMode`/`ChartFormatVersion` gate legacy behavior.
