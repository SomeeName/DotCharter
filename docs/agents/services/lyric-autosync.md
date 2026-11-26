# `Audio/Lyrics/` — lyric auto-sync (deep reference)

Extracted verbatim from `DotCharter.Services/AGENTS.md`. **Read this before
touching the acoustic model, the model catalogue/installer or the auto-sync
orchestration.** The alignment MATH is pure and lives in
`DotCharter.Analysis/Lyrics/` — read `DotCharter.Analysis/AGENTS.md` first; this
file is the service half (models, decode, stem selection, windowing, install).

---

## `Audio/Lyrics/`

Stem time-base consumers checked for #619:

| Consumer | Same mistake? | Source of audio |
|---|---|---|
| Lyric editor and standalone CLI | Fixed | The editor's captured stem list; folder discovery only without a list. Both go through the duration guard in `LyricAutoSyncService`. |
| Fit tab to recording | Fixed | `Editing/Tempo/RecordingAlignment/RecordingAlignmentAudio.cs` takes declared instrument stems and checks each duration before summing. |
| Guitar roles | No | `Audio/StemSeparation/StemFolderProbe.cs` supplies the declared, resolved guitar path; `GuitarRoleStemService` derives from that file and keys its sidecar by its content. |
| Tone match | No | `Ai/Match/StemIsolator.cs` signs the input song and checks its content-keyed cache, rather than looking for a sibling guitar file. |
| Tone sections (proposal and matching) | No | `Ai/Match/Sections/ToneSectionProposer.cs` and `ToneSetMatcher.cs` use that same isolator for the requested song and time window. |
| Tone generation from placed changes | No | `Editing/Tones/ToneGenerationRun.cs` captures declared project stem paths; no folder guess. |
| AI tone generation, creator and feedback | No | `Ai/Generate/RsToneGenerator.cs`, `ToneCreator.cs` and `RsToneFeedbackLoop.cs` use the content-keyed isolator. |
| Live note detection | No | `DotCharter.UI/ViewModels/Editor/LivePlayViewModel.Tick.cs` reads live input capture, never song stems (`LiveDetectionAudioSourceTests`). |

