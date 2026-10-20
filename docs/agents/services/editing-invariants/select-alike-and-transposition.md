# `Editing/` — the chart-model invariants (deep reference) — select-alike, patterns and transposition

## Select-alike, patterns and transposition

- **`Editing/Selection/ChordShapeTransposition` is "the same grip elsewhere on the
  neck" (0.40), and its rules must stay identical to `FretRule.Transposable`** or
  "select alike" and "finger alike" would disagree about what alike means: an OPEN
  string never transposes (stopped by the nut, not the hand; under a capo "open"
  means at or below it), the STRINGS must match, and an arpeggio is a different
  KIND of shape rather than another position of one. Shift 0 counts — a separate
  template at the same frets is what the chart mints whenever a chord is
  re-fingered privately, and that is exactly the case worth unifying. It backs the
  chord panel's "+ the same grip elsewhere" rung (#484; it replaced the "finger N alike shapes" button), which edits those templates IN PLACE: that
  is NOT the "never mutate a shared template" violation it resembles, because no
  fret moves, so no chord's SHAPE changes.

- **`Editing/Selection/Patterns/` is where "alike" is DATA (0.39), and it does
  not replace the three hard-coded matchers — it exists to argue with them.**
  `SelectLikeService`, `ChordShapeSignature` and `AnchorSpanSignature` each
  encode one specific, defensible answer, and there was no way to ask a
  different question or to find out which one a real session reaches for. A
  `MatchPattern` is that question as a serializable record of FACETS
  (scope, fret rule, strings/size/kind, sustain, fingering, name, rhythm,
  technique flags, anchor width/span) plus its own keybinds; `PatternMatcher` is
  the pure engine; `MatchPatternStore` persists them in `AppConfig.MatchPatterns`.
  Rules that are not obvious from the type:
  **(a) a pattern is a question ABOUT the selection, never a standing filter** —
  an empty seed matches nothing, and the seeds are never their own matches.
  **(b) the engine is a canonical string KEY per element, except fingering**,
  which cannot go in it: an unauthored finger is a HOLE, not a value, so
  agreement is not transitive and fingers are compared pairwise alongside the
  key (same rule as `ChordShapeSignature`, and for the same reason).
  **(c) a bend curve and a slide are compared RELATIVE** — the bend to the
  note's own onset (`BendValue.Position` is ABSOLUTE, so comparing it raw makes
  a bend alike only to a bend at the same instant of the song, i.e. never), the
  slide as a distance rather than a destination fret (a slide is a movement, so
  it survives transposition by construction).
  **(d) `FretRule.Transposable` NEVER transposes an open string** — fret 0 only
  ever matches fret 0, because a capo-less open string does not move with the
  hand. The transposition base is the lowest FRETTED fret of the element (or of
  the whole run), never the lowest fret.
  **(e) `PatternMatches.Groups` is what the navigate verb walks**, not the flat
  element lists: a run pattern's occurrence is N elements, and without the
  grouping "go to next match" steps through one lick note by note. Overlapping
  windows are claimed, so four identical notes yield two occurrences of a
  two-note lick and not three.
  **(f) the starter set in `MatchPatternCatalog` ships UNBOUND** — seven patterns
  times three verbs is twenty-one chords, and `ChartKeybindHandler.RefreshBinds`
  resolves a collision last-registration-wins with only a log line. The builder
  can run a pattern without a key, so binding is a decision made after the
  pattern has earned it.
  **(g) `AppConfig.MatchPatternsSeeded` is what makes the starter set arrive
  once.** An empty list is a legitimate state (the user deleted them all) and
  must not be re-seeded on the next launch.
  **(h) `PatternCompanions` is what makes a pattern an EDITING unit rather than
  a selection.** A chord, the hand shape drawn over it and the FHP anchor
  holding it are one authored decision — re-fingering the chord alone leaves the
  other two describing a shape that is no longer there. Companions travel WITH a
  match and never become matches of their own, or the navigate verb would stop
  once per companion; and a companion that is already a seed is not re-reported,
  so "matches never include the seeds" survives the expansion.
  **(i) Three facets shipped as booleans in 0.39.0 and became tri-states in
  0.39.1** (`MatchStrings`→`Strings`, `MatchRhythm`→`Rhythm`,
  `MatchSustain`→`Sustain`). The booleans survive as `bool?` properties that
  `Normalized()` folds in and drops (`JsonIgnoreCondition.WhenWritingNull` keeps
  them out of everything this build writes) — without them the "Same bend
  anywhere" built-in would silently tighten from "ignores strings" to "same
  strings" on the first load. `Clone` deliberately does NOT carry them; delete
  the shims once no 0.39.0 config can plausibly survive.
  **(j) `PatternSummary.Describe` is not decoration.** The question the builder
  answers ("which definition is useful?") is a comparison, and comparing means
  reading several patterns at once — so each has to be legible without being
  opened. Two patterns differing in one facet must not describe identically.

- **"Similar" for select-like is `Editing/Selection/ChordShapeSignature`, and it
  is NOT template identity.** The key is the string→fret map the sound actually
  PLAYS (chord notes, so a still-untemplated import counts) plus the string→
  finger map its template authors — with an **unauthored finger treated as a
  HOLE, not a value**: fingers only have to agree where both sides specify one.
  Technique is absent by construction. `ISelectLikeService.FindLikeShapes`
  matches chords AND hand shapes against the SAME seed set (a hand shape's
  template is a chord shape, and the bar has to follow its chords or the 3D
  fingering detaches from the note heads). Keying on `TemplateId` was the bug:
  templates are shared and deduped, so re-fingering ONE chord of eight identical
  ones mints it a private template and an id-keyed select-like then found
  nothing at all. A drifted template (frets ≠ the chord's notes) contributes no
  fingering — it is describing some other shape.

