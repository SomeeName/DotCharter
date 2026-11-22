# Lyric alignment — the pipeline, the two models and every measured number

Deep reference for `DotCharter.Analysis/AGENTS.md`. `Lyrics/` turns pasted
lyrics into syllable onsets by CTC forced alignment. It is PURE: it never
touches audio, only a frames × symbols log-probability matrix the Services layer
hands it (`Services/Audio/Lyrics/LyricAcousticModel` — see
[../services/lyric-autosync.md](../services/lyric-autosync.md) for the
orchestration, the model catalogue and the installer).

**Every accuracy number in this file is measured, and several of them exist to
stop a plausible "improvement" being re-tried.** Always say WHICH corpus a
number came from — the two disagree by 24 points.

## `Lyrics/` — the types

- `Lyrics/` — **pasted lyrics → syllable onsets** (CTC forced alignment). Pure:
  never touches audio, only a frames × symbols log-probability matrix the
  Services layer hands it (`Services/Audio/Lyrics/LyricAcousticModel`).
  - `LyricSheetParser` — the paste format: newline = phrase, space = word,
    `-` = syllable. Deliberately identical to what `LyricsInspectorViewModel.
    Apply()` already parses AND to Rocksmith's own `-`/`+` suffixes, so a chart
    exported and pasted back round-trips losslessly.
  - `RomanizedLyricTokenizer` + `LyricVocabulary` — text → model columns.
    Diacritics FOLD (`ō` → `o`); doubling a long vowel is wrong because CTC
    requires a blank between two identical tokens, which would force a silent
    gap into a sustained note.
  - `CtcForcedAligner` — the Viterbi trellis (blank-interleaved `2N+1` states,
    backpointers packed 2 bits/cell). Label states are never skipped, so every
    syllable is guaranteed a non-empty span.
  - `LyricAligner` — folds token spans back up to syllables. Interpolated runs
    stay between adjacent measured STARTS even when onset corrections make the
    measured spans overlap (`LyricAlignerOnsetClassTests`). Cancellation is passed
    into `CtcForcedAligner` and checked through preparation, trellis and recovery.
- **The `<star>` wildcard's cost is 0.0 and that is MEASURED, not inherited.**
  A star wraps every line so instrumental gaps are absorbed by a wildcard
  instead of by a real syllable. At 0.0 it is ≥ every emission, so it provably
  eats the front of the syllable it precedes (pinned by
  `AnUnpenalisedStarEatsTheFrontOfTheSyllableItPrecedes`) — and penalising it to
  fix that measures WORSE, because the same penalty stops it absorbing long
  instrumentals and the aligner starts parking syllables there instead:
  90.4% → 88.9% of onsets within 100 ms, mean signed error −1 ms → +119 ms.
  Don't "fix" it without re-running the probe.

## Rules / gotchas — measured, and several falsified

