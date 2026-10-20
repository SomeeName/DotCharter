# `Editing/` — the chart-model invariants (deep reference) — a chord, its hand shape and its anchor as one decision; anchors (FHP); capo

## A chord, its hand shape and its anchor are ONE decision

- **A chord, the hand shape over it and the anchor under it are ONE authored
  decision, and `Editing/Selection/SoundCompanions` is where that is resolved at
  EDIT time (0.40).** `PatternCompanions` already encoded it for select-alike;
  the edit paths did not, and re-fingering a chord left the bar above it
  describing the shape that used to be there — which is what the 3D highway draws
  fingers from. Coverage is `HandShape.Covers` (start inclusive, end EXCLUSIVE),
  the same span rule the matcher and the anchor generator use. **The resolver only
  answers "which elements go with these sounds"; whether to ACT on them stays with
  the caller**, because the answer differs per verb — a fingering edit repoints the
  bar, a copy clones it, a delete must NOT cascade to it.

## Anchors (FHP)

- **Three anchor derivations, three different questions — do not merge them.**
  `FretAnchorGenerationService` answers "where should every anchor in this
  arrangement go" and is measured against the EOF corpus as a whole-chart
  generator. `Editing/Anchors/AnchorFretSuggester` answers "what fret should THIS
  new anchor open on" (lowest fretted note it will govern; the charter has already
  decided where the hand moves) — added because all six anchors created in one
  session were corrected within 3.4 s, five of them on the fret.
  `Editing/Anchors/IndexFingerPosition` answers "what fret does the fingering
  REQUIRE" and is shared by inspector rule E04 (which reports the disagreement),
  `AppConfig.SyncAnchorToChordFingering` (which fixes it, to `IndexFret.Nearest`),
  the AI pass's fingering-only check and `ChordFingeringFollower` — a fixer that
  corrects an anchor the rule still flags, or flags one it just wrote, is worse
  than having neither. All three fold the capo in; the anchor floor is `capo + 1`
  (rule L02).

