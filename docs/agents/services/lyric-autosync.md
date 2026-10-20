# `Audio/Lyrics/` — lyric auto-sync (deep reference)

Extracted verbatim from `DotCharter.Services/AGENTS.md`. **Read this before
touching the acoustic model, the model catalogue/installer or the auto-sync
orchestration.** The alignment MATH is pure and lives in
`DotCharter.Analysis/Lyrics/` — read `DotCharter.Analysis/AGENTS.md` first; this
file is the service half (models, decode, stem selection, windowing, install).

---

## `Audio/Lyrics/`

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
    **(a) covers a BROKEN one too, and that is a fourth rule because `IsInstalled`
    cannot tell the difference:** it is a bare presence check, so the ensemble path
    is taken the moment two files exist, and a truncated `model.onnx` / an
    unsupported opset / a CUDA OOM with both the 340 MB and 303 MB sessions alive
    used to fail the WHOLE run. The optional head's load, encode and blend live
    inside `LyricAutoSyncService.BlendOptionalSecondary`, which answers any
    non-cancellation failure with the primary's own emissions and one warning;
    pinned by `LyricSecondaryModelDegradationTests`, including a source lint that
    the ensemble branch still routes through it.
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
  - **The lyric model runs on DirectML through `OnnxSessionFactory`'s Auto
    ladder** (DirectML → CPU; #151): any DirectX 12 GPU, no runtime to register,
    `DirectML.dll` ships beside the exe. It opens on DXGI adapter 0 (the display
    adapter), unlike stem separation, which picks the fastest. Checked 2026-10-05 on
    `lyric-align-en-v1`: DirectML logits match the CPU's argmax on 99.9 % of frames
    (relative L2 3.9e-2), with graph fusion on or off.
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
  - **The CTC end is a FLOOR, not the syllable's end** (`BuildPhrases`). The
    model stops at the last frame it can still hear a syllable, which left
    short pills with dead gaps — so a syllable SPANS to the next syllable's
    start minus the note-style minimum gap
    (`ChartSpacingEnforcer.GetMinDistance`, anchored at the earlier syllable
    like `EnforceSoundSpacing` does). Within a word it always spans; across a
    word boundary `LyricAutoSyncOptions.SpanWordEndToNextWord` decides
    (default ON, `AppConfig.LyricSpanWordGap`); across a phrase boundary
    `SpanPhraseEndToNextPhrase` (default OFF, `AppConfig.LyricSpanPhraseGap`).
    Spanning never SHORTENS what was heard, and `MaxSyllableSeconds` caps a
    spanned end exactly like a heard one. The inspector's sync call site builds
    options through `LyricAutoSyncOptions.FromConfig` — the ONE seam the two
    editor settings reach auto-sync through, so a settings change is live on
    the next sync. Pinned by `LyricPhraseBuildingTests`; the mapping and the
    call site by `LyricAutoSyncOptionsFactoryTests` (source lint — a call site
    reverted to `Default` ships two settings that do nothing).

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
  one-way.** It carries the three knobs the alignment itself reads (onset bias,
  star cost, provider) and DROPS the beat-grid ones. A knob left out of it is a
  setting that silently stops working in the app while the CLI keeps honouring
  it, which is why `TheProjectionCarriesEveryAlignmentKnob` enumerates them.
- **The seam accepts an EXPLICIT stem and still never separates.**
  `LyricSyncOptions.VocalStemPath` names a file; null falls back to
  `VocalStemLocator`. A named stem that is missing is a hard `no-stem` failure
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
`tools/lyric-sync/README.md`. It writes seconds, never beats. Its two writers
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

## The call site hands over a SNAPSHOT, and drops a result for a project that closed

**`SyncAsync` takes `IList<BeatMarker>` and must never be given
`ProjectState.ActiveProject.Beats` itself.** It was, until 2026-09-04:
`LyricsInspectorViewModel.AutoSync` passed `_chartEditor.Beats` — the live
`ObservableCollection` — into a multi-second decode + CTC alignment on a worker.
The UI thread can edit that collection mid-alignment, and opening a project
REPLACES it outright. A `ToList()` costs nothing beside the decode and cannot be
torn.

**And the outcome belongs to the project it was computed for.** The call site
captures `ProjectState.ActiveProject` before the await and compares by
`ReferenceEquals` after; a mismatch logs, reports
`Status_LyricAutoSync_ProjectChanged` and applies NOTHING — the phrases were
timed against a beat grid that belongs to a chart nobody is looking at, and
writing them into the newly-opened song would silently mistime its lyrics. This
is the same guard the stem pipeline already had
(`StemSeparationViewModel`'s identity check before
`RegisterAndPersistStemsAsync` applies); this path had none.
`LyricAutoSyncProjectIdentityTests` covers the snapshot, the drop and the
apply-when-still-open case.

## The model download

- **A downloaded lyric model is fetched ONCE and never overwritten**
  (`Audio/Lyrics/LyricModelInstaller`). The models are 340 MB / 303 MB and do
  not change between releases, so they are their own release assets rather than
  zip payload — bundling them would make every update re-download 640 MB the
  machine already has. An existing model is authoritative whatever its
  provenance (hand-installed, newer export, mid-use on another thread), so the
  presence check is the whole gate. Extraction stages into a sibling folder and
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
