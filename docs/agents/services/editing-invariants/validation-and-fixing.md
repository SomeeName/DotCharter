# `Editing/` — the chart-model invariants (deep reference) — `Editing/Validation/` + `Editing/Fixing/`, the inspector and auto-fix

## `Editing/Validation/` + `Editing/Fixing/` — the inspector and auto-fix

The inspector (`ArrangementValidationService` over the `Rules/` set) and
`ArrangementAutoFixService` are two halves of one contract: the rule says what is
wrong, the fixer repairs exactly that and nothing else. Both re-run constantly —
validation on the inspector's 150 ms debounce after every history step, auto-fix
on demand and inside `BatchChartLintService`.

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
  `ChordTemplateResolver.FindById`, which is null for any negative id. Both
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
  report depends on it (#690).** `ChartQualityReportViewModel` runs
  `ChartQualityReporter.Build` OFF the UI thread on a `QualityReportSnapshot`
  copy while the Inspector validates the live chart on the UI thread with the
  same singleton `IArrangementRule`s. Every rule's only fields are `const`s,
  `ArrangementValidationService` holds a logger and a rule array,
  `ChartExportValidator` is new per call, and nothing in `Core/Domain` caches
  lazily. A rule that gains a scratch field or a static cache breaks that
  report. Its findings name the COPY's elements: `QualityReportSnapshot.Rebind`
  maps them to the live chart by position (an element that moved is dropped, a
  chart whose arrangements or levels changed gets no report), pinned by
  `QualityReportSnapshotTests` and `ChartQualityReportOffThreadTests`.

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
  session is 36–73 ms. Pinned by `ChartQualityReportCopyTests` and
  `QualityReportCopiesTests`; the report over a restored spare equals the
  report over a clone on all 40 corpus charts
  (`EveryCorpusChart_ReportsTheSameOverARestoredSpare`, env-gated).
