# The domain model — every type and what it is for

Deep reference for `DotCharter.Core/AGENTS.md`. The in-memory model of a chart
project, folder by folder, including the fields that exist only as
backward-tolerant `.rscp` extension nodes and the ones that are import-transient.
The RULES over these types are in
[timeline-and-project.md](timeline-and-project.md) (positions, cloning,
traversal) and [guitar-and-tuning.md](guitar-and-tuning.md) (frets, capo,
tuning, naming).

## Layout

- `Domain/Primitives/` — `Fraction`, `FractionalPosition` (`readonly struct`s;
  the value-type foundation for musical timing).
- `Domain/Project/` — the aggregate: `ChartProject` (root), `ChartData`,
  `ChartLevel` (one difficulty, and ALWAYS the whole song at it — the hardest
  level is "the chart", `Utilities/ChartLevels.Hardest`; an imported Rocksmith
  ladder is filled to that shape, see [parsing-psarc.md](../services/parsing-psarc.md)),
  `Arrangement` (+ `ArrangementType/Subtype`, `TuningOffset`),
  `ArrangementProperties`, `PhraseInfo`, `PhraseIteration`, `Stem`,
  `ToneDefinition` (project-owned Rocksmith tone bodies — key/name + the raw
  manifest tone JSON as an opaque string; arrangements reference them by name
  via their tone slots/changes), `NotationVoice`
  (`Arrangement.NotationVoices`: NOTATION-ONLY sidecar voices from multi-voice
  score imports — GP voices 2–4. The playable chart in `Levels` stays
  single-voice; only the sheet view renders these. Per-arrangement because the
  authored score has no difficulty dimension. Serialized as the
  backward-tolerant root-level `dcNotationVoices` rscp extension node — absent
  in every pre-voice file).
  **`ChartProject.ProjectId` is the project's stable identity** (#562,
  `ProjectIdentity`: 32 lowercase hex digits, minted by the constructor, saved
  as `<dcProjectId>`, copied by `ChartProjectRestorer`). It is not chart
  content; the AI chat history keys conversations by it
  ([ai-chat-history.md](../services/ai-chat-history.md)).
  **`ChartProject.UnknownRscpNodes` is NOT part of the model** in the usual
  sense: it is the `.rscp` forward-compatibility residue — top-level project
  nodes a NEWER build wrote, kept as opaque XML fragments so the next save puts
  them back instead of deleting them
  ([`docs/RSCP-FORMAT-GUARANTEES.md`](../../RSCP-FORMAT-GUARANTEES.md)). The
  `.rscp` reader is its only producer, the `.rscp` writer its only consumer;
  nothing else may read or interpret it. It still has to ride
  `ChartProjectRestorer`, or the first undo-able edit after opening such a file
  deletes what the reader kept.
- `Domain/Guitar/` — `GuitarNote`, `Chord`, `ChordNote`, `ChordTemplate`,
  `HandShape`, `BendValue`, `IGuitarSound`, `GuitarEnums`
  (`MuteType`/`HopoType`/`HarmonicType`/`BassPickingType`).
  `GuitarNote.NotationDuration` / `Chord.NotationDuration` (`Fraction?`,
  quarter-note units) are OPTIONAL authored-rhythm hints from score importers
  (Guitar Pro) consumed only by the sheet view — null everywhere else, never
  gameplay-relevant, serialized as the backward-tolerant rscp attribute `nd`.
  `GuitarNote.GraceFret` (`int?`) is the same kind of hint for a BEFORE-BEAT
  grace note (fret, same string; rscp attribute `gf`) — notation ornament only,
  gameplay ignores it. **`ChordNote.GraceFret` (`int?`, 0.33.2) is the same
  field per STRING of a strum** (GP graces a chord with one grace note per
  string; same `gf` attribute, now on `chordNote`) — without it every plain
  chord grace was dropped at import. Fret `0` is a real (open-string) hint, not
  "absent". `GuitarNote.GraceSlide` (`bool`) is an IMPORT-TRANSIENT
  companion: a "sl." grace that slides into the note. `GpPostProcessor.
  MaterializeGraceSlides` consumes it during import (turns the hint into a real
  sliding note and clears both fields), so it is never persisted true — it is
  NOT serialized.
  `GuitarNote.LeftHandFinger` (`int`, -1 = none, 0 = thumb; RS XML `leftHand`),
  `GuitarNote.PickDirection` and `Chord.Strum` (`PickDirectionType`, `None` =
  not stated; RS XML `pickDirection` 0/1 and `strum` down/up) are written only
  when set, so a chart that states none exports what it always did. The SNG
  writes the finger into `RsNote.LeftHand` and the directions into
  `PickDirection` (note: `None` stays 0; chord: `None` stays -1); the SNG
  importer reads only the finger back, because a note's default byte 0 is also
  "down". rscp attributes `leftHand`, `pickDirection`, `strum`. Chord notes take
  their finger from the chord template.
