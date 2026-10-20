# Auto-chart eval — the draft-from-audio score (`Analysis/AutoChartCorpusProbeTests`)

How good Tools → Chart from audio is, measured against hand-corrected charts
(#296). The live-play detection baseline it sits beside is in
[../services/live-play.md](../services/live-play.md) (§ Detection v8) and
`tools/detection-eval/README.md`; that one judges whether a PLAYED note is
credited, this one whether the app can WRITE the chart from a recording.

## The number now (#391, same corpus and method)

Same 89 arrangements, same MIX input (the harness never reads stems), after
#391's legato lead correction (`JuceGuitarEventDetector` reports legato events
40 ms later) and the CQT pitch stage (`AudioToChartPitchStage.ConstantQ` via
`CqtPitchEvidence`; a bass onset drafts at most two notes):

| | onset P | onset R | **onset F1** | pitch acc. | pitch-class acc. | string/fret | note P | note R | **note F1** |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| **ALL (89)** | 58.3 % | 62.1 % | **60.2 %** | 23.7 % | 40.5 % | 45.5 % | 10.1 % | 15.0 % | **12.1 %** |
| Lead (44) | 62.0 % | 59.9 % | 60.9 % | 23.8 % | 40.3 % | 46.5 % | 9.7 % | 14.8 % | 11.8 % |
| Rhythm (18) | 58.5 % | 64.5 % | 61.3 % | 21.7 % | 35.7 % | 55.2 % | 13.9 % | 13.9 % | 13.9 % |
| Bass (27) | 52.4 % | 65.0 % | 58.0 % | 27.9 % | 52.0 % | 26.1 % | 7.4 % | 18.2 % | 10.6 % |

The oracle offset diagnostic is now 60.8 % (was 59.9 %): the lead is gone. In
the app, the dialog also listens to an already separated lossless stem when one
is cached (`ChartFromAudioStemLocator`; never separates, never an Opus stem).
Without one it says the full mix is analysed and offers **Separate stems**, which
runs the app's own separation (`ISongStemSeparation`) only when pressed (#641).
`AutoChartStemSubsetProbeTests` scores stem against mix on the same excerpts.
The sections below are the 2026-10-07 baseline that motivated #391.

## The baseline (35 songs, 89 arrangements)

Corpus `TestData/EOF` (the owner's hand-corrected EOF projects, git-ignored):
35 songs with a mix WAV, 89 instrument arrangements (44 Lead, 18 Rhythm, 27
Bass; 9 vocal XMLs skipped), 71,528 charted onsets, 122,125 pitch-checkable
charted notes. Onset window **±50 ms**, the draft at the dialog's default
sensitivity (50 → `MinConfidence` 0.185).

| | onset P | onset R | **onset F1** | pitch acc. | pitch-class acc. | string/fret | note P | note R | **note F1** |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| **ALL (89)** | 44.6 % | 44.8 % | **44.7 %** | 10.9 % | 24.1 % | 45.8 % | 3.8 % | 4.8 % | **4.3 %** |
| Lead (44) | 49.1 % | 43.9 % | 46.4 % | 12.8 % | 24.8 % | 44.3 % | 4.8 % | 5.7 % | 5.2 % |
| Rhythm (18) | 45.5 % | 46.2 % | 45.9 % | 8.9 % | 19.8 % | 57.0 % | 5.1 % | 3.9 % | 4.4 % |
| Bass (27) | 37.1 % | 45.6 % | 40.9 % | 8.6 % | 30.6 % | 28.0 % | 1.4 % | 3.9 % | 2.1 % |

Spread over the 89 arrangements (min / p25 / median / p75 / max): onset F1
7.0 / 33.7 / 44.2 / 57.5 / 77.8 %; note F1 0.1 / 2.3 / 3.6 / 5.1 / 18.0 %;
pitch accuracy 1.6 / 7.9 / 10.0 / 14.2 / 38.3 %.

In words: the draft finds about half the onsets, and almost never the right
note at them. **It is not yet a chart; it is a rough onset map.**

## What limits it (same run)

- **Pitch, by far.** At the onsets it finds, 10.9 % of the charted pitches are
  drafted (single notes 14.7 %, chord tones 8.6 %); only 14.8 % of the misses are
  octave errors. The detector is the live-play `JuceGuitarAnalyzer`, built for a
  DI guitar; here it hears the whole MIX (vocals, drums, the other guitar). On
  clean synthetic plucks the same detector names every pitch right.
- **A constant onset lead of ~33 ms.** Median signed error, draft minus chart:
  −32.5 ms overall; per arrangement p25 −45 / median −36 / p75 −33 ms. Removing
  each arrangement's own median offset (an ORACLE diagnostic, not a product
  number) lifts onset F1 from 44.7 % to 59.9 %. On synthetic plucks with an
  instant attack the detector is within −5..0 ms, so the lead comes from real
  recordings (soft attacks, pick noise, another instrument's transient) or the
  charts' sync convention — not from a fixed offset in the analyzer.
- **Not the confidence floor.** Sweeping the sensitivity 0 → 100 moves onset F1
  only 43.6 → 44.8 % and note F1 3.7 → 4.3 %: the same detections are kept or
  dropped, so the slider cannot buy accuracy.
- **Not the grid snap.** Scoring the raw onset times instead of the snapped
  positions: onset F1 44.0 % (snapped 44.7 %).
- Window: ±30 ms 20.1 %, ±50 ms 44.7 %, ±100 ms 64.2 % onset F1 — the steep
  30 → 50 step is the lead above.
- string/fret (45.8 %) is only measured on pitch hits, i.e. 5,923
  notes; the analyzer's hand-position heuristic picks the charted string about
  half the time.

## Method

- **Input**: each song's own mix WAV (never a `*preview*` file, never a stem,
  never Opus). **No stems** — nothing is separated, nothing reads the stem
  cache — and no GPU: the detector is managed CPU code.