- `Audio/Lyrics/` — **lyric auto-sync**: paste a song's lyrics, get every
  syllable timed. `LyricAcousticModel` (ONNX wav2vec2 CTC) +
  `LyricAudioSource` (decode → 16 kHz mono) + `LyricAutoSyncService`
  (orchestration → `LyricPhrase`s on the beat grid, applied through
  `SetVocalPhrasesAction` so one undo takes it back). The alignment MATH is pure
  and lives in `DotCharter.Analysis/Lyrics/` — read that guide first.
  - **The model is MMS-FA, and it aligns ROMANIZED text.** That is the whole
    design: the owner charts Japanese as romaji, so Japanese and English share
    one 31-symbol alphabet, one model and one code path — no kana conversion,
    no G2P, no lexicon. Ship the **int8** export (340 MB, single file);
    measured indistinguishable from fp32 (1.26 GB + external weights) because
    alignment reads which symbol wins per frame, not the fine structure.
  - **TWO models since 0.41, resolved by `LyricModelCatalog`:** `lyric-align`
    (MMS-FA, multilingual, the default) and `lyric-align-en`
    (`wav2vec2-large-960h-lv60-self`, English only, OPTIONAL). Roots are searched
    **exe-relative `models/` first, then `%APPDATA%/DotCharter/models/`** — so a
    shipped or portable copy is found with no setup while a hand-installed model
    keeps working. The router is `Analysis/Lyrics/LyricScriptDetector`; the
    measurements and the five agreement rules are in `DotCharter.Analysis/AGENTS.md`.
    Three rules: **(a) a missing English model falls back to the multilingual one**
    — it is an optional download, so English must still align, a little less well,
    rather than refuse; **(b) `IsModelInstalled` means the MULTILINGUAL one** — the
    English head's absence must never read as "not installed"; **(c) the per-model
    onset bias lives on `LyricModel`, never on the caller** (MMS-FA 0, English
    −10 ms), because it describes where that CTC head fires inside a syllable.
    A blend carries its own bias on `LyricModelSelection`: romaji 0, English
    −35 ms plus −30 ms on vowel-initial syllables (JamendoLyrics, below).
    **(a) covers a BROKEN one too, and that is a fourth rule because `IsInstalled`
    cannot tell the difference:** it is a bare presence check, so the ensemble path
    is taken the moment two files exist, and a truncated `model.onnx` / an
    unsupported opset / a CUDA OOM with both the 340 MB and 303 MB sessions alive
    used to fail the WHOLE run. The optional head's load, encode and blend live
    inside `LyricAutoSyncService.BlendOptionalSecondary`, which answers any
    non-cancellation failure with the primary's own emissions and one warning;
    pinned by `LyricSecondaryModelDegradationTests`, including a source lint that
    the ensemble branch still routes through it.
    A failed secondary also restores the primary's SOLO onset and vowel corrections,
    description and `IsEnsemble` flag; `LyricSyncSeamTests` compares the degraded
    timings against an explicit solo run for both primary models.
    A `modelDirectoryOverride` PINS one folder and disables selection entirely, so
    a test cannot silently depend on which models happen to be on the machine.
  - **It listens to an already-separated VOCAL STEM when one exists, else the
    MIX — and it never starts a separation of its own** (`VocalStemLocator`,
    0.40). The published ablations on separation are negative but measure
    MUSIC-trained systems, for which separation is a distribution shift; this
    model is SPEECH-trained, so isolation moves the input TOWARDS its training
    distribution. Measured 2026-07-28 over 40 official English arrangements
    (14,272 syllables): within 100 ms **66.2% → 75.5%**, p90 |error| **5,862 →
    1,082 ms**, paired per song **36 wins / 2 losses / 2 ties**. The tail is what
    moves; the median barely does, because songs that already worked were never
    the problem.
    Three rules fell out of it: **(a) never separate on the user's behalf** — they
    asked for lyrics to be timed, not for a minutes-long GPU job; **(b) the stem
    name test is EXACT, never `Contains("vocal")`** — the complement stem is
    called `no_vocals`, and picking it still aligns, still returns every syllable,
    and simply places them badly, with no exception and no log line; **(c) quote
    BOTH corpora.** The romaji `TestData/EOF` figure (90% within 100 ms) shipped
    for several releases as the accuracy claim and is the optimistic end of a
    24-point range — those four charts have sparse, vocal-forward mixes.
    The editor snapshots the project's stem names and paths in `LyricAutoSyncRunner`.
    A nonempty project list is authoritative: `VocalStemLocator` selects the vocal
    role OR bare filename (excluding the instrumental half in either name), and
    resolves its path through `StemFolderProbe.ResolveStemPath` (project
    directory first, the mixer's legacy directory second). An absent or missing
    voice in that list means mix, never an unlisted `stems/vocals.wav`. Only an
    empty list or a project-free CLI run permits the folder guess. With an empty list,
    the editor checks the project's `stems/` first, then the resolved audio's `stems/`;
    a project with audio stored elsewhere must still find its project-side voice (#572).
    The legacy base for declared stems is the resolved audio's directory.
    Resolution runs
    on the sync worker through `LyricAutoSyncOptions.ToSyncOptions`.
    Every candidate, explicit or discovered, must match the decoded mix's duration
    within 100 ms (`StemTimeBase`, allowing codec padding). Otherwise the mix is
    used, the vocal envelope is omitted, and a warning names both files and both
    durations in the log and result. `LyricAutoSyncOutcome` retains the seam's
    warnings and actual `AudioPath`; the applied status already displays `AudioSource`.
    Regression coverage: `LyricSyncSeamTests`, `VocalStemLocatorTests` and
    `LyricAutoSyncRunnerTests`, with injected samples and acoustic sessions.
    A nonzero declared `Stem.Offset` is refused before the duration comparison;
    the mix is used with a warning naming the offset. Only unshifted stem samples
    are compared with the mix, so the duration check compares the same time base.
    The mixer currently passes only name/path in `StemLoadRequest`; it does not
    implement the serialized offset. Sync must not infer offset support from playback.
    Owner's confirmed durations: mix and padded vocals 272.453 s, original vocals
    260.453 s. The 100 ms tolerance accommodates the padder without accepting that
    12 s mismatch.
  - **The lyric model runs on DirectML through `OnnxSessionFactory`'s Auto
    ladder** (DirectML → CPU; #151): any DirectX 12 GPU, no runtime to register,
    `DirectML.dll` ships beside the exe. It opens on DXGI adapter 0 (the display
    adapter), unlike stem separation, which picks the fastest. Checked 2026-10-05 on
    `lyric-align-en-v1`: DirectML logits match the CPU's argmax on 99.9 % of frames
    (relative L2 3.9e-2), with graph fusion on or off.
  - **A run that fails on the GPU is retried on the CPU, per model, for the rest
    of the process** (`Audio/Lyrics/LyricProviderFallback.cs`). The ladder only
    falls back when a provider fails to ATTACH; graph construction can fail after
    attachment, and a graph can create on DirectML and
    still fail its first `Run`. `LyricAutoSyncService.OpenSession` wraps every head
    (the optional second one too) in `LyricProviderFallbackSession`: a failed
    `Encode` on a non-CPU provider logs ONE warning, disposes the GPU session,
    re-opens the same model on the CPU and encodes the WHOLE song again (never a
    per-window retry: a half-GPU, half-CPU matrix mixes two numerics), and
    `LyricProviderMemory.Process` keeps that model on the CPU, so a later sync
    opens it there directly. A failure on the CPU, and cancellation, are not
    retried. Each model is remembered on its own: an ensemble whose English head
    fails keeps the multilingual head on the GPU. A session reports the provider it
    really attached through `ILyricAcousticSession.ActiveProvider` (default: CPU,
    which is never retried). Pinned by `LyricProviderFallbackTests`.
    Non-CPU graph OPEN failures use the same remembered CPU fallback.
    `LyricAcousticModel.Load` owns the session options before graph preprocessing
    starts, so a failed in-memory rewrite releases them too
    (`LyricAcousticModelDisposalTests`).
    **Why the published models need it:** both v1 exports (the archives the app downloads) are `torch.export` graphs, which
    write `allowzero=1` on every `Reshape` (145 nodes multilingual, 97 English), and
    ONNX Runtime 1.24.4's DirectML provider fails the first `Run` on that
    (`Reshape 'node_view_1'`) for every window length, adapter, optimisation level
    and graph-fusion setting. The shapes contain no zero dimension, so dropping the
    attribute is a no-op on the CPU and makes the graph run on DirectML (about
    130 ms per 24 s window against 3.1 s on the CPU). **The fix (#222) is in
    the app, not in the archive:** the download stays `lyric-align-v1.zip` /
    `lyric-align-en-v1.zip` (no `-v2` asset is published), and
    `LyricAcousticModel.Load` strips the attribute IN MEMORY for a GPU session
    when the installed file still has it (`Audio/Lyrics/OnnxReshapeAllowZero`, a
    protobuf rewrite that copies every other byte; the file on disk is untouched).
    A re-export without the attribute is the way to produce a future archive
    (`tools/lyric-sync/export-models.md`); stripping v1 gives that file byte for
    byte. Measured on the romaji probe corpus with the stripped model on DirectML
    against the CPU, accuracy is within 0.5 points on every row, and the run takes
    96 s instead of 282 s.
    Pinned by `OnnxReshapeAllowZeroTests` and the catalogue pins (v1 names and hashes) in
    `LyricModelGraphTests`; with model folders configured, also by the file scan
    there and `LyricModelDirectMlSmokeTests`. The CPU fallback stays for every
    other failure.
  - **`LyricAcousticModel.Encode` reports the frames it really WROTE, never
    `samples / 320`** (`Audio/Lyrics/LyricWindowStitch`). A song is encoded in
    overlapping windows, and wav2vec2's conv front end emits **n − 1** rows for
    320·n samples — so the last window is one row short and the song's final
    target row stayed as allocated: all zeros. A zero row in a LOG-probability
    matrix is probability 1.0 for every symbol, i.e. the cheapest frame
    `CtcForcedAligner`'s trellis can find, strictly ≥ every real emission — which
    inflated `CtcAlignment.TotalLogProb` and the last syllable's `Confidence`
    whenever its span reached the end, and that confidence feeds the
    user-visible `LowConfidenceCount`. The stitch arithmetic is a pure internal
    type (`Plan` / `CopyCount` / `FramesWritten`) because the model it drives is
    340 MB and cannot run in the default gate; `LyricAcousticModelWindowingTests`
    sweeps it. `FrameCount` is the CONTIGUOUS prefix — a hole is not a shorter
    matrix.
  - Decode via `PcmAudioLoader` then `SampleRateConverter` (Lanczos-8), never
    `JuceAudioDecoder.DecodeAs` — that resamples LINEARLY, and a 3:1 decimation
    folds 8-24 kHz straight back onto the formants the model reads.
  - **An in-phrase span ends at the next start minus note spacing.**
    `LyricPhraseSpacing` uses `ChartSpacingEnforcer.GetMinDistance` at the earlier
    syllable's start. Auto-sync always spans syllables within a word.
    `LyricSpanWordGap` defaults on and spans across words within a phrase.
    With it off, word endings keep their measured duration up to the same spacing
    limit. Lyric imports and the inspector's **Close gaps in phrases** command
    follow the same preference. Timing gestures use the same spacing limit.
  - **No placed syllable is 0 long or shares the previous start (#1041).** The LAST
    step of `BuildPhrases`, after the grid fold and `CloseGaps` (both can collapse a
    syllable), is `LyricMinimumTiming` over `Core/Domain/Vocals/VocalTiming.EnforceMinimum`: the export's own
    25 ms rule, plus every syllable at least 25 ms or the room to the next start. Only
    the syllables it changes move, onto the next 1/192 tick, never past the next
    start. `LyricAlignmentReview` judges `Compressed` on the RAW spans, across a line
    end too, so the spread pair stays flagged (`LyricMinimumTimingTests`).
  - **Phrase-final chart ends use the alignment and vocal envelope by default.**
    `LyricSpanPhraseGap` defaults off. Turning it on extends the last syllable
    toward the next phrase using the measured span rules. `LyricSyllableEnds.ComputeWithReview`
    measures ends up to the next onset and follows any sustained voice on the
    stem. A hold that reaches the duration cap retains its review finding.
    `LyricAutoSyncOptions.FromConfig` reads both editor settings.
    The CLI uses the same measurements with its seconds-based span options.
    `LyricSyncEndParityTests` pins the shared phrase-final behavior at the defaults.
  - **Review findings are chart data (#627).** Each `AlignedSyllable` retains
    the original path's low-score/gap/compression findings after local repair;
    `BuildPhrases` carries its score and reasons into `LyricEvent`, adding the
    shared end rule's cap-hit finding. The low-confidence count includes an
    original low score even if the repaired path scores higher. Native saves
    use optional vocal attributes; older charts and interchange exports keep
    their existing shape. The row mark and inspector navigation read these
    findings, rather than trying to reconstruct them from the summary count.
    The CLI JSON also includes each syllable's `reviewReasons`, so a higher
    repaired confidence does not hide the original finding from scripts.
  - **Vowel-initial syllables are the late ones, not the unvoiced-consonant
    ones** (`LyricAlignmentOptions.VowelInitialOnsetBiasSeconds`, fed from
    `LyricModelSelection.VowelInitialOnsetBiasSeconds`, −50 ms for the
    multilingual head only). See lyric-alignment.md for the measurement and the
    envelope onset snap that was tried and rejected.

## Probes: the corpus, and what it measured

The accuracy numbers on this page come from committed probes that skip without
the corpus (#237), so anyone with the inputs can rerun them:

- `DotCharter.Tests/Services/Audio/Lyrics/Probes/`. `LyricProbeCorpus` is the
  on-disk format: `corpus.json`, plus one folder per song holding `mix.f32` and
  `vocals.f32`, raw 16 kHz mono decoded once. `LyricProbeReport` is the pure
  arithmetic, pinned in the gate by `LyricProbeReportTests`. Errors are SIGNED,
  positive = late, and "late" means more than 50 ms.
- **Building the corpus** (`LyricCorpusBuildProbeTests`): `ExportRomajiSources`
  writes `*.lyric-truth.json` sources for the four hand-synced romaji charts in
  `TestData/EOF`. The add-on's lyric-truth exporter (its `Local/` probes) writes the same sources
  for the first 40 stock English vocal arrangements, because only the package
  reader can open them, and it needs the real keys (`DOTCHARTER_RS_KEYS`).
  `BuildCorpus` then separates every source with `htdemucs-6s-onnx`,
  downloading it into `DOTCHARTER_LYRIC_STEM_MODELS`, never the user's models
  folder. It runs about 25 s a song on DirectML.
- **Measuring** (`LyricCorpusProbeTests.MeasureCorpus`): it aligns every song on
  the mix and on the stem through the real `Align` seam and folds them onto the
  song's own beat grid through `BuildPhrases`. The report covers onsets overall
  and by `SyllableOnsetClassifier` class, all ends, phrase-final ends, a
  further-vowel-shift what-if and the vocal-end threshold sweep. It writes the
  markdown to `DOTCHARTER_LYRIC_PROBE_OUT`. The variables are listed in
  `DotCharter.Tests/AGENTS.md`.

**Romaji, vocal stem** (4 songs, 2,036 syllables, measured 2026-10-23; the sheet
runs the `lyric-align` + `lyric-align-en` ensemble, w 0.75, with its −50 ms
vowel-initial shift):

| | n | median | p90 abs | within 50 ms | within 100 ms | late > 50 ms | early |
|---|---:|---:|---:|---:|---:|---:|---:|
| onset, all | 2,036 | -14 ms | 77 ms | 81.6% | 93.1% | 8.5% | 65.7% |
| onset, vowel-initial | 258 | +13 ms | 88 ms | 74.8% | 93.0% | 17.4% | 36.8% |
| onset, voiced-initial | 1,023 | -12 ms | 54 ms | 88.3% | 95.6% | 6.9% | 66.3% |
| onset, unvoiced-initial | 755 | -22 ms | 101 ms | 75.0% | 89.7% | 7.7% | 74.8% |
| end, all | 2,036 | -4 ms | 106 ms | 75.1% | 89.5% | 9.6% | 53.2% |
| end, phrase-final | 175 | -13 ms | 333 ms | 45.1% | 62.9% | 18.9% | 58.3% |

On the MIX the onsets are a little worse (90.8% within 100 ms, p90 90 ms). The
phrase-final ends are where the stem matters: 5.7% within 100 ms, median
−274 ms, because there is no envelope to follow.

- **The threshold sweep (30-50% of the phrase level, held 30/50/80 ms) does not
  beat the shipped 30% / 50 ms.** Every step up in ratio trades phrase-final
  within-100 ms for a lower late share: 62.9% / 18.9% late at 30%, then 58.9% /
  13.7% at 40% and 50.3% / 10.3% at 50%. All-ends within-100 ms falls too
  (89.5% → 87.1%). Within 30%, a 30 ms hold is 1.7 points better phrase-final
  (64.6%), which is three syllables of 175, so the default stays.
- **The vowel-initial shift is about right.** After the ensemble's −50 ms,
  vowel-initial onsets still sit at +13 ms against −16 ms for consonants. A
  further −25 ms centres them (−12 ms) and halves their late share (17.4% →
  8.5%), but within-50/100 ms do not improve (74.8 → 74.4%, 93.0 → 92.6%) on
  258 syllables of one artist, so the constant stays where it is.
- **English is measured on JamendoLyrics instead (below).** This machine has no
  Rocksmith keys, so the stock arrangements cannot be exported. The recipe for
  the stock English corpus, when the keys are there: set
  `DOTCHARTER_RS_KEYS`, `DOTCHARTER_LYRIC_SOURCES` and `DOTCHARTER_LYRIC_LIBRARY`,
  run the add-on's lyric-truth exporter (filter `*LyricTruthExport*`), then `*LyricCorpusBuildProbeTests`
  and `*LyricCorpusProbeTests`. The report then gains an `english` section in
  the same shape.

**JamendoLyrics, word onsets on the vocal stem** (79 songs, 21,580 words, one
syllable per word, raw `Align` seconds; measured 2026-11-22). The English-primary
blend used to run a median +52 ms late. Same German annotations, two routes: the
songs the router sent to the English head sat +28 ms, the multilingual ones
−19 ms, so the lateness is the blend's, not the language's or the reference's. Its
old +10 ms bias had been fitted to Rocksmith charts, whose onsets sit ~25 ms
after the voice. Vowel-initial English words were later still (+71 ms against
+44 ms). Now −35 ms, plus −30 ms on vowel-initial syllables (an offline sweep of
both, then this re-run):

| within 100 ms | en | fr | de | es | romaji (4 songs) |
|---|---:|---:|---:|---:|---:|
| before (+10 ms, no vowel shift) | 74.0% | 85.7% | 92.5% | 92.2% | 94.0% |
| after | 84.8 | 86.2 | 93.5 | 92.3 | 94.0 |

FR/DE/ES move only on the few songs the router reads as English. The re-run is the
git-ignored local Jamendo onset probe (not committed) in the test project (build
with `IncludeLocalOnlyHarnesses=true`, set `DOTCHARTER_LYRIC_JAMENDO` to a corpus
in `LyricProbeCorpus` form and `DOTCHARTER_LYRIC_CORPUS` to the romaji one); it
fails under English 82% or 0.5 points under any other "before" value.

**Model trials (#870, measured 2026-11-22): nothing beat the shipped aligner, so
nothing ships.** Same corpora and seam as above (vocal stem, word onsets, raw
`Align` seconds). "gross" = more than 1 s off, PCO = within 300 ms, AAE = mean
absolute error. The baseline is the shipping router and ensemble.

| romaji (4 songs, 945 words) | within 50 ms | within 100 ms | PCO 0.3 s | gross | AAE |
|---|---:|---:|---:|---:|---:|
| shipped ensemble (baseline) | 84.0% | 94.0% | 96.7% | 2.43% | 120 ms |
| shipped multilingual head alone | 83.3 | 93.3 | 95.9 | 3.07 | 171 |
| A: kana CTC head alone (romaji turned into kana) | 62.0 | 91.9 | 95.4 | 3.39 | 236 |
| A: English INT8 head in place of `lyric-align-en` | 83.8 | 94.0 | 96.7 | 2.43 | 122 |
| B: phoneme refiner, every onset | 58.2 | 81.1 | 93.0 | 1.90 | 119 |
| B: phoneme refiner, only moves over 500 ms | 83.3 | 93.2 | 96.2 | 2.01 | 90 |

| JamendoLyrics English (5,693 words) | within 50 ms | within 100 ms | PCO 0.3 s | gross | AAE |
|---|---:|---:|---:|---:|---:|
| shipped (baseline) | 65.5% | 86.1% | 94.5% | 2.90% | 273 ms |
| A: English INT8 head in place of `lyric-align-en` | 64.7 | 85.3 | 93.7 | 3.86 | 413 |
| B: phoneme refiner, every onset | 44.1 | 66.1 | 86.0 | 4.53 | 350 |
| B: phoneme refiner, clamped to ±100 ms | 46.6 | 74.0 | 94.7 | 2.95 | 291 |
| B: phoneme refiner, only moves over 500 ms | 63.2 | 82.9 | 90.8 | 4.37 | 309 |

The English INT8 head also leaves FR/DE/ES within ±0.1 point (87.8 / 94.6 / 93.3%
within 100 ms against 87.9 / 94.6 / 93.2%).

- **A: singscope-align** (Hugging Face `shinjibass/singscope-align`, Apache-2.0,
  INT8 dynamic quantisation, ONNX opset 17; `en/` 317 MB, `ja/` 320 MB). **Dropped.**
  `en/` is the same `wav2vec2-large-960h-lv60-self` as `lyric-align-en`, quantised
  again: it can only match or lose, and it loses (gross 2.90 → 3.86%). `ja/` is
  `reazon-research/japanese-wav2vec2-large-rs35kh`: a 3,003-symbol kana/kanji
  vocabulary whose CTC blank is id 0 (`<unk>`), while its `vocab.json` lists 3,004
  entries (`<s>`, `</s>`, `<pad>`, `|` sit past the 3,003-wide output). Fed the
  romaji turned into hiragana, it loses to the multilingual head alone on every
  metric, and even after an in-sample shift for its −37 ms median it stays behind
  (81.2% against 84.8% within 50 ms). It cannot join the ensemble without a
  cross-alphabet blend, and it would not help as a solo head.
- **B: HubertFA v0.0.7** (GitHub `wolfgitpr/HubertFA` release asset, Apache-2.0
  code, the release names no separate weight licence; 257 MB zip, 415 MB
  `model.onnx`, raw 44.1 kHz waveform in, 10 ms frames, its own CMUdict and romaji
  dictionaries). Used as a refiner: each line of aligned words, ±0.5 s, forced
  through its phoneme sequence (Viterbi on the frame and edge outputs), and the word
  onset taken from its first phoneme. **Dropped.** It is much less precise than the
  CTC heads (within 100 ms falls by 13-20 points) and does not even cut the gross
  errors in English. Gating it to large moves only fixes a handful of romaji words
  while costing English 3 points within 100 ms. SOFA was not tried: it is the same
  model family (HubertFA is built on it), with a non-commercial weight licence.

## The SEAM: everything above the beat grid, and the standalone tool on top of it

**`ILyricSyncCore.AlignAsync` is the chart-free half, and
`LyricAutoSyncService` implements it.** Text + audio in, syllable SECONDS out:
the stem choice, the decode, the model router, the ensemble and the trellis.
Everything the chart owns — the beat grid, the span rules, `SetVocalPhrasesAction`
— stays above it, in `SyncAsync`/`BuildPhrases`. `SyncCore` is now four lines:
guard `no-beats`, call `Align`, `BuildPhrases`, report.

- **It is a seam, not a second implementation, and that is load-bearing.** The
  standalone `dotcharter-lyricsync` CLI constructs the SAME
  `LyricAutoSyncService` and calls `AlignAsync`, so anything that regresses the
  app regresses the tool and vice versa. `LyricSyncSeamTests
  .TheAppsAutoSyncRoutesThroughTheSeam` is a source lint on exactly that call —
  a fork would pass every behavioural test in the file and still drift.
- **`LyricAutoSyncOptions.ToSyncOptions()` is the ONE projection and it is
  one-way.** It carries onset bias, star cost and provider, resolves the captured
  project stem references to a path plus a discovery policy, and DROPS the beat-grid ones. A knob left out of it is a
  setting that silently stops working in the app while the CLI keeps honouring
  it, which is why `TheProjectionCarriesEveryAlignmentKnob` enumerates them.
- **The seam accepts an EXPLICIT stem and still never separates.**
  `LyricSyncOptions.VocalStemPath` names a file; null falls back to
  `VocalStemLocator` only when `DiscoverVocalStem` is true. A named stem that is missing is a hard `no-stem` failure
  rather than a quiet fall-back to the mix — the caller said which file, and the
  mix is measurably worse, so degrading silently would read as a bad model. A
  named stem whose bare name is not a vocal-stem name (the `no_vocals`
  complement) produces a WARNING, not a refusal: a hand-renamed stem is
  legitimate.
- **Decode failure carries its own prefix**, `ILyricSyncCore.DecodeFailedPrefix`.
  It is the one failure a caller can act on — install ffmpeg, or hand over a
  `.wav` — and the CLI turns it into its own exit code. Everything else stays an
  opaque message.
- **Two ctor test hooks exist because the model is 340 MB and cannot run in the
  default gate.** `LyricAcousticSessionOpener` (an `ILyricAcousticSession`
  factory, defaulting to `LyricAcousticSessionFactory.Default`) and an audio
  loader delegate. Before them, the whole orchestration was assertable only by
  reading the source; `LyricSyncSeamTests` now drives it with a known emission
  matrix in milliseconds. **A fixture that asserts ABSOLUTE onsets must price
  the star out** — at the shipping cost of 0.0 the wildcard is ≥ every emission
  and eats the front of the first syllable, which over a two-letter matrix is
  the whole signal.
- **`LyricModelCatalog.SelectForSheet` gained `forceEnglish` and
  `allowEnsemble`**, both defaulted so every in-app caller is byte-identical.
  They are the CLI's `--lang` and `--no-ensemble`. An override replaces the
  GUESS, never the installed-ness fall-backs.
- **`LyricAutoSyncService` gained a `modelRoots` parameter, and it is NOT
  `modelDirectoryOverride`.** Roots say where to LOOK and still select between
  heads (so `--model-dir` can find an ensemble); the override pins ONE folder
  and disables selection entirely.

The tool itself is `tools/lyric-sync/DotCharter.LyricSync/`, published by
`tools/lyric-sync/Publish-LyricSync.ps1`, and documented for users in
`tools/lyric-sync/README.md`. It writes seconds, never beats. **Its ends and review
flags are the app's, through the app's code (#809):** `LyricSyncSpacing.Apply` runs
`BuildPhrases` (ends, `LyricAlignmentReview`, `LyricPhraseSpacing.CloseGaps`) on a
`LyricSyncBeatGrid` — tracked from the audio by `BeatMapAnalyzer`, a `--bpm` grid,
or with no grid a dense neutral grid and a fixed 30 ms gap (`MinNoteDistanceMode.Milliseconds`),
so the fallback differs in the gap only — then reads the syllables back in seconds.
Review flags go to the report, to XML comment lines before the flagged `<vocal/>`
(EOF skips any line holding `<!`) and to the JSON. Never re-implement a span rule in
the tool. Its two writers
live in Services because they need `Fmt` and `LrcLyricsWriter`'s stamp format:
`Export/Lyrics/RsVocalsSecondsWriter` and `Export/Lyrics/LrcSecondsWriter`.
**The `-`/`+` convention is shared with the chart writer through
`Parsing/Xml/Vocals/RsVocalLyricEncoding`** — two copies would drift, and the
failure is silent: a missing `+` does not break an import, it loses every line
break. Three shapes are load-bearing for Editor on Fire, whose importer reads
the file LINE BY LINE rather than with an XML parser — the literal
`<vocals count` (that IS its format detection), one `<vocal/>` per line, and all
four attributes on every entry — and RsCharter needs `time`/`length` at exactly
three decimals, because its converter parses the string with the dot deleted.
`LyricSyncWriterTests` pins all of it, plus a real round trip back through
`RsXmlImporter`.

## The call site snapshots before downloading, and cancels when its project closes

**`SyncAsync` takes `IList<BeatMarker>` and must never be given
`ProjectState.ActiveProject.Beats` itself.** It was, until 2026-09-04:
`LyricsInspectorViewModel.AutoSync` (now `LyricAutoSyncRunner.RunAsync`) passed `_chartEditor.Beats` — the live
`ObservableCollection` — into a multi-second decode + CTC alignment on a worker.
The UI thread can edit that collection mid-alignment, and opening a project
REPLACES it outright. `LyricAutoSyncRunner` clones each `BeatMarker` into a new
list before any await, including the model download. A plain `ToList()` still
shares mutable markers, so edits to a marker would leak into inference.
If tempo or marker times change during the run, returned phrases are cloned and
remapped through absolute milliseconds from the captured grid to the live grid.
Both phrase and syllable endpoints keep their aligned audio times.

**And the outcome belongs to the project it was computed for.** The call site
captures `ProjectState.ActiveProject`, audio references and stems before the
model download and compares by `ReferenceEquals` after download and alignment;
a mismatch logs, reports
`Status_LyricAutoSync_ProjectChanged` and applies NOTHING — the phrases were
timed against a beat grid that belongs to a chart nobody is looking at, and
writing them into the newly-opened song would silently mistime its lyrics. This
is the same guard the stem pipeline already had
(`StemSeparationViewModel`'s identity check before
`RegisterAndPersistStemsAsync` applies); this path had none.
`LyricAutoSyncRunnerTests` covers download-time changes and marker isolation;
`LyricAutoSyncProjectIdentityTests` covers the drop and apply-when-still-open
case. The inspector also captures the destination vocal track before starting;
a switched or removed destination discards the result with
`Status_LyricAutoSync_TrackChanged`, rather than replacing another track.

**`LyricAutoSyncRunner.RunAsync` captures every input before its first await:**
project reference identity, lyric text, resolved mix path, project/audio references,
stem values, option values and cloned `BeatMarker` values. Copying only the beat
collection leaves its mutable markers shared with the editor. The spacing config
also contains independent values, so edits during model installation or inference
cannot change the inputs of a run already in progress.

The runner checks reference identity after model installation, before inference and
after inference. A replacement takes precedence over a failed installation or
inference result. `LyricsInspectorViewModel.AutoSync` captures its own owner and
checks it immediately after the runner await, before handling statuses or applying
phrases. This closes the interval between a successful runner result and the UI
continuation. Reopening the same persisted project creates a new owner too.
A mismatch reports `Status_LyricAutoSync_ProjectChanged` and applies nothing to
either project. An unchanged owner still applies through one undo action.

`LyricAutoSyncRunnerTests` mutates captured inputs behind an injected download
gate. `LyricAutoSyncProjectIdentityTests` covers download, inference and pre-apply
replacement boundaries plus unchanged controls, for both another project and a
reopened project with the same persisted identity. All gates are injected; these
cases run without models, network, audio decoding or stem separation.

## The model download

- **A downloaded lyric model is fetched ONCE and never overwritten**
  (`Audio/Lyrics/LyricModelInstaller`). The models are 340 MB / 303 MB and do
  not change between releases, so they are their own release assets rather than
  zip payload — bundling them would make every update re-download 640 MB the
  machine already has. An existing model is authoritative whatever its
  provenance (hand-installed, newer export, mid-use on another thread), so the
  presence check is the whole gate. Extraction stages into a UNIQUE sibling
  `.incoming-*` folder owned by that attempt; publication is serialized per target
  across installer instances, with the installed winner checked again under the lock.
  Repair removes copies from ALL search roots, including incomplete fallback copies.
  The completed payload
  is `Directory.Move`d into place only when COMPLETE — an interrupted download
  must never leave a half-model that the presence check then trusts forever.
  Only `OperationCanceledException` escapes; an optional model that cannot be
  fetched degrades to "not installed" and the aligner falls back to multilingual.

- **`LyricModelAssetFetcher` has a SECOND consumer and is not lyric-specific.**
  The AI match lane's rig estimator reuses it verbatim
  (`Ai/Match/Model/RigEstimatorModelDownloadService`), because the one thing
  that fetcher knows and a copy would silently lose is that the release scan
  must PAGE — 5 × 100 — since a model asset sits on a release a hundred behind
  HEAD. **Narrowing that window re-breaks BOTH downloads**, and the failure
  reads as a plain "the model is not available". If it is ever renamed for
  honesty, rename it; do not fork it. The rig lane's own root, required files
  and catalogue are its own — see
  [ai-match-model.md](ai-match-model.md) § Getting the model.
