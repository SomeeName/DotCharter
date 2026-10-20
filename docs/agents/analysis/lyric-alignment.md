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
  - `LyricAligner` — folds token spans back up to syllables.
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

- **Lyric alignment accuracy is measured against TWO corpora and they disagree by
  24 points — always say which one a number came from.**
  `TestData/EOF` is four hand-synced ROMAJI vocal arrangements (2,036 syllables):
  **90% of onsets within 100 ms, 76% within 50 ms**, median |error| 28 ms, median
  signed −13 ms (slightly early — the direction listeners forgive; they tolerate
  ~0.33 s early vs ~0.22 s late, so ALWAYS report this signed). Harness:
  `LocalOnly_LyricAlignmentProbe`.
  Since 2026-07-28 there is also a 40-song ENGLISH set drawn from official
  Rocksmith vocal arrangements (14,272 syllables, `LocalOnly_LyricAlignmentPsarcProbe`):
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
