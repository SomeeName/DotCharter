# DotCharter.Analysis Agent Guide

Offline **audio → chart** analysis ("Pillar A"): turns raw mono audio into an
auto-detected beat map, lyric timings and a chart-vs-audio lint. Deterministic, streaming, and
advisory — results are review candidates, never auto-committed.

References `DotCharter.Core` + `JuceSharp.Dsp` only; no external NuGet packages.
`InternalsVisibleTo("DotCharter.Tests")`.

**Routing.** The lyric half of this project — the CTC forced aligner, the two
acoustic models, the router between them, and every measured accuracy number
they are judged by — is ~11 KB that only matters if you are touching lyrics, so
it moved VERBATIM into
[docs/agents/analysis/lyric-alignment.md](../docs/agents/analysis/lyric-alignment.md).
Nothing was summarised away. **Read that file before you touch `Lyrics/`**, and
update it in the same change.

## Layout

- `BeatMap/` — audio → tempo/beats (`BeatMapAnalyzer`, "v2"). Spectral-flux
  envelope (`JuceFft`, 1024/256, Hann) → whiten → comb-autocorrelation tempo
  windows (drift-tracking, octave-error suppression) → forward beat tracking →
  downbeat phase. Outputs `DetectedBeat` + average BPM (`BeatMapAnalysisResult`,
  `BeatMapAnalyzerOptions` 60–220 BPM).
  `BeatMap/RecordingAligner` (#784, #792) is the other half: it warps an EXISTING
  chart's beat grid onto a recording of it. `RecordingAlignmentFeatures`
  (log-flux onsets on ~2.7 ms frames, normalised to the local peak; 4096-point
  chroma per 40 ms) → `CoarseWarp` (a constant lag searched on onsets AND chroma —
  onsets alone are periodic in a full mix — then a banded DTW, steps (1,1)/(1,2)/(2,1),
  an onset the chart does not expect costing a quarter of a missing one; chart
  onsets + MIDI-pitch chroma, each sound RINGING until the next one (≤ 1 beat;
  `RecordingAligner.LetRing`), vs the recording's; the path re-fitted PER BEAT, only
  over frames inside the grid, so an uneven chart grid does not bend the tempo it is
  measured against) → a Viterbi pass over the beats (±160 ms around the coarse
  estimate; each interval scores the onsets under its notes at their own fractions
  minus a squared log-tempo-ratio penalty; FIXED beats have one candidate).
  Confidence per section = recall, precision and chroma agreement; extra uncharted
  attacks lower precision even when every charted note is heard. Peak spacing widens
  before subtracting the `int.MinValue` sentinel (`RecordingAlignerTests`). Whole clip in
  memory, like the lint. `FluxPeakLeadMs` (4.5 ms) is measured on the synthetic pluck.
  **The coarse warp is STIFF on purpose (#792)**: a skew step costs 2 (it was 0.4), because
  a loose warp wandered off the diagonal by part of a beat wherever a full mix was
  ambiguous — on tabs written at EXACTLY the recording's tempo. A stiff warp cuts corners when its diagonal is not the song's tempo, so it
  runs on up to three chart axes (`CoarseWarp.Candidates`: the written tempo, the best
  straight line at another tempo, a loose warp's smoothed path) and keeps the written
  tempo's unless another costs ≥ 2.5 % less per in-recording chart frame
  (`CandidateMargin`; on the corpus a wrong candidate was ≤ 1.6 % cheaper, on synthetic
  tabs 3–10 % off tempo the right one 3.5–15 %). Frames past the END of the recording
  cost the worst match, so a chart longer than its recording keeps its tempo there
  instead of being squeezed in. Measured on the synthetic fixtures
  (`RecordingAlignerTests`, `RecordingAlignerDenseMixTests`, which also pins that the
  #784 options FAIL its off-tempo full mixes): drift mean 0.75 ms, max 1.6 ms from a
  start 1.27 s off; full mixes 0.9–1.08× the written tempo max ≤ 12 ms. Real songs
  (`Analysis/AlignToRecordingCorpusProbeTests`, `DOTCHARTER_ALIGN_CORPUS=1`, reports,
  never asserts; 25 GP tabs on the full mix against their hand-synced RS grids,
  nearest-beat error, 2026-10-13): median 5.0 ms, p90 20.3 ms (#784: 9.9 / 168). Its
  nearest-beat metric cannot see a whole-beat slip, and on the one song whose tab beat
  is 1.5 reference beats it cannot go below ~58 ms median on a correct alignment.
- `AudioToChart/ChartAudioLintAnalyzer` (v0.29) — offline chart-vs-audio lint:
  whole-clip quarter-tone CQT (`JuceConstantQ.AnalyzeWhole`) + Binary-Match-Mask
  verify per charted pitch; failed pitches probe ±4 semitones for the "audio
  says B4" hint. CQT constants come from **`JuceSharp.Dsp.LiveCqtConfig`** — the
  single source of truth Services' `LiveCqtVerifier` and the
  `CqtStreamingEvalTests` harness also read (E1/144 bins/hop 256/kernel 4096).
  Never re-type those numbers here: the hand-copied mirrors this replaced are
  exactly what drifted. Two of them (`NoiseMargin`, and the veto's floor
  precondition) were tuned on the STREAMING statistics and are inherited, not
  measured, on this whole-clip path. Wrapped by `Services/Editing/Linting/
  AudioChartLintService` (decode + chart projection) — the one place SERVICES
  references this project; never reference Services back (cycle).
- `Lyrics/` — **pasted lyrics → syllable onsets** (CTC forced alignment). Pure:
  no I/O; the aligner reads a frames × symbols log-probability matrix the
  Services layer hands it, and `VocalEnvelope` + `VocalEndRefiner` read a vocal
  stem's RMS envelope to place syllable ENDS. `LyricSheetParser` (the paste format) →
  `RomanizedLyricTokenizer` + `LyricVocabulary` → `CtcForcedAligner` (the Viterbi
  trellis) → `LyricAligner`. Two acoustic models since 0.41, routed by
  `Lyrics/LyricScriptDetector` and ENSEMBLED by `CtcEmissionBlender`.
  `VocalOnsetFeatures` + `LyricOnsetSnap` then move each onset to the stem's own onset (#868).
  → **[docs/agents/analysis/lyric-alignment.md](../docs/agents/analysis/lyric-alignment.md)**
- `Tuning/` — **the song's A440 reference offset** (#821). `ReferencePitchEstimator.Estimate` pairs Hann
  frames a quarter frame apart and takes each steady peak's frequency from the phase advance (exact for a
  steady partial, so low notes are as precise as high ones); peaks that change level by more than 20 % between
  the two frames (attacks, drums) do not vote. Votes fold to cents around the nearest semitone into a circular
  1-cent histogram; the smoothed peak, refined by a circular mean within ±15 cents, is the offset, and
  confidence is the share of the vote near it above an even spread. Measured: synthetic sines and
  8-harmonic notes at 0/+15/−30/+45 within ±3 cents, white noise 0.03; 12 corpus mixes (all authored 0)
  read +0.5..+5.8 cents at confidence 0.36–0.76, hence `DefaultMinConfidence` 0.3
  (`ReferencePitchEstimatorTests`, `ReferencePitchCorpusTests`). `ReferencePitchComparison` keeps the
  authored whole semitones and offers only a residual move over 10 cents.
- `Tone/` — **one span of audio → the measurements a tone is built from.**
  `ToneAudioAnalyzer.Analyze(ReadOnlySpan<float> mono, int sampleRate)` →
  `ToneAudioProfile`. The owner's rule for the feature: the tone PLACEMENT is not
  generated — the charter places a tone change and the tone is built from the
  audio between that change and the next one — so nothing here knows about time,
  charts or gear, and `ToneAudioAnalyzer.Slice` is the only concession to the
  caller's world. Hann-windowed 4096/1024 frames, one `JuceFft(8192)`; every
  per-frame feature is averaged weighted by that frame's ENERGY. Mapping the
  profile onto amps/cabs/pedals happens ABOVE this project.

## Rules / gotchas

- **Determinism is a hard contract** across the project — same samples + options
  (+ beat grid) ⇒ identical output. **It is guarded by RE-RUNNING, and the
  catalogue is DERIVED** (`Analysis/AnalysisDeterminismTests`): it reflects over
  this assembly for every public type carrying an `Analyze`/`Align` entry point
  and demands a runner for each, so a NEW analyzer fails the gate until it is
  actually re-run and compared — a name in a list does not satisfy it. It also
  varies execution HISTORY (run the subject, run every other pipeline, run the
  subject again), which is the one thing each analyzer's own
  `Analyze_IsDeterministic` cannot do: those call ONE analyzer twice in a row and
  are therefore blind to state shared BETWEEN analyzers. Proved by breaking it —
  a static that `ChartAudioLintAnalyzer` writes and `BeatMapAnalyzer` reads leaves
  `BeatMapAnalyzerTests.Analyze_IsDeterministic` green in isolation while the
  history test fails. **What it does NOT prove:** the fixtures are synthetic, so
  it is a repeatability guard and says nothing about accuracy on real
  recordings; and it is one process, so per-process `string` hash randomization
  and cross-machine floating point are out of its reach.
- Audited 2026-08: the determinism surface is CLEAN and this is not a claim to
  carry forward untested — no ambient time, no randomness, no parallelism, and
  no hash-ordered enumeration reaching an ordered output anywhere in the project.
  `JuceSharp.Dsp` holds no static mutable state either (its statics are all
  `readonly` tables or pure methods), which is what makes the history test above
  pass today. The one `Dictionary.Keys` iteration —
  `CtcEmissionBlender.BuildSymbolMap` — writes to DISJOINT indices (one token
  yields one char yields one id), so it is order-independent by construction
  rather than by luck; if you ever make two symbols share an id, that stops
  being true.
- **A pitch the CQT cannot ADDRESS is unknowable, not wrong, and is skipped
  like a mute.** `JuceConstantQ` covers `LiveCqtConfig.MinMidi` (28, E1) up
  `BinCount` quarter-tone bins, i.e. MIDI **28..99**; outside that
  `JuceChartVerifier.Verify` returns before it touches the audio and
  `ProbeObserved` (which only looks NEARBY) has nothing to name, so every such
  pitch used to come back as a finding reading "silent". Not exotic input: a
  5-string bass's low B is 23 and a bass dropped to D is 26, so those charts
  filed one bogus finding PER NOTE of the low string and counted each as
  CHECKED. `ChartAudioLintAnalyzer.IsVerifiable` /
  `MinVerifiableMidi` / `MaxVerifiableMidi` are DERIVED from `LiveCqtConfig`,
  never re-typed; a sound whose every pitch is unaddressable is not counted at
  all, and a chord straddling the floor keeps the tones the CQT can hear. Pinned
  by `ChartAudioLintAnalyzerTests.APitchOutsideTheVerifiableRange_*` plus two
  edge cases (28, 99) that must stay CHECKED.
- **The Services wrapper decides what is verifiable AT THE FRETTED PITCH, and
  that is more than mutes and harmonics.** `AudioChartLintService
  .ProjectExpectedSounds` also skips unpitched slides and **pre-bends** — a
  pre-bent string is already bent when it is struck, so it sounds at
  `fret + step` from its first frame and the verifier's t-40..t+140 ms window
  never contains the fretted pitch. An ORDINARY bend starts at the fretted pitch
  and stays checked, so the test is a bend point AT the sound's own position
  (`RsTechniques.IsPreBend` spells the same rule; bend positions are absolute).
  The chord arm rejected harmonics only until 2026-09-04 and now rejects all
  three, because the verifier is asked per pitch and a chord cannot exclude one
  tone.
- **`ChartAudioLintResult.Findings` order is part of the OUTPUT, so the per-sound
  dedup is an order-preserving `List`, never a set.** A charter reads the lint
  report top to bottom and the determinism contract is byte-identical output —
  `HashSet<T>`'s enumeration order is explicitly unspecified, so relying on it is
  relying on the runtime build. Honest note: `HashSet<int>` with no removals does
  in fact enumerate in insertion order today, so the switch changed nothing
  observable; what it buys is that the property is now stated and pinned
  (`AChordsFindings_ComeOutInTheChartedOrder_WithDuplicatesRemoved` fails against
  a `SortedSet`, the plausible "cleanup").
- **`BeatMapAnalyzer` has ONE product caller: Tempo map ▸ Estimate BPM (#488).**
  `Services/Editing/Tempo/Estimate/TempoEstimateService.EstimateSamples` decodes the
  mix, cuts the whole song or the marked part, runs the analyzer and takes the
  MEDIAN beat tempo (one mistracked beat cannot move it); the dialog offers it with
  its half and double. Beat-map auto-detect as a chart builder is still not a
  surface: the app's beat grids come from `TempoService` and the importers.
- **The comb scores FRACTIONAL periods, which is what fixed the octave errors.**
  The tempo used to be the best INTEGER lag of the flux autocorrelation, and at
  172.27 fps a 160 BPM period (64.6 hops) is misaligned by 0.4 hop on every period
  while its double (129.2) is off by only 0.2, so the raw correlation preferred the
  wrong octave (160→80, 180→90, 200→66.7). Now `EstimateWindowPeriods` box-sums three
  neighbouring lags (a pulse pair lands on one of two integer lags depending on the
  fractional part; the sum keeps every pair), interpolates that at `P·m`, and scores
  a grid of periods in 1/8-hop steps over `CombHarmonics` = 4 multiples (weights
  1, .5, .33, .25). Measured constant click tracks (30 s, default options,
  `AverageBpm`): 60→60.09, 75→74.90, 90→89.88, 100→99.98, 120→120.19, 140→139.67,
  150→149.80, 160→160.25, 170→169.44, 180→179.76, 185→184.57, 190→189.98,
  195→195.02, 200→199.73, 205→204.67, 210→209.87; pluck tracks at 80/120/160/200
  within 1.5 (`BeatMapAnalyzerTests`). The default range is 60–220 BPM so 210 is
  inside it. The first beat is now searched over at least 1.5 s, not 1.5 periods:
  above ~180 BPM the old window ended before a first hit at 0.5 s and the whole map
  came back empty (hidden until the octave fix made such tempos reachable). Wholly
  silent search windows are skipped until there is onset evidence, so a silent
  intro longer than 1.5 s cannot discard an otherwise detectable track
  (`BeatMapAnalyzerTests.Analyze_AfterASilentIntro_FindsTheFirstClickAndTracksTheRest`).
  Still a click/pluck-track measurement, not a corpus one: the `TestData/EOF`
  corpus the roadmap asks for remains the real check, and a song whose true tempo
  is a power of two away from the read one is why the dialog shows half and double.
- **`BeatMapAnalyzer` drops a frame containing a non-finite SAMPLE rather than
  letting the FFT see it.** One bad sample (decoder glitch, denormal blow-up) is
  smeared across every bin, and its NaN log-magnitude then becomes the PREVIOUS
  frame's spectrum — so the poison has a direct route to the whole rest of the
  clip: NaN flux ⇒ a NaN running sum in `Whiten` ⇒ no tempo window ever scores ⇒
  an empty beat map for a song that is 99% clean. It did NOT get there before the
  guard, because `if (d > 0)` is false for NaN — but that was an accident of a
  comparison three lines from a `sum +=`, and rewriting it as
  `sum += MathF.Max(0f, d)` (`Max(0, NaN)` IS NaN) restores the catastrophe.
  Pinned by `BeatMapAnalyzerTests.ASingleNonFiniteSample_DoesNotDestroyTheRest
  OfTheTrack`; the guard costs the four overlapping frames (~23 ms) and nothing
  else, and the path is byte-identical for finite input.
- **`BeatMapAnalyzerOptions.MinBpm`/`MaxBpm` are a CONTRACT, and the lag bounds
  round INWARD to keep it.** A lag is an integer number of hops, so a `floor` on
  the fast bound admitted a period faster than `MaxBpm` and a `ceil` on the slow
  one a period slower than `MinBpm`, and the parabolic refinement then moved the
  winner up to a whole hop further out again — `MinBpm = 75` is 137.8 hops at
  172.27 fps, and lag 138 is 74.898 BPM, outside the range the caller asked for.
  The refined period is clamped back into `[minLag, maxLag]` for the same reason;
  the median-of-three smoothing picks one of three in-range values and `PeriodAt`
  only interpolates between two, so every beat's `Bpm` is then in range by
  construction. Nothing downstream re-checks. Pinned by
  `BeatMapAnalyzerTests.EveryDetectedTempo_StaysInsideTheRequestedBpmRange`.
- **`ToneAudioProfile.Drive` is calibrated for a PERFORMANCE, and a steady tone
  falls off the end of that calibration.** Its crest term maps 18 dB → 0 and
  6 dB → 1, but a continuous full-level sine has a crest of 3.01 dB by
  definition, so it clamps to "maximally squashed" and the sine scores
  **Drive 0.50** while a clean plucked note scores **0.067**. Nothing is wrong
  with either number — a sustained, unvarying waveform genuinely has no
  dynamics — but it means **Drive may only be compared between spans of real
  playing**, and a test that asserts "clean ⇒ low Drive" has to use a DECAYING
  fixture or it is asserting the calibration, not the analyzer. The measured
  table (`ToneAudioAnalyzerTests.MeasuredProfiles_AreReportedForCalibration`
  prints it on every run): clean pluck 0.067, hard-clipped pluck 0.541, three
  plucked notes 0.201, white noise 0.990.
- **White noise scores Drive 0.99, so a hissy or near-silent span reads as a
  wall of gain.** Every one of Drive's three terms genuinely maxes out on noise
  (crest 4.78 dB, flatness 0.85, no harmonic series). `SilenceRms` catches a
  QUIET span; it does nothing for a loud noisy one, and neither does anything
  else in this project. A caller that hands over a span of crowd noise, a
  fade-out or a badly separated stem will be told, confidently, to build a
  high-gain tone.
- **`HarmonicRatio` describes the harmonicity of the span, NOT the note being
  played.** The fundamental comes from an unbiased autocorrelation taken through
  the FFT already in hand (the power spectrum of a real signal is real and even,
  so its FORWARD transform is the autocorrelation — `JuceFft` exposes no
  inverse). Autocorrelation is used over a spectral peak-picker precisely because
  the frames it must not lie on are DISTORTED ones, where most of the energy has
  moved off the fundamental and a peak-picker reports the loudest harmonic as the
  note. It picks the SHORTEST lag within 90 % of the best rather than the
  strongest, because an ACF peaks at every multiple of the period and a
  fundamental reported an octave low packs the harmonic bands twice as densely,
  inflating the ratio. Anything above `MaxFundamentalHz` (1320 Hz) is still
  described by a subharmonic — a 3 kHz sine is measured at 1 kHz and scores
  0.999 — which is the right answer to "is this harmonic?" and the wrong answer
  to "what note is this". Do not expose the fundamental without re-checking that.
- **Bounded memory:** input is never buffered whole; work streams in ≤8192-sample
  chunks (only the flux envelope / detector ring is retained).
- Ownership: keep disjoint from Play Mode / scoring (that is the app + Services).

### Lyric rules — one line each, full text one file away

All under
[docs/agents/analysis/lyric-alignment.md](../docs/agents/analysis/lyric-alignment.md).

- **The `<star>` wildcard's cost is 0.0 and that is MEASURED, not inherited** —
  penalising it to stop it eating the front of the following syllable measures
  WORSE (90.4% → 88.9% within 100 ms). Don't "fix" it without re-running the probe.
- **Accuracy is measured against TWO corpora that disagree by 24 points — always
  say which one a number came from.** Romaji (4 arrangements, 2,036 syllables) is
  the optimistic end; the 40-song English set is the realistic one. Report the
  error SIGNED: listeners forgive early far more than late.
- **Aligning against an isolated vocal stem BEATS the mix, measured** — and
  `LyricAutoSyncService` uses a stem only when one already exists on disk.
- **The script router must stay biased towards MMS-FA**, and its FIVE agreement
  rules include a function-word test that is the load-bearing one.
- **The two heads are ENSEMBLED, not chosen between** (`CtcEmissionBlender`) —
  renormalize every blended frame, weight 1.0 returns the primary INSTANCE, and
  the onset bias belongs to the COMBINATION.
- **The English model carries a −10 ms onset bias and MMS-FA carries none** — that
  asymmetry is measured, and the bias belongs to the MODEL, never the caller.
- **The references themselves are only ~28 ms precise** — on romaji the aligner is
  already AT the annotation noise floor. Do not chase the 50 ms bucket there.
- **The remaining English tail is COHERENT, and the star is not the mechanism** —
  a uniform star penalty and a silence-aware per-frame star cost were both
  falsified against the corpus. Do not re-try them.

## Validation

`dotnet test --project DotCharter.Tests\DotCharter.Tests.csproj --filter-namespace "*.Analysis*"`.
