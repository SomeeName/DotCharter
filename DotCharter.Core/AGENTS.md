# DotCharter.Core Agent Guide

The pure domain-model / primitives layer — the in-memory model of a chart
project plus the timeline/fretboard math and clone/validation helpers over it.
**No UI, no I/O, no serialization.**

**Bottom of the dependency graph:** zero NuGet packages, zero `DotCharter.*`
references. Everything else depends on Core, never the reverse. `net10.0`,
nullable, `AllowUnsafeBlocks` (reserved for perf math; none currently).

**This guide is a ROUTER, not a reference.** It auto-loads for every agent that
opens a file in this project, so it carries only what everyone needs: the folder
map, the invariants that bite from anywhere, and the ONE extra file to open for
the area you are touching. Every rule, measured number and hard-won invariant
that used to sit inline is in [docs/agents/core/](../docs/agents/core/), moved
VERBATIM — nothing was summarised away. **Read the routed file before you touch
that area**, and update it in the same change when you change its subsystem.
Every bare `*.md` name below is a file in that one folder; they are plain links
rather than `@import`s on purpose — an import would re-load the bytes and defeat
the point.

## Layout + routing — one row per area

| Folder | What lives there | Read this first |
|---|---|---|
| `Domain/Primitives/` | `Fraction`, `FractionalPosition` — the `readonly struct` value-type foundation for musical timing | `timeline-and-project.md` |
| `Domain/Project/` | the aggregate: `ChartProject` (root), `ChartData`, `ChartLevel`, `Arrangement`, `ArrangementProperties`, `PhraseInfo`, `PhraseIteration`, `Stem`, `ToneDefinition`, `NotationVoice` | `domain-model.md` |
| `Domain/Guitar/` | `GuitarNote`, `Chord`, `ChordNote`, `ChordTemplate`, `HandShape`, `BendValue`, `IGuitarSound`, `GuitarEnums`, plus the notation-only hint fields (`NotationDuration`, `GraceFret`, `GraceSlide`) | `domain-model.md` · `guitar-and-tuning.md` |
| `Domain/Events/` | timeline markers: `BeatMarker`, `EventPoint` (phrases/sections + `Status`), `ToneChange`, `Showlight`, `Bookmark`, `ChartAnnotation`, `FretAnchor`, `FretHandMute` | `domain-model.md` |
| `Domain/Vocals/` | `VocalTrack` → `LyricPhrase` → `LyricEvent` | `domain-model.md` |
| `Domain/Validation/` | result types ONLY: `ArrangementIssue`, `ArrangementIssueType` (~60 codes), `ArrangementIssueSeverity`. The validation *service* is in `DotCharter.Services` | `guitar-and-tuning.md` § validation codes |
| `Utilities/` — timing + graph | `TimelineMath`, `ITimedElement`, `ChartProjectCloner` + `ChartProjectRestorer`, `ChartProjectPositions`, `BulkObservableCollection<T>`, `ChartPositionLabel`, `BarCount` | `timeline-and-project.md` |
| `Utilities/` — guitar + tuning | `FretboardMath`, `TuningUtilities`, `StandardOpenStrings` (the ONE open-string table, extra strings extend DOWN, up to 9 strings; a package spells 8 so a 9-string is refused on export), `StringColorSlots` (string → colour slot by identity: low E red on any guitar, extra low strings grey), `TuningNameResolver` + `ExtendedRangeTunings` (standard/drop names for 5/6-string bass and 7/8/9-string guitar, which rscharter's six-string `TuningType` table cannot name; written to a .rscp as CUSTOM + offsets and re-derived on load), `ArrangementInstrument` (bass PART vs path, the A220 octave), `TuningOffsetResolution`, `ChordNameSuggester` + `ChordNamingOptions` + `ChordKeyEstimator` (naming style and key), `ChordTemplateFingerSetter` (+ `LearnedChordFingerings`), `ShowlightCatalog`, `ChartDifficultyEstimator`, `ArrangementPropertiesDeriver`, `PhraseMarkers`, `RsSectionCatalog`, `RsEventCatalog` | `guitar-and-tuning.md` |
| `Linking/` | `MeasureContentIndex` (THE measure-content comparison, detection + integrity keys), `MeasureContentKey`, `MeasureContentOptions`, `SpacingGap` (the enforcer's gap, one implementation), `MeasureGrid`, `LinkCandidateDetector` + `LinkCompatibility` (repeated-run detection), `LinkRuns` (placements on the grid, keys, splits), `LinkedRunCopier` (one copy onto another placement), `LinkGroupVerifier` (the load check); the model is `Domain/Project/LinkGroup` | `domain-model.md` § `Linking/` |
| `Diff/` | `ChartDiffEngine.Diff(old, new)` + `ChartDiffResult` — the pure beat-aligned structural diff, plus `DiffStructure` for everything that is NOT a sound | `domain-model.md` § `Diff/` |

**Task routing** (the same answer, keyed by what you are doing):

| Doing this | Read this |
|---|---|
| adding a field or a type to the model | `domain-model.md` + the `Clone`/`CopyFrom` and restorer rules in `timeline-and-project.md` |
| adding a collection that carries POSITIONS | `timeline-and-project.md` (four traversals must learn about it, and one test enforces it) |
| anything that converts ms ↔ position | `timeline-and-project.md` (`TimelineMath` only — never a local copy) |
| anything that reads or derives a FRET | `guitar-and-tuning.md` (fold the capo in; be a strict no-op at capo 0) |
| naming a chord, or comparing two shapes | `guitar-and-tuning.md` |
| writing a tuning, or an `ArrangementProperties` flag | `guitar-and-tuning.md` |
| adding a validation code | `guitar-and-tuning.md` § validation codes (they mirror external tools) |

## Hard-rule index — one line each, full text one file away

These are the rules that have cost this project a shipped bug, and several are
cited from `DotCharter.Services` and `DotCharter.UI` doc-comments. If your change
is near one, open the file it points at (all under `docs/agents/core/`).

- **Primitives are immutable, always-canonical value types** — `Fraction` reduces
  via GCD with a positive denominator, `FractionalPosition` normalizes so
  `Fraction ∈ [0,1)`. Equal positions therefore have identical
  `(BeatId, Fraction)`, safe as dictionary keys.
  → `timeline-and-project.md`
- **`Fraction`'s constructor takes `long`s and EVERY operator widens before it
  multiplies — do not "simplify" a cast away.** An unchecked `int` product wrapped
  to 0 and threw `DivideByZeroException` out of a fraction with no zero in it,
  from one `.rscp` attribute that arrives off disk AND over the unauthenticated
  LAN transfer.
  → `timeline-and-project.md`
- **Timing is grid-relative, not absolute** — a `FractionalPosition` is meaningless
  without the `Beats` timeline; convert only through `TimelineMath`. Don't store ms
  on domain notes. `TimelineMath.PositionFromMs` is a true inverse of
  `CalculateTimeMs` INCLUDING outside the grid; `TryBeatOffsetFromMs` is its
  un-rounded half and is public so no caller re-derives the arithmetic. A file
  that stores ms imports through `PositionFromImportedMs` and exports through
  `ToWholeMs` (rounded, never an `(int)` cast), so a whole ms round-trips exactly.
  → `timeline-and-project.md`
- **`GetVisibleRange` requires an ascending-by-time list and does NOT check it** —
  an out-of-order element vanishes from rendering AND hit-testing with no error.
  → `timeline-and-project.md`
- **Domain objects are mutable reference types with `ObservableCollection<T>`
  props, and each has a `Clone()` + `CopyFrom(other)` pair** — follow that pattern
  for new types.
  → `timeline-and-project.md`
- **`ChartProjectCloner` is the single source of truth for snapshotting**
  (undo/redo + torn-save protection), and since 0.35 it is a THIN WRAPPER over
  `ChartProjectRestorer` — "added there" means added to the restorer. Never
  re-inline field copying into the cloner.
  → `timeline-and-project.md`
- **`Utilities/ChartProjectPositions` is the one exhaustive walk over a project's
  ABSOLUTE positions, and a new position-bearing element class MUST be added
  there** (`Services/Editing/Tempo/BeatScaleService` is the one sanctioned
  hand-written copy, held to the same inventory). Relative spans and ms-keyed
  markers are deliberately excluded. A scale is not a shift — it must also
  re-derive sustains by ms.
  → `timeline-and-project.md`
- **`ChartProjectRestorer.Restore(live, snapshot)` reconciles a snapshot INTO an
  existing graph** rather than swapping a clone in — that is what keeps undo
  history attached across a whole-project tool.
  → `timeline-and-project.md`
- **A BAR NUMBER comes from `Utilities/BarCount` and nowhere else.** The bar is
  the COUNT of `IsMeasure` beats at or before the time, floored at 1 so a pickup
  reads as the bar it leads into. Four surfaces print one for the same instant —
  the timeline's measure pill, the status rail's `M{bar}.{beat}`, the Issues-tab
  location cell (`ChartPositionLabel`) and a recorded run's bar range
  (`LiveBarNumbers`) — and until 0.60 there were two answers: the last two started
  at bar 1 and incremented only after beat 0, which agrees only when beat 0 is
  itself a marker. On a PICKUP grid the rail printed `M2.1` where the timeline drew
  the pill `1`. `BarCount` is FOLDABLE precisely so the rail's incremental scanner
  and the from-scratch walks cannot drift apart. Pinned by
  `UI/BarNumberAgreementTests`.
  → `timeline-and-project.md`
- **Two parallel note stores — don't confuse them**: legacy flat
  `ChartData.SingleNotes`/`Chords` vs per-difficulty `ChartLevel.*`.
  → `timeline-and-project.md`
- **Frets are ABSOLUTE on the un-capoed neck; fret `0` = open, which a capo stops
  at the capo fret — so a sound's sounding offset is `max(fret, capo)`, and every
  fret consumer must fold `Arrangement.Capo` in and be a strict no-op at capo 0.**
  A non-zero fret below the capo is malformed (inspector `L01`). EOF/`.eof` AND
  Guitar Pro store frets RELATIVE and convert at both ends; RS XML/SNG/rscp are
  absolute like us. A tool that DERIVES a fret must refuse one below the capo
  rather than write it.
  → `guitar-and-tuning.md`
- **No exporter reads `Arrangement.Properties` directly — it resolves through
  `ArrangementPropertiesDeriver.Derive` (0.35).** Every flag sits in exactly one
  provenance group (authored elsewhere / derived from content / undecidable) and a
  test fails if a new flag joins none; `ManualOverride` pins the derived group only.
  → `guitar-and-tuning.md`
- **PHRASES LIVE IN TWO STORES AND BOTH CAN BE LIVE AT ONCE — always read them
  through `Utilities/PhraseMarkers`.** Reading one is a silent whole-feature
  failure: over the 89 hand-finished exports in `TestData/EOF`, `EventPoint.Phrase`
  yields **0** markers and `PhraseIterations` yields **2,596**. It has bitten three
  separate pieces of code. `PhraseMarkers.All` gives the markers;
  `PhraseMarkers.Iterations` gives phrase list + iterations (explicit ones kept, editor
  markers merged in, names minted) and is what the RS-XML writer, the SNG mapper, the
  Phrases inspector and propagation read, so XML and SNG ship the same iterations. Both
  stores stay: nothing normalises one into the other on load, which keeps imported
  charts round-tripping byte-identically.
  → `guitar-and-tuning.md`
- **A chart states its tuning on the ARRANGEMENT and there is NO project-level
  copy — do not add one back (0.44.2).** `ChartData.Tuning` was one, it was a lie
  in every path that filled it (the psarc back-fill guard could never fire against
  its six-name default; the `.rscp` header parser stamped standard names), and
  populating it would still be wrong because a project holds several arrangements
  at once. Nothing persisted it, so removing it was not a format change. Ask
  `Arrangement.Tuning.Length` — `DotCharter.UI/Input/StringDigitTargeting.StringCountOf`
  where a null-safe answer is needed.
  → `guitar-and-tuning.md`
- **The arrangement states it twice in turn, and everything that WRITES one goes
  through `Utilities/TuningOffsetResolution`** — authored offsets always win; the
  letters-only fallback covers 4- and 6-string instruments ONLY, because a
  5-string bass offset has no agreed base.
  → `guitar-and-tuning.md`
- **A tuning NAME the table does not know is an UNKNOWN, not E standard** — ask
  `TuningUtilities.TryGetOffsetsForTuningType`; `GetOffsetsForTuningType` still
  answers "standard" and is now only for the two callers that deliberately want
  a default. The table spells tunings Rocksmith's way (**Drop C# is
  `E_FLAT_DROP_D_FLAT`**), and `"CUSTOM"` is not in it at all.
  → `guitar-and-tuning.md`
- **`ChordTemplate.Id`/`TemplateId` default to `-1` = untemplated/custom**, and
  **`ChordTemplate` has VALUE equality (excluding `Id`) — so no collection call
  resolved by `EqualityComparer<T>.Default` may stand in for identity.**
  `Services/State/ChordTemplateCollectionOps` is the identity answer; do not
  weaken the equality to "fix" it.
  → `guitar-and-tuning.md`
- **A `HandShape` span is HALF-OPEN and every containment test goes through
  `HandShape.Covers`** (start inclusive, end EXCLUSIVE) — shapes ABUT, so a closed
  reading counts the next shape's opening sound as content of this one.
  → `guitar-and-tuning.md`
- **`ChordNameSuggester` names at SOUNDING pitch, capo included**, and **a
  TWO-STRING shape gets no automatic chord name** (`DoubleStopNameOptions`,
  enforced at the EXPORT boundary too, and power chords are suppressed with it).
  → `guitar-and-tuning.md`
- **`ChordTemplateFingerSetter` is a PURE FUNCTION OF THE FRET SHAPE — keep it
  that way.** 81.0% exact over the owner's finished charts (`tools/chart-eval`,
  43,517 chords) against a ceiling of 92.4% for ANY function of the shape; the
  rest is per-charter habit. Don't chase past it, and don't make it
  context-dependent (that collides with "never mutate a shared template in
  place"). Seven shapes take a fingering LEARNED from the corpus first
  (`LearnedChordFingerings`, generated `LearnedChordFingeringData.cs` —
  regenerate with `tools/chart-eval --learn`, never hand-edit). The song's 1·3 /
  1·4 for a power chord or two-fret octave arrives as an INPUT
  (`PairFingering? twoFretPair`, #511), decided per arrangement in Services —
  never read inside Core.
  → `guitar-and-tuning.md`
- **A GHOST gem lives in a hand shape's grip (`ChordTemplate.GhostMask`), never in
  a note, and `ChordTemplate.IsPinned` is the ONE "may an automatic flow rewrite
  this grip?" answer** (#785) — a new automatic fingering/rebuild flow must ask it
  (`TemplatePinReachabilityTests`).
  → `guitar-and-tuning.md`
- **The two Rocksmith marker vocabularies are CLOSED** (`RsSectionCatalog` +
  `RsEventCatalog`, ported verbatim from EOF): sections compare case-INsensitively,
  events ORDINALLY, and a code outside the catalogue is preserved, never dropped.
  → `guitar-and-tuning.md`
- **Validation codes mirror external tools** (`I##` DLC Builder/Rocksmith2014.NET,
  `L##` Log Charter, `E##` Editor-on-Fire, `D##` DotCharter-native). Several string
  formats are "compatible with rscharter" — treat them as an external contract.
  → `guitar-and-tuning.md`

## Keeping this current

**When you change a subsystem in a way that would make its guide misleading — a
new/removed type or folder, a changed invariant, a new gotcha, a moved entry
point — update the routed `docs/agents/core/*.md` file in the same change**, and
add a row here only if a whole FOLDER appeared or moved. Terse, accurate bullets
with real file/type names beat prose. This file is auto-loaded for every agent in
the project: anything that is not routing or a cross-cutting invariant belongs in
the topic file, not here.
