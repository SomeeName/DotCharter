# `Audio/LivePlay/` + `Audio/Tuner/` — the Live Play brain and the tuner core (deep reference)

Extracted verbatim from `DotCharter.Services/AGENTS.md`. **Read this before touching the session service, the note matcher, scoring/tiers, DD/phrase mastery, the take recorder or the tuner DSP.** The best-score invariant, the CQT-hit timing rules (§ timing tiers) and the one-string-at-a-time guided-tuning constraint are all load-bearing.

---

- `Audio/LivePlay/` — the pure Live Play session brain (`LivePlaySessionService`,
  `LiveNoteMatcher`, `SpeedLadder`, `DynamicDifficultyEngine`,
  `PracticeProfileService`), driven by ticks; no audio/dispatcher deps.
  **The matcher is three types (#650).** `LiveNoteMatcher` judges at run time
  (`Load`/`Reload` and their carry-over, `Rewind*`, `AdvanceTo`, `Offer*`,
  `Verifies`, grading); `LiveExpectedTimeline` BUILDS what it judges (the
  sorted `ExpectedSound` list from a level or a per-phrase selector,
  `BuildPhraseSpans`, `LevelIndexFor`, `ChordShape`); and the static
  `LivePitchAcceptance` holds the pure pitch rules (`Accepts`, `SoundingMidi`,
  `HarmonicOvertoneSemis`, `MaxBendSemis`, `NeedsTimbreEvidence`,
  `PitchIsUnreliable`). Pinned directly by `LivePitchAcceptanceTests` and
  `LiveExpectedTimelineTests`.
  **The detection pump is a DEDICATED WORKER THREAD (0.24)** —
  `LivePlayDetectionPump` (~33 ms cadence, drift-aware pacing: ticks aim at
  absolute cadence multiples and REALIGN instead of bursting after a stall;
  Start/Stop idempotent, Stop JOINS so no tick survives it — and since
  2026-08-29 the stop flag and wake event are PER RUN, so a Start racing a
  not-yet-joined Stop cannot reset the flag under the old worker; a
  replacement worker joins its predecessor before its first tick, which is
  what keeps `CoalescedUiChannel`'s single-producer contract even when the
  restart comes from inside a tick — concurrency audit #12,
  `LivePlayDetectionPumpTests.A_restart_from_inside_a_tick_never_overlaps_the_old_worker`),
  driven by
  `LivePlayViewModel.WorkerTick`. This is the architectural half of the
  2026-07-17 judging-inertness fix: any dispatcher timer (Background or Input
  priority) shares the UI thread and starves under render load, stretching tick
  gaps past `SeekJumpThresholdMs`; a worker thread cannot be starved by
  rendering. The worker also reads the playhead from `_audio.CurrentTimeMs`
  (designed for cross-thread polling), NOT `ProjectState.CurrentTimeMs` — the
  latter is the ≤60 Hz UI-coalesced copy, which stalls exactly when the UI
  does. **Threading contract (LivePlayViewModel):**
  - Session/matcher/analyzer/CQT/take-tee/rock-meter/calibration state is
    WORKER-CONFINED. The tick runs under `_sessionGate`; every UI-side session
    operation (Start/Stop via `IsActive`, loop commands, `BeginResumeRewind`,
    `ArmRockMeter`/`DisarmRockMeter`, `ArmFirstNoteCountIn`, `MarkRunFailed`,
    calibration arm/cancel) takes the same gate — synchronous on the UI thread,
    serialized against the tick. The gate is a plain monitor on purpose:
    `BeginGameplay` needs `IsActive = true` to start (or refuse) SYNCHRONOUSLY.
  - **Never stop the pump while holding the gate** — `LivePlayDetectionPump.Stop`
    joins, and the worker may be waiting on the gate (deadlock).
    `TearDownAnalysis` is only called from UI paths that hold nothing; `Start`'s
    failure path flips `IsActive` OUTSIDE the gate for exactly this reason, and
    the pump itself skips the self-join if a tick stops its own pump.
  - Worker → UI flows ONLY through ONE coalesced snapshot per tick
    (`CoalescedUiChannel<TickUiState>`, the `AudioPlaybackCoordinator` pattern
    generalized): payload-replace coalescing — an unapplied snapshot is
    superseded, scalars keep the newest value, list payloads (monitor lines,
    miss entries) are merged forward, a `_uiEpoch` stamp (bumped by Start/Stop)
    drops stale snapshots across run boundaries (calibration outcomes exempt).
    Never set an observable from the worker.
  - Audio calls the worker makes directly — verified engine-safe off-UI:
    `ReadInputCapture*` (worker = the single streaming consumer),
    `TryScheduleClick`/`TryScheduleCalibrationClap` (coordinator already calls
    from the position-timer thread), `PlayMetronomeTick`/`PlayClap`
    (trigger-now), `SeekTo` on loop wrap (snapshots sources, catches disposal
    races; its synchronous `PositionChanged` fires on the worker, which the
    coordinator handles cross-thread). Speed changes are NOT audio-safe-by-fiat:
    they ride the snapshot to the UI (`PlaybackSpeedChangeRequested` keeps the
    speed slider in sync).
  - Two pre-existing lock-free channels bypass the snapshot by design: the 3D
    feedback queue (`FeedbackEvents`, a `ConcurrentQueue` the render driver
    drains) and the dead-note horizon (`ResolvedThroughMs`, atomic bits).
    `RockMeterFailed` (on `LivePlayViewModel.Meters`, a
    `LivePlayMetersViewModel` that takes no lock of its own — every call into
    it is made under `_sessionGate`) keeps its own one-shot
    `Dispatcher.UIThread.Post`.
  **Best-score invariant:** `PracticeProfileService.Record` keeps the MAX per
  (song, arrangement) — a worse run NEVER lowers a stored best/accuracy/streak,
  but is still appended to `Sessions` (history). **The same bests are kept PER
  DETECTOR** (`PracticeArrangementProfile.DetectorBests`, keyed
  `LiveDetectorModes.StoreKey`: "Classic"/"Cqt"; #329) and each session record
  carries its `Detector`, stamped by `LivePlaySessionService.Stop` from the matcher's
  mode. Both are null on stores written before 0.64.0 = UNKNOWN, never either mode. "Best" is ranked by SCORE alone
  (accuracy tracked separately), matching the local leaderboard ordering. The pure `RunBestComparison` in
  `LivePlaySessionReport.cs` is the single source of truth for the "new best /
  by how much" verdict; `report.Comparison` drives the results screen.
  **`BestAccuracy` additionally needs COVERAGE** (`PracticeProfileService.
  MinCoverageForBestAccuracy`, 0.5, via the one predicate
  `QualifiesForBestAccuracy`): `Accuracy` is `Hits / (Hits + Misses)` over the
  JUDGED sounds only, so two notes played cleanly and quit used to store `1.0`
  forever — and that value is the grade badge (`GradeCalculator.FromAccuracy` in
  three places) and the leaderboard's secondary rank. The gate only ever
  declines to RAISE, so the
  invariant above is untouched; the run is still recorded in `Sessions` and still
  folds into the coverage-weighted `MasteryPercent`. `RunBestComparison.Evaluate`
  takes `runCoverage` (REQUIRED, not defaulted) and
  `LivePlaySessionReport.IsNewBestAccuracy` applies the same predicate, so the
  results screen cannot celebrate a record the store refused.
  **A CHORD'S SHAPE MAY LIVE IN THE TEMPLATE, NOT IN THE CHORD.**
  `Chord.ChordNotes` is allowed to be EMPTY, with `Chord.TemplateId` naming the
  shape — which is exactly what RS-XML does (`chordNote` children are written
  only for strings that carry per-note techniques, so an ordinary strum has
  none, and `RsXmlSongReader.ReadChord` does not expand the template). The
  matcher read `ChordNotes` and nothing else, so such a chord reached judging
  with an EMPTY `MidiNotes`, and `Verifies` answers an empty pitch set with a
  flat `false`: **every templated chord in an RS-XML-imported arrangement was an
  automatic Miss**, in the candidate path AND in the CQT path (`LiveNoteMatcher.
  AdvanceTo` routes a pitchless sound to the timbre path, and `LiveCqtVerifier.
  Verify` returns `false` for an empty note set anyway). No amount of correct
  playing could score one. `LiveExpectedTimeline.ChordShape` now falls back to
  `Arrangement.ChordTemplates` — the chord's OWN notes always win, a dangling
  `TemplateId` degrades to the old behaviour instead of throwing. Pinned by
  `ChordShapeVerificationTests`. **The chord-level `palmMute`/`fretHandMute`
  attributes of such a chord are still lost at the PARSER** (the domain `Chord`
  has nowhere to put them, and `ReadChord` only propagates them onto chordNotes
  that exist) — a template-only palm-muted chord therefore reads as un-muted,
  which the percussive veto turns into a Miss when the player mutes it. That
  fix belongs in `Parsing/Xml/Song/RsXmlSongReader`, not here.
  **A chord tone accepts the same physics-shaped variants a single note does.**
  `LivePitchAcceptance.Accepts` is the ONE predicate both paths use: the chart pitch,
  ±1 octave (detector error; off in Strict and in string-aware single-note mode),
  the range a slide traverses, the range a bend reaches, and a harmonic's real
  overtones. The chord path used to open-code a narrower `d == 0 || |d| == 12`
  test, so a natural-harmonic strum — which sounds at **+19/+24**, not +12 —
  verified none of its own tones. A strum slides as ONE SHAPE, so the chord form
  of a slide is the signed displacement `ExpectedSound.SlideSemis`, never the
  absolute `SlideToMidi` (which would widen the upper tones down towards the
  lowest tone's destination); the two are never both set, and `LiveTechniqueTally`
  counts either as `PlayedTechnique.Slide`. `Accepts` is also a predicate rather
  than the `HashSet` of accepted pitches it replaced — that set was rebuilt for
  every candidate sound of every detected attack, i.e. an allocation on the
  detection path.
  **A loop pass re-arms from the loop START, not a hit window before it** —
  `LiveNoteMatcher.RewindToLoop`, which is why it is a different method from
  `Rewind`. Rewind's horizon is `playhead − HitWindowLateMs` because a SEEK
  lands the player mid-window; `ArmLoopPass` used it, so up to 250 ms of chart
  OUTSIDE the region came back armed — and that band sits inside the count-in
  pre-roll, where `Offer` refuses to judge at all. `AdvanceTo` expired every one
  of those sounds as a miss the moment the pre-roll ended, once per pass, into
  the run's accuracy, the "you missed these" list, and the rock meter's drain,
  which no rewind undoes. `LoopPassPreRollMissTests` pins it, including the half
  the old horizon got RIGHT: the loop's own first sound is still re-armed by a
  wrap.
  **A note or chord marked `Ignore` is not an expected sound** —
  `LiveExpectedTimeline.AppendLevelRegion` skips it, so it is never judged, never a
  miss and never in `TotalSounds` or the accuracy denominator (Rocksmith's rule;
  the SNG exporter leaves them out of `MaxNotesAndChordsReal`).
  `ChartSpacingEnforcer` sets the flag itself on fret 23+ and sustained 7th-fret
  harmonics, so judging them made unplayable notes guaranteed misses. The
  highway still draws them: it reads the chart, not `Matcher.Expected`. Only the
  CHORD's flag counts — a chord note's own `Ignore` leaves the strum judged, as
  the SNG chord mask does. Pinned by `LiveNoteMatcherIgnoreTests`.
  **ONE attack resolves EVERY expected sound at the same chart time**
  (`LiveNoteMatcher.SimultaneousToleranceMs`, 1 ms) — not just the first.
  A double stop is one onset carrying both pitches, but the chart may spell it
  as two separate `note`s rather than as a chord, and RS-XML, EOF and the
  Guitar Pro importers all do. `OfferPitches` used to judge the first and
  `return`, and the sibling was never offered anything else (there IS no second
  onset), so `AdvanceTo` expired it: a perfectly played double stop scored 50%
  and broke the streak, every time. Each sibling is still verified on its own
  evidence, so one string sounding still misses the other. The extras ride an
  optional `alsoJudged` list — `LivePlaySessionService.Offer` CLEARS it and
  tallies every judgment whether or not the caller passes one (the since-removed
  couch co-op seats did not), so only the HUD/feedback surfacing depends on it. Pinned by
  `SimultaneousSoundJudgingTests`, including the negative: a note a 16th later
  is inside the same hit window and must still be earned separately.
  **A BASS sounds an OCTAVE below where it is charted, and
  `LivePitchAcceptance.SoundingMidi` did not know that until 2026-09-04.** It
  anchored every arrangement at E2 (`LowEMidi = 40`) while `SoundingPitch.MidiFor`
  (Core, what the SNG mapper and the inspector use) and `ArrangementTunerLogic`
  (bass anchor E1 = 28, what the guided check tells the player to TUNE to) both
  subtract 12 — so live judging graded a bass against notes the instrument
  cannot make. The classic candidate path hid it behind its ±12 detector-error
  tolerance; **the two paths that deliberately DROP that tolerance did not.**
  With `StringAwareVerification` on, every bass note missed. In CQT mode it was
  worse than a mismatch: the played E1's second harmonic lands exactly on the
  charted E2 bin, so `JuceChartVerifier`'s sub-octave veto — which exists to stop
  a player scoring by playing an octave DOWN — fired on the correct note and
  refused it. `LiveCqtVerifier.LiveMinMidi` was widened to 28 "for bass" while
  nothing ever asked it for a pitch that low, which is the tell that the two
  halves were never joined up. `SoundingMidi` also read `TuningPitches` RAW: a
  chart that states its tuning as note LETTERS only (RS-XML always writes
  offsets, other importers do not) was judged as E standard. Both go through
  `TuningOffsetResolution.For` now. Pinned by
  `BassSoundingPitchJudgingTests` — including the agreement with
  `SoundingPitch.MidiFor` and with `TuningCheckTargets`, so the tuner and the
  matcher can no longer disagree by an octave. **The same octave comes back
  through the tuning REFERENCE (#161):** a 5/6-string bass charted on a guitar
  path at A220 (`CentOffset` −1200) sounds an octave below its guitar offsets, so
  `SoundingMidi` adds `ArrangementInstrument.ReferenceOctaveSemitones` (its low B
  is 23, not 35); `TuningCheckTargets` and `ArrangementTunerLogic` fold that
  octave into the NOTE (named B0) and keep only the residual cents as an offset,
  and `LiveTuningDriftMonitor.Reset` folds every whole semitone out of the offset
  before comparing it with a nearest-semitone reading (−1200 ¢ read as 1200 ¢
  of drift). `GuitarPathBassListeningTests`. **The caveat this created** — a
  5-string bass's low B (MIDI 23) and a drop-tuned bass sat BELOW
  `LiveCqtConfig.MinMidi = 28`, so in CQT mode they were an automatic Miss — is
  closed since 0.57.4: `MinMidi = 21` (A0), `BinCount = 158` (A0..E7). A
  B-standard bass part measured 12.4 % → 77.7 % on the chart eval
  (`LiveDetectionAccuracyTests.ABStandardBass_LowString_IsHeard`).
  **Timing tiers (0.16 item, shipped in the 0.14 cycle):** hits grade a
  `LiveTimingTier` (Perfect/Great/Good) from |detected − expected| — event
  times are latency-offset-compensated by the UI BEFORE `Offer`, so tiers
  measure player timing. Tier-weighted scoring lives in `LivePlayStats`
  (100/80/50 base; technique mismatch still halves); an UNTIMED hit (tier None)
  pays the full base. Tier is stored on `ExpectedSound` next to the verdict so
  rewind/reload replays stay deterministic.
  **CQT hits are timed from the attack detector's onsets (#329, 0.64).** The CQT
  verifier confirms WHAT was played, never WHEN; the analyzer's attack events still
  reach `OfferPitches` in CQT mode, with the same latency-compensated time the classic
  path grades from, and `LiveAttackOnsets` keeps them. A confirmed sound is graded
  from the onset that times it best: nearest its chart time inside
  [chart − `HitWindowEarlyMs`, chart + `AttackWindowLateMs`] (the PRESET's late edge,
  250 ms on Normal — not the CQT's ~378 ms verdict horizon), an onset whose pitch
  candidates do not name the sound counting `PitchMismatchPenaltyMs` (100) further
  away (else a player consistently late in a fast run is graded against the NEXT
  note, and told "early"). An onset times one attack: it is claimed by the chart
  time it timed, shared only by sounds at that same time (a double stop). A timed
  CQT hit carries the ONSET as its attack time, exactly like a classic hit. No onset
  in the window keeps tier None.
  **The ORDER is the trap.** The analyzer REPORTS an onset ~180–250 ms after the
  attack (`JuceGuitarAnalyzer.LatencySamples` = 8,392 samples at 44.1 kHz), the CQT
  confirms ~100–190 ms after the note. Pairing only at confirmation found NO onset
  for any on-time note (0 of 8 timed, measured). **And the hit may NOT wait for it**
  (owner rule: timing never makes hit feedback later — holding the credit showed
  every CQT hit ~200 ms after the attack instead of ~110 ms). So the factory's CQT
  matcher has `AwaitAttackOnsets`: a confirmation is CREDITED at once, as before
  #329 — "Hit", streak, combo, meters, 3D marker — untimed
  (`ExpectedSound.AwaitingGrade`), and the onset GRADES it when it is reported: a
  `LivePlayGrade`, out of `OfferPitches`/`LivePlaySessionService.Offer` in their own
  `graded` list. **A grade is not a judgment and is a different type on purpose**:
  every `LivePlayEvent` consumer counts one event as one judged sound, so the VM's
  `OnGraded` feeds only the timing collector (`AsTimedHit`), the EARLY/LATE label
  (`TickUiState.HasTiming`, separate from `HasVerdict`) and the 3D tier/direction
  cue (`LivePlayFeedbackEvent.IsGrade`: the 3D driver's `LivePlayFeedbackDrain` adds no
  second marker and no second crowd-meter verdict for it — `UI/LivePlayFeedbackDrainTests`). The tier counters ride the
  stats. **Score never moves down:** the credit pays `LivePlayStats.CreditFloor`
  (Good's 50, halved for a technique mismatch) at its multiplier; the grade pays the
  rest of its tier's base at that SAME multiplier (Perfect +50, Great +30, Good +0);
  a hit no onset graded is paid the rest of the untimed 100 when its window closes
  (nothing emitted — it was shown already). The total is exactly `BaseScore(tier)`.
  **Nothing drops a credited hit:** `LivePlaySessionService.Stop` calls
  `ResolvePendingGrades` (untimed), a seek that KEEPS the hit resolves it untimed
  (`Rewind`/`RewindToLoop` clear `AwaitingGrade`, `RecomputeFrom` pays the full
  base), one cleared by a seek back goes with its verdict, a loop pass counts it,
  and `Reload` carries it untimed. Onsets are forgotten on every seek and re-arm.
  `LiveNoteMatcherCqtTimingTests` (credit then grade, tiers, the edge, the fast
  run, the double stop, seeks, the score scheme, the calibrator),
  `LivePlayCqtGradeLifecycleTests` (stop, loop wrap, no double tally),
  `LivePlayGradeRoutingTests` (the VM routes a grade to timing only), and
  `CqtTimedJudgingEndToEndTests` through the real analyzer + CQT: 8/8 timed at 0
  and ±60 ms, median within 2 ms of the played offset, and every hit shown no later
  than the CQT's own confirmation tick (median 89–168 ms after the attack, graded
  210–223 ms after it); with the hold put back every hit fails that check.
  **The CUE comes from the PROVISIONAL onset (#643); the grade still comes from the
  finalized one.** `JuceGuitarAnalyzer.Push(samples, notices)` reports each attack
  ~20 ms after it as a `JuceOnsetNotice`; `GuitarInputAnalyzer.OnsetNotices` maps them
  (`LiveOnsetNotice`: `DetectorSamplePosition` is the pairing KEY,
  `StreamSamplePosition` the attack estimate) and withdraws its outstanding ones itself
  on a ring-skip `Reset`, which the analyzer drops silently. The VM offers them FIRST
  each read (`LivePlaySessionService.OfferOnsetNotice`), then the events with
  `LiveDetectionEvent.DetectorSamplePosition`. `LiveNoteMatcher` keeps them in a SECOND
  `LiveAttackOnsets` (no pitch, keyed; never mixed with the finalized set). A hit
  credited untimed takes the best provisional onset as a CUE —
  `LivePlayGrade.Stage = Provisional` in `LivePlayTickResult.Grades` with the credit, or
  out of `OfferOnsetNotice` when the onset comes after it; `ExpectedSound.ProvisionalAttackMs`
  remembers it. The finalized onset (it retires its provisional one by key; a
  `Withdrawn` notice retires a rejected one) grades as before and is `Confirmed` (same
  tier AND side: `ShowsCue` false, no second flash) or `Corrected` (shown). **Only the
  finalized grade pays, counts a tier or reaches the timing collector** — `OnGraded`
  skips `_timing` for a provisional grade — so no stat moves; a cue nothing finalizes
  stays on screen and the hit resolves untimed as before. `LiveNoteMatcherProvisionalOnsetTests`
  (cue at credit, after credit, correction by tier and by side, withdrawal, key
  retirement, seek, classic no-op, stats identical with and without),
  `GuitarInputAnalyzerOnsetNoticeTests` (key pairing, the ring-skip withdrawal),
  `LivePlayGradeRoutingTests` (a provisional grade never reaches the collector),
  `CqtTimedJudgingEndToEndTests.ProvisionalOnsets_PutTheTimingCueOnTheHit_AndMoveNoStat`
  (real analyzer + CQT: cues at the credit tick, stats identical).
  `tools/detection-eval` pumps them as the VM does (`--noprovisional` turns them off)
  and prints when the cue and the finalized grade landed and each grade's stage.
  Measured 2026-10-12 (Guitar-TECHS `--quick`, CQT): hit rates and every tier count
  identical with and without; the cue lands a median 117 ms after the note instead of
  185 ms; 681 of the 950 late-graded hits had an early cue, 570 confirmed (the finalized
  attack moved 0 ms, median and p90) and 111 corrected (moved a median 53 ms). Not
  every attack is cued early: the analyzer publishes only attacks the audio already
  shows (the flux detector also fires on the ripple of a ringing note).
  **Bends, slides and vibrato are judged from the pitch CONTOUR — one switch per technique;
  bends ON by default, slides and vibrato OFF (#645, #730).** `AppConfig.LivePlayJudgeBends` /
  `LivePlayJudgeSlides` / `LivePlayJudgeVibrato` (Play Mode settings ▸ Gameplay, each disabled
  while the CQT verifier is off) fold into `TechniqueContourJudge.Switched` →
  `LiveNoteMatcher.JudgedTechniques`; any switch on wires `LiveCqtVerifier.JudgeTechnique` into
  `LiveNoteMatcher.JudgeContour`; none on, the matcher judges exactly as before (pinned). A
  switched-off technique is NEVER judged: `LiveTechniqueChecks.Add` narrows the sound's
  expectation to the switched-on set, so a bend with vibrato (vibrato off) is ruled on the bend
  alone and does not wait for the vibrato's evidence (`LiveNoteMatcherTechniqueSwitchesTests`).
  The 0.68 single switch `LivePlayJudgeTechniqueContours` is read once by
  `JsonConfigurationService.NormalizeAppConfig` (`true` → all three on; `false` was the default →
  the new defaults) and never written back (`TechniqueJudgingSwitchesConfigTests`). The
  contour is the verifier's own CQT, no new DSP: `ReadContour` takes the loudest quarter-tone
  bin per hop in the band the note may travel (a semitone either side of the slide's range and
  the bend's peak), parabolic-interpolated, NaN where the bin floor or the presence gate fails.
  The pure `TechniqueContourJudge` rules Met / NotMet / Unknown on what
  `LiveExpectedTimeline` charted into `ExpectedSound.TechniqueContour` (single notes only — a
  chord, a harmonic, an unpitched slide asks nothing; the window ends at the next sound): a
  bend's rise reaches 70 % of its step by the charted peak + 250 ms; a slide travels half its
  interval without first going 50 cents the wrong way; a vibrato shows two peaks of 6-cent
  swings after an 800 ms detrend. **Anything it cannot see is Unknown, a full hit.**
  **The credit never waits for the ruling** (owner rule: judging never makes hit feedback
  later): `LiveTechniqueChecks` holds the credited hit, PAID as a mismatch
  (`ExpectedSound.PaidVerdict`), and rules once the playhead passes its evidence (asking again
  while the audio has not reached the CQT, Unknown after 1.5 s): NotMet makes it a
  `HitWrongTechnique` — half score, NEVER a miss — and Met/Unknown pays the rest
  (`LivePlayStats.UpgradeTechnique`), so the score never moves down. A seek, loop pass or reload
  ends a pending check Unknown; `Stop` rules on what was heard. The rulings ride
  `LivePlayTickResult.TechniqueJudgments` — NOT judgments: a NotMet turns the HUD's verdict to
  "Hit (check technique)", as a palm mute played open does; the streak stands, but the rock and
  crowd meters (HUD and 3D background) give back the hit's extra over a half hit at the streak it
  was credited at (`LiveTechniqueJudgment.StreakAtCredit`, `RockMeter`/`CrowdMeter.OnTechniqueDowngrade`)
  and the 3D highway flashes a wrong-technique marker at the ruling
  (`LivePlayFeedbackEvent.ForTechniqueDowngrade`, #730; `TechniqueDowngradeMeterTests`,
  `LivePlayTechniqueDowngradeTests`). `LiveTechniqueTally` counts it (`PlayedTechnique.Vibrato` appended), and both results
  screens print the technique line (`ResultsTechniqueLine`). Tests:
  `TechniqueContourJudgeTests`, `TechniqueContourExpectationTests`, `LiveCqtTechniqueContourTests`
  (synthesized tones through the streaming CQT), `LiveNoteMatcherTechniqueContourTests`.
  **Measured 2026-10-12, real DI** (`tools/detection-eval --techniques`, Guitar-TECHS; full hit =
  Met + Unknown of the credited sounds):

  | | credited | met | not met | unknown | full hit |
  |---|---|---|---|---|---|
  | bends played (P1 / P2) | 33 / 21 | 75.8 / 100 % | **0 / 0 %** | 24.2 / 0 % | **100 / 100 %** |
  | vibrato played (P1 / P2) | 132 / 108 | 80.3 / 32.4 % | 7.6 / **67.6 %** | 12.1 / 0 % | 92.4 / **32.4 %** |
  | full-step bend charted, not played (singlenotes P1 / P2; scales P1 / P2) | 141 / 136; 3,449 / 3,342 | 0 / 0; 13.8 / 13.4 % | 97.9 / 100; 85.3 / 83.5 % | | |
  | two-fret slide charted, not played (same) | same | 0 / 0; 3.9 / 4.3 % | 99.3 / 100; 95.6 / 93.1 % | | |
  | vibrato charted, not played (same) | 141 / 136; 3,348 / 3,244 | 0 / 0; 0.1 / 0.1 % | 85.8 / 97.8; 79.1 / 80.7 % | | |

  Bends pass the bar; **vibrato does not**: the CQT's long kernels (to ~170 ms) smear a 5 Hz
  vibrato, the contour of P2's (subtle) vibrato swings ~12–19 cents against 2–6 for a held note,
  and two of three of P2's vibratos still read NotMet at the most lenient threshold that keeps
  held notes at 0 % (the issue's 20 cents: P1 44 %, P2 0 % met). The 6-cent swing and the
  800 ms detrend are that measurement (`TechniqueContourJudge.VibratoMinSwingCents`). Guitar-TECHS
  has no slides, and neither does any other local set (GuitarSet: acoustic, no technique labels;
  EGFxSet: isolated single notes; the `TestData` corpus: full mixes) — so slides stay off by
  default until a real slide recording is found; the rendered-chart run (`--charts --quick`, 12 real RS charts, synthetic glides — relative only) credited 173 played slides 73.4 % Met / **26.6 % NotMet** and 80.3 % of unplayed ones NotMet, so slides do not pass either (bends there: 37/37 Met, 97.3 % of unplayed NotMet; vibrato: 80 % Met + 20 % Unknown). A scale's fake bend reads Met 13 % of the time — the next scale
  step a whole tone up rings into the window; a false Met only fails to punish.
  **The latency calibrator now learns from CQT runs too** — the deltas are the same
  onsets a classic run teaches it with; untimed hits and credits still teach nothing.
  **Early/late direction (0.19):** tiers grade `|delta|`, so a 60 ms rush and a 60 ms
  drag both read "Great". `LiveTimingDirection` (None/Early/OnTime/Late) carries the
  SIGN, classified by the pure `LiveTiming.Classify` and shipped on
  `LivePlayEvent.Direction` from the two honest judge paths (`OfferPitches`,
  `OfferChroma`). **Informational only — it never touches scoring**, which stays
  magnitude-only. The on-time band is `LiveTimingReport.OnTimeWindowMs` (15 ms),
  deliberately NOT the tier windows (those are preset-scaled, and the hit window is
  asymmetric 150/250).
  **The gate is the whole subtlety.** A MISS and an UNTIMED CQT hit both carry the
  playhead at judgment (a window expiry or a confirmation), not a detection —
  signing it would label every one of them a ~250 ms "late". Both classify as `None`, and `AdvanceTo` calls `Classify` explicitly rather
  than letting the parameter default, so the gate is exercised rather than incidental.
  **…and `LiveTimingCollector.Add` is a consumer, which it did not act like until
  2026-09-04.** It filtered on the VERDICT alone, so every (then always untimed) CQT hit
  contributed a delta of `playhead − chartTime` ≈ `+HitWindowLateMs` (≥ 230 ms in
  that mode). The results screen therefore told a perfect CQT run it was 230 ms
  late — and the damage OUTLIVED the run: a whole run's fake deltas are
  near-identical, so `StdDevMs` came out small and passed
  `LatencyBiasCalibrator`'s consistency gate, which exists to reject exactly
  this kind of non-latency signal. The calibrator then moved the device's
  PERSISTED offset by `0.4 × 230 ms`, leashed only by
  `MaxDriftFromBaselineMs`, and that offset is read back by every later run on
  that device including non-CQT ones. Its own refusal message already said "no
  timed hits"; the guard is what makes that word true.
  `LiveTimingAnalyticsTests.AnUntimedHit_ContributesNoTiming` and
  `.AnAllUntimedRun_TeachesTheLatencyCalibratorNothing` pin both halves.
  (Two opt-in flags gate the persisted half — `LivePlayUseCqtVerifier`
  and `LivePlayAutoCalibrateTiming` — but they are exactly the two a player
  chasing timing accuracy turns on.)
  **Consumers must render `None` as NO label at all, never as on-time.** Surfaced as
  the HUD's EARLY/LATE popup and as a bounded depth nudge on the 3D hit flash
  (`LivePlayFeedbackRenderComponent.TimingNudgeZ` — saturating on purpose: the exact
  `GetZDepthForTime` of a legal 250 ms late hit is −5 world units, i.e. behind the
  camera at −2.5).
  **FC + per-phrase mastery (0.20/0.21 items):** `LivePlaySessionResult.IsFullCombo`
  / `SectionResult.IsFullCombo`; `PracticeArrangementProfile` persists the
  sticky `FullComboAchieved` badge, `BestRun*` FC/tier metadata (follows the
  score-ranked best; nullable = unknown on pre-0.20 data) and `PhraseMastery`
  (`PhraseMasteryRecord`: DD ladder level — persists DOWNWARD too, it is
  ladder state, not a best — plus an EMA accuracy). The DD ladder resumes via
  `LivePlaySessionOptions.InitialPhraseLevels` → `DynamicDifficultyEngine.Seed`;
  `LivePlaySessionService.LastPhraseOutcomes` hands results back at Stop.
  **Dynamic difficulty needs a LADDER: `DynamicDifficultyEngine.CanLevel` is
  `Levels.Count > 1`.** On one level the matcher clamps every phrase to it, so
  the engine would move and persist levels that select nothing. A ONE-level
  chart (most CDLC) therefore runs on a ladder generated IN MEMORY for that run
  (`DynamicDifficultyLadder.For`: a clone, `LevelGenerationService` with
  `GeneratedLevels` = 8, the chart untouched, #295) and the strip labels it
  "Generated ladder"; only a chart with NO notes shows the toggle disabled with
  its reason (`LivePlayViewModel.CanUseDynamicDifficulty` / `IsDynamicDifficultyOn`
  / `IsDynamicDifficultyGenerated` — the stored preference is kept). Psarc and
  RS-XML imports keep their whole ladder since #323 ([parsing-psarc.md](parsing-psarc.md)), and
  every level is the whole song, so a level the ladder picks never drops a
  phrase. **The 3D highway draws what is judged (#382):** `PublishGameplayLevel`
  builds `DynamicDifficultyComposite` (per phrase span, the picked level's
  sounds, through `LiveExpectedTimeline.LevelIndexFor` — the matcher's own rule) at
  run start and on every `DifficultyChanged`, and swaps it into
  `ProjectState.GameplayLevel`, which `Preview3DRenderDriver` reads (via
  `ChartEditorViewModel.GameplayLevel`) instead of the active level; Stop clears
  it. The 2D timeline keeps the editor's level. Spans the playhead already passed
  keep the level the matcher froze them at, while the composite shows the
  current pick everywhere: they are behind the strike line.
  `LivePlayDynamicDifficultyAvailabilityTests`, `DynamicDifficultyLadderTests`,
  `DynamicDifficultyCompositeTests`.
  **A song with no ladder history starts where the player usually plays
  (#295 step 3).** A level only compares across songs with its ladder's size,
  so `PhraseMasteryOutcome` / `PhraseMasteryRecord` carry `LadderSize`, where 0
  means unknown (pre-#295 records count for nothing until replayed).
  `PlayerLadderDepth.From(GetAll())` is the mean `level / (size − 1)` over
  phrases played at least twice. A run whose arrangement has no `PhraseMastery`
  passes it as `LivePlaySessionOptions.StartingDepth`, and
  `DynamicDifficultyEngine.LevelFor` starts each unseen phrase at
  `round(depth × top)` of its own ladder; a stored level always wins. The strip
  shows "From your history N%" with "Start at 0"
  (`LivePlayViewModel.StartLadderAtBottomCommand` →
  `LivePlaySessionService.StartLadderOverAtBottom`: the depth and every moved
  level are dropped and the sounds ahead are reloaded). `PlayerLadderDepthTests`.
  **A DD reload re-levels only the iterations the playhead has NOT reached.**
  `DynamicDifficultyEngine` keys levels by phrase NAME, so the iteration whose
  own accuracy just moved the ladder is rebuilt by that move too —
  `LiveNoteMatcher.Reload` rebuilt EVERY span at the phrase's current level, the
  played ones included. Levelling UP put notes BEHIND the cursor, which can
  never be judged: `TotalSounds` grew, `Coverage` fell (a run can drop under
  `PracticeProfileService.MinCoverageForBestAccuracy` that way) and an FC became
  unreachable. Levelling DOWN deleted notes the player had already HIT, so
  `Hits` and `Score` went DOWN mid-run — the scoreboard moving backwards over
  playing that never happened. `Reload` now freezes an iteration's level the
  moment ANY of its sounds is behind the horizon (a change part-way through one
  phrase would add or remove notes under the player's hands). Pinned by
  `DynamicDifficultyReloadTests`, including the half the reload exists FOR: an
  unplayed iteration still moves, and nothing is frozen before the first note.
  **A `LivePlaySection`'s `StartBar`/`EndBar` are captured ONCE, in
  `BuildSections`, and every later rebuild must carry them forward.** They are
  `init`-only, so a rebuild that omits them nulls them SILENTLY, and there is no
  recovery: the practice store persists no beat map, so a recorded run can never
  be re-barred. `RebuildSectionSoundCounts` (the difficulty-reload path, reached
  from `CompleteElapsedPhrases` and the loop-pass path) did exactly that until
  v0.44 — so with Dynamic Difficulty on, the FIRST phrase whose level moved
  stripped the bars off every section for the rest of the run, and the loss
  propagated through `SectionResult` into the stored record and the results
  screen's "bars N–M" labels (and, until they were removed on 2026-09-19, the
  History and Trends read-models).
  Pinned by `LivePlaySessionSectionTests`.
  **`Stop(transportSpeed)` takes the run's speed and the parameter is REQUIRED
  (no default) on purpose.** `LivePlaySessionResult.SpeedPercent` used to be
  `Ladder?.CurrentSpeed ?? 1.0`, and `LivePlaySessionOptions.Ladder` is non-null
  ONLY for the riff-repeater ladder (and, until 2026-09-19, an armed
  smart-practice drill) — so an ordinary run on the speed slider recorded as
  100 %, inflating `PracticeArrangementProfile.MasteryPercent` (it multiplies by
  the speed factor). The ladder still wins
  when there is one — it owns the transport while it runs.
  **Detection v8** is the CQT judging mode, **the default since 0.57.4**
  (`AppConfig.LivePlayUseCqtVerifier`, moved over ONCE for existing configs by the
  `LivePlayCqtDefaultMigrated` latch — a later "off" sticks). Measured on real DI
  with ground truth (`tools/detection-eval`, Guitar-TECHS, 14,250 sounds): the
  classic candidate matcher credited 59.5 % of perfectly played sounds (40 % of the
  chord sets, 45 % of the songs), the CQT verifier 92.4 %. (The classic path's
  `JuceGuitarAnalyzer` WRITING a chart from a song's mix — Chart from audio — is
  scored separately:
  onset F1 44.7 %, note F1 4.3 % on `TestData/EOF`, see
  [../testing/autochart-eval.md](../testing/autochart-eval.md).) `LiveCqtVerifier`
  (wraps JuceSharp.Dsp's `JuceConstantQ`/`JuceChartVerifier`) plugs into
  `LiveNoteMatcher.ChartVerify`, which then judges pitched sounds *pull-based*
  in `AdvanceTo` (candidate paths short-circuit); pitchless sounds (mutes,
  unpitched slides) still use the pushed timbre evidence. Fed **raw
  capture-axis** PCM by the UI tap — *never* transport-aligned/speed-scaled PCM:
  the CQT's position axis IS the sample axis of what you push, so scaling it by
  playback speed re-anchored the ring on every push and judged every pitched
  note a Miss at any speed ≠ 1.0. The chart time is mapped INTO the capture axis
  at `Verify` time via `SetAnchor` (`LiveTimeMapping.ToCaptureSeconds`).

  **MEASURE THE STREAMING PATH, NOT THE OFFLINE ONE.** `JuceConstantQ` has two entry
  points that compute *different statistics*, and only one of them ships:

  | | offline `AnalyzeWhole` | streaming `Reset`+`Push` (**ships**) |
  |---|---|---|
  | per-bin `Floor()` | 0.10 × 95th pct of the WHOLE clip | 0.10 × rolling **MAX** over the ring |
  | `NoiseFloorEnergy()` | 5th pct over the WHOLE clip | 5th pct over `NoiseHistoryFrames` |

  `CqtDatasetEvalTests` drives `AnalyzeWhole`, so every accuracy number ever quoted
  from it describes code we do not run. **`CqtStreamingEvalTests` is the twin that
  drives `Reset`+`Push` in 512-sample blocks and pull-`Verify()`s each note as its
  window closes — the shape of `LivePlayViewModel.Tick` → `LiveNoteMatcher.AdvanceTo`.
  Use it for any detection claim.** Both are opt-in (`GND_IDMT_ROOT`, `GND_EVAL_CAP`);
  results are sample-dependent, so **only ever compare at the SAME cap, and state it.**

  **Two fixes, measured on IDMT (streaming, cap=120, 120 tracks / 893 notes):**
  - `LiveCqtVerifier.LiveNoiseHistorySeconds` = **10 s** (`JuceConstantQ.NoiseHistoryFrames`).
    The noise floor used to be the 5th percentile of the 384-frame ring — the last
    **2.23 s** — which during continuous playing is not the idle floor at all but "the
    quietest moment while playing". The presence gate then demands `peak > 3 ×
    near-playing-level`, which real notes FAIL. Widening to 10 s took per-note
    **89.5% → 91.9%** and per-event **88.0% → 90.9%**, which *exactly closed the gap to
    the offline path*, at no false-accept cost. **The entire offline-vs-streaming deficit
    was this one estimator.**
  - `LiveCqtVerifier.LiveSubOctaveVeto` = **0.5** (`JuceChartVerifier.SubOctaveVeto`).
    A decoy an octave ABOVE a played note confirmed **89.7%** of the time — even when
    that octave was annotated SILENT — because the played note's 2nd harmonic lands
    exactly on the charted bin, loud, and is a real local peak. A presence test cannot
    tell "played X" from "played X−12" on its own. The veto looks an octave BELOW (a
    string has no subharmonic, so a real fundamental has nothing there) and cuts octave
    FA to **35.3%**, also taking tritone FA 6.6% → 6.0% and fifth FA 12.9% → 11.6%, at
    zero wrong-fret cost. **It is applied to SINGLE notes only** — guitar chords are full
    of real octaves, so `LiveCqtVerifier` passes `allowSubOctaveVeto: false` for chord
    tones. Net vs the old shipping code: recall UP and *every* false-accept DOWN.
  - **…and the 10 s horizon was NOT the whole story — the "entire deficit" claim above
    held for IDMT's isolated notes, not for music.** Measured 2026-09-29 with
    `tools/detection-eval` on the Guitar-TECHS DI songs (12 recordings, 1,263 sounds):
    during a phrase that does not pause for 10 s the 5th percentile is STILL the playing
    level, and the gate refused 38.6 % of perfectly played notes (61.4 %; no gate at all:
    85.4 %). Fix: the **streaming idle floor** (`JuceConstantQ.IdleSteadyFrames`,
    `LiveCqtConfig.IdleSteadySeconds = 2`, `IdleSteadyRatio = 1.5`,
    `IdleFloorRiseDbPerSecond = 0.05`) — it drops to any quieter frame, creeps up only
    slowly while the player plays, and snaps to the input's level whenever the last 2 s
    were steady (hum and hiss are, phrases are not) — plus the live margin
    `StreamingNoiseMargin = 2` (the offline lint keeps `NoiseMargin = 3`). Songs 61.4 % →
    83.0 %; idle 0 % → 0 %; player stops half way 3.0 % → 0.0 %; +12 dB gain while idle
    21 % → 4.5 %; every note a semitone off 10.1 % → 14.9 % (the owner's call:
    forgiving first). Pinned by `JuceConstantQIdleFloorTests` and
    `LiveDetectionAccuracyTests`.
  - **Harmonics are verified at the overtone they ring at.** A charted harmonic carries
    its FRETTED pitch; a natural harmonic over fret 7 rings an octave above it and the
    fretted bin is silent, so the CQT path missed every one. `LiveCqtVerifier` now also
    tries `LivePitchAcceptance.HarmonicOvertoneSemis` (+12/+19/+24/+28, no sub-octave veto on
    an overtone) for harmonic and pinch-harmonic sounds — the tolerance the classic path
    always had.
  - **An octave leap is not an octave-down performance.** The sub-octave veto refused a
    correctly played note whenever the note an octave BELOW it was still ringing from the
    previous beat — 72 % of such notes on the Guitar-TECHS songs (57 of 79). `LiveCqtVerifier`
    remembers when it last CREDITED each pitch; if the octave below was credited within
    `RingOnMs` (3 s) before this note, the veto stands aside. Songs 83.0 % → 87.3 %;
    playing a whole song an octave down: 12.6 % → 16.1 % of single notes accepted.
    `LiveDetectionAccuracyTests.AnOctaveLeap_IsHeard_ButPlayingAnOctaveDownStillIsNot`.
  - **A chord gets longer to finish sounding than a note.** A strum is spread: a slow
    downstroke puts its top strings 100 ms+ after the charted time, past the single-note
    window's 140 ms after-edge, and a chord whose only third was the top string then failed
    the quality rule outright. Chord tones verify through a second `JuceChartVerifier` whose
    after-edge is `LiveCqtConfig.ChordPostWindowAfterSeconds` (0.25 s); single notes keep 0.14
    (widening theirs cost the songs a point). Guitar-TECHS chord sets grouped as strums:
    79.9 % → 95.6 % together with the longer low-bin kernel; songs and wrong-note false
    accepts unchanged. `LiveDetectionAccuracyTests.ASlowStrum_IsCreditedOnceItsTopStringSounds`
    (0/9 with the single-note window).
  - **The kernel cap is 8192 since 0.57.4** (`LiveCqtConfig.MaxKernelSamples`, was 4096).
    Below ~400 Hz every bin is capped, and at 4096 a loud open string ringing under a melody
    smeared over the notes beside it (a Guitar-TECHS song with an A2 drone under B2/C3/D3:
    69 % → 83 %). Songs 87.3 → 90.1 %; semitone-off false accepts 15.0 → 17.9 %; the CQT
    costs 18 % of a core instead of 11 %.
    `LiveDetectionAccuracyTests.ANoteBesideARingingOpenString_IsHeard`. **It moved the idle
    floor's steadiness bound too** (`IdleSteadyRatio` 1.5 → 1.2): the longer kernel smooths
    frame energy, and at 1.5 non-stop strumming read as "idle" and lifted the floor ~165x to
    the playing level. `JuceConstantQIdleFloorTests.DuringContinuousStrumming_TheFloorStaysAtTheIdleNoise`.
  - **A CQT hit is credited as it is played, not when its window closes.** The matcher
    re-asks `ChartVerifyEarly` (`LiveCqtVerifier.VerifyEarly`) on every tick for each due,
    still-open pitched sound from `LiveCqtConfig.EarlyCreditAfterMs` (90 ms) after its time
    (`LiveNoteMatcher.CreditConfirmedEarly`); a confirmation credits it at once. Only a MISS
    waits for the window. The early check demands a RISE over the pre-onset level
    (`EarlyCreditRiseRatio`, `JuceChartVerifier.PreRatio`) and waits out the attack, because it
    gets several chances where the final check gets one: asked from 0 ms with the plain check,
    every-note-a-semitone-off was accepted 25–27 % of the time instead of 18.6 %. Songs: hits
    shown a median 111 ms after the note (was 266 ms, 395 ms with the late edge below), 91.9 %
    credited, wrong notes 19.4 %. **The CQT late edge is derived, not 230 ms**
    (`LiveCqtConfig.FinalVerdictAfterMs` ≈ 378 ms = chord window + half the 8192 kernel at
    44.1 kHz + a tick): 230 was right for the 4096 kernel and the 140 ms window only, and cut
    every chord's window short (slow strums 95.6 → 96.7 %). Misses therefore show ~150 ms
    later than they did; hits no longer wait for it.
    `LiveNoteMatcherCqtModeTests` (early credit, the wait, the run of confirmations, the
    factory wiring). **Since #329 (0.64) the hit is still SHOWN at that confirmation and
    graded later**, when the attack onset that times it is reported (`AwaitAttackOnsets`,
    § timing tiers); the 111 ms above stays the display. The tests above build the matcher
    without the grade wait, so their hits pay the full base at once.
  - **Targeted leniency (0.58), each measured on the Guitar-TECHS songs for recall AND for
    wrong-note / octave-down / idle / stop-mid-song acceptance:**
    - *Dense chords* (`LiveCqtConfig.DenseChordTones` = 4, `DenseChordCoverage` = 0.4): no
      lone-third rule and a lower bar; free (songs 91.9 → 92.0 %, nothing else moved).
    - *Pinch harmonics* also count from the attack detector's pushed evidence in CQT mode
      (`LiveNoteMatcher.PinchHarmonicAttackEvidence`).
    - *Bends and slides* are verified at every semitone they travel through
      (`LiveCqtVerifier.TryTravel`) — the range the attack path always accepted.
    - *Fast-run semitone tolerance* was MEASURED AND REJECTED: +0.8 recall, but semitone-off
      notes 19.4 → 31.3 % accepted and idle hum scored. See the note in `LiveCqtConfig`.
    - *Forgiving* (`LiveCqtVerifier.Forgiving`, created by `LiveCqtVerifier.ForPreset` for
      `LiveHitWindowPreset.Lenient`): a quarter-tone-detuned note counts when that bin is the
      local peak AND the input clears TWICE the presence margin (mains hum sits between the
      semitone bins — without the doubled margin idle hum scored a note), and chords drop the
      lone-third rule. Songs 92.0 → 93.3 %, semitone-off 19.4 → 25.9 %, idle 0 %. Normal and
      Strict are unchanged. `LiveDetectionAccuracyTests` pins each (and fails with it removed).
  - **Guard, do not regress:** idle phantom-confirm rate must stay **0%**
    (`NoiseMargin_RejectsPhantomNotes_OnTheStreamingPath`, which mirrors the live config
    exactly — an ungated verifier phantom-confirms ~2/3 of charted notes against pure noise).

  **Measured and REJECTED — kept as default-off knobs so they are not rediscovered:**
  - *Rolling-percentile per-bin floor* (`JuceConstantQ.FloorPercentile`, default 1.0 =
    rolling max). Theory: one loud transient pins a bin's gate for 2.23 s. On IDMT it
    changes recall by **nothing** (89.5% → 89.5%) and makes tritone FA **worse**
    (6.6% → 7.2%) — the per-bin floor is not the binding constraint; `IsPeak` and the
    presence gate decide nearly every verdict.
  - *Harmonic-relocated peak test* (`JuceChartVerifier.HarmonicPeakBelowHz`, default 0).
    Works exactly as designed (low-band recall 87.6% → 93.2%, ≥370 Hz band untouched) but
    it is a pure recall↔FA trade: per-note 89.5% → 93.7% while wrong-fret FA 3.2% → 5.6%
    and tritone FA 6.6% → 8.9%. The 10 s noise horizon buys more recall for ~zero FA, so
    it strictly dominates this. **Its premise was also falsified** (below).
  - *Harmonic comb* (`JuceChartVerifier.HarmonicComb`) and spectral whitening were
    measured and rejected earlier — do not re-add them.

  **A falsified prediction, recorded so nobody re-derives it.** It was predicted that
  false-accepts concentrate BELOW ~370 Hz, because the kernel cap (4096) means full
  constant-Q is only achieved above `f = Q·sr/L ≈ 367 Hz`, so `IsPeak`'s ±1-semitone
  comparison bins fall inside the note's own main lobe down low and the test goes vacuous.
  **Measured, the opposite is true** (streaming, cap=120):

  | band | n | per-note | FA(+1) | FA(+6) |
  |---|---|---|---|---|
  | f0 <250 Hz | 532 | 87.6% | **2.1%** | **5.3%** |
  | f0 250–370 Hz | 219 | 92.2% | 4.8% | 6.4% |
  | f0 ≥370 Hz | 142 | 92.3% | 4.9% | **12.0%** |

  FA is *lowest* in the low band and *worst* at the top. What IS real down low is a
  **recall** deficit. Do not build a low-note plan on the FA story.

  **The FA-vs-decoy-interval curve is the cheapest diagnostic available** and it names the
  mechanism: FA peaks at the HARMONIC intervals (+12 ≈ 90%, +7, ±5, −12) and is *lowest*
  at ±1/±2 — harmonic bleed, not resolution smear. **Report FA against decoys the
  annotation says are SILENT.** On polyphonic material (IDMT dataset3/4) a +7 or +12 decoy
  is very often a note the player is genuinely holding — a power chord IS root+fifth+octave
  — so the naive metric counts *correct* detections as false accepts (raw +7 FA 23.2% vs
  clean 12.9%). `IdmtDataset.Sounding` is what makes the number honest.
  **Resume rewind (Rocksmith pause semantics):** `LivePlaySessionService.BeginResumeRewind(pausedAtMs, leadInMs)`
  is the loop wrap MINUS the rewind — it arms the backwards seek as the session's
  OWN (`_pendingSeekTargetMs` **and** `_lastPlayheadMs = target`, so `HandleTick`
  can never see a jump) and returns the target; `LiveNoteMatcher.Rewind` must NOT
  run, or the verdicts (and the score) past the pause point are DELETED.
  **`Rewind` is destructive by design** — that stays the semantics of an EXTERNAL
  backwards seek (a manual scrub), and `ExternalBackwardsSeek_StillRewinds` guards it.
  The replayed sounds are inert for free: `index < _cursor` ⇔ resolved, and both
  `AdvanceTo` and `Offer*` skip resolved sounds, so the stretch can neither re-score
  nor re-miss. The dead zone is the CURSOR horizon (`LiveNoteMatcher.ResolvedThroughMs`),
  never the raw pause point — sounds whose window was still open at the pause must
  stay judgeable or a full combo becomes impossible after any pause. It also must not
  touch `_completedPhrases` (a spurious phrase re-completion DEMOTES the DD ladder,
  which persists downward) or the section tallies. `ResolvedThroughMs` is published to
  the 3D highway (`FrameSnapshot.LivePlayResolvedThroughMs` → `DeadNoteMath.IsDead`)
  to draw the replayed notes DIMMED. UI: `LivePlayViewModel.BeginResumeRewind` (seeks +
  re-anchors the CQT), called from `PlayModeViewModel.ResumeGameplay` with
  `AppConfig.GameplayResumeRewindMs` (default 1500, 0 = off) — **skipped on LAN sync
  runs** (`_isSyncRun`), where moving one machine's transport alone would desync the match.
  **Timing analytics (0.17 foundations):** `LiveTimingCollector`/`LiveTimingReport`
  (`LiveTimingAnalytics.cs`) — pure post-run aggregation over judged
  `LivePlayEvent`s (signed early/late deltas, hits only; misses excluded — their
  DetectedAtMs is window expiry). A separate consumer by design: never fold it
  into `LivePlayStats`/`LiveNoteMatcher` scoring.
  **Take recorder (0.17 foundations):** `Audio/LivePlay/Takes/` —
  `LiveTakeRecorder` is a bounded SPSC ring + background 16-bit-WAV writer teed
  off the SAME capture pump (`LivePlayViewModel.Tick`): `Write` never blocks,
  never allocates, and DROPS (counted) when the writer starves — keep it that
  way. `LiveTakeStore` owns the active profile's `takes/` dir, encodes finished
  takes to Opus via the `FfmpegStemEncoder` seam (WAV without ffmpeg), attaches
  the file name to the newest `PracticeSessionRecord.TakeFile`
  (`IPracticeProfileService.AttachTakeFile`) and sweeps orphaned/over-cap takes
  (`SelectDeletions` is the pure policy). Opt-in via
  `AppConfig.LivePlayTakeRecorderEnabled` (default OFF; Settings → Audio →
  "Listen back", `AudioSettingsViewModel.TakeRecorderEnabled`).
  **Listen-back (0.19):** the take is reachable from Play Mode's results screen
  (`LivePlayViewModel.RunArchive.TakeSaved` — `ViewModels/Editor/LivePlayRunArchive.cs`,
  which owns both stores, starts the take and runs the off-thread finalize and
  replay save — fires after the off-thread finalize, so the
  button appears a beat AFTER the screen; `ILiveTakeStore.TakesDirectory`
  resolves the stored NAME back to a path, and a swept file must read as
  "no take", never a dead button). The History screen's listen-back went with
  that screen on 2026-09-19. **There is only ONE master transport**, so
  playback BORROWS it: `RunListenBackViewModel.PlayTakeAsync` (Play Mode's
  `ListenBack`) snapshots
  `IAudioPlaybackService.CurrentAudioPath` + the playhead, run-scope-mutes the
  stems (they ride the same transport and would play the song under the take),
  and `StopAsync` puts all three back. Every exit path — Stop, the take
  running out (`_takeWatchTimer`), leaving the screen — goes through it, or the
  results screen's Retry would play the recording instead of the song.
  **Take library (v0.27) — the UI was removed on 2026-09-19** (the History
  screen's star / rename / delete / re-amp panel, `TakeReampService` and the
  store's `SetTakeStarred`/`SetTakeLabel`/`AttachReampFile`/`DetachTakeFile`
  mutators; restore from tag `pre-cut-2026-09`). **What stays, on purpose:**
  the session record's schema-tolerant `TakeStarred`/`TakeLabel`/`ReampFiles`
  fields — nothing writes them now, but an upgraded store round-trips them —
  and the sweep policy that reads them (`LiveTakeStore.SelectDeletions`,
  optional pinned set): attached re-amps count as referenced files; STARRED
  takes + their re-amps are PINNED, never cap-evicted (an all-starred store may
  sit over the budget — deliberate); orphaned re-amps and interrupted `.tmp`
  writes sweep like orphaned takes. Dropping the fields would let the next
  sweep delete recordings the player chose to keep.
  `Services/Gameplay/RemovedPracticeDataCompatibilityTests` pins a store
  written by the library: it loads, per-song stats read from it, the metadata
  survives a re-save and the sweep deletes none of its files.
  **Run replays (v0.32):** `Audio/LivePlay/Replays/` — `RunReplay` (judged
  event stream: chart time + verdict + tier STRINGS, schema-tolerant like the
  technique tallies; the take already carries the audio) +
  `RunReplayCapture.FromExpected` (pure; resolved sounds only) + `IReplayStore`
  (profile-scoped `replays/` dir — inside the profiles tree so LAN exclusion +
  backup are inherited; KB-sized, deliberately NO sweep — a dangling
  `PracticeSessionRecord.ReplayFile` reads as "no replay"). Capture rides
  `LivePlayViewModel.StopCore`: projected UNDER the gate from
  `Matcher.Expected` BEFORE `_session.Stop()` (same reason as the sections
  snapshot), persisted + attached (`AttachReplayFile`, newest-session-once
  like takes) OFF-thread by `LivePlayRunArchive.SaveReplayAsync`. Recorded runs only; always on (no flag).
- **Per-string detection confidence in the analysis monitor (#314)** —
  `Audio/LivePlay/LiveStringConfidence.cs`, pure. It surfaces evidence the judging path
  ALREADY computes and decides nothing: every verdict, and so every detection
  score, is unchanged (pinned by
  `LiveStringConfidenceTests.ACqtMiss_CarriesWhatTheVerifierFound_AndIsStillAMiss`, which
  also asserts the verifier is still asked exactly once).
  - **The number.** `LiveDetectionCertainty.Of`: a hit is as certain as its
    `LivePlayEvent.Confidence` (CQT `1 − gate/level`, chord weighted coverage, classic
    salience); a miss is `1 − LivePlayEvent.MissEvidence`, the confidence the FINAL
    verifier returned for the sound it did not confirm (a single note's bin above its floor
    that failed the peak test; a chord's coverage under its bar). `LiveNoteMatcher.AdvanceTo`
    keeps that outcome instead of discarding it. **A miss no verifier looked at (the classic
    path, a pitchless sound) has NO certainty** — null, never a confident 1 it did not earn.
  - **Unsure** is `< LiveDetectionCertainty.UnsureBelow` (0.5), a display threshold: an
    unsure MISS (`IsUnsureMiss`) wears the monitor's "low confidence" hint.
  - **The window.** `LiveStringConfidenceTracker`: per chart string, the mean of the last 8
    judgments within 10 s of chart time before the playhead (the injected clock); a chord
    counts on every string carrying one of its pitched tones (`ExpectedSound.StringMask`)
    with its one chord-level number; judgments more than 1 s AHEAD of the playhead are from
    before a seek back and are dropped. Worker-confined: recorded in
    `LivePlayViewModel.OnJudged`, sampled into the tick snapshot only when it changed (or the
    epoch did), cleared at run start. The UI is `LiveStringConfidenceViewModel` +
    `Views/Editor/LiveDetectionMonitorView` (hosted by `Preview3DView`'s monitor row;
    `Perf/LiveDetectionMonitorConstructionUiPerfSmokeTests`).
- `Audio/Tuner/` — the 0.18 tuner suite's MANAGED core (pure DSP/logic, no audio
  deps): `MpmPitchDetector` + `YinPitchDetector` behind `TunerPitchEngine`
  (MPM→YIN fallback; span-based, allocation-free `Detect`, clarity/RMS gated);
  `VirtualStrobe` (single-bin DFT phase-drift fine tuner at a known target,
  contiguous fixed-size blocks) + `TunerDisplaySmoother` (median→EMA +
  stability hold, injected `nowMs`); `GuidedTuningCheck` over
  `TuningCheckTargets` (arrangement tuning + capo + CentOffset, reuses
  `ArrangementTunerLogic` math) — one-string-at-a-time flow, NEVER polyphonic
  strum-all-6 (US8334449B2); `TuningDriftWatchdog` (passive cent accumulator
  with decay + hysteresis + an outlier clamp).
  **`ArrangementTunerLogic` reads the arrangement's `CentOffset` too, and did
  not until 2026-09-05.** It is the engine behind Tools > Tuner, and it computed
  the deviation from an integer MIDI number with the field never mentioned in
  the file — so for a chart referenced off A440 the tuner dialog said "tune to
  A440" while the Get Ready guided check, over the SAME arrangement, said "tune
  50 ¢ flat". Two screens, one chart, two answers, and live judging follows the
  chart. The offset now moves each target's `FrequencyHz`, the reported cents
  and the nearest-string comparison; `NoteName` and `Midi` stay the note, which
  is the same split `TuningCheckTargets` makes. Pinned by
  `TunerCentOffsetTests`, whose middle case asserts the two target tables are
  the same frequencies — the assertion that would have caught it.
  **One resolver for a chart's open strings:**
  `ArrangementTunerLogic.TryResolveTuningSteps` (`TuningPitches`, else the
  `TuningType` name decoded by `TuningUtilities.TryGetOffsetsForTuningType`;
  `false` when neither says anything). `Load(Arrangement)` (Tools ▸ Tuner,
  through `LiveTuner`) and `TuningCheckTargets.TryBuild`/`Build` (Get Ready's
  `GuidedTuningCheck`, the timeline gutter) both go through it, and both fall
  back to standard the same way for an unknown name (`ResolveTuningSteps`).
  Before #303 `Load` read `TuningPitches` raw, so a named-only chart read as E
  standard in the dialog while Get Ready decoded the name. The capo never moves
  the dialog's targets (tune open, then place it — the guided check's default
  `TuneOpenThenPlaceCapo`). Pinned by `TunerTargetResolutionTests`.
  **A tuning-match transpose (#312) moves every expected pitch and every tuning
  target by the same step** — `LiveNoteMatcher.TransposeSemitones` (from
  `LivePlaySessionOptions.TransposeSemitones`; notes, slides and chords all go
  through `ExpectedMidi`) and `TuningCheckTargets.Build(…, transposeSemitones)`.
  `SoundingMidi` itself stays the chart's own pitch. Pinned by
  `TuningMatchJudgingTests` and `TuningMatchTransposeTests`.
  **A reading more than `MaxStringDistanceCents` (150, the same as
  `GuidedTuningCheck.WrongStringCents`) from its nearest string never touches
  hold or confirmation** — `Process` returns it with `TunerFrame.InRange =
  false`, so a stray note or an octave-off reading cannot restart a string the
  player already tuned. `NearestString` itself stays unbounded on purpose: it is
  the DISPLAY pick (the lit chip, the pinned needle that says which way to turn
  a peg several semitones off; `RigTunerViewModel` uses it the same way).
  **No octave folding, on purpose**: open strings stand in octaves of each other
  (E2/E4, Drop D's D2/D3), so a folded reading would credit a string the player
  never plucked. Pinned in `ArrangementTunerLogicTests` and `LiveTunerTests`.
  **The in-tune band is a SCHMITT TRIGGER** — entered at
  `TunerDisplaySmoother.InTuneCents`, left at `ReleaseCents` (1.5× that). With
  one threshold a string parked ON the edge could never latch: every reading a
  fraction of a cent outside cleared the band-entry stamp and restarted the
  stability hold from zero, so the lamp strobed at exactly the tuning the player
  was trying to hold. `RigTunerViewModel` consumes only `InTune`, so the needle
  is unaffected. Pinned by `TunerDisplayHysteresisTests`.
  **The pitch engine is octave-correct on a low string with NO fundamental** —
  a magnetic pickup makes the fundamental of a 7-string low B / drop-A F#1 /
  bass D1 the quietest partial or removes it, and the waveform is still periodic
  at f0, which is exactly what an autocorrelation method sees. Both MPM and YIN
  pass this INDEPENDENTLY (`LowStringPitchDetectionTests`) — the composite's
  MPM→YIN fallback would otherwise hide a broken half. Every other detector test
  feeds a tone whose fundamental is the loudest partial; do not "optimise" the
  NSDF/CMNDF search on that assumption.
  **Drift evidence source (0.19) — the CQT verifier cannot provide it.**
  `JuceChartVerifier.Verify` returns `{Hit, Confidence, Level}`: a binary
  match-mask verdict with NO signed cent offset, over quarter-tone (50 ¢) bins.
  So `LiveTuningDriftMonitor` (`Audio/LivePlay/LiveTuningDriftMonitor.cs`, pure:
  `BeginCheck` hands the worker the window to fill, `CompleteCheck` answers the
  new hint; driven by `LivePlayViewModel.UpdateTuningDrift` under the session
  gate) feeds the watchdog from the MANAGED
  `TunerPitchEngine` instead (4 Hz, off the same non-destructive capture
  window), measuring the deviation from the NEAREST equal-tempered semitone:
  a guitar that has gone x ¢ flat plays every note x ¢ flat, and the harmonics
  of a flat note are flat by the same x, so the residual is the drift even when
  the detector locks onto a partial or a chord's root. The arrangement's
  authored `CentOffset` is subtracted (a deliberately non-A440 song is not
  drift). Surfaced as a HUD badge (`HasTuningDrift`/`TuningDriftHint`) — a
  sibling row, never an overlay (airspace). Fed by the
  `IAudioPlaybackService.ReadInputCaptureWindow` tap. Doubles as the
  validated reference implementation for the Phase-0 native graduation — its
  tests are the acceptance contract. Distinct from `Audio/Tuning/` (the
  arrangement-tuner needle logic and `LiveTuner`, which the Tuner dialog and
  the Tools popover's readout share).