- `Domain/Events/` — timeline markers: `BeatMarker` (the beat grid), `EventPoint`
  (phrases/sections), `ToneChange`, `Showlight`, `Bookmark`, `ChartAnnotation`,
  `FretAnchor`, `FretHandMute`. `EventPoint.Status` (`SectionStatus`, v0.29
  section tracker) is a DotCharter-only rscp extension: additive `ss`
  attribute, lowercase NAME, absent when `None` (untracked charts stay
  byte-identical), tolerant parse both ways (`RscpParsingUtilities.
  ParseSectionStatus` — unknown/garbage/numeric reads `None`, old apps ignore
  the attribute; pinned by `RscpSectionStatusTests`).
- `Domain/Vocals/` — `VocalTrack` → `LyricPhrase` → `LyricEvent`.
- `Domain/Validation/` — result types only: `ArrangementIssue`,
  `ArrangementIssueType` (~60 codes), `ArrangementIssueSeverity`. (The validation
  *service* lives in `DotCharter.Services`.)
- `Utilities/` — `TimelineMath` (position↔time: `CalculateTimeMs`,
  `PositionFromMs`, `BinarySearchBeats`, GC-free `GetVisibleRange<T>`),
  `ITimedElement`, `ChartProjectCloner` + `ChartProjectRestorer`,
  `BulkObservableCollection<T>` (`ResetWith` rebuilds `Items` in full BEFORE
  notifying and raises ONE `Reset` — never restore the old clear→Remove→refill
  ordering, which emptied the collection permanently if a handler threw and left
  it observably empty between the two events),
  `FretboardMath`, `TuningUtilities` (`GetTuningNames`' reference table is
  per string count — 7-string starts on B, 8-string on F#, matching the standards
  the GP importers measure their `TuningOffsets` against; 5-string stays on the
  6-string table because GP and the RS SNG path disagree on what a 5-string bass
  offset means), `StandardOpenStrings` (THE open-string table every
  string-index→pitch consumer reads — tuner, gutter, live judging, refret, chord
  tools, property deriver, `SoundingPitch`, `GetStandardMidiPitches`; extra
  strings extend DOWN, 7 = B1 …, 8 = F#1 …; a private copy fails
  `Infrastructure/OpenStringTableLintTests`, #742), `TuningNameResolver` (pure: concrete tuning
  → canonical name "E Standard"/"Drop C"/"DADGAD" from offsets OR note letters;
  unknown ⇒ null; display counterpart to `TuningUtilities`),
  `TuningOffsetResolution` (the ONE answer to "what are this arrangement's
  offsets" — see the invariant below),
  `ChartPositionLabel` (pure: chart ms + beat map → the readout a charter reads
  a POSITION from, `M19.3 · 02:13.16`. The bar/beat counting is deliberately
  `StatusBarViewModel`'s — two readings of one beat map would put two bar
  numbers on screen for the same instant — but a beat map with NO measure marker
  gets no `M…` token at all, only the time, the same call `LiveBarNumbers.BarAt`
  makes. Display counterpart to `TimelineMath`; the FEHLER inspector's location
  cell reaches it through `Converters/ChartPositionLabelConverter`),
  `ChordNameSuggester` (a tuning entry that is not a note name holds its SLOT —
  the open-pitch list is indexed BY STRING INDEX, so skipping one shifted every
  higher string down and dropped the top string, naming the shape confidently
  wrong), `ChordTemplateFingerSetter`, `ShowlightCatalog`,
  `ChartDifficultyEstimator` (pure, deterministic 1–10 rating over the hardest
  level: density / chord-change rate / technique mix / tempo),
  `ArrangementPropertiesDeriver` (the one owner of what an export ships in
  `ArrangementProperties` — see the invariant below),
  `ChartProjectPositions` (the ONE exhaustive walk over every absolute
  `FractionalPosition` a project stores — see the invariant below).
- `Diff/` — `ChartDiffEngine.Diff(old, new)` + `ChartDiffResult`: pure
  beat-aligned structural diff of two arrangements (added/removed/changed
  sounds keyed by (position, string) with a position tolerance; levels paired
  by `DifficultyIndex`). Drives the Tools → Compare With Project File viewer
  (`ChartDiffViewModel`) and is the intended source of the LAN
  "has update" check (`IsEmpty`).
  **A second pass, `DiffStructure`, covers everything that is NOT a sound** —
  anchors, hand shapes, fret-hand mutes, section/phrase/event markers, tone
  changes, phrase iterations and chord templates — feeding the same
  added/removed/changed counts. So `IsEmpty` means "identical chart";
  `SoundsIdentical` is the narrow "no note changed". A new element class on
  `ChartLevel`/`Arrangement` MUST be added there or a chart edited only in it
  diffs as unchanged and peers are told there is no update.
  **Sections/phrases/events are diffed as three independent MARKER streams, not
  by pairing `EventPoint` objects** — an `EventPoint` is a container whose
  grouping is reader-specific (EOF stores each marker as its own text event, so
  a lossless round trip legitimately returns one authored point as two).
  Chord templates have no position and are compared as a multiset via
  `ChordTemplate.Equals`.
- `Linking/` — the linked-measures foundation (#76). **`MeasureContentIndex` is
  THE measure-content comparison — detection, link verification, the load check
  and the stored hash all go through it; never write a second one.** Content is
  in beats, exact (relative `FractionalPosition`s, no rounding); time signatures
  and beat counts are part of the key. Two roles (`MeasureContentKind`):
  **Detection** compares what is played and ignores exactly what
  `ChartSpacingEnforcer` changes — a sustain or hand-shape end within the
  spacing gap of the next onset (or the run's end) reads "held", anchors compare
  as the position in effect at each onset (5 ms snap), a hand shape from before
  the run is clipped to it, derived `Ignore` is skipped; staccato never equals
  legato. **Integrity** is the full stored content (exact sustains, notation
  durations, notation voices, stored anchors/shapes, template names) — it
  fingerprints ONE placement and never compares two. The gap is `SpacingGap`,
  the ONE implementation: `ChartSpacingEnforcer.GetMinDistance` delegates to it,
  and `ChartSpacingEnforcer.SpacingGapOf(config)` feeds
  `MeasureContentOptions.Spacing` — key a chart with the gap it was cleaned
  with. Every key starts with a header naming the format version
  (`MeasureContentOptions.FormatVersion`) and every option, so a key or hash
  from another rule never matches; bump the version with any change to what a
  key holds (`MeasureContentIndexTests.TheHash_IsStable` pins one literal).
  `LinkCandidateDetector.Detect(parts)` finds repeated whole-measure runs over
  detection keys (greedy, longest first, no overlap, never starting or ending on
  a rest, each run re-checked WHOLE); hand it one part for repeats within an
  arrangement or one `LinkCompatibility` class (same instrument, strings,
  tuning, capo, cents; never vocals or play-only) for shared runs. Groups are
  named by letter (`Letter`); the detector's results are `LinkCandidateGroup` /
  `LinkCandidatePlacement`, not the model.
  The MODEL is `Domain/Project/LinkGroup` + `LinkPlacement` on
  `ChartProject.LinkGroups` (real copies in every placement; the arrangement by
  reference, the run as absolute barlines; the restorer, `ChartProjectPositions`
  and `BeatScaleService` all carry it). `LinkRuns` resolves placements to
  measures and keys, `LinkedRunCopier` plans one copy, `LinkGroupVerifier` is the
  load check. Propagation and the per-action policy are services-side:
  [linked-measures.md](../services/editing-invariants/linked-measures.md).
- `Diagnostics/` — **currently empty**; the diagnostic/validation types are under
  `Domain/Validation/`, not here.