- **Doubtful lines are rechecked against the voice, one voiced region at a time (#627).**
  `LyricAlignmentReview` flags low scores, interpolation, blank gaps over one
  second and adjacent onsets at most 80 ms apart whose CTC span is at most
  40 ms. With a stem envelope a blank gap is only a finding when a rest lies
  between the two onsets: a note held for two seconds is not a gap.
  `LyricVocalRegions` lists EVERY voiced region between the neighbouring lines
  (split at rests of 150 ms or more), because a line can hold a breath or an
  instrumental break. `LyricLocalRepair` gives each onset the region it sits on
  and re-runs the CTC trellis, without boundary stars, per region. **An onset on
  voiced frames never leaves its region; only an onset on unvoiced frames is
  moved, into the region nearest in time, also one where no other onset
  sits.** Forcing a whole line into its majority region used to drag correct
  syllables across a rest at confidence 0.01, and so did handing an onset
  100 ms ahead of its own region to its neighbours' region
  (`LyricLocalRepairRestTests`). Two exceptions, both
  for LOW-SCORE syllables only: the stranded opening, a line's FIRST syllable
  that sits on the previous line's held note (no pause since that line) while
  the rest of its line is in a later region, is treated as unvoiced; and the
  empty region described below. A line is left alone
  when fewer than half its onsets are voiced at all.
  The rules only PROPOSE starts; one gate decides, per syllable, whether a
  proposed start is taken (below). Original findings survive a repair;
  `Repaired` marks only a syllable whose start changed. With no stem envelope
  there is no local repair. This leaves the whole-song wildcard at zero; it is
  not a per-frame wildcard penalty.
  **The opening check measures from the voice, not from the previous line:**
  `OpeningVoiceStart` is the frame after the last pause since the previous
  line's CTC end; a pause is at least 20 ms under the threshold, because one
  frame is a consonant inside a word. A first onset more than 150 ms after it flags the
  opening pair; when the voice never paused (the previous line's note runs into
  this line) nothing can be said and nothing is flagged. An onset on unvoiced
  frames flags itself and its preceding neighbour. These checks apply even to a
  high model score. Measured on one hand-corrected song (359 syllables, 42 of
  356 matched starts moved by its owner, replayed emissions and real stem):
  173 flags before these two rules, 135 after; flagged starts the owner had not
  moved 135 -> 100 (86 of them low score alone), flagged moved starts 38 -> 35,
  no start more than 500 ms off unflagged in either.
  **One gate decides every move: a start moves only with evidence for the new
  place; without it the syllable keeps its old start, end and score and is
  flagged.** A flag costs a look, a wrong move costs a correct timing. Each
  rule that moved starts on its own (rest adoption, unheard opening, empty
  region, nearest region) was once caught moving a correct one, so the rules
  only propose and `LyricRepairGate` is the one place that knows what counts as
  evidence. For every syllable whose proposed start differs from its old one
  (G3 also for a line opening no rule proposed to move):
  - **G0, a heard start never moves.** Old score at least
    `LowConfidenceThreshold`: start, end and score stay; a start a rule wanted
    more than one onset away is flagged `Gap`.
  - **G1, letter evidence.** A low-score start moves where its own letters
    score at least that threshold AND strictly more than at its old start.
  - **G2, displacement proof**, for the unheard opening: the model emitted only
    blank while the line's voice began, so the forced path put the first
    syllable on the second one's letters and squeezed the second. Trigger: the
    first onset lies more than 150 ms after `OpeningVoiceStart` and stays late
    when its group is aligned inside the voice; `RealignBehindOpening` then
    proposes the rest of the group aligned without it and the first starting
    with the voice. The gate takes that start only when all three hold: the
    syllable's OWN letters score under the threshold at its old start (a start
    heard where it stands never moves, whatever its score says); the next
    syllable now starts on that old start, within one onset, with its letters
    at least the threshold there; and the two do not open on the same letter
    (then the next one's letters can be its own). A heard opening behind a
    breath, a backing vocal or an ad-lib keeps its start and its `Gap` flag,
    and so does a line of one syllable. Own letters beat displacement: when the
    place the line's own alignment gave the opening lies inside the span G2
    would give it and G1 takes it (against the old start, and scoring higher
    than at G2's place), the opening goes there by G1 in the same run; when
    they score no higher there than at G2's place, neither move is taken.
  - **G3, stranded line opening** (`LyricStrandedOpening`), settled after the
    others and whether or not a rule proposed a move: a line's first syllable
    the model never heard, left on the previous line's held voice (or in
    silence) while the rest of its line is sung after a long silence. It moves
    when all hold: its score is under the threshold; its OWN letters score
    under it at its old start (as in G2); nothing began there for it (the old
    start is on unvoiced frames, or on voice that began no later than one onset
    after the previous syllable started, followed back past the line's window
    to the last rest (`LyricVocalRegions.VoiceBegan`; one onset, so a held note
    that began with the previous syllable does not hang on one frame of
    aligner jitter); the song's first syllable only gets the unvoiced half);
    between the end of that voice (or the unvoiced start) and the voiced region
    holding the line's next syllable lies ONE unbroken silence, no voiced
    region at all (a faint pick-up of the syllable's own breaks it, whatever
    its letters score), at least the `Gap` flag's blank (`MaxBlankGapSeconds`,
    1 s) long; and the target,
    that region's start, is not before the previous line's last end. It ends
    where the next syllable starts. When that leaves less than one onset
    (`SameOnsetSeconds`), the next syllable is pushed to one onset after the
    target, only when its own score is under the threshold, the gate kept it
    where it stood, and it keeps a length and stays before the syllable after
    it; otherwise neither moves and both are flagged `Gap`. A moved or pushed
    syllable keeps its score and is flagged `Gap` + `Repaired` (placed by
    structure, not heard). Its own letters heard anywhere inside the span it
    would get (as a G1 start) refuse it: they would prove a place of their own. Deliberately NOT covered: any syllable that is not
    a line's first; a first syllable heard where it stands; one with an onset
    of its own before a rest (a sung pick-up: its voice began after the
    previous syllable); a silence shorter than the blank. The late opening
    pairs stay flag-only.
  - Anything else keeps its old start and is flagged. A taken start that leaves
    the line (before the previous line's last end, past the next line's first
    start), has no length, or breaks the order next to a neighbour is withdrawn,
    and so is that neighbour.
  - **Last, the ends.** No syllable ends past the next one's start. Next to a
    moved start an end is cut back to that start (an end is derived data, so
    cutting it is not a move and needs no evidence; it keeps at least one
    onset); two kept neighbours keep the ends they came with. Where the cut
    would leave less than one onset, the MOVE is withdrawn and flagged `Gap`,
    never the kept neighbour changed; G3's opening and its push stand or fall
    together. These withdrawals run to a fixpoint, and in it G3 is placed again
    on the settled starts: an opening (with its push) whose next start was
    withdrawn after it was placed, so that G3 no longer gives the same span,
    goes back to its old start and is flagged `Gap`. Across a line boundary the bounds above already hold every taken
    start at or after the previous line's last end, and the previous line's
    last syllable and the next line's opening are neighbours: letters heard
    before the previous line's settled end are that line's, and prove no G1
    move of the opening (`LyricLetterEvidence`'s `notBefore`).
  The empty region is proposed the same way: a low-score syllable whose measured
  span crosses a rest into a voiced region where no syllable of its line starts
  is proposed where that voice resumes (`ClaimEmptyRegions`), and G1 takes it
  only where its letters are; voice without its letters is a breath or another
  voice. Voice that no rest separates from the next line's first onset is that
  line's and is never proposed (a next line that repeats the syllable would
  give it letters there, so G1 alone does not cover it).
  **Two kinds of evidence, both from the letter model** (the voice only says
  where a proposal may go, never that it is right). LETTERS
  (`LyricLetterEvidence`): the syllable's letters split into groups, each run of
  vowels and each run of consonants; a group scores its strongest letter
  emission, so ONE letter of a run is enough ("ng", "ee", "str" are one sound
  to a letter model, which often emits only one of them), and the syllable
  scores its WEAKEST group. The first group must sound within
  one onset of the start, the others from there to the end of the proposed
  span. A strong vowel cannot carry a consonant run the model never emitted (a
  geometric mean over the letters let 0.9 and 0.003 through at 0.052).
  DISPLACEMENT: the next syllable's letters, scored the same way, at the old
  start, with the syllable's own letters absent there.
  **The flags are a function of the final starts.** The voice a line is checked
  against reaches to the next line's first start, which that line's own repair
  may still move, so after every start is final each line runs once more
  through the same rules and gate in a pass that only flags (nothing is taken).
  A repaired syllable keeps the flags its repair was decided on; a kept one
  keeps what it came with plus that last pass. Running the repair on its own
  output changes no start, end, score or flag, with one deliberate exception:
  a line is repaired against the next line's opening where the aligner put
  it. When G3 then moves that opening off the line's held voice, a second run
  would search that voice again and can move the line's last syllable into
  its own held note. The product never feeds the repair its own output
  (`LyricAligner` calls it once, after placement). Repairing the line again
  inside the run was measured and not taken: it moved a correct start later
  on the reference song.
  **Proved by a seeded property test, not by examples**
  (`LyricLocalRepairPropertyTests`, default gate): four seeds, 2,000 generated
  sheets of 1 to 3 lines of 1 to 8 syllables, scores on both sides of the
  threshold, single letters and multi-letter runs ("eng", "see", "str"),
  letters present, missing, faint, shifted, only some letters of each run, or
  heard again at a second place slightly stronger, as strong or slightly
  weaker, at strengths on both sides of the floor (0.94, 0.3, 0.06, 0.045,
  0.03), displaced openings with and without a shared first letter, 0 to 4
  voiced regions (breaths, rests inside a line, voice into the next line) at
  levels on both sides of the line's voice threshold, and unheard syllables
  stranded before a silence on both sides of the 1 s blank: a line's first one
  in silence or on the previous line's voice (begun well before, just within
  or just past one onset after the previous syllable, sometimes behind a
  lead-in near the threshold), either sometimes with a short voice of its own
  inside the silence, on a pick-up of its own, or one inside its line; a
  displaced opening whose own letters sit later inside its voice; a later
  line's opening without letters after the previous line's voice runs on,
  where that line sounds the opening's first letter once more. Per
  case, from what the generator placed and never through the product's
  predicates (letters scored per run as above, each line's voice re-derived
  from the levels): heard starts unchanged; every moved start satisfies G1,
  G2 or G3 (a pushed neighbour only as G3's push); order, bounds, positive
  lengths; no end past the next start, a kept start's end changed only by
  that cut and left at least one onset; `Repaired` exactly when the start
  moved; a second run equals the first; and every seed moves at least five
  stranded openings. Disabling G0, G1, the same-letter guard, the old-start
  condition of G2, the final flag pass, G3's "nothing began there", G3's
  silence, its unbroken silence, G3's first-syllable restriction, the end
  cut, its one-onset room, G1's second look on a cut span, own letters before
  displacement, the previous line's letters refused to an opening, or G3's
  refusal when its letters sit inside its span each turns it red.
  That second look: a G1 start whose end is cut is judged again on the span it
  keeps, because letters past the next start are the next syllable's (the
  wider generator caught a move carried only by them).
  Same song as above (reference sheet, 42 of 356 matched starts moved by its
  owner), the gate gives back more of the gain and moves nothing wrong. Two
  starts still move (a late opening and the syllable squeezed behind it, both
  within 17 ms afterwards); G3 moves the stranded opening the raw path left
  6.6 s early on the previous line's note to 24 ms before its owner's start and
  pushes the syllable behind it from 127 to 67 ms early (both flagged); the
  previous line's last syllable, no longer cut off by the stranded start, now
  ends 343 ms late instead of 137 ms early: the end refiner
  (`LyricSyllableEnds`, line-final) follows its voice to the fade, 20 ms before
  that voiced region ends, which is the intended answer (the voice holds on
  past the end its owner set). The end rule of the gate does not touch it: the
  gate keeps it, and its own end stays before the moved start. The unbroken
  silence, the end rule and the one-onset tolerance changed no start, end,
  score or flag on that song. Flag-only: the other late opening
  and its second syllable, and two small nudges of 20 and 60 ms. Moved starts
  median 106 ms, p90 1,003 ms, worst 1,606 ms, 12 over 500 ms (before G3 123 /
  1,061 / 6,634 ms, 13; before the gate 101 / 998 / 1,606 ms, 11); 136 flags (35 of the 42 moved starts, 101 of the
  314 kept ones), no start more than 500 ms off unflagged. One start its owner
  kept is now 60 ms from it: the gate refuses the nudge that put it there, and
  it stays where the model placed it, flagged `LowScore`. Left as they are,
  all flagged, because the audio evidence is missing: seven labels on 4.4 s of
  continuous voice with no letter posterior (dips of the stem envelope match
  all seven onsets there, but the same rule misplaces seven correct syllables
  of another line by 0.6 to 2.1 s, and an even spread leaves three over
  500 ms: do not ship either without a second corrected song); two syllables
  on voice carried over from the previous line with several short pauses
  behind them; one syllable whose letters score 0.39 at the wrong place.
  **The thresholds that remain, and what each means.** The voice thresholds
  were swept alone before the gate existed, over the synthetic
  `LyricLocalRepair*` classes and that song's replay ("same" means every start
  and every flag); they are not re-swept under it. A range that ends inside a
  factor of two is FITTED, not measured.
  - `LowConfidenceThreshold`, 0.05: one number, one meaning, "the model heard
    this". It is G0 (a start scored at least this stays), the G1 floor of a
    syllable's weakest letter group at the new place, the support the next
    syllable needs in G2, the ceiling for the syllable's own letters at its
    old start in G2 and G3, and the score under which G3 may move or push.
  - One onset: one emission frame plus the model's vowel correction (20 ms in
    the tests, 70 ms on that song's model), not a constant. How far a letter
    may sit from a start, how close the next syllable must land in G2, and how
    far a refused proposal must be before G0 or the gate flags it, and the
    least room G3 leaves between a moved opening and the next syllable.
  - `MaxBlankGapSeconds`, 1 s: the `Gap` flag's blank inside a line, reused as
    the silence G3 needs between a stranded opening and its line's voice. Not
    swept: the one stranded opening on that song has 6.1 s.
  - `MinPauseSeconds`, 20 ms (two envelope frames): the shortest stretch under
    the stem threshold that counts as the pause before a line; one frame is a
    consonant inside a word. Tests same 20 to 100 ms; at 10 ms the consonant
    case fails. Song: starts same 20 to 100 ms, two more flags from 30 ms and
    four from 70 ms. FITTED: it flips at half its value, on one line of one
    song.
  - `MaxOpeningLeadSeconds`, 150 ms: how long after its voice a first onset may
    start before the opening is flagged and G2 is asked. A trigger and a flag
    only. Tests same 75 to 190 ms (one case is built 200 ms late). Song: starts
    same 50 to 400 ms, flags same 100 to 400 ms, four more flags at 75 ms.
    FITTED at the low end for flags, not for starts.
  - `OnsetLeadSeconds`, 80 ms: how far an onset may lead the first voiced frame
    and still count as voiced (a consonant before its vowel). Tests same 40 to
    95 ms: the case of an onset just ahead of its own region is built 100 ms
    ahead, so it is defined against this value, not a measurement of it. Song
    same 40 to 240 ms; three more flags at 20 ms.
  - `MinRestSeconds`, 150 ms, as the guard between an empty region and the next
    line: the definition of a rest, re-used. Tests same 75 to 300 ms; at 0 a
    next line that repeats the syllable pulls it over.
  The next line's lower bound is the preceding line's repaired end, so two
  local repairs cannot claim the same acoustic frames across a line boundary.
  The synthetic cases are in `LyricLocalRepairTests`,
  `LyricLocalRepairRestTests`, `LyricLocalRepairUnheardTests` and
  `LyricLocalRepairConfidenceTests`.

