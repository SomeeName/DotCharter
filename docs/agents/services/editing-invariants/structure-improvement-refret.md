# `Editing/` — the chart-model invariants (deep reference) — `Editing/Structure/`, `Editing/Improvement/` (dynamic difficulty), `Editing/Refret/`

## `Editing/Structure/` — what may be derived, and what may not

- `Editing/Structure/` — `StructureGenerator` (pure) + `StructureGenerationService`.
  Derives only the parts of a chart's structure that are **not a judgement
  call**: the COUNT and END phrases (89 of 89 corpus arrangements carry exactly
  one of each) and a `noguitar` section per silent stretch **whose resume point
  the charter has already marked** — 171 of 171 corpus `noguitar` sections
  contain literally no sound, and the resume-marker requirement is what stops a
  convenience from swallowing the music after the gap.
  **It invents no section boundary and no section NAME, and the two obvious
  ways round that were measured on 2026-07-27 and both failed — do NOT
  re-derive them** (`LocalOnly_SectionStructureProbe`, git-ignored; full numbers
  in `StructureGenerator`'s summary).
  **(a) Labelling by example.** "Sections repeat, so propagate the label"
  is false in practice: nearest-EARLIER-neighbour by note content is right
  56.2% of the time over 2,511 predictable sections (69.5% only after giving up
  58% of the recall), while the content-FREE rule **"copy the previous section's
  name" is right 68.9% at full coverage** — and 76.2% on the very rows where the
  content matcher is most confident. A hybrid that defers to content when
  confident is WORSE than copy-previous at every threshold. Four encodings and
  four minimum-size gates land within 1.4 pp of each other; per-folder accuracy
  runs 4.8%–100%; the ceiling (true name present in any earlier section) is
  81.6%. The failure is systematic, not noisy: `outro` scores 7.1% and
  `interlude` 17.2% because a short, sparse section is a Jaccard of 1.00 against
  any other short, sparse section.
  **(b) Boundary detection.** Over 12,778 candidate measure starts at an 18.3%
  base rate, scored top-k per arrangement (so precision == recall): repeat-start
  39.8%, checkerboard novelty 35.7%, chord-template change 23.4%, note-density
  change 20.9%, rest/sustain-before 23.0%, the charter's own FHP anchors 38.3% —
  against **20.9% for the trivial "fire every N measures"**. An IN-SAMPLE
  weight search over all of them (fitted on the corpus it is scored on, so an
  upper bound on any hand rule) reaches 48.9%, and 22 of 83 charts sit at or
  below the trivial baseline. 12.5% of section starts are not on a measure at
  all, capping any measure-grid detector at 87.5% recall before a signal fires.
  Judge every candidate against that baseline and not against zero: sections are
  ~6 s apart, so a ±1-bar tolerance already scores 60.2% by accident.
  `StructurePlan` carries POSITIONS only — `StructureGeneratorTests.
  StructurePlan_CannotExpressAGuessedName` fails if a member appears that could
  hold a name.

## `Editing/Improvement/` — dynamic difficulty (generate ⇄ squash)

`LevelGenerationService.TryGenerate` and `LevelSquashService.TrySquash` are
inverses, and **there is no other writer of `Arrangement.Levels`** in the app —
no add-level, remove-level or copy-level command exists, and both verbs go
through ONE `ArrangementSnapshotAction` (they change only the arrangement, #370),
so a regeneration is a single Ctrl+Z
(`ChartMaintenanceService.GenerateLevelsCoreAsync` / `.SquashLevels`). The same
node drops the chord templates the operation left with no user, composed with
the snapshot by `ChartMaintenanceService.LevelOperationAction` (#520; the rule is
in [chord-templates.md](chord-templates.md)).

- **How close the generator is to a stock ladder — measured, #295.**
  The plugin's owner-only ladder score (it reads stock ladders through the
  package reader, #889; `DOTCHARTER_LADDER_SCORE_OUT=<dir outside the repo>`) regenerates every
  stock ladder of 5+ levels from its hardest level and scores level k against
  the stock level k by the ONSETS each keeps (`LevelLadderScore`; the random
  baseline keeps the same count at random). 2026-10-07, 159 arrangements, 3,155
  levels: onset F1 **0.775** against 0.694 random (+0.08) with the old tiers.
  What stock easy levels keep (same corpus): a bar start ~13x as often as a
  plain beat, a mid-bar beat ~2x, and long notes (gap or sustain over 1.5 s)
  ~5x as often as the average; a note after a silence adds nothing once length
  counts. The generator now ranks by metric weight × length and thins along the
  stock density curve (`OfficialDensity`): onset F1 **0.819** (easiest fifth
  0.551 → 0.580, next fifth 0.580 → 0.624), density within 0.01 of the stock
  in every band; the weights are flat around their values (×6/×25 bar, ×10
  length move it by ≤ 0.001). Re-run this score after any change to the ranking.

- **What a kept CHORD onset plays is measured too — #490.**
  The plugin's owner-only chord-ladder measurement (read-only, through the package
  reader; `DOTCHARTER_CHORD_LADDER_OUT=<dir outside the repo>`) records, for every hardest-level chord and every lower
  level of its phrase, whether the level plays it full, reduced (which strings),
  as one note or not at all, plus the hand shape there. 2026-10-09, 688
  arrangements, 159,386 chords: a lower level keeps the bass string 75–99 % of
  the time it plays the onset at all, and the strings above it join IN ORDER as
  the depth rises (a single note is the bass string 75–85 %; a reduced grip is
  the lowest strings 67 %, `R5`/`R58` the commonest). `ChordReductionPlanner
  .JoinDepth` is where each string's keep rate crosses one half — the subset
  that minimises the strings a charter must add or remove — and the report
  prints the same table back, so re-derive it from there, never by hand.
  Reduced grips in stock charts sit on their OWN template (fingered as the full chord
  97–100 %, unnamed 46–90 %) with the bar on that template (99.5–100 %); a chord
  that came down to its bass note loses its bar (96–100 %); a note passage keeps
  its bar while two or more distinct notes remain (`LevelHandShapes`).
  On the stock levels' own onsets the old rule (bass note below ratio 0.5,
  else full) matched 53.8 % of grips exactly at 0.806 string edits per onset, the
  measured ladder 59.5 % at 0.685; the passage-bar rule 79.0 % → 85.9 %. End to
  end the generator's ONSET choice still dominates the disagreement
  (stock-nothing → generated-full is the largest cell), so read the report's
  "rule alone" tables when tuning this, and the end-to-end ones for the whole.

