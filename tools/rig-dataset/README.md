# `tools/rig-dataset` — the audio→rig training corpus

The data lane of "match a tone from audio". It turns a pool of licence-clean **DI**
recordings plus `RigSampler` into (wet clip, exact rig) pairs: the one thing every
published blind-parameter-estimation paper had to fake, and we do not, because we
own a deterministic renderer whose labels are exact by construction.

**The dataset lives OUTSIDE the repo**, at
`..\DotCharter-local\datasets\rig\` (a sibling of the repo):

```
rig/
  di/<set>/*.wav      the DI pool: mono 48 kHz, exactly 5 s, silence-trimmed
  di-optional/<set>/  OPT-IN pools, deliberately OUTSIDE di/ — `ClipSource.Scan`
                      enumerates di/ recursively, so anything dropped in there is
                      in the pool whether you meant it or not
  beds/bed_NNN.wav    backing beds for bleed + separation — GENERATED, see below
  beds/bed_NNN.json   the bed's whole plan + its licence line
  clips/<bucket>/<index:D8>.wav       the rendered wet clips, mono 48 kHz 16-bit
  clips-sep/<bucket>/<index:D8>.wav   the stage-B separated stems (v2)
  manifest.jsonl      one RigDatasetRow per line, sorted by clip index
  LICENSES.md         written by fetch-di.py; every licence read from Zenodo
  _raw/, _models/     downloads. Deletable; re-fetching is cheap.
```

A 200-clip **smoke** set for the model lane to build a loader against lives at
`DotCharter-local\datasets\rig-smoke\`, same shape.

---

## 1. The DI pool

```bash
python tools/rig-dataset/fetch-di.py --root ../DotCharter-local/datasets/rig
```

Needs `ffmpeg` on PATH and nothing else — no numpy, no soundfile. **It reads each
set's licence from the Zenodo record's own API at fetch time and REFUSES to
prepare a set whose licence is not on its allow-list**, because a licence copied
into a table is a licence nobody re-checked.

| Set | Licence | Prepared | Taken |
|---|---|---:|---|
| **EGFxSet** | CC BY 4.0 | 689 clips · **0.96 h** | `Clean.zip` only. The other 13 zips are the same notes through *real pedals* — wet audio with someone else's labels. **Concatenated before cutting**: each source is one 5 s note whose tail is silence, and per-file trimming kept 9 of 690. |
| **Guitar-TECHS** | CC BY 4.0 | 3,545 clips · **4.92 h** | `audio/directinput/*.wav` only, all nine subsets (P1/P2 × single notes, chords, scales, techniques; P3 music). The `micamp` leg is an amp someone else chose. |
| **owner** | ours | 0 | `di/owner/` — see below. |
| | | **4,234 clips · 5.88 h** | the DEFAULT pool (re-measured 2026-09-13) |
| **GuitarSet** *(opt-in)* | CC BY 4.0 | 1,990 clips · **2.76 h** | `--only guitarset`. `audio_hex-pickup_debleeded.zip` only — a real per-string direct pickup signal, and the last licence-clean source the study names. It is an **ACOUSTIC** guitar, so it lands in `di-optional/guitarset/` and is NOT in the default pool: 2.76 h of acoustic poured into a 5.88 h electric pool is a third of the corpus teaching a model whose whole job is electric tone. Point `--di` at it deliberately, at a low weight, or run it as its own ablation. |

Deliberately **not** used, with reasons, in `LICENSES.md`: EGDB (**no licence
stated**), IDMT-SMT-\* (NC-**ND**), GuitarSet (acoustic), GAPS (classical),
tonetwist (~40 % of its dry material is IDMT/YouTube-derived; the rest needs a
per-record review for ~25 min of audio), MUSDB18-HQ / MoisesDB (both NC).

### `di/owner/` — the owner's own DI

Drop **mono, 48 kHz, 16-bit WAV files of exactly 5 s**, recorded DRY off the
app's live input — no amp, no cab, no pedals, and the drop tuner off. Any file
name. Anything shorter than 5 s is ignored by the counter and by the render.
One Saturday of playing is the only data on the exact instrument the model will
be asked about, and it is also the reference for "does it match MY input".

### `beds/` — backing beds, and we GENERATE them

```bash
dotnet run --project tools/rig-dataset/DotCharter.Tools.RigDataset -- beds   --out .../datasets/rig-v2 --count 30 --seed 20260913
```

A bed is a full backing track with **no guitar** — drums, bass, keys — and the
render takes a random window of one. They drive two stages:

* **mix bleed** — a bed at −40…−25 dB under the clip (`--bleed-fraction`, default
  0.30). Cheap, applied during the render.
* **stem separation (stage B)** — the clip mixed INTO the bed at a guitar-to-mix
  level of −12…+3 dB, run through htdemucs, guitar stem taken back. Its own
  subcommand; see §3.

**With no beds both are skipped and the row says so** (`bleed:skipped-no-beds`) —
never silently, and the v1 corpus is exactly what that looks like: `beds/` held
nothing but a README, so `render.log` reads **`0 separated`** across 49,908
clips.

**They are synthesised, not collected, and the licence is the reason.**
MUSDB18-HQ is "educational purposes only" and MoisesDB is CC BY-NC-SA, so both
are fine for a notebook and fatal for a model that ships; the owner's own songs
are the 35-song EVAL corpus and putting one in a bed leaks the evaluation into
training. So `beds` writes its own:

* **three layers** — drums, bass, keys — whose notes come from a key, a scale and
  a four-bar progression drawn from the bed's seed, synthesised by `BedSynth`
  (pitch-swept sine kick, band-passed noise snare and hats; saw + sub bass
  through an envelope-tracking one-pole; three detuned saws for the pad), and
  then **rendered through the engine**, one rig per layer — `compressor` +
  `bassamp`, `transient` + `multiband` + `reverb`, `chorus` + `hallreverb` +
  `tilt`. The character is the app's own DSP; only the note sources are written
  in C#, because every pitch module the engine has is a TRACKER and needs an
  input this pipeline must not give it.
* **spread by construction**: bed *i* takes tempo *i* mod 8 out of
  72…146 bpm, alternates major/minor, and walks the circle of fourths, so 30 beds
  cover all twelve keys, both modes and all eight tempos. Lengths are 60–120 s.
* **a `bed_NNN.json` sidecar** carrying the whole plan — every note, every rig
  param, the measured RMS and peak — and a **licence line** saying it is ours and
  carries no third-party audio. A bed with no sidecar is a bed nobody can vouch
  for; treat it as unusable.
* **resumable**: a bed with both files is skipped unless `--force`.

Measured 2026-09-13: **~13 s per bed** (≈82 s of audio, three engine passes), so
30 beds is **~6.5 min** and ~250 MB.

`BedPlanner.DeclaredKnobs` is linted against the real engine catalogue by
`RigBedPlanTests`, because a param id the catalogue does not have is dropped
SILENTLY and the bed then renders through that module at its defaults with
nothing anywhere saying so.

---

## 2. Rendering

```bash
dotnet run --project tools/rig-dataset/DotCharter.Tools.RigDataset -- render \
  --out  .../datasets/rig  --di .../datasets/rig/di \
  --nam  "%AppData%/DotCharter/models/nam" \
  --clips 50000 --seed 20260912 --threads 4 --profile mixed
```

| Flag | Default | |
|---|---|---|
| `--clips N` | 1000 | clip indices 0…N−1 |
| `--seed S` | 20260912 | the run seed; a clip's own seed is derived from it |
| `--threads N` | 4 | **N child PROCESSES**, see below |
| `--profile` | `mixed` | `clean` `crunch` `highgain` `bass` `ambient` `direct` |
| `--seconds` | 5 | |
| `--nam <dir>` | — | enables `namamp`; without it the module is never drawn |
| `--beds <dir>` | `<root>/beds` | |
| `--bleed-fraction` | 0.30 | |
| `--separate-fraction` | 0 | fraction sent through htdemucs |
| `--stem-model <onnx>` | — | `htdemucs_6s.onnx` |
| `--cuda-device N` | −1 | the card at `nvidia-smi` index N, mapped to its DirectML adapter by name (−1: DXGI adapter 0). The `separate` subcommand picks by NAME instead; prefer it. |
| `--di-pitch-max` / `--di-level-max` | ±2 st / ±6 dB | the DI-stage augmentations. Both always recorded. |
| `--dry-run` | | manifest only, no WAVs |

`merge --out <dir>` folds leftover `manifest.shard*.jsonl` if a run was killed
before it could.

### `--threads N` means N PROCESSES, and that is not a preference

`JuceDspChain.ProcessOffline` refuses while a device is open — so the renderer
opens the JUCE **`Null`** device (`DCA_ENABLE_NULL_DEVICE=1`) and `Stop()`s it —
and it is **also not concurrent, not even across engine seats**. Four workers on
one engine produced **1 clip and 199 `JuceAudioStatus.Device` refusals** on the
first smoke render (2026-09-12). So the parent spawns N children at **Idle**
priority, each rendering `index % N == shard` into its own
`manifest.shard<i>.jsonl`, and merges them sorted at the end. N is not capped at
`MaxPlayers`; seats are irrelevant here.

### A render process is FLAT in memory, and the DI cache is why it was not

`ClipSource` caches decoded pool files, and until 2026-09-13 it was bounded by
ENTRY COUNT (`if (_cache.Count > 4_096) _cache.Clear()`) over entries that are
one whole decoded clip each — a 5 s mono `float[]` is 960 KB, so the real
ceiling was **~3.9 GB per child**, times `--threads N`. The overnight 100k run
measured **9.5 and 10.3 GB of private bytes per child after 140 minutes**, and a
supervisor was recycling children over 1.5 GB to keep the box alive.

It is an LRU bounded by BYTES now — `ClipSource.DefaultCacheBudgetBytes`,
192 MB per pool, per process. Measured on a 400-clip probe over the real
4,234-file pool: **114 MB → 680 MB in 350 clips (1.62 MB/clip) before**,
**flat at ~450 MB from clip 200 on after**, at unchanged throughput (a miss is
one 480 KB WAV decode against a ~500 ms render). A 20-file pool that FITS was
flat either way, which is what identified the cache rather than the engine, the
rig or the WAV writer. `ClipSourceCacheTests` pins the bound.

### Determinism is per CLIP, not per run

Every decision for clip *i* — which DI file, which window, which rig, every knob,
every augmentation, which bed — comes from
`new Random(SplitMix64(runSeed, i, attempt))`. Therefore:

* four processes produce the same corpus as one,
* a **resumed** run produces the same corpus as an uninterrupted one (indices
  already in the manifest are skipped, and the index IS part of the seed, so
  nothing is renumbered),
* a single row can be re-rendered years later from its own `Seed` alone,
* the manifest is **rewritten sorted by clip index** on every flush, so the FILE
  is reproducible even though the WORK is not.

The DI pool is enumerated **sorted ordinally** for the same reason: filesystem
order would make the same seed produce a different corpus on another machine.

### Reject rules

A draw is thrown away and re-attempted (up to 3 times, each with its own derived
seed) when the DI window is near-silent, the render is > 20 % clipped, the render
is below −60 dBFS RMS, the render **correlates > 0.9995 with the DI** — a rig
that did nothing carries no information about its own label — or the final peak
trim leaves it **more than 6 dB under the −18 dBFS level target** (a crest factor
the corpus cannot level).

### Augmentation, and why every stage is recorded

`RigDatasetRow.Augmentations` is a list of tags, and an augmentation nobody can
see in the manifest is an unexplained distribution shift when the model
underperforms on one slice. Tags are formatted **invariant** — a manifest whose
tags depend on the renderer's locale (`tilt:-1,2dB`) is one two machines cannot
compare.

| Tag | Stage |
|---|---|
| `di-pitch:-1.19st` | the DI resampled by ±2 semitones BEFORE the rig. A rate change, so formants and tempo move with it — which is what physically happens on a guitar tuned down, and unlike a phase vocoder it does not smear the transients a drive keys off. |
| `di-level:+1.8dB` | ±6 dB on the DI AFTER its normalisation. How hard the DI hits a drive is most of what "the same rig sounds different" means. |
| `pickup-comb:2.49ms/0.64` | a feed-forward comb standing in for pickup position, on half the clips. **An approximation, not a physical model** — a real pickup's comb follows the STRING, and we do not know which string was played. |
| `norm:-18.4dBFS` | RMS normalisation to −18 dBFS ±3 dB **jitter**. The jitter is the point: a corpus at exactly one level teaches the model that level. |
| `tilt:+2.3dB` | bounded ±3 dB first-order tilt about 1 kHz. Stands in for guitar/mic/mastering differences without ever changing which modules are in the rig. |
| `bleed:-32dB` / `bed:<path>` | a backing bed under the clip |
| `bleed:skipped-no-beds` | there were no beds |
| `mix:+1.2dB` | stage B's guitar-to-mix level: the guitar relative to the BED, drawn in −12…+3 dB. The opposite direction from `bleed:`, and the difference is the whole stage. |
| `separated:htdemucs_6s` | stage B; `:no-bed` when it ran without one |
| `separated:unavailable` / `:failed` | asked for, could not be done |
| `peak-trim:-1.2dB` | the render was over 0.97 FS |

### The clip is rendered STEREO and stored MONO

`ProcessOffline`'s `channels: 1` overload folds the rig's **output** down as well
as duplicating its input, and allocates where the stereo overload works in place.
So the DI is duplicated into L/R, the stereo overload runs, and `0.5*(L+R)` is
stored. Mono is right — the inference input is a mono guitar stem, and a mono sum
is what a listener of a mono mix hears. The cost: a purely stereo effect
(`autopan`, `midside`, `widener`) partly cancels in the sum, which is why those
three sit at the bottom of the sampler's weight table. **This is a known label
imprecision, not an oversight.**

---

## 3. Stage B: mix-and-separate — its own pass, its own GPU rule

```bash
dotnet run --project tools/rig-dataset/DotCharter.Tools.RigDataset -- separate \
  --out .../datasets/rig-v2 --beds .../rig-v2/beds \
  --stem-model .../rig/_models/htdemucs_6s.onnx \
  --separate-fraction 0.40 --seed 20260913 --gpu-name 3080
```

**The most important augmentation, and the one v1 never ran.** Every real target
IS a separated stem — the match lane gets its guitar out of `StemIsolator` — so a
corpus of clean renders trains the model on a distribution that does not occur.
This pass takes a rendered clip, mixes it INTO a bed at a realistic
guitar-to-mix level (−12…+3 dB, drawn and recorded as `mix:+1.2dB`), separates
the mix with the app's own htdemucs, and stores the guitar stem as the training
input **under the original rig label**.

| | |
|---|---|
| **Where the stem goes** | `clips-sep/<bucket>/<index:D8>.wav`, and the row's `ClipPath` is repointed at it. **Never over the original.** In-place overwriting makes a crash between the WAV write and the manifest flush leave a separated clip whose row says "clean" — and the resume then separates it a SECOND time, producing a stem of a stem. A side path makes the pass idempotent. |
| **Which rows** | `Random(SeedFor(seed, i, 11)).NextDouble() < fraction`. Selection is a property of the CORPUS, not of the run: four shards choose the same set as one, and raising the fraction later only ever ADDS rows. |
| **Resume** | any row already carrying a tag starting `separated:` is skipped — `:failed` and `:unavailable` included, or one unseparable clip is retried for ever. |
| **Stopping** | `--stop-file <path>`; its EXISTENCE ends the pass cleanly between rows with the manifest flushed. |
| **Shards** | `manifest.shardsep<i>.jsonl`, folded by the ordinary `merge` (the glob is `manifest.shard*.jsonl`, and merge loads `manifest.jsonl` FIRST so a shard row wins). |

### The GPU is chosen BY NAME, and the gaming card is refused

This box has two cards and three disagreeing numberings: `nvidia-smi` is PCI
ordered (**0 = RTX 3080**, 1 = RTX 5070 Ti), CUDA defaults to `FASTEST_FIRST`,
and torch calls the 5070 Ti `cuda:0`. The 5070 Ti is the owner's **gaming** GPU.
So `--gpu-name 3080` (the default) resolves the name against
`nvidia-smi --query-gpu=index,name,uuid`, then maps that NAME onto exactly one
DXGI adapter — the DirectML `deviceId` (DXGI's adapter 0 is the display card, the
5070 Ti, so an index would be wrong here too). Two identical cards or no match
refuse rather than guess. A card matching `5070` is **REFUSED** unless
`--allow-gaming-gpu` is passed.

The app's ONNX Runtime is the DirectML build — there is no CUDA provider,
so `CUDA_VISIBLE_DEVICES` pins nothing — and `OnnxStemSeparator` takes the
`deviceId` directly.

### It REFUSES to fall back to CPU, and that is the point

`OnnxStemSeparator` degrades a GPU request to the CPU on its own and hands back a
working session either way, so a GPU that cannot load the model does not throw —
it makes the pass fifteen times slower and says so in one log line nobody is
reading at 05:10. The stage therefore checks `ActiveExecutionProvider` and exits
**3** unless it got DirectML or `--cpu` was passed. Exit 3 is not a corrupt corpus: the render is intact
and `-Stages separate,merge` picks it up later.

Measured CPU throughput, one process, 4 intra-op threads, Debug build:
**6.7 clips/min** (~9 s per 5 s clip; `run` 3.0–8.8 s, iSTFT 1.5–2.6 s). At 40 %
of 100,000 clips that is **~100 CPU-hours**, which is why the refusal exists. For
scale: a full 224 s song separated on DirectML in 22–24 s on either card here, against 97–158 s on 8 CPU cores.


## 4. Monitoring and stopping the overnight render

```bash
tail -f  ../DotCharter-local/datasets/rig/render.log
wc -l    ../DotCharter-local/datasets/rig/manifest.shard*.jsonl   # live progress
```

Stop it: **Ctrl-C once** in the parent flushes and exits cleanly. From elsewhere:

```powershell
Get-Process DotCharter.Tools.RigDataset | Stop-Process
```

then `render ... --merge` — actually `merge --out <root>` — to fold the shards,
and re-run `run-render.sh` to resume from where it stopped. Nothing is lost: the
manifest is flushed at every progress tick.

The whole run is at **Idle** priority and touches no audio device.

---

## 5. `knob-sensitivity` — how much each knob is worth to the metric

```powershell
dotnet run --project tools/rig-dataset/DotCharter.Tools.RigDataset -c Release -- `
  knob-sensitivity `
  --out tools/rig-model/knob_sensitivity.json `
  --di ..\DotCharter-local\datasets\rig\di\guitar-techs\P1_chords__directinput_Drop3_7_00000.wav
```

One number per `(module, param)` in `RigLabelVocabulary`: render ONE fixed
reference DI through that module twice — the param under test at normalised
**0.1** and at **0.9**, every other param at its catalogue **default** — and
record the **raw** `ToneSimilarity` distance between the two renders. The python
trainer (`tools/rig-model`) normalises it and applies its own floor to get a
per-knob loss weight; a knob nothing can hear must not cost the model the same
as a gain knob.

- **Context is probed, not hand-listed.** Each module is first rendered solo at
  its defaults against the DI through an empty chain. Audible ⇒ every param of
  that module is measured **solo**. Inaudible ⇒ the module is measured behind a
  default **`amp → cab`** instead (that is what reaches `micpos`, `volume`,
  `transient`, `gain`, …). The context is recorded per module in the JSON, and a
  distance measured in `ampcab` is not on the same scale as a `solo` one.
- **`splitter` / `merger` are skipped** — routing is topology, not tone; they are
  recorded as 0 with context `skipped`.
- **Level knobs read near zero, and that is the metric's position**, not a bug:
  `RigRenderer` level-matches its output and the feature vector is
  content-invariant, so `master` / `makeupDb` / `outputDb` genuinely do not move
  it.
- **`inert` names modules that rendered but moved nothing on any knob** — today
  `freeze`, `looper`, `namamp`. They need a footswitch event or a string param
  (a NAM capture path) that an offline A/B never supplies. Those zeros are a gap
  in the measurement, not a verdict on the module.
- `--limit N` measures only the first N params (smoke run); `--di` also takes a
  directory and uses its first WAV ordinally. With no DI pool at all it falls
  back to the repo's synthesised `ReferenceDi` and says so in `referenceDi`.

Sequential, one engine per process — `ProcessOffline` is not concurrent, the same
rule §2 states. The full 573-param sweep is **≈6½ minutes** at ~85–100 params/min
(measured 2026-09-13, Release, low priority). It is deterministic: two runs give
byte-identical numbers.

---

## 6. The unattended v2 run

```powershell
pwsh tools/rig-dataset/pipeline-data-v2.ps1          # the real thing
pwsh tools/rig-dataset/pipeline-data-v2.ps1 -WhatIf  # print the four commands
pwsh tools/rig-dataset/pipeline-data-v2.ps1 -SkipWait -Clips 2000 -Beds 4
```

`beds` → `render` → `separate` → `merge`, into
`DotCharter-local/datasets/rig-v2`. Every stage is resumable and every child runs
at **Idle** priority. **Do not point it at the v1 root**: a resumed render SKIPS
indices already in the manifest, so it would render nothing.

**Stage 0 waits for the GPU.** It records `model.onnx`'s mtime under
`%LOCALAPPDATA%/DotCharter/ai-match/rig-estimator/` *before* it waits, then waits
for a NEWER one that then stops changing for `-SettleSeconds`. Polling for the
directory, or even for the file, would be useless — an export from the previous
run is already sitting there.

**Stopping it**: create `<Root>\STOP`. The separate stage checks it between rows
and exits with the manifest flushed; the outer loop checks it between stages.
Delete it and re-run to resume.

### Measured stage costs (2026-09-13, Debug build, idle box)

| Stage | Measured | Projected for the full run |
|---|---|---|
| beds | 3 beds / 39 s = **13 s per bed** | 30 beds ≈ **6.5 min**, ~250 MB |
| render | 199 clips / 58 s over 4 processes = **~224 clips/min** | 100,000 clips ≈ **7.4 h**, ~48 GB |
| separate (CPU) | 10 clips / 89 s, one process = **6.7 clips/min** | 40,000 clips ≈ **100 h** — not viable, and the stage refuses rather than starting it |
| separate (DirectML) | not yet measured per clip — a whole 224 s song took 22–24 s on either card | on the measured split (inference 3–9 s on CPU, iSTFT 1.5–2.6 s, and the iSTFT is CPU either way) expect roughly **2.5–4 s per clip**, i.e. **28–44 h** for 40,000 |

So the render is an overnight job and the separate pass is **several nights**, or
one if DirectML turns out much faster than the CPU/GPU split above
suggests. Both resume, so running it in nightly slices costs nothing.

**`knob-sensitivity` (§5) is not one of its stages and must not become one.** It
measures the METRIC against a fixed reference DI; it depends on neither the beds
nor the corpus, it takes six minutes, and folding it into a seven-hour unattended
run would only make it something nobody re-runs after touching `ToneSimilarity`.

## 7. `eval-render` — does the estimator's guess SOUND close?

```powershell
dotnet run --project tools/rig-dataset -- eval-render `
  --predictions <run>/predictions-test.jsonl `
  --root ..\DotCharter-local\datasets\rig `
  --limit 200 --seed 20260914 --threads 2
```

Renders the PREDICTED rig over each row's dry DI span, measures the
`ToneSimilarity` (`tonefx-v2`) distance to the target, and writes
`render-eval.jsonl` plus `render-eval.summary.json` next to the predictions file.
`tools/rig-model/render_eval.py` drives it over several run directories and
prints one table — the "sound distance" column beside famF1 and knobMAE.

Per row: `distance`, the target's own `selfFloor` (a disjoint window of itself —
NaN, never 0, when the span is too short for two), `distanceOverFloor` (the only
column comparable across rows), `genericDistance` (a fixed
`overdrive → amp → cab` at catalogue defaults, the audio eval's `floor-generic`,
over the SAME span), and the predicted rig's plausibility penalty. The summary
adds median/mean distance, median `d/floor`, the fraction under 1.5×/2×/3×, the
fraction that beats generic, and the same again over the top-N most confident
rows.

- **`--target clip` (default)** scores against the wet clip on disk — the product
  question. It carries the corpus augmentations AND the 16-bit storage, so a
  perfect prediction does NOT measure 0 (that round trip alone measured 0.1436).
- **`--target render`** re-renders the truth rig as the target, where an
  identical prediction measures **exactly 0**. That is the harness's own
  non-vacuity check, and the only mode a predictions file synthesised from
  `manifest.jsonl` can use — the manifest does not record the DI window offset.
- **A predictions row must carry `"seconds": [start, length]`**, or the
  prediction is rendered over the START of the DI while the clip came from
  somewhere inside it. The tool warns when no row has one.
- **`--refine`** (opt-in, slow) runs the matcher's CMA-ES refinement from the
  prediction as its prior and reports the distance after it.

One engine, one seat, renders serialised — `--threads` only bounds the rows in
flight. The process drops to BelowNormal unless `--normal-priority`. Measured
2026-09-14 on a box running two GPU trainings: **2.63 s/row** at three renders a
row (predicted, generic, truth target).

**It inherits the metric's ceiling**: `tonefx-v2` measures 1.56× content
invariance, so two models within a few percent of each other are the same model
as far as this table can tell. Detail and the validation numbers:
`docs/agents/testing/rig-dataset.md`.

## 8. `build-library` — the shipped retrieval library

```powershell
dotnet build tools/rig-dataset/DotCharter.Tools.RigDataset -c Release
tools/rig-dataset/DotCharter.Tools.RigDataset/bin/Release/net10.0/win-x64/DotCharter.Tools.RigDataset.exe `
  build-library --out <dir>\ [--count 4000] [--seed 20260912] [--progress-every 50]
```

Renders the matcher's retrieval library (`DotCharter.Services/Ai/Match/Search/RigLibrary.cs`)
for the BUNDLED reference DI — K sampled rigs through the real engine over the
20 s `ReferenceDi.Medley()`, embedded with `tonefx-v2` — through the SAME
`RigLibraryBuilder` the app grows its own cache with, so the file is
byte-for-byte what the app would have produced: same header, same rows, same
order. **RESUMABLE**: rerun it and it continues at the last complete row. With
`--out` a directory it writes the app's own file name
(`library-tonefx-v2-4000-20260912-5e4b70d7.bin`); a different name is accepted
and the expected one printed. Runs at BelowNormal unless `--normal-priority`.

At the end it reads the file back the way the app will and prints rows,
feature dimension, byte count and the SHA-256 **of the .bin**. The asset is the
ZIP of that file (`rig-library-v1.zip`), and the digest the catalogue pins
(`RigEstimatorModelCatalog.Library.Sha256`) is the ZIP's — compute it after
zipping. Measured 2026-09-14 on an otherwise idle box: **1.7–1.9 s a row at
BelowNormal, ~2 h for 4,000 rows**, 357 features a row, ~2.6 KB a row on disk.
Why it exists, and what the app does WITHOUT it (small-first, then a
background grower): `docs/agents/services/ai-match-search.md` § "The library
GROWS".
