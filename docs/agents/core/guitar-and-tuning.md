# Frets, capo, tuning, chords and the closed vocabularies

Deep reference for `DotCharter.Core/AGENTS.md`. Everything the guitar model
guarantees: the absolute-fret/capo convention the whole app agrees on, the two
places a chart states its tuning, how templates and hand shapes compare, how
chords are named, and the two Rocksmith marker vocabularies. The time/graph
invariants are in [timeline-and-project.md](timeline-and-project.md).

- **Frets are ABSOLUTE on the un-capoed neck; fret `0` = open, which a capo
  stops at the capo fret.** So a sound's sounding offset is `max(fret, capo)`,
  and a NON-ZERO fret below `Arrangement.Capo` is unplayable/malformed
  (inspector `L01`, `ChartExportValidator.KeyNoteBelowCapo`). This is the
  RS2014 convention and the whole app agrees on it: `RefretPlanner.SoundingMidi`,
  `LivePitchAcceptance.SoundingMidi` (the one sounding-pitch derivation, shared with
  the audio lint), `ChordNameSuggester`, the renderers (`fret <= Capo` ⇒ draw as
  open) and `TuningCheckTargets`. **EOF/`.eof` AND Guitar Pro store frets
  RELATIVE to the capo; RS XML/SNG/rscp are absolute like us.** Both relative
  formats ADD the capo on import (to FRETTED strings only — fret `0` stays `0`,
  since `max(0, capo)` already sounds it at the capo, and lifting it would turn
  an open string into a stopped one) and subtract it on export: EOF via
  `EofProGuitarTrackParser`/`EofNoteBuilder` ("Capo added to fretted strings
  only", `rs.c`), Guitar Pro via `GpCapoFrets.ToAbsolute` at both importers'
  fret reads (0.34.0 — before that, every fretted note on a capo'd GP track
  imported `capo` semitones flat). Anything deriving pitch or hand position from
  a fret MUST fold the capo in — and must be a strict no-op at capo `0`, which
  is nearly every chart. **A tool that DERIVES a fret must also refuse one below
  the capo rather than write it**: `ChartMaintenanceService
  .BuildPowerChordTemplate` (0.41) took the root's pitch off the un-capoed neck
  and guarded only `fret < 0`, so under a capo an open string was read at the nut
  and the derived 5th/octave landed on a sub-capo fret — the malformed shape
  `L01` exists to flag, minted by the app itself. It now derives from
  `max(fret, capo)` and returns `null` when the interval is unreachable.
  **The same fault shipped a second time in the keyboard fret verbs (0.50.1),
  and `SoundingPitch.SnapToNeck` is now the shared answer for any verb that
  WRITES one.** `SelectedNoteMutationCommands`' `SetFret` / `MoveFretBy` /
  `TransposeShapeTo` clamped to a plain `0..24` while their own summary claimed
  they were "capo-agnostic by construction", so on a capo-5 chart a typed `3`, a
  `FretDown` off the capo and a `FretDownOctave` each wrote an `L01` that
  `FixCapoFrets` then raised silently on the next auto-fix — keystroke and
  correction separated by an unrelated edit, which is why nobody caught it.
  `SnapToNeck`'s legal set is `{0} ∪ [capo, 24]`; a DESCENT off the back of the
  capo lands open so a fret-down run stays monotone, an ascent or an absolute
  write lands on the capo (`FixCapoFrets`' own answer), and **capo `0` is a
  strict no-op**. A CHORD transpose that would strand a string behind the capo
  is refused whole, the same rule that already governs running off the 24th:
  snapping one string forward squashes the grip into a different chord.
- **No exporter reads `Arrangement.Properties` directly — it resolves through
  `ArrangementPropertiesDeriver.Derive` (0.35).** Rocksmith folds those ~30 flags
  into `SongDiffEasy/Med/Hard`, and until 0.35 nothing in the editor could author
  one while only the RS-XML writer derived them — into a local clone the psarc
  manifest never saw — so a chart built from scratch told the game it used no
  techniques. Every flag now sits in exactly one provenance group, and
  `TheThreeProvenanceGroups_PartitionEveryFlag` fails if a new flag joins none:
  **authored elsewhere** (`represent`/`bonusArr` ← `Subtype`, `path*` ← the
  resolved route, `bassPick` ← `PickedBass`, folded in every time and never
  pinnable); **derived from content** (the 20 technique flags + `standardTuning`,
  SET *and* CLEARED so a stale imported flag cannot outlive its content);
  **undecidable** (`fingerPicking`, `twoFingerPicking`, `pickDirection`,
  `syncopation` — no note field encodes them, so they are carried verbatim and
  authored by hand). `ArrangementProperties.ManualOverride` is the
  `IgnoreDerived`-style pin over the derived group only (rscp `manual="T"`,
  absent at the default); `standardTuning` stays outside it because the tuning is
  a first-class field and pinning its summary could only produce a lie. The
  design is EOF's (`rs.c:1836-1988` derives at export; the user authors only the
  arrangement type, the priority and picked bass) plus DLC Builder's opt-in
  override (`ArrangementPropertiesOverride.fs`) — DotCharter sits in EOF's seat,
  with nothing upstream to trust. Deliberate divergences from EOF, each matching
  Log Charter: `sustain` counts sustained chords, `unpitchedSlides`/
  `fretHandMutes` are really derived (EOF hardcodes both to 0), and `dropDPower`
  is a per-chord flat-shape test rather than a whole-track tuning AND.
- **PHRASES LIVE IN TWO STORES AND BOTH CAN BE LIVE AT ONCE — always read them
  through `Utilities/PhraseMarkers`.** The timeline and song-overview editors
  author a phrase as `EventPoint.Phrase` (the EOF-style text marker `.rscp`
  keeps); the RS-XML and psarc readers fill `Arrangement.Phrases` +
  `Arrangement.PhraseIterations` (the `<phraseIterations>` element). Open an
  imported chart and edit it and you have both. **Reading one is a silent
  whole-feature failure**: measured over the 89 hand-finished RS2 exports in
  `TestData/EOF`, `EventPoint.Phrase` yields **0** markers and
  `PhraseIterations` yields **2,596**. It has now bitten **five** separate
  pieces of code — `RsXmlSongWriter.BuildPhraseExportData` (editor phrases
  silently absent from the export), `ArrangementAutoFixService
  .AddMissingAnchorsAtPhraseStarts` (never did anything to an imported chart),
  the E06 rule (reported every imported chart clean) and, found in 0.44,
  `ChartImprovementService.EnsurePhraseStartAnchors` +
  `ChartSpacingEnforcer.RemoveRedundantAnchors`. **The last pair is the worst
  shape it has taken: a LOOP that cancels itself out.** The two fixers add the
  anchor RS requires at a phrase start; the enforcer's protected-position set
  was `EventPoint`-only, so on an imported chart it protected nothing and
  deleted that anchor again on the very next save — and it always matched,
  because a phrase-start anchor restates the position in force by construction
  and is therefore "identical" to the one before it. `PhraseMarkers.All` unions
  and dedupes by position; `PhraseMarkers.Playable` also drops
  COUNT/END/noguitar. **A new reader of phrases goes through it, and one that
  also cares about SECTIONS unions `EventPoint.Section` itself** — sections are
  not phrases and `PhraseMarkers` deliberately does not carry them.
- **A chart states its tuning on the ARRANGEMENT, and there is no project-level
  copy — do not add one back (0.44.2).** `ChartData.Tuning` existed until then
  and was a LIE in every path that filled it. The psarc importer's back-fill,
  `if (project.Chart.Tuning.Length == 0) … = Arrangements[0].Tuning`, could never
  fire, because the field's class default was six E-standard names — so every
  Rocksmith import reported E standard whatever the song was in (measured on
  `Galneryus - Brutal Spiral of Emotions_p.psarc`: both arrangements are
  offsets `-3,-1,-1,-1,-1,-1` / names `C# G# C# F# Bb Eb`, while the project
  record said `E A D G B e`). `RscpHeaderParser` lied differently — it stamped
  STANDARD-tuning names sized by the first `<tuning strings="N">` node it found,
  so an Eb chart reloaded as E. **Populating it would still have been wrong**,
  which is what decided the removal: a project holds several arrangements at
  once, so a 6-string lead beside a 4-string bass has two right answers and one
  slot. Seven production files read it for a lane count and every one of them was
  wrong for the non-first arrangement — `MinimapOverviewControl` (×2) drew six
  lanes over a bass, `InspectorViewModel.Chords` loaded a six-string chord
  template into it, `CommandCatalog`'s `StringUp` disagreed with the identical
  KEYBIND (already migrated off in the `StringDigitTargeting` pass), and
  `ChartRenderer` / `LaneLayer` / `FrameSnapshot` each carried it as a stale
  second-choice fallback. The lane-count answer is
  `Arrangement.Tuning.Length`, via `DotCharter.UI/Input/StringDigitTargeting.StringCountOf`
  where a null-safe one is needed. **Nothing persisted the field** — `RscpWriter`
  never emitted it, and the recovery store and checkpoint service both serialize
  through `RscpChartSerializer` — so its removal was not a file-format change and
  needed no migration. Pinned by `UI/Input/ProjectTuningSingleSourceTests`
  (a real `.rscp` round trip of a 6-string Eb lead + 4-string drop-D bass, plus a
  source lint over Core/Services/UI/Rendering/Analysis).
- **The arrangement states it twice IN TURN, and everything that WRITES one goes
  through `Utilities/TuningOffsetResolution`.** `Arrangement.Tuning` holds note LETTERS
  (what the editor shows, what Song Select stores, what `TuningNameResolver`
  reads); `Arrangement.TuningPitches` holds per-string OFFSETS (what RS-XML, SNG,
  Guitar Pro and the `.rs2dlc` write). All four exporters read the offsets
  directly with a `0` default for a missing entry, so **an arrangement carrying
  only letters exported as E standard — silently, in four places at once.**
  The resolver is deliberately CONSERVATIVE: authored offsets ALWAYS win, so
  every chart that has them is byte-identical; only a completely EMPTY vector
  falls back to deriving from the letters.
  **That derivation covers 4- and 6-string instruments ONLY.** A 5-string bass
  offset has no agreed base — GP measures against B-E-A-D-G, the RS SNG path
  against E-A-D-G-B, the same ambiguity `TuningUtilities.GetTuningNames` documents
  and refuses to resolve — and guessing would write a WRONG tuning into a shipped
  package, which is worse than the zeros it replaces. Every other string count
  keeps those zeros. The derived offset is the nearest signed spelling in
  `[-6, +5]`, so the letters re-derive EXACTLY and `TuningNameResolver` names a
  derived vector the same way it names the letters it came from —
  `TuningOffsetResolutionTests` pins that round trip.
- **A tuning NAME the table does not know is not "E standard" — it is an unknown,
  and `TuningUtilities.TryGetOffsetsForTuningType` is the call that says so.**
  `GetOffsetsForTuningType` ended in `_ => new[] { 0,0,0,0,0,0,0,0,0 }`, so any
  unrecognised name resolved to standard tuning, indistinguishably from a real
  `E_STANDARD`. The table uses Rocksmith's low-note spelling — **Drop C# is
  `E_FLAT_DROP_D_FLAT` and Drop B is `D_FLAT_DROP_B`**, which almost nothing else
  calls them — so a name arriving from another tool, a hand-edited `.rscp` or a
  UI preset misses it easily, and `"CUSTOM"` (what the PSARC importer stamps on
  every Rocksmith chart) is not in it at all. Five callers take the honest
  answer: `TuningCheckTargets.TryBuild` (⇒ the gutter prints `?`),
  `RscpArrangementParser` (⇒ empty offsets and `?` names rather than fabricated
  zeros), `RscpWriter` and `PhraseCatalogFragment` (⇒ an undecodable name is
  never certified as a valid shorthand) and `LibraryDoctorAnalyzer`.
  `GetOffsetsForTuningType` survives as the DELIBERATE default for the two that
  need some vector to do fret arithmetic against — `RefretPlanner` and the MIDI
  import dialog — and both say so at the call site.
  `Core/Utilities/UnknownTuningNameTests`.
- **"Bass" is a PART, not only a PATH — ask `Utilities/ArrangementInstrument`
  (#161).** Rocksmith has no 5/6-string bass path, so CDLC charts one on a GUITAR
  path at A220 (`CentOffset` −1200, offsets `-5 -5 -5 -5 -4 -4` against the
  guitar's strings; a 5-string leaves the sixth slot alone). `IsBassPart` = the
  Bass path, OR a guitar path at or below `OctaveDownThresholdCents` (−1150; the
  owner's library has 44 such arrangements, all at exactly −1200, and no other
  guitar path below −45 ¢), OR — where the caller has the project —
  a guitar path whose tones are ALL bass tones (`Bass_` amp AND the
  `$[35715]BASS` descriptor; `DotCharter.Tone`'s `ArrangementBassTones`, because
  it parses tone JSON). Ask it for SOUND and NAMES (synth voice, stems, sheet
  clef, the bassline anchor rule); an export and every PATH rule (the Bass path's
  implicit −12 in `SoundingPitch`, its 4-string validation, `bassPick`, the route
  mask) stay on `ArrangementType`. **The A220 octave lives in the reference, not
  the path:** `SoundingPitch.MidiFor` does not add it (the chart synth adds the
  full −1200 ¢ itself), and anything that NAMES a note or compares it with real
  audio adds `ReferenceOctaveSemitones` and measures cents against
  `ResidualCents` — never both the octave and the raw offset.
  `TuningNameResolver.ResolveFor(arrangement)` reads such a part's low five or
  four strings as a 5-/4-string bass when its six slots name nothing.
  `Core/ArrangementInstrumentTests`.
- **`TuningNameResolver`'s 5-string bass names carry a `5-String Bass` prefix**,
  the same convention the pre-existing `6-String Bass Standard` entry uses. A bare
  `B Standard` would collide with the SIX-string GUITAR tuning of that name — and
  would then be read as that guitar tuning by anything that matches tuning names
  (a bass arrangement filed under a guitar tuning). The entries are keyed by ABSOLUTE pitch
  classes (a 5-string bass is not a transposition of a guitar), which is what
  sidesteps the offset ambiguity above entirely.
- `ChordTemplate.Id`/`TemplateId` default to `-1` = untemplated/custom chord.
- **TWO flags spell arpeggio, so Core owns both answers and nothing reads one
  flag (0.44).** `ChordTemplate.IsArpeggio` answers "is this an arpeggio?"
  (either flag — `RsXmlSongReader` sets only `ForceArpeggioInRs`,
  the package reader's SNG mapper only `Arpeggio`), and the `ArpeggioFlavour` readonly record
  struct answers "is this the SAME flavour?" (`Matches`, both flags exactly) plus
  stamps a mint (`ApplyTo`, so a mint can never carry half a flavour). The
  flavour comparison is deliberately the conservative one: two templates that are
  both arpeggios but spell it differently are NOT interchangeable, because the
  RS-XML writer re-appends `-arp` from `ForceArpeggioInRs` while the renderers
  and `EofExporter` read `Arpeggio`. See
  [../services/editing-invariants.md](../services/editing-invariants.md) for the
  six editing sites this fixed and the lint that keeps them fixed.
- **`ChordTemplate` has VALUE equality (excluding `Id`) — so no collection call
  resolved by `EqualityComparer<T>.Default` may stand in for identity.**
  `Remove`/`Contains`/`IndexOf` hit the FIRST value-equal template, not the
  instance the caller owns, and two value-equal templates with different ids are
  ordinary chart state (nothing dedupes an RS-XML / psarc / rscp template list on
  the way in, and the template editor's create-then-rename path mints them by
  hand). Undoing a template-creating edit therefore deleted somebody else's
  still-referenced template, and `SngFromDomainMapper` filters chords on
  `TemplateIndexById.ContainsKey` — so those chords vanished from the exported
  psarc silently. `Services/State/ChordTemplateCollectionOps`
  (`ContainsInstance`/`RemoveInstance`) is the identity answer; the value
  comparison stays for "a template LIKE this one" (the diff engine, the
  power-chord builder). Keep the equality — do not "fix" this by weakening it.
- **Comparing two grips goes through `ChordTemplate`, never a local loop.**
  `HasFrets(frets)` is "exactly these frets on exactly these strings" (fingers,
  name and flags ignored — pair it with `ArpeggioFlavour.Matches` for the
  find-or-create key); `HasSameShapeAs(other)` adds the fingers (the grip
  Rocksmith renders); `Equals` is that plus the name and both arpeggio flags.
  All three stand on `Utilities/MapEquality.SameEntries`. Seven private copies
  of the same loop lived in the refret, fingering, resolver, spacing and EOF
  code until 2026-10-03.
- **A `HandShape` span is HALF-OPEN and every containment test goes through
  `HandShape.Covers` (start inclusive, end EXCLUSIVE)**; any other half-open
  span (an anchor's reach, a pattern region) uses
  `FractionalPosition.IsWithin(start, end)`, which `Covers` itself calls. Shapes ABUT — the
  per-chord shapes every tab importer emits park one shape's end exactly on the
  next one's start — so a closed reading makes the next shape's opening sound
  count as content of this one. Four sites have open-coded `<=` and been
  corrected: `RefretPlanner.ApplyHandShapeFixing`, `RefretService.ApplyChordGroup`
  and (0.41) both predicates in
  `GlobalEditingCoordinator.DeleteSelectedRelated`, where it meant an emptied
  shape looked occupied and "delete + related" never collected the orphan.
- **`ChordNameSuggester` names at SOUNDING pitch, capo included.** The
  `Arrangement` overload reads `Arrangement.Capo` itself; the raw-tuning overload
  takes an optional `capo` (default `0`). A capoed open shape therefore names
  like the equivalent fretted shape on an uncapoed neck (capo 2 + open-C shape ⇒
  `D`). Negative template frets stay "string unused" and are never folded up to
  the capo.
- **Chord names come in two STYLES (`ChordNamingOptions`, #493), and neither is
  a bug fix of the other.** `ChordNamingStyle.Eof` (the default,
  `AppConfig.ChordNamingStyle`, Settings ▸ Editor "Musical chord names" off) is
  Editor on Fire's spelling — `min`, `Maj7`, sharps first, an inversion by its
  PLAIN name — which the owner's finished charts carry. `Musical` spells roots
  and bass notes in the song's key, writes `m`/`maj7`/`m7b5`, and ranks the
  SLASH form of an inversion first (`Cm7/Bb`). Two answer keys, both committed
  as stripped chord data under `DotCharter.Tests/Core/Utilities/Fixtures/` and
  held as floors by `ChordNameAnswerKeyTests`: EOF style vs 1,688 named
  templates of finished charts = **1,529 exact (90.6%)**, unchanged by #493;
  Musical vs 258 diagrams typed in Guitar Pro tabs = **230 (89.1%)**, from 114
  (44%). Rules:
  **(a) the #493 vocabulary (`ExtendedPatterns`: 9 / m9 / maj9 shells without
  the 5th, 7sus2 with and without the 5th, maj7sus2, 7(b9) shell, (b9),
  m6(#5)) is a FALLBACK in EOF style** — consulted only when the original
  `Patterns` name no root. Ranked with the rest, the root-position `Maj7sus2`
  took first place from the inversion `Fadd4`/`Cadd4` EOF writes (10 of the
  1,688). Musical ranks them together.
  **(b) the key is read from the NOTES, the authored key is a hint**
  (`ChordKeyEstimator.Resolve`): Krumhansl–Kessler profiles over the hardest
  level's pitch-class onsets (tuning and capo folded in, full mutes skipped,
  templates when the level is empty); the authored `ChartData.KeyAccidentals` /
  `KeyIsMinor` wins while it scores within 0.15 of the best key, and stands
  alone under 8 notes. A GP tab's key is its first bar's signature, and of the
  owner's 12 tabs five say C major over flat-key songs — authored-key-only
  spelling scores 77% where the estimate scores 89%. `KeyAccidentals == 0`
  major is "unset". No notes and no key ⇒ no key ⇒ today's sharp-then-flat order.
  **(c) spelling touches only the five black keys** (`SpellsFlat`): a note in
  the scale follows the signature; outside it, ♭2/♭3/♭6/♭7 are flats and ♯4 a
  sharp in major, ♭2 a flat and the raised 3rd/4th/6th/7th sharps in minor.
  Never `Cb`/`E#`. **(d) resolve the options ONCE per batch**
  (`ChordNamingOptions.For`): Musical reads the whole arrangement. The chord
  panel's Suggest pills and Auto-fill pass them; import, `ChordTemplateResolver`
  and the export strip still name in EOF style. **(e) the double-stop gate is
  the same in every style.**
- **A TWO-STRING shape gets no automatic chord name, and the export strips one
  it already carries (0.41).** Two pitches do not determine a chord — the same
  two notes sit inside a dozen of them — so a detected name for a double stop is
  a guess the charter cannot tell apart from a real one. `DoubleStopNameOptions`
  is the policy (`Suppress`, default ON; `KeepPowerChordNames`, default OFF) and
  `ChordNameSuggester.IsDoubleStop`/`IsPowerChord`/`SuppressesAutoName` are the
  predicates. This is EOF's `eof_dont_auto_name_double_stops`
  (`EOF_SRC/tuning.c:425`, gated on `stringctr == 2`) with the same default
  behaviour it has when switched on, including suppressing POWER CHORDS; rscharter
  never auto-names at all and draws a blank name as nothing. Four rules:
  **(a) the gate is on the AUTOMATIC namer only** — `SuggestChordNames` (the
  candidate list) is never filtered, so the inspector can still label a double
  stop by hand and that name survives.
  **(b) naming does NOT require adjacent strings.** EOF's
  `eof_pro_guitar_note_is_double_stop` and our `RsTechniques.
  IsDoubleStopAdjacentStrings` do, because that flag describes a TECHNIQUE;
  naming only cares that there are two pitches to name from. Do not unify them.
  **(c) the interval test folds in tuning AND capo** (via the same
  `GetSortedSounds` every name goes through), so a power chord on a dropped or
  capoed neck is judged by what it plays. A fifth is 7 semitones and its
  inversion the fourth is 5; a unison/octave is not a power chord.
  **(d) it is enforced at the EXPORT boundary too** — `RsXmlSongWriter.
  ChordNameOptions` and `SngFromDomainMapper`'s `chordNameOptions`, because a name
  also arrives from a psarc import, a paste, or a chart authored before the
  setting existed. The strip runs BEFORE the `-arp` suffix is derived (or an
  arpeggio double stop ships as the bare marker `-arp`), and the Nop bit comes
  from the `ChordTemplate.NopInRs` FIELD (#489), never the name, so the strip
  cannot drop it.
  `Services/Editing/Fixing/DoubleStopChordNames` is the chart-wide tool for names
  already on disk; mutating the shared template's NAME in place is safe there
  precisely because no fret moves.
- **`ChordTemplateFingerSetter` is a PURE FUNCTION OF THE FRET SHAPE — keep it
  that way.** Templates dedupe by frets, so one shape can carry only one
  fingering; making the suggestion context-dependent (what the hand did before)
  would need one shape to back several templates, which collides with the
  "never mutate a shared template in place" invariant that caused a real
  corruption bug in v0.33.2. Measured with `tools/chart-eval` over the owner's
  86 finished charts (43,517 fingered chords, `--split all`): **81.0% exact,
  88.1% per string, against a ceiling of 92.4%** for ANY function of the fret
  shape (each shape's most common fingering) — train 80.2%, holdout 84.3%
  (before the learned shapes below).
  Over the `TestData\EOF` templates (1,648, the
  `LocalOnly_ChartQualityEvalHarness.ScoreGroundTruthShapesWithOurAlgorithms`
  definition): 74.6%, against 88.8%. Most of what is left is ONE charter's
  habit — the octave shape as 1·3 in 8 songs and 1·4 in 5 (but 1·4 by chord
  count), a G/B double stop as 1·2, 2·3 or 3·4 — so a rule that serves one
  charter loses another; count SONGS, not chords, before adding one. Order is
  catalogue-of-standard-shapes first, then the ergonomic heuristic: legal
  barre grouping → the index barre laid across a muted string to take in a
  second barre at its own fret (`1x111x`, `1x121x`, never `1x222x`/`1x243x`)
  → no barre directly beneath an open string (Em is `x12`, never `x11`) → no
  finger crossing → position-dependent spread. `ChordTemplateFingerSetterTests`
  lints every shape of up to four fretted strings for fret-order crossings;
  string order is NOT linted, because the open D (`xx0232` → 1·3·2, 707 chords,
  10 songs) puts the ring finger between index and middle.
- **Seven shapes take a LEARNED fingering before the heuristic (#502) —
  `Utilities/LearnedChordFingerings` over the generated
  `LearnedChordFingeringData.cs`.** Still a function of the shape alone: the key
  is the shape moved down to its lowest fretted fret (open and unused strings
  kept apart), so an entry serves it anywhere on the neck.
  `tools/chart-eval --learn` writes the file from the TRAIN split only: a shape
  is listed when one fingering is at least 75% of its chords, chosen in at least
  3 songs, and not what the heuristic gives all of them. A song-grouped 5-fold
  cross-validation on train chose those bars (a shape from one song, or a share
  below 75%, lost accuracy on the songs left out; 2 songs and 3 within 0.15 pp).
  `SetHeuristicFingers` is the suggestion without the table. The thumb-over
  `0x0 01x` shape (T·1·3, 177 of 219 train chords, 5 songs) is the biggest entry.
  Chord fingering exact, context-free / after FHP, train / holdout: 80.1 / 84.2
  → 81.0 / 85.5 %, 80.5 / 84.4 → 81.4 / 85.7 %; the Guitar Pro answer key
  83.9 → 83.4 % (its judge fingers two of the shapes another way).
  **What is left is per-song style, and 90% on held-out songs needs the
  owner:** a table learned on train from the EXACT shape scored 76.7% on
  holdout (and 93.2% on train), because the commonest misses are one charter's
  habit per song — the two-string power chord as 1·3 or 1·4 (consistent within
  an arrangement, opposite across them, independent of fret, palm mute or
  sustain), the octave shape the same. Two per-arrangement toggles (power chord,
  octave) chosen by an oracle reach 92.4% on holdout; anchors as input add
  little (the charter's own anchor + shape, learned on train: 86.8% holdout;
  the generated anchor: no gain).
- **…so a power chord or a two-fret octave follows the ARRANGEMENT's own 1·3 /
  1·4 (#511), and the setter stays pure by taking it as an INPUT.**
  `SetSuggestedFingers(template, maxStrings, PairFingering? twoFretPair)`
  applies the hint only to exactly two fretted strings, the upper two frets up
  (`TwoFretPairStrings`); it never sees an arrangement (pinned by
  `PairFingeringHintTests`, which also lints that the setter takes no
  `Arrangement` and keeps no mutable static). Whether the template IS a power
  chord (adjacent strings, a fifth) or an octave (one string skipped) is decided
  on the Services side by sounding pitch (`Editing/Fixing/TwoFretPairFingering`),
  and the choice by `Editing/Fingering/ArrangementFingeringStyle`: one vote per
  template of that pair fingered `1·3` or `1·4` (2·4, thumbs and the shapes
  that never vary — root·fifth·octave, the three-fret G–B pairs — do not vote),
  a STRICT majority, a tie keeps the suggester's own. **A template never votes
  for itself** (`HintsFromOthers`), or one authored `1·4` would read as the
  suggestion and lose its anchor pin in the FHP walk. Every automatic fingering
  passes it: `ChordTemplateResolver` (a template an edit mints), Refret
  (`IFingeringGenerationService.SuggestFingeringFor(Arrangement, …)`),
  `ChordFingeringFollower.FreeGripResolver` (Generate FHP, the AI pass) and the
  tab import (`ImportFhpPass` → `FollowAuthored`: the file's own fingered pairs
  set the style for the ones it left to the suggester), the chord panel's
  Generate fingerings and its display seed for an untemplated chord
  (`ChordTemplateEditorViewModel`) and a wheel-resized hand-shape bar
  (`ArrangementFingeringStyle.RebuildHandShapeGrip` → `HandShapeAutoTemplate.Rebuild`,
  the replaced grip not voting).
  Measured with `tools/chart-eval --style` (the hint for a template is never
  computed from its own answer), chord fingering after FHP, train / holdout:
  none 81.4 / 85.7 %; `prefix` (from the shapes first played EARLIER, the
  in-app flow; the default) 83.8 / 88.6 %; `halves` 81.8 / 88.1 %; `loto`
  (every OTHER template authored) 84.4 / 89.7 %; `oracle` (own answer included,
  a ceiling) 84.5 / 91.4 %. FHP unmoved; the Guitar Pro answer key unmoved
  (87.8 % FHP, 73.7 % fingering of 10,006, identical with the style off).
  **Decided on the data:** one vote per template, not per chord (equal on train,
  −0.6 pp on holdout `halves`); no fallback from the other pair's majority
  (520 of 4,449 train pair chords lost under `halves`, and a loss on holdout too); a strict majority rather than
  2 : 1 (train has 5 narrow-majority templates and all 5 follow it; holdout's one
  mixed arrangement, 2 + 2 templates split by fret, goes the other way).
- **GHOST GEMS live in a hand shape's GRIP, never in a note (#785).**
  `ChordTemplate.GhostMask` is EOF's per-note ghost bitmask (`song.h:200`, bit n =
  string n, our numbering): strings the hand HOLDS (frets + fingers, the chord box,
  the Rocksmith template) and never plays. EOF exports a partially ghosted chord's
  chord tag from a ghost-less clone and its handshape tag from the full shape
  (`rs.c:2369-2420`), so in a package a ghost IS a hand-shape template fret nothing
  under the shape plays — that is how we store it, which makes "never played,
  scored, detected or exported as a note" true by construction (no `ChordNotes`
  consumer sees it). A chord's own template is always exactly what it plays:
  `HasFrets` (the find-or-create key) and `DescribesChordNotes` never match a ghost
  grip. The verbs are `Services/Editing/ChordShapes/GhostGems` (Ctrl+G
  `ToggleGhostGems`: selected notes / masked chord strings → ghosts of the bar over
  them, a bar alone → plain frets again; the inspector's per-string boxes;
  `TappedHarmonic`, Ctrl+Shift+R, holding `AppConfig.TappedHarmonicFretOffset`
  frets lower, never below the nut or behind the capo). Imports: the RS-XML reader
  and the package reader's SNG mapper mark a template fret ghost
  (`GhostGems.MarkUnplayedHandShapeFrets`) only when NO chord names the template
  and the fret is unplayed under every bar naming it that covers a sound. **Limit
  (#795): a ghost string some OTHER sound under the same bar plays at the same fret
  reads back as a plain fret.** Rocksmith XML / SNG have no ghost attribute: a
  hand-shape template fret is the same data whether it is held or played, and "bar
  over picked notes / partial first strum, its strings played later" is the common
  official shape, so treating such a fret as a ghost would invent ghosts in stock
  content (`GhostGemTests.Import_NeverReadsAGhostIntoATemplateAChordPlays_NorUnderAShapeThatCoversNothing`
  pins the conservative side). The
  `.eof` importer copies EOF's ghost bits (pure-ghost notes register their shape
  too). `.rscp` writes `ghost="2,4"` / `pinned="T"`, absent at the default.
  `HandShapeContentRule` (E05) never judges a ghost string. The `.eof` exporter
  (`EofNoteBuilder.AddGhostGems`) writes a bar's ghosts as ghost-flagged gems of
  its CARRIER (`GhostCarrier`, #795): the first sound inside the bar that plays none
  of the ghost strings and only the grip's own frets, skipping single notes but
  never a chord — EOF's RS export reads the handshape's chord from the first note in
  the phrase with two or more gems, ghosts counted (`rs.c:2302-2316`), and the
  importer from the first ghost-carrying note in the window. So a bar that opens
  before its first sound, and a tapped harmonic followed by its chord, round-trip.
  **No EOF spelling (reported as `EofLoss.HandShapeGhostGem`, never moved in time):**
  a ghost on a string its only possible carrier plays — the tapped harmonic alone
  under its bar — because an EOF note holds one fret per string, two notes at one
  timestamp merge with the played gem winning (`song.c:6905-6960`) and a disjointed
  pair sharing a string merges too (`song.h:141`); and a bar over no sound at all.
  `Services/Parsing/Eof/EofGhostCarrierTests`.
- **A PINNED grip is never replaced or re-fingered by an automatic flow (#785).**
  `ChordTemplate.IsPinned` = `Pinned` (authored: a fingering or fret edit in the
  chord panel, the inspector's box, every ghost verb) OR any ghost gem. Both the
  hand-shape card and the chord panel (`PinnedToggle`, #795) carry an in-place,
  undoable pin box (`SetTemplatePinnedAction`), locked while the grip has ghosts. Every
  automatic flow asks it: `ChordTemplateResolver.ResolveForChord` keeps a pinned
  template its chord still plays, `ArrangementFingeringStyle` (`FollowAuthored`,
  `SetSuggestedFingers`), Refret (`RefretPlanner.IsChordPinned` fixes every chord on
  one, so no bar holding it is repointed; a bar on another grip than its chords was
  already "mixed" and fixed),
  `ChordFingeringFollower.FreeGripResolver` (Generate FHP, the AI pass) and its
  bar repointing, `HandShapeAutoTemplate.Rebuild` + `TimelineWheelAdjuster`, the
  chord panel's Generate fingerings (`ChordTemplateFingeringBackfill`) and
  `AiChartApplier`. `Services/Editing/TemplatePinReachabilityTests` holds the
  registry (file + behaviour test per flow, each test with an unpinned control in
  `TemplatePinTests`) and lints every product file that calls an automatic
  fingering/grip verb: it asks `IsPinned` or is listed with why it never rewrites
  an existing template.
- **The two Rocksmith marker vocabularies are CLOSED and live in
  `Utilities/RsSectionCatalog` + `Utilities/RsEventCatalog`** — both ported
  verbatim from Editor on Fire (`eof_rs_predefined_sections[]` at
  `EOF_SRC/rs.c:31`, `eof_rs_predefined_events[]` at `:66`), including their
  display names and their order. `EventPoint.Section` and `EventPoint.Events`
  are the fields; `EventPoint.Phrase` is deliberately NOT constrained (a phrase
  name is the charter's own label, and RS uses it for dynamic difficulty rather
  than display). Two traps:
  **(a) sections compare case-INsensitively, events ORDINALLY** — EOF uses
  `ustricmp` for one and `ustrcmp` for the other, and the event set is why:
  `e0`/`e1`/`e2` are lower-case while `B0`/`B1`/`E3`/`D3`/`E13` are upper-case,
  so `E3` (crowd applause) is not `e3`.
  **(b) a code outside the catalogue is preserved, never dropped** — it belongs
  to whoever authored the chart. `RsEventCatalog.Toggle` keeps every code it
  does not recognise, and the section picker shows an off-list name as a
  transient entry rather than rewriting it.
- **Validation codes mirror external tools** (`I##` DLC Builder/Rocksmith2014.NET,
  `L##` Log Charter, `E##` Editor-on-Fire, plus `D##` DotCharter-native pre-export
  gates ported live into the FEHLER inspector — see `ArrangementIssueType.GetCode()`).
  **The I29 boundary carries a convention**: a raw audio length ≤ 0 means "no
  audio decoded", never "the song ends at 0:00" —
  `ArrangementValidationService.Validate` normalizes it to null (an audio-less
  GP import once got one I29 per sound, 1,011 duplicates), and `D05`
  (`SongEndUnverifiedNoAudio`, `SongEndAudioRule`) states ONCE per
  arrangement-with-content that the check could not run. `D08`
  (`ChordWithoutTemplate`, `ChordRule`) is the same shape for the chord-fingering
  checks: a chord on the `-1` `TemplateId` sentinel is judged by the shape its
  notes play (`ArrangementValidationContext.ChordTemplateFor`), and only the
  residue nothing can resolve is summarized — once per arrangement, never per
  chord. `D09` (`HandShapeWithoutTemplate`, `HandShapeRule`) is its hand-shape
  twin: a shape on the sentinel declares no grip at all and exports with
  `chordId -1`. `D11` (`ChordOutsideHandShape`, `ChordOutsideHandShapeRule`) is
  a chord no shape covers (half-open, per level), one finding per chord
  POSITION across levels; a Warning, because every hit measured on finished
  charts is a chord ON a shape's end (DLC Builder's retired I19). A chord with
  `SplitIntoNotes` never needs a shape (it exports as single notes).
  Several string formats (tuning names, `EventPoint.Events`, tuning aliases) are
  "compatible with rscharter" — treat them as an external contract.