- **A generated or squashed level may not MANUFACTURE an inspector finding.**
  `LinkNextRule` walks every level, so a level whose own rewrites broke a chain
  reports on content the charter never touched. Three ways it did, all fixed
  and all tested (`LevelGenerationLinkIntegrityTests`,
  `LevelSquashLinkIntegrityTests`, `LevelGenerationGradedChordTests`): a
  generated level reduces the landing CHORD to fewer strings, so nothing may land
  on the source's string — which is why the spelled-link check runs on EVERY
  generated level below the top, over notes and chord notes; a simplify level strips the
  SLIDE that explained a fret jump; and a squash takes each phrase segment from
  a different level, so a sustain linked across a phrase boundary points into
  content the other level never had. "Did my target survive the thinning?" is
  only half the question on a level that also rewrites what it keeps.
- **A reduced grip never touches a template.** It resolves through
  `ChordTemplateResolver.ResolveReduced` (frets + the source's fingers + its
  arpeggio flavour; mints go to `Arrangement.ChordTemplates` only after every
  level is built), so regenerating mints nothing new and the source template
  stays the same instance with the same values — pinned by
  `LevelGenerationGradedChordTests`.
- **Chord → root-note reduction is a DIFFICULTY reduction and may only drop
  what makes a sound harder.** `Harmonic` changes the sounding pitch (the same
  reason technique stripping leaves harmonics alone), `BassPicking` is how the
  note is produced, and `Ignore` decides whether the game judges it at all.
  The reduced note is BUILT rather than cloned, which is what let all three
  vanish — a strum marked ignore came back judged two difficulties down.
- **`PhraseInfo.MaxDifficulty` is a fact about the arrangement whose levels it
  describes, not about the phrase**, and it is written through
  `PhraseMarkers.All` — BOTH stores, the same blind spot that caught
  `PhraseRule`, the squash and the phrase-start anchor fixer. Two corollaries:
  the STRUCTURAL phrases (`PhraseMarkers.IsStructural` — COUNT / END /
  noguitar) stay at 0, which a blanket pass over the dictionary raised to N-1;
  and copying structure between arrangements
  (`ChartMaintenanceService.MergeStructureInto`) carries the SOLO flag only —
  an eight-level lead used to leave a single-level rhythm's phrases claiming a
  ceiling of 7.
- **The level the editor adopts is the HARDEST one**
  (`ProjectState.ActiveArrangement`'s setter, `MaxBy(DifficultyIndex)`), which
  is what Play Mode, Live Play, the generator's source pick and the matcher's
  no-phrase fallback all already chose. `Levels[0]` on a DD chart is the
  thinnest beginner reduction, and opening one on it reads as a lossy import.
  Pair by `DifficultyIndex`, never by position — nothing promises the two agree
  (`ChartDiffEngine` and `ChartDifficultyEstimator` both key on the index).

## `Editing/Refret/`

  - `Editing/Refret/` — the refret engine (charting power tool): pure DP
    fretting optimizer over (candidate, hand-position) states. Same sounding
    pitches (tuning + capo aware, 24-fret ceiling), new string/fret
    assignments minimizing time-weighted hand travel, chord stretch and string
    crossing; cost weights documented on `RefretOptions`. Technique-pinned
    sounds (harmonics, bends, taps, full mutes, mixed hand shapes) stay fixed;
    link-next chains and hammer/pull runs move as one string decision. Chord
    templates are find-or-create — NEVER mutated in place — and hand shapes +
    FHP anchors are recomputed with the move. UI entry:
    `ChartMaintenanceService.Refret()` (selection range, else the whole active
    arrangement; one undo snapshot).