- **Pipeline**: the dialog's own path. `ChartFromAudioAnalyzer.OptionsFor(arrangement,
  ChartFromAudioAnalyzer.DefaultSensitivity)` (tuning, string count, capo from
  the arrangement) into `AudioToChartAnalyzer`. The detector runs once per song
  (`DetectEvents`) and each arrangement is drafted from the same events
  (`DraftFromEvents`); `Analyze` is exactly that composition
  (`AudioToChartAnalyzerTests.DetectThenDraft_IsTheSameDraftAsAnalyze`).
- **Beat grid**: the chart's own (hand-corrected) one — the best case. The
  product has no beat detection in this path (`BeatMapAnalyzer` is reached by
  no product code), so a grid from the charter is what the dialog gets too.
- **Ground truth**: the arrangement's HARDEST level (dense after import,
  `RsLevelLadder`), notes and chord tones. Full mutes are dropped; harmonics,
  unpitched slides and pre-bends count as onsets but not for pitch.
- **Alignment** (`AutoChartScorer`, pure): sounds within 0.5 ms are one onset;
  truth and draft onsets are matched one-to-one within ±50 ms by a two-pointer
  sweep (a maximum matching for points on a line). Inside each matched pair,
  notes are paired one-to-one by MIDI pitch, the charted string first.
- **Why ±50 ms**: the MIREX / mir_eval onset convention, and it covers the
  draft's own snap to a 1/12-beat grid (25–50 ms slots at 200–100 BPM). ±30 and
  ±100 ms are reported alongside.
- **Metrics**: onset P/R/F1; pitch accuracy = pitch hits / charted
  pitch-checkable notes at matched onsets; pitch-class accuracy the same mod 12;
  string/fret = pitch hits on the charted string (same pitch + same string is
  the same sounding fret, so a capo-stopped "open" string needs no
  reconciling); note P/R/F1 = pitch hits over all drafted / charted notes.
  Ratios are pooled from counts, never averaged, and 0/0 is 0.
- **Determinism**: the CSV carries a SHA-256 fingerprint of every draft. Two
  full runs (4 songs in parallel, different interleavings) produced a
  byte-identical `scores.csv`.

## Re-running it

Through the polite gate wrapper only (low priority, cores 8–15), never with
the GPU, one run at a time. 41–57 min for the whole corpus at
`DOTCHARTER_AUTOCHART_CORPUS_DOP=4` in the Debug build (the detector runs at
about 1–1.6× real time per song).

```bash
export DOTCHARTER_AUTOCHART_CORPUS=1
export DOTCHARTER_AUTOCHART_CORPUS_DOP=4                      # songs in parallel (default 1)
export DOTCHARTER_AUTOCHART_CORPUS_OUT='C:\path\outside\repo'  # report.md + scores.csv
export DOTCHARTER_AUTOCHART_CORPUS_LIMIT=3                     # optional: first N songs
export DOTCHARTER_AUTOCHART_CORPUS_COMPARE='C:\earlier\scores.csv'  # optional: must be byte-identical
powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -FilterClass "*AutoChartCorpusProbeTests"
```

The harness refuses an `_OUT` inside the repo, skips when the env var or the
corpus is missing, and asserts no quality number. Compare a new figure only
against this table at the same window and sensitivity, and say which.