- **Lyric alignment accuracy is measured against TWO corpora and they disagree by
  24 points — always say which one a number came from.**
  `TestData/EOF` is four hand-synced ROMAJI vocal arrangements (2,036 syllables):
  **90% of onsets within 100 ms, 76% within 50 ms**, median |error| 28 ms, median
  signed −13 ms (slightly early — the direction listeners forgive; they tolerate
  ~0.33 s early vs ~0.22 s late, so ALWAYS report this signed). Harness:
  `LyricCorpusProbeTests` (corpus-skipping; the corpus and its current tables are
  in `docs/agents/services/lyric-autosync.md` § Probes).
  Since 2026-07-28 there is also a 40-song ENGLISH set drawn from official
  Rocksmith vocal arrangements (14,272 syllables; now exported by the add-on's lyric-truth exporter (its `Local/` probes) into the same `LyricCorpusProbeTests` corpus):
  **66% within 100 ms, 42% within 50 ms** on the mix, **75% / 47% on a separated
  vocal stem**. The romaji figure shipped for several releases as THE accuracy
  claim; it is the optimistic end of the range, because those four charts have
  sparse vocal-forward mixes. The gap is a mixing property, not a language one.
- **Aligning against an isolated vocal stem BEATS the mix, measured** (English set,
  paired per song: stem wins 36, loses 2, ties 2; p90 |error| 5,862 → 1,082 ms).
  This runs counter to the published source-separation ablations, and the reason it
  still holds is specific: those ablations measure MUSIC-trained systems, for which
  separation is a distribution shift, while MMS-FA is SPEECH-trained, so isolation
  moves the input TOWARDS its training distribution. `LyricAutoSyncService` uses a
  stem only when one already exists on disk — it never starts a separation.
- **TWO acoustic models since 0.41, routed by `Lyrics/LyricScriptDetector`, and the
  router must stay biased towards MMS-FA.** MMS-FA aligns romanized text in 1,100+
  languages; the `wav2vec2-large-960h-lv60-self` head aligns exactly one, so a
  wrong "English" hands a German or Spanish sheet to a model that has never seen
  the language while a wrong "not English" merely forgoes an improvement. FIVE
  rules must all agree before it says English: ≥12 syllables, vowel-final share
  <0.70, mean syllable length ≥2.4, ≥2% of syllables containing `l/q/v/x`, and ≥2
  distinct English function words. The first four separated the two real corpora
  with NO overlap (romaji 0.962-0.985 / 1.99-2.05 / 0.000; English 0.217-0.457 /
  2.82-3.53 / 0.066-0.337), so the thresholds sit in empty bands. **The
  function-word test is the load-bearing one**: German and French pass all four
  phonotactic rules, and MMS-FA is right for both. Validated end-to-end over all
  44 real sheets by `LocalOnly_LyricAlignmentSweep.
  TheLanguageRouterAgreesWithBothCorpora` (40 English + 4 romaji, all correct);
  the function-word test reads WORDS, not syllables, because the sheet splits
  `a-bout` in two and neither half is a word.
  **English accuracy (40 official arrangements, 14,352 syllables, on a stem):**
  within 100 ms **74.5% → 75.2%**, within 50 ms **46.0% → 57.1%**, median |error|
  **54 → 40 ms**, p90 **1558 → 601 ms**; paired per song **26 wins / 14 losses**,
  best +37.4 pp, **worst −43.0 pp** — a favourable trade, not a strict
  improvement. `wav2vec2-base-960h` was measured and REJECTED (loses the 100 ms
  bucket outright at 70.5%).
  **Romaji accuracy — the other half of the split, measured 2026-07-31**
  (`CompareAcousticModelsOnRomaji`, 4 hand-synced arrangements, 2,036 syllables,
  on a stem). MMS-FA **90.5%** within 100 ms / 75.8% within 50 ms / |med| 27 ms /
  p90 **96 ms**; `wav2vec2-large-960h-lv60-self` 84.3% / 66.7% / 29 ms / p90
  133 ms; `wav2vec2-base-960h` 71.1% / 52.4% / 44 ms / p90 276 ms. **So the
  router is buying accuracy in BOTH directions, not trading one corpus for the
  other** — English head wins English by 0.7 pp on the 100 ms bucket and 11.1 pp
  on the 50 ms one, MMS-FA wins romaji by 6.2 pp and 9.1 pp — which is the
  evidence that the split earns its complexity. Two things worth knowing before
  re-opening this: the English head is far from useless on Japanese (84.3% having
  never seen the language, so a MISROUTE is a degradation and not a disaster,
  which is what makes the router's MMS-FA bias the cheap side to err on), and the
  star cost is inert here too — the whole 0.00…−1.00 sweep moves MMS-FA/romaji by
  ≤0.2 pp, the same null result the English corpus gave. Do not re-sweep it
  without a new hypothesis about what the wildcard is for.
- **The two heads are ENSEMBLED, not chosen between (0.41.1, `CtcEmissionBlender`).**
  The errors arrive in coherent RUNS (177 runs, mean 9.1 syllables) — the trellis
  losing the thread through one hard passage, not jitter — so two independently
  trained heads lose it in different passages and blending lets one carry the
  path where the other is confused. Measured 2026-07-31 on a stem:
  **English** ≤100 ms 76.7% → **79.2%**, p90 **602 → 292 ms**, paired 28 W / 9 L,
  worst-case song −38.9 → **−5.9 pp**; **romaji** ≤100 ms 90.5% → **91.3%**,
  p90 97 → **92 ms**, paired **4 W / 0 L**. The ≤50 ms bucket gives back ~3 pp on
  English — the deliberate side of the trade, because a syllable 600 ms out must
  be dragged by hand and one 60 ms out need not be.
  Four rules: **(a) renormalize every blended frame** — a weighted mean of two
  log-softmax rows is not one, and the trellis compares absolute scores ACROSS
  frames, so an unnormalized row silently weights some frames more than others;
  **(b) weight 1.0 returns the primary INSTANCE**, so the single-model path is
  byte-identical rather than merely equivalent; **(c) the onset bias belongs to
  the COMBINATION** (`LyricModelSelection`), not to either model — blending pulls
  the English median to −14 ms, so the correction flips from −10 ms solo to
  +10 ms ensembled, and one bias per model would have been wrong in both
  directions at once; **(d) a symbol only one head knows keeps the primary's
  opinion**, and case needs no handling because `LyricVocabulary.FromTokens`
  already folds tokens to lower case — which is what makes the two alphabets
  comparable at all.
  Blend weights are per-language (English 0.60, romaji 0.75) because the optima
  differ and the router already knows which case it is in. Cost is a second
  encode pass; one model missing degrades to the other alone.
- **The English model carries a −10 ms onset bias and MMS-FA carries none — that
  asymmetry is measured, not arbitrary.** Averaged over 14,356 syllables the vocal
  stem's onset strength peaks **30 ms BEFORE** the professional charter's marker,
  and on the owner's own romaji charts it peaks at −10 ms: the two human
  conventions disagree by ~20 ms. MMS-FA's median error of −24 ms therefore means
  it already fires AT the acoustic onset, so a bias would move it AWAY from the
  voice (and the sweep agrees — +30 ms buys the 50 ms bucket and COSTS the 100 ms
  one). The English head fires ~11 ms late of the same reference, so −10 ms moves
  it TOWARDS the voice and lifts both buckets (100 ms 75.2% → 76.7%). The bias
  belongs to the MODEL (`LyricModelCatalog`), never to the caller.
- **These references are only ~28 ms precise.** The professional annotations sit a
  median 28 ms from the nearest detectable vocal onset, and only 77% within 50 ms
  — which is exactly the aligner's median absolute error on the romaji corpus. On
  romaji it is already AT the annotation noise floor; do not chase the 50 ms
  bucket there.
- **The remaining English tail is COHERENT, and the star is not the mechanism.**
  11.2% of syllables land worse than a second, in only 177 runs (mean 9.1
  syllables) — blocks of consecutive lines parked seconds away, not scattered
  misses. Falsified against the corpus, do not re-try: a uniform star penalty
  (every value worse, and the tail gets WORSE — p90 1558 → 3392 ms) and a
  silence-aware per-frame star cost priced off the stem's own energy (19
  configurations, all slightly worse). The reason both fail is that CTC's own
  BLANK already absorbs frames near-free on a stem, so no star price can constrain
  position. And some songs are simply unreadable: `AFI - Totalimmortal` scores
  0.0% within 100 ms on ALL THREE models — a free decode of its shouted vocal
  returns vowel mush, which is a limit of speech-trained models, not a search bug.
- **Which syllables start late: vowel-initial ones, not unvoiced-consonant ones —
  measured, and the owner's hypothesis was the opposite.** Romaji corpus on a vocal
  stem (2,036 syllables, signed onset error, pred − hand-synced), by the syllable's
  first sound: vowel n=258 **median +62 ms, 61% later than 50 ms**; voiced
  consonant n=1,023 −11 ms; unvoiced consonant n=643 **−24 ms** (ts −6, ch −23,
  sh +20 — only `sh` is late). CTC fires on the confident vowel; a vowel after a
  vowel (`a-i`, +70 ms, n=133) or a breath has no consonant to anchor the frame.
  The fix is a flat **−50 ms on vowel-initial syllables** (`SyllableOnsetClassifier`,
  `LyricAlignmentOptions.VowelInitialOnsetBiasSeconds`, multilingual head only,
  clamped so a start never passes its predecessor's): vowel median +62 → +12 ms,
  late > 50 ms 61% → 20%, whole chart within 50 ms **77.3% → 82.1%**, within 100 ms
  **91.9% → 93.7%**, p90 85 → 73 ms (mix: 75.1% → 79.7%, 89.4% → 91.1%). Sweep:
  −20 ms 79.9/93.1, −30 81.0/93.5, −40 81.9/93.5, −50 82.1/93.7, −60 82.6/93.6,
  −70 82.5/93.6 (within 50 / within 100) — −50 chosen to stay short of the optimum,
  because early is the forgiven direction and the English head is unmeasured (it
  carries 0). **Falsified, do not re-try:** snapping each start to the first clear rise
  of the stem's RMS envelope within 60-100 ms BACK (and 0-20 ms forward) of the CTC
  start. For unvoiced syllables it never helps (within 50 ms 77.3% -> 76.5-77.2%;
  the class is already early, and a sung consonant's energy rise is not reliably
  before the vowel's); for vowel syllables it helps a little alone (77.3% -> 78.2%,
  vowel median +62 -> +52 ms) and HURTS once the -50 ms shift is in (82.1% -> 79.0-81.9%
  for every window and rise ratio tried). The flat per-class shift does the work.

## Anchor-first alignment (#830) — design note

**Why.** On the Jamendo set two thirds of the gross (>1 s) onset errors are words the
model never heard: its output over their true spans is almost all blank. One forced
path over the whole song has nothing to hold such words, so it piles them next to the
nearest heard words — on a neighbouring line (66%), on stem bleed after the last sung
line (16%), on the wrong repeat of a chorus (11%). Tuning the one path (star cost,
blank penalty, spreading unheard runs) tops out at 3.47% gross; see the issue.

**How (`Lyrics/LyricAnchors`, `Lyrics/AnchoredCtcAligner`).**
1. **Free decode** the emissions the aligner already has (greedy best path: argmax per
   frame, repeats collapsed, blanks dropped) into a letter stream with the frame of
   each letter. No new model.
2. **Match** that stream to the sheet's letters by a monotonic edit-distance
   alignment (unit costs). An **anchor** is a lyric WORD whose letters all matched
   exactly and consecutively and whose letters the model heard with confidence. A
   matched run must also be UNIQUE in the sheet: the letter n-gram the anchor sits in
   (the word plus its neighbours until the n-gram is long enough) must occur once in
   the whole sheet's letters. A repeated chorus line therefore anchors only through
   the words where it differs from its other repeats (a line boundary into a
   different line), never on its shared middle — that is what keeps it off the wrong
   repeat. Anchors are thinned so each span keeps enough frames for its tokens.
3. **Force-align each span between consecutive anchors on its own** with the existing
   `CtcForcedAligner`, over that span's frames only. An anchored span starts AT its
   anchor letter, so it carries no leading star (a free star would eat the onset).
   An unheard word can now only move inside its span, between two heard words.
4. **The tail** ends at the stem's last voiced frame (under 5% of its 90th-percentile
   level) plus 1.5 s (`AnchoredCtcAligner.VoiceEndFrame`), so an unheard last line cannot
   be stretched over the band's bleed after the vocals end. Without a stem the whole song
   stays available. 15% of the loud level cut fading last notes (en lost 5 onsets in the
   100 ms bucket); 5% and 2% measure the same.

Everything downstream (fold to syllables, interpolation, review, local repair) is
unchanged. With no anchors one span covers the song; if a span has no path, the
whole-song path runs as before.

**Measured** (vocal stem, gross = word onset > 1 s off / within 100 ms):

| Set | Before | After |
|---|---:|---:|
| Jamendo overall | 3.57% / 89.1% | 3.38% / 89.2% |
| English | 5.20% / 84.8% | 5.20% / 84.8% |
| French | 5.38% / 86.2% | 5.29% / 86.2% |
| German | 1.73% / 93.5% | 1.28% / 93.9% |
| Spanish | 1.73% / 92.3% | 1.48% / 92.4% |
| Romaji | 2.43% / 94.0% | 2.43% / 94.0% |

**What did not reach 2%, and why.** Anchoring alone moved 11 of 23,616 onsets: the
whole-song Viterbi path already passes through the words the model hears, so cutting it
there changes almost nothing. The remaining gross errors are whole SECTIONS the model does
not hear (runs of 30-60 syllables at confidence ~0.003), and nothing in the emissions says
where inside their window they were sung. Two spreads were measured and NOT shipped:
- unheard = no letter in an exact decode run of 3+: half of all syllables qualify
  (MMS-FA's free decode is sparse), 17.4% gross;
- runs of 10+ syllables under confidence 0.01 spread over the voiced frames, moving only
  those more than 1 s from their spread place: 3.01% gross, but English loses
  0.1-0.3 points in the 100 ms bucket at every setting tried.

## Syllable splitting (#869)

`Lyrics/Syllables/`: `LyricSyllabifier` (one word -> syllables: LibreOffice/TeX hyphenation patterns read by
`HyphenationPatterns` from gzip resources for en/de/fr/es, then no-vowelless-syllable, silent final e/ed/es and
opening-vowel rules), `LyricSheetSyllabifier` (splits a sheet ONLY when it has no hand-hyphenated word at all: any word the parser
returns as 2+ syllables (`HasHandHyphenation`) means the charter split by sung notes, so nothing is auto-split, EN/DE/FR/ES
and romaji/kana alike; a lone dash is no syllable and does not count; function-word
language detection first; a romaji sheet (no l/q/v/x, 95 % of words valid, 12+ words) or a kana word goes to
`ApplyJapanese`), `RomajiMoraSplitter` (kya/shu/cho = one mora, n/m before a consonant or at the word end = one, the
first letter of a doubled consonant = one, every vowel one, macron vowels stay whole; null for non-romaji) and
`KanaMoraSplitter` (kana morae). A kana sheet is only split: the aligner still has no kana path.
`LyricSyncOptions.SplitUnhyphenatedWords` (default on) applies it in `LyricAutoSyncService.Align`. Pieces carry
`LyricSyllable.SplitByRules`; `LyricSyllableVowelSnap` moves the start of such a piece that continues a word from its
first letter's frame to the first frame of its vowel token on the CTC path (no vowel, vowel-initial, or a word's first
syllable = the path's start stands; starts stay monotonic and inside the word). Today's behaviour was no splitting at
all. The eval corpora are word-level, so they cannot judge syllable counts: word-onset numbers are identical or within
0.1 point before/after.

## Pooled path (#830, second pass)

**Why.** The geometric blend (`CtcEmissionBlender`) needs BOTH heads to agree on a letter: a
weighted mean of log-probabilities lets one head's confident blank drown a word only the other
head heard. On Jamendo the English head and MMS-FA hear different words of the "unheard"
sections, so most of what PR 860 called unheard was heard by one head.

**How.**
- `Blend` also returns `CtcEmissions.Pooled`: the same frames as a linear opinion pool (a
  mixture of the two heads' probabilities, renormalised). The primary's share is
  `CtcEmissionBlender.PoolPrimaryWeight` = 0.3: the pool exists to recover what the primary
  missed. Measured share 0.3 / 0.5 / 0.7: Jamendo gross 1.86% / 2.11% / 2.38%.
- `LyricAligner` runs the anchored path over the blend AND over the pool, and
  `Lyrics/LyricPooledPath.Merge` keeps the blend's spans for every syllable the two place within
  `AgreementSeconds` (0.3 s) of each other (the blend is the sharper one) and takes the pool's
  where they disagree by more.
- Romaji keeps the blend alone (`LyricScriptDetector.IsLikelyRomaji`): the English head's letters
  on Japanese syllables are guesses, and the pool lost romaji 0.2-0.7 points in the 100 ms bucket
  at every share tried.

**Measured** (vocal stem; gross = word onset > 1 s off; PCO = within 0.3 s; AAE = mean |error|):

| Set | Before #835 gross / ≤100 ms / PCO / AAE | PR 860 | Pooled path |
|---|---|---|---|
| Jamendo overall | 3.59% / 85.8% / 94.5% / 0.309 s | 3.38% / 89.2% / 94.7% / 0.260 s | 1.86% / 90.3% / 96.2% / 0.155 s |
| English | 5.27% / 74.0% / 92.4% / 0.483 s | 5.20% / 84.8% / 92.6% / 0.462 s | 2.90% / 86.1% / 94.5% / 0.273 s |
| French | 5.40% / 85.8% / 91.6% / 0.363 s | 5.29% / 86.2% / 91.6% / 0.335 s | 3.00% / 87.9% / 93.8% / 0.180 s |
| German | 1.73% / 92.5% / 97.3% / 0.194 s | 1.28% / 93.9% / 97.7% / 0.113 s | 0.70% / 94.6% / 98.7% / 0.085 s |
| Spanish | 1.73% / 92.2% / 97.1% / 0.178 s | 1.48% / 92.4% / 97.3% / 0.106 s | 0.68% / 93.2% / 98.2% / 0.070 s |
| Romaji | 2.43% / 94.0% / 96.7% / 0.120 s | 2.43% / 94.0% / 96.7% / 0.120 s | 2.43% / 94.0% / 96.7% / 0.120 s |

**Measured and not shipped:** anchors from each head alone, merged with the blend's (no change:
2.12% with the pool, 3.38% without); spreading low-confidence runs over voiced time on top of the
pool (2.09% at share 0.5, at most 14 words moved); the pooled path alone without the agreement
merge (same gross, but German 93.8% and Spanish 92.1% in the 100 ms bucket, below PR 860). What is left is concentrated: sustained
vowel lines ("oh" runs) that neither head hears, and last lines over long stem bleed.

## Onset snap (#868)

**Why.** CTC fires somewhere inside a syllable's first sound, so an onset can be right to
100 ms and still be 30-50 ms off where the sound starts. On Jamendo the aligned onsets sit
early in Spanish and French (median -30 to -40 ms) and close in English.

**How (`Lyrics/VocalOnsetFeatures`, `Lyrics/LyricOnsetSnap`).** After alignment, and only on
a vocal stem, `LyricAutoSyncService` reads three onset functions on 32 ms Hann frames at a
5 ms hop (`JuceFft`), computed only within reach of the aligned onsets: whole-band
log-magnitude flux, flux in 2-8 kHz, and the rise of a normalised autocorrelation peak over
70-1000 Hz (pYIN-style voicing). The syllable's first letter (`SyllableOnsetClassifier`)
picks the function: unvoiced consonant = 2-8 kHz flux; voiced consonant = whole-band flux;
vowel = whole-band flux plus the voicing rise. The strongest peak 50 ms before to 80 ms after
the onset wins when it is at least 10 and at least 3x (unvoiced: 8x) the median over
±300 ms. An onset never comes within 50 ms of its neighbours' starts or its own end; a
previous syllable that would overlap is ended at the new onset. Interpolated syllables stay.
Romaji is skipped (`LyricOnsetSnap.AppliesTo`). `LyricSyncOptions.OnsetSnap = null` turns it off.

**Measured** (vocal stem; PCO50 = word onset within 50 ms; gross unchanged everywhere):

| Set | Before PCO50 / ≤100 ms / PCO / AAE | After |
|---|---|---|
| Jamendo overall | 66.4% / 90.3% / 96.2% / 0.155 s | 67.9% / 90.4% / 96.2% / 0.154 s |
| English | 65.5% / 86.1% / 94.5% / 0.273 s | 65.8% / 86.2% / 94.5% / 0.273 s |
| French | 60.8% / 87.9% / 93.8% / 0.180 s | 63.4% / 88.0% / 93.8% / 0.178 s |
| German | 75.3% / 94.6% / 98.7% / 0.085 s | 76.6% / 94.7% / 98.7% / 0.083 s |
| Spanish | 64.2% / 93.2% / 98.2% / 0.070 s | 66.3% / 93.3% / 98.2% / 0.068 s |
| Romaji (skipped) | 84.0% / 94.0% / 96.7% / 0.120 s | same; snapped it measured 83.6% / 93.8% |

**Measured and not shipped:** the same 3x threshold on unvoiced syllables (Jamendo PCO50
68.4%, but ≤100 ms 89.8%: the 2-8 kHz flux jumps to the previous word's sibilant); 2x on
voiced and vowel syllables (PCO50 68.9%, ≤100 ms 90.2%; German and English lose); 5x and 8x
everywhere (67.4% and 66.8%, French gains least); a distance penalty (no change); a larger
voicing weight (no change).