- **E04 reads an INFERRED index when no finger 1 is authored (#452).** An
  authored finger 1 wins (lowest fret for a barre; a finger 1 only on an open
  string states nothing and blocks inference). Otherwise the lowest finger *f*
  on fret *x* (thumb and open strings ignored) puts the index on `x − 1` for
  the middle finger — the owner's Am7, 2-3-4 on fret 5 under FHP 5, is E04 with
  "fret 4 (inferred)" in the message — and anywhere in `x − (f − 1) … x − 1`
  for the ring or little finger (`IndexFret.Allows`), because a ring-and-pinky
  grip on one fret is played with the index squeezed one fret below as well as
  two. Measured on the 183 verified corpus arrangements (2026-10-10, on
  78ee4c698): 4,339 of 45,356 fingered chords author no finger 1; the plain
  point `x − (f − 1)` added 622 findings, 414 of them that squeeze (a ring
  finger lowest, the charter's anchor one fret below it — mostly 3-4 double
  stops on one fret in two songs); the range adds 208 in 22 songs (173 middle-finger,
  35 ring-finger; 65 under a hand shape I46 also reports). A sample of 10: 3
  where the anchor should move (the issue's shape, an open-position grip, a chart
  that moved the anchor one chord late), 6 where the anchor is attested by the passage and the chord
  should be re-fingered (the same double stop fingered 1-3 elsewhere under the
  same anchor), 1 defensible as charted (a ring finger squeezed beside the
  pinky in a moving line). The message offers both fixes.

- **One mistake, one finding: I46 owns a no-finger-1 grip under a shape it
  reports (#618).** `IndexFingerAnchorRule` skips a chord whose index is INFERRED
  when a hand shape on its level, on the chord's template, covering it, is reported
  by I46 against the same anchor — it asks `HandShapeRule.MissingBarreAnchor`, the
  predicate I46 itself runs, so E04 reports again whenever I46 is excused (a
  neighbour's finger-1 barre) or the shape is another grip. I46's sentence then
  names the inferred fret and BOTH fixes. Measured on the verified corpus
  (2026-10-11, 183 arrangements): E04 242 → 174, I46 34 → 34, E04 findings also
  reported by I46 68 → 0 (`IndexAnchorFindingOwnerTests`).
- **The fix is `Editing/Anchors/IndexAnchorRepair`, and re-fingering is the
  DEFAULT when the arrangement attests it (#618).** Evidence: another template of
  the same frets (same arpeggio flavour) whose index the anchor allows, played by a
  chord whose governing anchor is on the SAME fret; the most-played one wins.
  Re-fingering REBINDS the finding's chord, the hand shape over it on that grip and
  that shape's other strums onto the attested template (no template is edited or
  minted, no note moves); with no evidence, moving the anchor to `IndexFret.Nearest`
  is the default; with both, the move is the alternative. Each is one undo step.
  The Issues tab offers them for the finding last jumped to
  ([inspector-mixer-and-fehler.md](../../ui/misc-surfaces/inspector-mixer-and-fehler.md)).
  On the corpus re-finger is the default for 66 of the 208 E04 + I46 findings.
  Limit: a grip the passage plays nowhere else (Paledusk's 2-3 at fret 19 under FHP
  17, notes on 17 around it) still defaults to moving the anchor.
- **The opt-in corrector (`SyncAnchorToChordFingering`) moves the anchor after a
  TYPED fingering** — re-fingering would undo what the user just typed. A RESHAPE
  is different: it takes the fingering of whichever template already has the new
  frets, so with the sync on `ChordTemplateEditPlanner.PreferGripTheAnchorAgreesWith`
  picks the agreeing grip `IndexAnchorRepair.AgreeingGrip` names, and the anchor
  stays (`AnchorFollowsFingeringTests`).

- **Every anchor-MINTING site owes the same two things: BOTH phrase stores and
  the `capo + 1` floor (0.44).** Three more sites were reading one store, and
  together they formed a loop that cancelled itself out on every imported
  chart: `ArrangementAutoFixService.AddMissingAnchorsAtPhraseStarts` and
  `ChartImprovementService.EnsurePhraseStartAnchors` add the anchor RS requires
  at a phrase start, and `ChartSpacingEnforcer.RemoveRedundantAnchors` — whose
  "protected positions" set was `EventPoint`-only — deleted it again on the very
  next save, *because a phrase-start anchor restates the position in force by
  construction and is therefore always "identical" to the previous one*. All
  three go through `PhraseMarkers` now. The two minting sites also clamp to
  `Math.Clamp(capo + 1, 1, 24)`: with no previous anchor the auto-fix minted a
  hard-coded fret `1`, i.e. the app creating the exact `L02` defect its own
  inspector reports, and copying an imported sub-capo anchor forward propagated
  it to every phrase start. Capo `0` stays a strict no-op.
  `PhraseAnchorFixerTests` pins all three plus the two vacuity keepers (the
  editor-authored `EventPoint` half still works; a restating anchor AWAY from a
  boundary is still dropped).
  **`Editing/FretAnchors/PhraseStartAnchors` is the one reading of E06 for code that
  ADDS such an anchor** (the phrase's first sound per level, "covered" anywhere from
  its start to that sound, the anchor in force before it): the auto-fix and the AI
  chat's sections & phrases job (#606, `AiStructureGate`) both go through it.
  `Needed` restates the anchor in force with the `capo + 1` floor; with none in force
  it takes the FHP generator's anchor at the phrase's first sound (the auto-fix keeps
  its own floor-fret fallback). It never moves or removes an anchor
  (`PhraseStartAnchorsTests`).

- **…and the PREVIEW anchor was a fourth minting site with no floor at all
  (0.50.1).** `FretAnchorInteractionTool.AddAnchorAt` says outright that "an
  explicit preview fret is the user having already said which fret they want, so
  it always wins" — it beats the capo-aware `AnchorFretSuggester`, which is only
  the fallback. But every writer of `ProjectState.PreviewAnchorFret` clamped at a
  bare `1` (`TimelineWheelAdjuster.TryAdjustPreviewAnchorFret`), so on a capo'd
  chart wheeling the FHP ghost down and clicking placed an `L02` — while the
  KEYBOARD nudge one line away in `AppKeybindRegistrar.NudgeAnchor` had always
  read the capo at press time. The floor is now
  `AnchorFretSuggester.MinFret(capo)` (the `Math.Clamp(capo + 1, 1, 24)` above,
  made public so it stops being copied), applied in the `ProjectState` setter
  and in the wheel so the ghost can still reach it; `0` stays the "nothing
  armed" sentinel every reader tests with `> 0`.
  `Services/State/PreviewAnchorFretCapoTests`.

- **A `ChartLevel`'s anchors are NOT sorted mid-session, and every validation
  rule reads them through `ArrangementValidationContext`, never by scanning.**
  `HighwayEditingService` runs the enforcer with `runCompatibilityCleanup:
  false` — the branch that skips `ChartSpacingEnforcer.SortByPosition` — so
  between a committed edit and the next save `level.Anchors` can be in any order
  at all. Anything that binary-searches them has to sort defensively first, and
  the sort must be **stable**: two anchors at one position are ordinary chart
  state, and the scans this replaced resolved a tie by taking the FIRST in the
  collection's own order (the loop only replaces its candidate on a strictly
  GREATER position). `ActiveAnchor`/`PreviousAnchor`/`AnchorAt` +
  `ChordTemplateById` on the context are the indexed answers; the `Find*Anchor`
  statics remain as their reference implementation and as the local-only probes'
  entry point. **A rule that calls the statics reintroduces the O(sounds ×
  anchors) pass** — `Validate` runs on the inspector's 150 ms debounce over
  EVERY level, so they are asked once per fretted note, once per fretted chord
  note, once per chord and several times per hand shape (~10 M iterations and
  ~15,000 boxed `ObservableCollection` enumerators on a flat 5,000-note chart).
  `AnchorIndexEquivalenceTests` is both the equivalence proof and the lint.

- **Generated FHP follows the context, and a FREE chord fingering follows the
  anchor (#494).** Measured with `tools/chart-eval` on the owner's 86 finished
  charts (train / holdout split by song; `--fhp-fingers auto` is what a user
  gets). The walk in `FretAnchors/FretAnchorGenerationService` now:
  - holds the hand for a FREE double stop (`freeGrip` resolver; two strings, D
    string or higher, at most 2 frets below the grip's index fret) unless the
    previous sound is a chord with the index on the anchor and the next 4
    positions stay above it (`CanHoldFreeShape`). Guitar only;
  - re-plants UP on GUITAR too ("abandonment"), but only after 14 positions
    above the anchor (`HandAbandonment.GuitarHold`; bass stays at 6);
  - and after the walk, `FretAnchors/AnchorSmoothing` drops A → B → A bounces
    whose middle span (≤ 8 struck positions) is playable from A, and on BASS
    pulls a forced up-shift back over the run (≤ 2 notes) that already played
    above the old anchor. Neither post-pass moves a slide landing.

  **The abandonment and both post-passes run for EVERY caller of the walk**, not
  just the two that pass a `freeGrip`: Refret, the export findings' FHP Fix and
  Generate FHP for a selection get them too.
  Templates are resolved by `Id` (`ChordTemplateResolver.IndexById`), never by
  list position, in the walk and the follower alike. Under a capo every path is
  the uncapoed result transposed (`FretAnchorContextTests`).

  | `auto` fingers | guitar FHP (train / holdout) | guitar changes gen / ref | bass FHP | bass changes |
  |---|---|---|---|---|
  | before | 83.3 / 90.1 % | 11,854 / 11,698 · 3,119 / 2,888 | 82.6 / 80.2 % | 7,157 / 7,912 · 1,814 / 2,035 |
  | after | 84.8 / 91.0 % | 10,849 / 11,698 · 2,959 / 2,888 | 84.1 / 81.7 % | 6,892 / 7,912 · 1,734 / 2,035 |

  Measured on main @ 538fe0b1d (#488's fingering rules merged; they did not move
  FHP). Chord fingering after the follower: train 80.1 → 80.5 %, holdout 84.2 →
  84.4 %. With the charter's own fingers (`reference`): guitar 87.4 / 91.6 → 88.4 /
  92.1 %, bass as above. **The Guitar Pro answer key disagrees** (`--gold`,
  one judge's reading of 12 tabs): 92.0 → 89.1 % (chord fingering 85.0 →
  83.9 %), of which the guitar
  abandonment is −1.5 pp (Rubato's pedal riff, where the judge keeps FHP 5 for a
  line on 8-7-6) and the double-stop hold −1.3 pp (Martian / Bubble, where the
  judge puts the index on the double stop — the opposite of what #494 reports
  from the owner's finished charts). Both are single constants to revisit if
  the owner sides with the key.
  **Rejected, measured:** a descending look-ahead merge (set the anchor at the
  lowest fret of the next stretch: +0.7 train, −0.2 holdout on chords only, a
  loss with notes); the bass pull-back on guitar (−0.5 / −0.4); holding 3+ string
  grips or the two lowest strings (holdout −1.2); a DP/Viterbi anchor chooser
  with HMM costs fitted on train (≈ the walk on guitar, +0.8 train / ±0 holdout
  on bass); skipping abandonment before a slide; a minimum 2-fret abandonment
  jump (keeps the key, loses the corpus gain).
- **A bass octave figure is anchored on its root, at the root (#501).** A
  fretted single note followed within the next two notes by the same note two
  strings up and two frets higher (`FretAnchors/BassOctaveFigures.FindStarts`;
  a repeated figure starts once) makes the walk plant the anchor ON the root
  there, even when the hand in force already reaches it — the index takes the
  root, the ring or pinky the octave. That anchor is never a group descend, no
  later note descends it, and it is PINNED like a slide landing (neither
  `AnchorSmoothing` pass drops or moves it). The bass pull-back also STOPS at
  any figure's octave note (`AnchorSmoothing.PullBackUpshifts`, `stops`), so a
  forced up-shift after the octave is never set on it and the root and its
  octave stay under one hand (`BassOctaveFigureTests`; measured −0.2 / +0.1 pp
  train / holdout against no stop, kept because a split figure is wrong by
  construction). The pair must SOUND an
  octave apart through the arrangement's tuning (`SoundingPitch.MidiFor`, as
  `TwoFretPairFingering.IsTwoFretOctave` asks; every caller passes the walk
  its arrangement as `tuning`): in drop D, low-string fret 3 + D-string fret 5
  is a ninth. Bass only. The charters do it at
  98.4% of 2,078 starts (`tools/chart-eval --octaves`); the walk before it
  missed 150, 145 of them exactly one fret low (the hand at root − 1 reaches
  the whole figure).

  | bass, `auto` | FHP train / holdout | changes gen / ref (train · holdout) | octave starts on the root (train / holdout) |
  |---|---|---|---|
  | before (#494 walk) | 84.1 / 81.7 % | 6,892 / 7,912 · 1,734 / 2,035 | 93.2 / 91.7 % |
  | after | 84.7 / 82.7 % | 7,052 / 7,912 · 1,812 / 2,035 | 99.8 / 99.8 % |

  Measured on the rebased tree (main @ 538fe0b1d + #494) with the rule switched
  off for "before"; 2,078 starts, unchanged by the tuning check.

  **Decided on the data:** slides do NOT exempt a figure — the charters anchor
  on the root 99.7% of the time after a slide into it and 100% when the root
  slides out, so a struck root triggers either way. A pitched slide that LANDS
  on the root keeps the slide's own anchor (P2): overriding it changed one
  sound in the corpus. Gap-2 figures (one note between root and octave) count:
  dropping them cost 0.2 / 0.1 pp. Open roots never trigger (an open note has
  no fret to anchor). **Rejected, measured:** letting the root descend the
  open group instead of planting (−0.02 / −0.04 pp train / holdout), leaving the root's group
  open to later descends (−0.07 / −0.10 pp), no pin (−0.30 / −0.54);
  the figure entered from its HIGH note (octave down): the charters anchor on
  the low note only 66% of the time and the walk already matches 68%; the rule
  on guitar (E/A-string roots): ±0.0 holdout, +0.1 train.
- **A bass hand covers three frets, and the walk knows it (#502).** Learned from
  `tools/chart-eval --sounds` (train split): the charters' bass anchor sits on
  the lowest fret of its span 97% of the time and an anchor + 3 governs 5% of
  their sounds (14% of the walk's). Three rules, bass only
  (`FretAnchors/HandAbandonment`, `FretAnchorGenerationService.BassDescendWidth`):
  - the abandonment re-plant HOLDS while the excursion comes back to the anchor
    within `BassStayScan` (24) positions without passing anchor + 2
    (`StaysWithinReach`; the charters re-planted on 8-33% of such runs, 72-78%
    of runs that leave the three frets);
  - it moves only onto the note that plays the new position's lowest fret —
    the charters move one note later than the look-ahead would;
  - a group descend may not leave the opening note at + 3 (the charters
    matched such descends 0-21% of the time).

  | `auto`, train / holdout | guitar FHP | bass FHP | bass changes gen / ref (train · holdout) | chord fingering after FHP |
  |---|---|---|---|---|
  | before (main @ 5473ccbab) | 84.8 / 91.0 % | 84.7 / 82.7 % | 7,052 / 7,912 · 1,812 / 2,035 | 80.5 / 84.4 % |
  | stay within reach | = | 85.2 / 84.8 % | 6,879 · 1,766 | = |
  | + onto the lowest fret | = | 85.2 / 85.2 % | 6,833 · 1,745 | = |
  | + descend width 3 | = | 85.9 / 85.6 % | 7,045 · 1,790 | = |
  | + scan bounded at 24 | = | 85.8 / 85.3 % | 7,047 · 1,792 | = |
  | + learned chord shapes (`core/guitar-and-tuning.md`) | 84.9 / 91.0 % | = | = | 81.4 / 85.7 % |

  The bound is not a corpus gain: train is flat from 16 up, and the Guitar Pro
  answer key, whose judge re-plants a long 2 → 4 bass figure, loses 0.9 pp of
  FHP instead of 1.5 (89.1 → 87.8 % overall with the shapes, chord fingering
  83.9 → 83.4 %). **Rejected, measured** (train / holdout pp): stay within + 3
  (−2.9 / −3.0); a three-fret reach for the hold itself (+0.7 / −0.2); the
  stay rule on guitar (−0.2 / ±0); moving only onto the lowest fret on guitar
  (−0.1 / −0.2); a bass look-ahead of 3 / 4 (+0.4 / −0.8, +0.2 / ±0); descend
  reach 1 / 5 on bass (+0.4 / ±0, −0.3 / +0.3); no abandonment when the next
  note leaves the new window (+0.1 / ±0); no pull-back, a pull-back run of 1, a
  pull-back box of 3 (−0.8 / −0.6, −0.2 / ±0, +0.1 / ±0); re-planting only
  from anchor + 2 or + 3 (−6 / −2 and worse); a walk that, forced to move, picks the anchor
  that lasts longest (−9 / −4).

  **Why 90% is out of reach here:** what is left is per-song style. The share of
  abandonment re-plants a charter accepts runs from 0% to 100% across songs,
  and choosing the best of 8 settings (look-ahead × reach) PER ARRANGEMENT —
  an oracle no rule can be — reaches only 88.2 / 87.8 % bass. Errors left on
  holdout bass: 6.1 pp under abandonment anchors, 3.8 under struck-walk
  anchors, 2.2 under descends; 2.3 pp of all are timing (the same frets one or
  two notes apart).
- **The accuracy floors run in the default gate without the corpus (#488 →
  #502, #511).** `ChartStyleFloorTests` scores `Fixtures/chart-style-floor.json`
  (24 windows × 48 struck positions per instrument, written by
  `tools/chart-eval --fixture`: frets, slides, hammer-ons, chord shapes with the
  charter's fingers, hand-shape spans and anchors at ORDINAL positions — nothing
  naming or dating a song) through the same pipeline chart-eval scores, and
  fails on one sound or chord below the measured counts (guitar FHP 998, bass
  992 of 1,152; fingering after FHP 581 of 650). A power chord or two-fret
  octave there follows the charter's fingering of the shapes first played
  EARLIER in its window (chart-eval's `--style prefix`, #511; 571 without it).
  Switching off the bass reach rules, emptying the learned shapes or dropping
  the style fails it. A change that gains raises the
  floors in the same commit; one that trades is measured on the corpus first,
  then re-floored and the fixture regenerated.
- **The variant is its own template — `FretAnchors/ChordFingeringFollower`.** A
  chord is FREE when its fingering is what `ChordTemplateFingerSetter` gives its
  frets (its BASE grip) — for a power chord or two-fret octave under the
  arrangement's own 1·3 / 1·4, counted over its OTHER templates
  (`ArrangementFingeringStyle.HintsFromOthers`, #511: a lone authored `1·4`
  stays authored) — or a variant the follower minted from it
  (`IsMintedVariant` = `ChordTemplate.FollowedFingering`, persisted in the
  `.rscp` as `followed="T"`, so it survives a reopen and a `ChartProjectCloner`
  copy alike; #508). Any hand edit of the fingering clears it, and a missing flag
  (an old project, an import) reads as authored. A uniform shift of the suggestion that
  a charter typed (a double stop fingered 2-2 or 3-4) is AUTHORED: treating
  every such shift as free re-fingered 3,187 charter chords on the corpus
  (`--fhp-fingers reference`, train) and cost guitar FHP 3.7 / 1.1 pp (train /
  holdout). Strings at or below the capo never shift (`SuggestedShift`). An
  authored grip is never touched and still pins its anchor. The walk reads a
  free chord through its base grip (`FreeGripResolver`), so a variant never
  implies its own root; where the anchor sits below the base grip's index fret
  the plan resolves the base grip shifted by the difference, and where it no
  longer does, the base grip itself (shift 0) — a second Generate FHP after the
  line that held the hand is gone puts the chord back. Each is resolved through
  `IFingeringGenerationService.FindIdenticalTemplate` (else minted) and the chord
  and the hand shape over it are repointed; the shared template is never
  mutated. A variant has no finger 1, so I46 / L08 stay quiet, and its
  inferred index (the base grip's lowest finger shifted up the hand) lands on
  the anchor that caused the shift, so E04 does too.
  Tools ▸ Generate FHP puts the minted templates, the repoints and the anchors
  in ONE undo node (`ChordFingeringFollowerTests`), and so does the AI chart
  pass's FHP-only mode (`AiChartApplier`, over the chords the model's anchors
  govern; `AiChartModeTests`). Generate FHP → save → reopen → delete the held
  line → Generate FHP puts the chord back on its base grip
  (`FollowedFingeringPersistenceTests`). Refret, Prepare for
  Rocksmith and Generate FHP for a SELECTION pass no resolver, so
  they never hold or re-finger.
  **The tab importers run it through `ImportFhpPass`, which differs in two
  ways.** A template a Guitar Pro builder created with a finger FROM THE FILE is
  authored even where it equals the suggestion
  (`GpAuthoredFingering.IsAuthoredTemplate`, marked in
  `ChordHandShapeBuilder`'s `ImportGripSource`). And with no undo to keep it for,
  a template the plan moved every chord and hand shape off is DROPPED and the
  ids above it close the gap (ids stay list positions; `0` is a real id, `-1`
  is never renumbered). Generate FHP keeps the old template (undo needs it), and
  it cannot know a hand-charted file's fingering was typed in: there a chord
  whose fingering equals the suggestion is re-fingered.

See also `FretAnchorGenerationService`'s group/descend rule and the four
falsified alternatives in
[import-tabs-audio.md](../import-tabs-audio.md) — they were measured on the
import path against the hand-corrected `TestData/EOF` corpus.

## Capo

- **Capo: frets are ABSOLUTE, fret `0` is the capo, and every fret consumer must
  fold `Arrangement.Capo` in.** Sounding offset is `max(fret, capo)` (see
  `DotCharter.Core/AGENTS.md` for the full invariant and the EOF relative-fret
  exception). Two consequences here: a sound at or below the capo is OPEN and
  never constrains the fret hand, and the lowest legal FHP anchor is `capo + 1`
  (an anchor on or below the capo is inspector rule `L02`,
  `CapoAnchorRangeRule`). `FretAnchorGenerationService` takes an optional `capo`
  (default `0`) and derives its whole floor from `MinAnchorFret = clamp(capo+1,
  1, MaxFret)` — the clamp matters, an uncapped `capo+1` can invert a
  `Math.Clamp(value, min, max)` range and THROW. **Capo `0` must stay a strict
  no-op** (`MinAnchorFret` = 1 = the constant it replaced); pinned by
  `Generate_Capo0_IsAStrictNoOp` and `SuggestChordNames_Capo0_IsAStrictNoOp`.
  Callers that own an `Arrangement` must pass its capo — `RefretService`,
  `ChartMaintenanceService` and both GP importers
  (`GpArrangementParser`, `GpLegacyImporter`) all do.

