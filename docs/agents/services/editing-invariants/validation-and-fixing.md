# `Editing/` — the chart-model invariants (deep reference) — `Editing/Validation/` + `Editing/Fixing/`, the inspector and auto-fix

## `Editing/Validation/` + `Editing/Fixing/` — the inspector and auto-fix

An `ArrangementIssue` keeps its original domain-field equality and hash identity.
`MessageKey` and `MessageArgs` render the English sentence in another language and
do not split the same finding into different identities on repeated validation passes.

The inspector (`ArrangementValidationService` over the `Rules/` set) and
`ArrangementAutoFixService` are two halves of one contract: the rule says what is
wrong, the fixer repairs exactly that and nothing else. Both re-run constantly —
validation on the inspector's 150 ms debounce after every history step, auto-fix
on demand and inside `BatchChartLintService`.

**I30 (`ToneChangeOnNote`, `NoteRule` / `ChordRule`) has a one-click fix** (#759):
`Editing/Tones/ToneChangeNudge` moves the tone change ONE 32nd note earlier (the charting conventions' offset; a 16th before #978), measured on
the beat grid (`NoteDenominator/32` of a beat; reaching back over a beat line, the rest is
measured on the previous beat's meter; before the first beat, on the lead-in the timeline
extrapolates from the first interval), as one undo step (`MoveToneChangeAction`). It moves
exactly a 32nd or not at all: it is offered DISABLED with the reason
(`ToneChangeNudgeBlock`) when more than one tone change sits on the note, the move would reach
before 0 ms, reach another tone change (never two on one time), cross a phrase or section start
lying strictly inside the 32nd, or land on another note's onset on any level. A change sitting
ON a phrase start may move before it. "Same time" is I30's own comparison,
`Editing/Validation/ValidationTimeMatch` (whole ms, 1 ms slop), for both matching the finding's
change and the landing-spot guards. The press recomputes the nudge on the live arrangement, so
an undo between the offer and the re-validation cannot stack two changes. The Issues tab offers
it through `IssueFixViewModel` (no Fix all: the fix menu has none).
Tests: `ToneChangeNudgeTests`, `IssueFixViewModelTests`, `InspectorIssuesViewModelTests`.

**D50-D56 (`Rules/FretHandPositionRule`)**: where an FHP sits and what it asks of the hand. D50 before
the level's first sound (inside COUNT too), D51 between sounds or after the last, D52 inside a ringing
sustain, D53 a hand move ON a hammer-on target that could have come one note earlier, D54 an anchor on
fret 22-24, D55 above 24 (Important), D56 a bend on finger 4 (the stated finger, else fret = FHP + 3).
Placement is `Editing/Validation/FhpPlacement.Classify`: a sound, a playable phrase start, a mover phrase,
a pitched slide landing and a HAND-SHAPE START are all good spots (the generator anchors every shape
start on purpose; the hand forms the grip there, even half a beat before the strum). Times compare
through `ValidationTimeMatch`. `FhpPlacement.MoveToNextSound` is the D50-D52 fix (one undo step through
`IssueFixViewModel` and Fix all): the anchor moves onto the next sound, or is removed when that sound
already has one or none follows; withheld when the next sound is a hammer-on / pull-off target or sits
inside a hand shape. The generator itself trips none of D50-D56 on the chart-style fixture
(`FhpGeneratorFindingsTests`), and its last pass, `Editing/FretAnchors/BendFingerShift`, moves an anchor up
one fret when an unfingered bend would fall to the pinky and every fretted sound and pitched slide
landing it governs still fits the four-fret box there (above the capo, on the neck); otherwise D56 is a
real finding. Tests: `FretHandPositionRuleTests`, `FhpGeneratorFindingsTests`, `IssueFixFhpMoveTests`.

**D17/D18 (`SustainCrossesBoundary`, `HandShapeCrossesBoundary`, `Rules/BoundaryCrossingRule`)**: a
sustain, a Link-Next chain or a hand shape across a phrase start, a section start or past END. The
boundary list and "crossed" (strictly inside; a Link-Next sound ending ON a boundary counts) live in
`Editing/Validation/BoundaryCrossing`, shared with its fix. A Link-Next note across a non-mover phrase
stays I10's finding. `BoundaryCrossing.Trim` ends the element on the first boundary it crosses, one
`Mutate*Action` undo step, offered through `IssueFixViewModel` (and Fix all) only when unambiguous: no
Link-Next, no slide, no bend point at or past the boundary, and for a shape no note or chord of its
level starting between the boundary and its end. Tests: `BoundaryCrossingRuleTests`,
`IssueFixBoundaryTrimTests`.

**D40/D41 (`NoteNearlyOnGrid`, `NoteOffGrid`, `Rules/OffGridRule`)**: a note or chord off the beat
grid, one finding per position and level. The grid (`Editing/Validation/BeatGridSnap`) is every
multiple of 1/16 of a beat (straight) or 1/24 (triplets), so triplet-feel swing (off-beats at 2/3,
sixteenths at 1/6 and 5/6) is ON the grid; quintuplets and finer tuplets are not. Distance is in ms
through the beat map: under 1 ms counts as on the grid (timestamp rounding), 1 to 3 ms is a D40,
beyond is a D41 with no fix. `BeatGridSnap.Snap` moves a D40's note or chord with the hand shapes and
anchors that start with it onto the nearest line (a sustain keeps its end), one undo step, through
`IssueFixViewModel` and Fix all; none for a bend, a Link-Next or sustain ending on it, or an occupied
line. **D42-D44 (`LeadInShort`, `TailShort`, `NoCountMeasure`, `Rules/LeadInRule`)** are Warning
suggestions: first note under 10 s (from 1 s; below that D02 and the export gate's unchanged 1 s check
speak), under 2 s from the last sound to the end of the audio (only with a known audio length), and no
empty measure before the first note. Nothing touches the beat map; the messages point at Tempo map ▸
Add silence / Add measures at start. Tests: `OffGridRuleTests`, `LeadInRuleTests`,
`IssueFixGridSnapTests`.

**D60-D67 (`Rules/TempoMapRule`)** are Warnings on the beat map; nothing edits it. D60/D61
(`TempoLooksDoubled`/`Halved`, within 3% of 2x or 0.5x) and D65 (`TempoOffAudio`, more than 1%) compare the
charted part's average tempo with a whole-song estimate in `Tempo/Estimate/AudioTempoEstimateStore`, matched
by audio file name. Only Tempo map ▸ Estimate BPM fills the store (`TempoEstimateService`, whole song only,
this session); without an estimate the three stay silent, so validation never decodes audio. D62
(`TempoOutOfRange`, below 40 or above 300 BPM, once per run) and D63 (`TempoSpike`, a beat at least 1.4x
faster than both neighbours) look only at beats from the first to the last note start. A slow beat is never a
spike (held fermatas are), and swing or triplet feel moves subdivisions, never beat lines. D64
(`TempoAnchorSpam`): 16 or more anchors that keep the previous beat's tempo (within 0.005 BPM). D66/D67
(`MeterMissingOnFirstMeasure`, `MeterChangeUnmarked`): the map must open on a measure whose time signature
matches its beat count, and a later measure whose beat count disagrees with its own signature is reported
once where such a run starts (the last measure is never judged). The thresholds come from
`TempoMapRuleCorpusTests` over the local `TestData/` corpus (269 charts: the fastest pushed beat is 1.13x,
the most redundant anchors 3). That run also lists D62 and D66/D67 findings: a 411 BPM reference
chart and RS XML exports whose `TS:` events disagree with their own measure lines (the Editor on Fire
projects of the same songs draw none). After an add-new Guitar Pro import the status line ends with
`Import/GuitarPro/GpImportChecklist`: time signature changes, slides and ties kept as Link-Next to check
by hand. Tests: `TempoMapRuleTests`, `TempoMapRuleCorpusTests`, `GpImportChecklistTests`.

**D80, D81, D83, D84 (`Rules/PhraseStructureRule`)**: the shape charters give phrases and sections,
findings only (nothing is moved or renamed). D80 a phrase over 16 measures (`MaxMeasures`), D81 one
under 2, D83 a section with no phrase at its position, D84 one silence split into several noguitar
sections. A phrase or section off the bar line is NOT a finding (D82 is gone: EOF's moveR places one
there on purpose, and neither EOF, DLC Builder nor Charter flags it). Measures are the beat map's bar
lines; a map with none is not judged on D80/D81. The AI structure gate judges them like every other
finding, so an AI phrase must sit at least two measures from its neighbours. The export's `Export/PhraseSectionGenerator` (only for a chart with
no authored structure) produces no D80-D84 finding: an `intro` section at the first note, body phrases
on bar lines about every 4 measures (moved a measure either way onto a bar whose content differs from
the bar before, never on a bar inside a sustain, Link-Next chain or hand shape, never shorter than 2
measures), up to 8 before a held bar forces a later cut. GP/Songsterr markers go through
`GpMarkerImport`: the phrase name is `RsPhraseName.Sanitize`d, "Verse 2" or "Pre-Chorus" find their
catalog section, and a second phrase at one position is dropped. Tests: `PhraseStructureRuleTests`,
`PhraseSectionGeneratorTests`, `GpMarkerImportSanitizeTests`.

**D110 (`Rules/AudioCheckRule`)**: an import is a draft. The spots it GUESSED live on
`Arrangement.AudioCheckSpots` (position + `AudioCheckReason`; saved per arrangement as a trailing
`<dcAudioCheck>`, absent when empty, moved with the grid by `ChartProjectPositions`, `BeatScaleService`
and `TempoMapBeats.RemoveContentAtBeat`). Marked today: a Guitar Pro dead-note slide-out's landing
(`Parsing/GuitarPro/GpAudioCheck`, both readers) and, when an add-new GP import replaced the beat map,
the tab's tempo map at each new arrangement's first sound (`GpImportApplier`). ONE Warning per
arrangement with the count, at the first spot, never one per spot. The Issues fix menu clears them
(`AudioCheckMarks`, `SetAudioCheckSpotsAction`): "Checked" the first spot (the finding moves on),
"All checked" every one, one undo step each; Fix all never applies them. Tests: `AudioCheckRuleTests`,
`GpImportCharterTests`.

Phrase boundary times, mover times, sustain-crossing queries and noguitar phrase
intervals all use the context's shared `PhraseMarkers.All` result. An imported
silence ends at the next phrase from either store; section-based silence
intervals remain independent (`ArrangementValidationPhraseTimingTests`).

`LinkNextRule` projects cloned landing-chord bend values for its initial-step
comparison (`ArrangementValidationServiceTests`). Linked-note joins require all
continuation technique, provenance and notation fields to match: only the span,
bend points and final link/slide state are absorbed. A reflection guard in
`ArrangementAutoFixServiceTests` checks each other writable note field and the
fixer's fixed point.

The Note queue's chord work is `Editing/ChordWorkEligibility`. Its split/link-landing
set comes from the export's `LinkLandingBreakup` projection, and
`TemplateAndOverlapRule` shares its `UsesFingering` and capo-aware `IsMissingFinger`
rules. A hand shape or an ordinary chord using the same template still supplies a
fingering-check use. The export and the queue also share `SynthesizeTemplate` for
grips with chord notes but no stored template.

For Issues validation, a link landing from either a note or chord note supplies no
use for L06/L07/L08. If an ordinary chord or hand shape shares the template, its
first eligible use still owns all three findings. Structural overlap checks still
run on landings. Fingerless, fully muted, explicit-split and capo-open handling is
unchanged. `TemplateAndOverlapRuleTests` pins these cases, including both link
sources and the finding's chord/hand-shape backreference.

- **`Fix` must be a FIXED POINT: running auto-fix on its own output has to change
  nothing.** A pipeline that is not idempotent is a pipeline that eats charts —
  it leaves the chart in a state it itself calls broken, and the charter sees a
  second unexplained round of edits. It shipped broken exactly once, and subtly:
  `FixSlides` runs before `FixCapoFrets`, and the capo raise moved a note's
  `Fret` without touching its `SlideTo`, so a note at fret 3 sliding to 5 under a
  capo at 5 came out sliding onto its OWN fret — the degenerate slide `FixSlides`
  exists to clear, one step too late. `FixCapoFrets` now raises the slide target
  with the fret (a slide target is a fret and obeys the same capo rule) and
  clears the degenerate case itself. **Every fixer gets an idempotence test**;
  `ArrangementAutoFixServiceTests.RunningTwice_*` are the worked examples,
  including a kitchen-sink chart that trips every fixer at once.
- **An auto-fix must never invent a finding the editor never showed.** The
  phrase-start anchor fixer defaulted to fret 1 when there was no earlier anchor
  to restate — and the anchor floor is `capo + 1`, so on a capo'd chart it
  manufactured an `AnchorOnOrBelowCapo` (L02) at every phrase it touched. It now
  clamps with `Math.Max(previous?.Fret ?? 1, capo + 1)`, which only ever raises,
  so an un-capoed chart still restates the previous anchor verbatim — the whole
  reason that fix is safe to apply in bulk.
- **A fixer repairs the FAULT, not the element carrying it.**
  `ArrangementAutoFixService.RemoveInvalidChords` deleted any chord whose
  `TemplateId` named no template — authored SOUNDS thrown away to fix a broken
  POINTER, while the shape sat in `ChordNotes` the whole time. Dangling ids are
  ordinary chart state, not corruption (the tab importers' idempotency skips
  leave chords on the `-1` sentinel, a cross-arrangement paste can bring an id
  its destination never had, and until 0.41 undoing a template-creating edit
  removed a value-equal template and left every chord pointing at it), and
  `SngFromDomainMapper` already drops such a chord from the psarc silently — so
  the two losses stacked. It goes through `ChordTemplateResolver.ResolveForChord`
  now and repoints the chord; only a chord with NO chord notes is still removed,
  because that one describes no sound. Same reasoning as the rules dropping their
  `TemplateId >= 0` gates: a guard that turns a missing input into an answer.
  The counter is `invalidChordsFixed` (log-only).

- **A linked chord is LEGAL TO AUTHOR and auto-fix must leave it alone.** The
  landing RS cannot spell is repaired at the EXPORT boundary
  (`Parsing/Xml/Song/LinkLandingBreakup`), never in the chart. The removed
  `UnlinkSoundsLinkedIntoChords` cleared `LinkNext` while leaving the SUSTAIN, so
  every tie became a bare sustain running past its own hand shape — 27 links in
  one run on the owner's chart. See `export-companions.md`.
- **PHRASES LIVE IN TWO STORES and a rule that reads one is SILENT on every
  imported chart.** Read them through `Core/Utilities/PhraseMarkers`, or via
  `ArrangementValidationContext.Phrases` / `.PlayablePhrases`, which build them
  once per pass (three rules want them and `PhraseMarkers.All` allocates a list
  plus a hash set per call). This trap has now caught FIVE pieces of code: the
  RS-XML writer, `PhraseAnchorRule`, the phrase-start anchor fixer, `PhraseRule`
  (all three of I25/I26/I27 returned early on an import, so the inspector read as
  "no problems found") and D03's empty-phrase gate. Measured over the 89
  hand-finished exports in `TestData/EOF`: 0 markers via `EventPoint`, 2,596 via
  `PhraseIterations`.
- **The inspector and `Export/Validation/ChartExportValidator` are two readings
  of ONE question and must not disagree.** I27 counted phrase MARKERS while the
  export gate counted `arrangement.Phrases` — so any song past a couple of
  minutes failed an in-game limit it was nowhere near, in the inspector only.
  Both now count the distinct phrase list. `KeyNoPhrases` reads
  `PhraseMarkers.All` (#184), so an editor-authored chart — whose phrases are
  `EventPoint.Phrase` — no longer draws it. **Known remaining disagreement:**
  `KeyFirstPhraseNotAtStart` still checks `PhraseIterations` only, so it never
  fires on an editor-authored chart (the RS-XML writer synthesises a COUNT at
  beat 0 there, so the package is not wrong; the check is just silent).
- **A check that cannot fire is worse than no check** — it reads as covered in
  the field map, the severity table and the message catalogue. I33
  (`MissingLinkNextChordNotes`) asked `chord.HasLinkNext() &&
  chord.ChordNotes.All(cn => !cn.LinkNext)`, and `HasLinkNext()` IS "any chord
  note links", so it was `A && !A`. `Chord` carries no chord-level link flag; the
  representable fault is the one the SNG mapper acts on —
  `ForceNoNotes` suppresses the `<chordNotes>` block while `CreateMaskForChord`
  still stamps `RsNoteMask.Parent`, so the chord ships claiming to be a link
  parent with nothing to carry the link. (Since the compact-repeat fix the
  RS-XML writer honours `ForceNoNotes` too — it never read it before, so one
  chart exported two different ways depending on the artifact.)
- **A GUARD THAT TURNS A MISSING INPUT INTO "no issue" IS A FALSE NEGATIVE.**
  `TemplateId < 0` is the worked example: `ChordRule` gated I44/I45 on
  `chord.TemplateId >= 0` and `TemplateAndOverlapRule` opened with
  `TemplateId < 0 ⇒ continue`, so every chord still on the sentinel — the tab
  importers' idempotency skips leave some, and hand-authored states exist — was
  reported as having no fingering problem at all. **A rule asks
  `ArrangementValidationContext.ChordTemplateFor(chord)`**, which falls back to
  `ChordTemplateResolver.FindByShape` (the find-or-create key
  `ChartSpacingEnforcer` will use on the next save), memoized per pass.
  `ChordTemplateById` is the raw index and does not answer that question. When
  even the shape resolves to nothing the rule says so — `D08`
  (`ChordWithoutTemplate`), ONE summary per arrangement, because a per-chord
  finding repeats itself once per difficulty level.
- **TWO RULES THAT JUDGE THE SAME ELEMENT MUST LOOK IT UP THE SAME WAY.**
  `HandShapeRule` (I46) read a shape's template through the raw id index with no
  negative guard; `HandShapeContentRule` (E05) read it through
  `Arrangement.FindChordTemplate`, which is null for any negative id. Both
  `HandShape.TemplateId` and `ChordTemplate.Id` default to `-1`, so on an
  untemplated shape E05 went silent while I46 judged it against whichever
  UNNUMBERED template sat first in the collection — a false negative and a false
  positive on the same input, and the neighbour-excuse lookup turned the second
  into a third (an untemplated neighbour "covered the anchor with a first
  finger"). **Both now ask
  `ArrangementValidationContext.HandShapeTemplate(handShape)`**, `FindById`'s
  rule wins, and the null case is reported as `D09` (`HandShapeWithoutTemplate`)
  rather than skipped. `HandShapeRuleTests` pins all three.
- **A HAND SHAPE is a USE of a chord template, and the fingering rules must see
  it.** `TemplateAndOverlapRule.ValidateTemplates` built its first-use map from
  `level.Chords` only, so L06/L07/L08 could not structurally reach a template no
  chord points at — which is exactly the let-ring shapes
  `GpHandShapeBuilder.FindOrCreateTemplate` mints, whose fingering is guessed by
  `ChordTemplateFingerSetter.SetSuggestedFingers` (74.6% exact). The shapes most
  likely to be wrong were the only ones exempt from the check for it. Chords are
  still swept first, so a template used by both anchors where it always did;
  `TemplateAndOverlapRuleTests` pins both halves. **A FULLY fret-hand-muted chord
  is no use of a grip (#599)** — EOF's rule with its default
  `eof_fingering_checks_include_mutes = 0` (`main.c:240`): muted strings are not
  inspected and a note with only open/muted strings and no fingering is valid
  (`rs.c:3628-3631`). So L06/L07/L08 skip such a chord's use; a template a
  sounding chord or a hand shape also plays is still judged there
  (`SplitChordFindingsTests`). **A `ChordTemplate.Fingerless` template is not
  judged at all (#609)** — EOF's Fingerless eflag (`song.h:137`), whose fingering
  `eof_pro_guitar_note_fingering_valid` calls valid before it looks at a string
  (`rs.c:3593-3596`). Generate fingering (`FingeringGenerationService.NeedsFingering`)
  leaves it alone too; a hand-set fingering clears the flag
  (`SetTemplateFingersAction`, `ChordTemplateEditPlanner.ApplyEdit`). Nothing
  else honours it: Refret, the FHP follower and sound sync copy or mint fingers
  as before (the follower leaves a fingerless grip alone only because it has no
  fingers). `FingerlessTemplateTests`.
- **D11 (`ChordOutsideHandShapeRule`, #479) and its auto-fix share ONE coverage
  predicate** — `Editing/HandShapes/ChordHandShapeCoverage` answers exactly
  `HandShapeCoverageIndex`'s question, which is what
  `ChordHandShapePlanner` asks before it plans a bar — so the fix
  (`ArrangementAutoFixService.CoverChordsOutsideHandShapes`, the "Generate hand
  shapes" planner over every level) is a fixed point by construction
  (`ChordOutsideHandShapeRuleTests.AutoFix_…_IsAFixedPoint`). The span is
  HALF-OPEN, so a chord ON a shape's end is outside it, and that is the only case
  any corpus contains: 670 in `TestData/RsCharter`, 247 (46 findings) in 691
  official arrangements, 0 in `TestData/EOF` and the owner's verified charts —
  DLC Builder's retired I19 ("looks ugly"), which it now fixes by LENGTHENING
  the shape. Hence a Warning. The fix ADDS a bar after such a shape rather than
  lengthening it (the planner never touches an existing bar). The finding is one
  row per chord POSITION across levels, pointing at the highest level's chord.
  **A chord with `SplitIntoNotes` is never uncovered (#599)**: it exports as one
  note per string with no chord frame (`LinkLandingBreakup`), so it needs no
  shape, and a bar over it would give it a chord box in the game. The predicate
  (`ChordHandShapeCoverage.NeedsShape`) and the planner (`ChordHandShapePlanner`
  skips it as a run start and breaks a run at it) agree, so neither D11, Generate
  hand shapes nor the D11 auto-fix ever wraps a split chord
  (`SplitChordFindingsTests`).
- **Validation is allocation-conscious because it re-runs after every history
  step.** Two shapes to avoid, both measured in `ChordRule`: re-answering a
  question about a SHARED `ChordTemplate` once per chord instead of once per
  template (six LINQ allocations each, five of them `OrderBy` buffers), and
  `foreach` over an `ObservableCollection<T>` — it derives from `Collection<T>`,
  exposes no struct enumerator, and therefore BOXES one per iteration site
  (40 bytes × every chord). Index instead.
  `ChordRuleTests.ValidatingManyChordsOverOneTemplate_DoesNotAllocatePerChord`
  compares a 2,000-chord pass against a 50-chord one with
  `GC.GetAllocatedBytesForCurrentThread` — thread-local, so it is immune to the
  rest of the suite, and sharp enough that a single boxed enumerator failed it.

- **The rules and the export gate keep NO state between calls, and the quality
  report depends on it (#690).** (The report DIALOG was removed in #497: its
  verdict, counts, per-arrangement counts and difficulties and the Editor on Fire round trip moved to the Issues
  tab, `ExportReadinessViewModel.ChartReport`, which runs the reporter over a snapshot off the UI thread; the
  reporter, the snapshot and the copies below stay for it, the AI chat and their own tests. The dialog-bound names in this
  entry, `QualityReportChecks` and `ChartQualityReport*Tests`, no longer exist.)
  The dialog ran `ChartQualityReporter.Build` OFF the UI thread on a `QualityReportSnapshot`
  copy while the Inspector validates the live chart on the UI thread with the
  same singleton `IArrangementRule`s. Every rule's only fields are `const`s,
  `ArrangementValidationService` holds a logger and a rule array,
  `ChartExportValidator` is new per call, and nothing in `Core/Domain` caches
  lazily. A rule that gains a scratch field or a static cache breaks that
  report. Its findings name the COPY's elements: `QualityReportSnapshot.Rebind`
  maps them to the live chart by position (an element that moved is dropped, a
  chart whose arrangements or levels changed gets no report), pinned by
  `QualityReportSnapshotTests`.

- **The quality report's COPY waits for the busy row, and reuses the last one
  (#711).** Recheck only raises `IsChecking`; the assemble + copy runs in
  `QualityReportChecks.AfterPaint` (`IUiDispatcher.Post`, background priority),
  and a step queued behind a newer check or a closed dialog copies nothing. The
  copy is `QualityReportCopies.Copy`: the spare a LANDED check gave back
  (`QualityReportSnapshot.Release`, after the rebind) restored with
  `ChartProjectRestorer.Restore`, or a clone when there is none — never a
  second field list, so `ChartProjectRestorerCoverageTests` (including
  `RestoreOntoAPopulatedGraph_SharesNoReferenceWithTheSnapshot`) guards it. A
  copy a check is still reading is never handed out; the spare is held while a
  report is open and weakly after. Restoring is ~45 % cheaper than cloning but
  still 13–31 ms on the corpus's biggest 20-level ladders (measured
  2026-10-11 on a contended box, `EofRoundTripCostProbeTests`), paid on the UI
  thread after the busy row has painted; the clone of the first check in a
  session is 36–73 ms. Pinned by
  `QualityReportCopiesTests`; the report over a restored spare equals the
  report over a clone on all 40 corpus charts
  (`EveryCorpusChart_ReportsTheSameOverARestoredSpare`, env-gated).

A finding's sentence is a `Validation_*` resx key plus arguments (`ArrangementIssue.MessageKey`/`MessageArgs`, built by `ArrangementIssues.Make`); `Message` stays the English text read from the same resource. Surfaces that show it call `ArrangementIssueText.Of(issue, localization)`; logs, reports and AI prompts keep `Message`.

## Chord fingering conventions

No rule judges a grip's fingering. A still-guessed fingering is NOT a finding (D100 is gone), and the
same frets fingered differently by two templates is NOT a finding (D101 is gone, #1023): a hand that
sits differently fingers the same shape differently; EOF, DLC Builder and Charter flag neither.
Pinning a grip (`SetTemplatePinnedAction`, undoable: "Pin grip" on its Chord shapes card, or "Pin all
grips", `ChordShapes/GripPins`, one undo step) only keeps automatic fingering off it; the Issues tab
offers no Fix for it. Generated chord names spell a major seventh `Maj7` in BOTH
naming styles, and an ESTIMATED key in a down-tuned arrangement takes the flat enharmonic
(`ChordKeyEstimator.FlatSpelling`: G#m becomes Abm); authored names and authored keys never change.
"Name only the first chord of a hand shape" (`AppConfig.NameOnlyFirstChordInHandShape`, on;
`Editing/ChordShapes/RepeatedChordNames`): Guitar Pro import (`GpPostProcessor.Run`) and Generate hand
shapes (`ArrangementHandShapeGenerator.BuildActions`) point a later chord that repeats the same NAMED
grip inside one hand shape at an unnamed twin (empty name, `Unnamed`, reused when the arrangement
holds one). Only chords the flow itself assigns move; loading never runs it. TWINS (`Editing/ChordShapes/ChordTemplateTwins.AreTwins`): two template
objects, exactly one with an empty name, otherwise the same grip (frets, fingers, ghost mask, arpeggio,
Nop and Fingerless flags), judged BEFORE an edit. An in-place fret or fingering edit to either one
(the chord card's `ChordTemplateEditPlanner.EditInPlace`, the Chord shapes panel's Suggest fingering)
carries onto the other through an `EditTemplateGlobalAction` in the same undo node
(`ChordTemplateTwins.Follow`), so the pair never drifts. A pitched slide on the second half of a tie merges into the tied note
(`GpPostProcessor.TieSlidesCompatible`), so a slide tied across a bar line imports as one slide.
