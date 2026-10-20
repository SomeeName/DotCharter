# rig-model — the "match a tone from audio" MODEL lane

A trained rig estimator: wet guitar audio in, a guessed effect chain with knob
values out. It is a **prior for the search-based matcher**, not a replacement
for it — the study's decisive finding (Okita & Katayose 2026) is that a DNN's
type prediction *plus* search over the real renderer beats prediction alone on
every metric.

The C# half is `DotCharter.Services/Ai/Match/Model/RigEstimatorOnnx.cs`.

## Files

| File | Purpose |
|---|---|
| `env.ps1` | Creates/activates the venv, pins the 3080, prints versions |
| `vocab.py` | Loads `rig_label_vocab.json`; the `[0,1]` normalisation, twinned with `RigLabelVocabulary` |
| `rig_label_vocab.json` | **Build product** of `RigLabelVocabularyTests` — 101 modules, 13 families, 595 knobs (573 until the 2026-10 amp / bass-head params; 583 until `rsamp`). Never hand-edit |
| `data.py` | Manifest loader, DI-source split, fp16 PCM memmap cache |
| `model.py` | `LogMel`, `ModulationBranch`, `MelCnn` (control), `HfTower` (CLAP/AST), the **v1** heads and loss |
| `model_v2.py` | The **v2** head: `ChainDecoder`, greedy + beam, the sequence loss, `ExportWrapperV2` |
| `metrics.py` | Chain metrics shared by train and eval — exact match, edit distance, the **readiness rate** |
| `sensitivity.py` | Reads `knob_sensitivity.json` into the per-knob loss weight vector |
| `knob_sensitivity.json` | **Measured** per-(module, param) audibility. Product of `rig-dataset knob-sensitivity` |
| `train.py` | Training loop, CSV log, checkpoints, GPU-by-name guard, `--head`, `--unfreeze-backbone` |
| `eval.py` | Per-head metrics on the test split + the per-module recovery table + readiness |
| `export_onnx.py` | Opset-17 ONNX + sidecar + the parity fixture the C# test pins against |
| `tests/test_v2.py` | The v2 head's unit tests. Runs with **or without** pytest |
| `make_smoke.py` | A **crude** synthetic set to prove the pipeline before real data exists |
| `bench_frontend.py` | The front-end bake-off (licence, params, CPU ms) |

## The front-end, and why

Measured, not assumed (`bench_frontend.py`, one 5 s clip, 4 CPU threads):

| Candidate | Licence | Params | torch CPU | Verdict |
|---|---|---|---|---|
| AST (`MIT/ast-finetuned-audioset-10-10-0.4593`) | Apache-2.0 | 86.2 M | **1422.6 ms** | fails the <1 s in-app budget |
| **CLAP HTSAT (`laion/clap-htsat-unfused`)** | **MIT** | **27.5 M** | **77.4 ms** | chosen |

CLAP also wins on a detail that matters more than it looks: it is pretrained at
**48 kHz**, the rate our renders are already in, so the ONNX graph needs no
resampler. AST is 16 kHz and would need a decimator in the graph.

> **`ClapAudioModel.from_pretrained` does not load this checkpoint on
> transformers 5.x.** The keys are `audio_model.audio_encoder.*` and every
> `audio_encoder` parameter comes back MISSING — randomly initialised. It still
> runs and still trains; it is just not pretrained, which was the entire reason
> to pick it, and the only signal is a warning block inside a download log.
> `model.load_clap_tower` strips the prefix itself and asserts zero missing keys
> plus a patch-embed spread that is not the random-init spread. Do not replace
> it with a bare `from_pretrained`.

The **CNN control** (`--frontend cnn`, 7.4 M, from scratch) exists so the
transformer's benefit is measured rather than assumed. Run both, every time.

## Heads and loss

**`--head v1|v2`, default `v1`.** v1 is the CONTROL and stays runnable
unchanged; v2 is the chain decoder. A change that also moves its own baseline
measures nothing, so nothing that was running before this flag existed behaves
differently.

### v1 — four independent heads (`model.py`)

| Head | Shape | Loss |
|---|---|---|
| family presence | 13 sigmoids (native palette categories) | BCE |
| chain slots | 7 × 101 softmax (100 modules + `<none>`), order-aware | CE, label smoothing 0.05 |
| knobs | 582 in `[0,1]`, **masked by module presence** | masked L1 (weight 2.0) |
| confidence | 1 sigmoid | BCE against the batch's realised slot accuracy |

The knob mask is load-bearing: a param whose module is not in the rig has no
correct value, and `0` is a real value for most ranges, so an unmasked L1 trains
every absent module toward its range minimum.

**What caps it, and it is structural.** Each slot is predicted INDEPENDENTLY
from one pooled embedding, so slot 3's softmax cannot see that slot 2 already
took the overdrive. The model spreads probability over every plausible
ordering and the argmax chain is often one no sampler would draw. Measured on
the first full-set CLAP run: family F1 **0.84** while occupied-slot accuracy
was **0.42**.

### v2 — a chain decoder (`model_v2.py`)

The same front-end and the same family head; the slot head is replaced by a
small autoregressive transformer decoder that emits
`<bos> m1 m2 … <eos>` and cross-attends the audio embedding.

| Piece | Shape |
|---|---|
| decoder | 3 layers, d=256, 4 heads, ff=1024, `norm_first`, causal — **5.22 M params** (11.61 M whole model with the CNN, 34.13 M with CLAP) |
| conditioning | 8 memory tokens projected from the 768-d trunk, cross-attended at every layer |
| token head | 103 classes = PAD/BOS/EOS + 100 modules, CE with label smoothing 0.05, PAD ignored |
| knob head | 256 → 512 → 582 per step, supervised on **that step's module's params only** |
| confidence | `exp(mean log p)` over the emitted tokens, **no parameters** |

Three conventions hold the whole thing together:

- **Token `k + 2` IS slot class `k`, and `EOS` IS the `<none>` class.** That is
  what lets the v2 export fill v1's `slot_probs` from the decoder's own
  distribution instead of a second, drifting mapping.
- **`PARAM_MASK[token]`, one table**, restricts a step to its own module in the
  loss AND in the export. Without it every step regresses all 582 knobs and
  learns the dataset's marginal — v1's failure one level in.
- **Greedy is UNROLLED** to a fixed 7 steps, so it exports at opset 17 as plain
  ops: no `Loop`, no data-dependent shape. **Beam search (k=3) is python-side
  only** and is reported, never shipped — the graph decodes greedy, so
  exporting on a beam number would ship a model measured by a decoder C# does
  not run.

Teacher-forced in training; the metrics are always taken from a FREE-RUNNING
decode, because a teacher-forced accuracy is a different and much kinder number.

### The knob loss is weighted by measured AUDIBILITY

A plain MAE treats a knob nobody can hear the same as the gain knob, and the
owner's bar is an audible one. So each (module, param) carries a weight
measured once by `tools/rig-dataset`'s `knob-sensitivity` subcommand: render one
reference DI through a default rig with the param at 0.1 and at 0.9, take the
`ToneSimilarity` distance between the two renders. `sensitivity.py` normalises
by the 95th percentile (not the max — one loud module would flatten everything
else) and clamps into `[0.1, 1]`.

**The 0.1 floor is load-bearing**: "the A/B render did not move the distance" is
not "this param does nothing", and a zero weight is an untrainable knob for
ever. With no `knob_sensitivity.json` every weight is 1.0 and the term is v1's
masked MAE exactly — which is what keeps the two heads' knob numbers
comparable.

```powershell
python train.py ... --head v2 --knob-weights knob_sensitivity.json   # or 'auto'
```

`--knob-weights` is IGNORED by `--head v1`, deliberately: v1 is the unweighted
control.

### The readiness rate — the owner's bar in numbers

> "I want it to get to a point where you can skip the refinement and are ready
> to go most of the time."

`metrics.py` answers exactly that. A clip is **ready** when the chain is
EXACTLY right (right modules, right order, right length) AND every knob of
every module in it is within **0.1** of travel; `ready_rate_loose` is the same
at 0.2. It is all-or-nothing per clip on purpose — a rig with six right knobs
and a seventh 0.4 out is a rig the user has to go and fix, and an average MAE
hides precisely that. Both numbers are in `eval.py`'s JSON, in `train.py`'s
per-epoch line and in `log.csv`, for BOTH heads, computed by one piece of code.

`chain_edit_distance` is reported beside them because exact match is brutal and
slot accuracy is too kind: a chain that is right but for one inserted pedal
scores 0 on the first and near 0 on the second (every later slot is shifted),
while the edit distance says 1 — which is what a user would say too.

## Running

```powershell
./env.ps1                                    # venv + versions + GPU check

# train the control and the transformer on the same data
python train.py --data <dir> --out runs/cnn  --frontend cnn  --epochs 60 --batch 32
python train.py --data <dir> --out runs/clap --frontend clap --epochs 45 --batch 24

python eval.py --checkpoint runs/cnn/best.pt --data <dir> --split test
python export_onnx.py --checkpoint runs/cnn/best.pt `
    --out $env:LOCALAPPDATA\DotCharter\ai-match\rig-estimator

# the v2 chain decoder, full fine-tune, audibility-weighted knobs
python train.py --data <dir> --out runs/clap-v2 --head v2 --frontend clap `
    --unfreeze-backbone --grad-checkpointing --knob-weights auto `
    --epochs 45 --batch 24
python eval.py --checkpoint runs/clap-v2/best.pt --data <dir> --split test          # greedy
python eval.py --checkpoint runs/clap-v2/best.pt --data <dir> --split test --beam 3

# the head's own unit tests -- CPU, seconds, no dataset, no pytest required
python tests/test_v2.py
```

`--head`, `--decoder-layers/--decoder-dim`, the knob weighting and the
checkpointing route are all recorded in `run.json`, and **`head` is written into
every checkpoint** so `eval.py` and `export_onnx.py` never have to guess which
architecture a `.pt` describes.

**`--unfreeze-backbone` builds two param groups**: the heads at `--lr`, the
pretrained tower at `--backbone-lr-scale` × that (default 1/10). A pretrained
tower fine-tuned at the head LR forgets what it was pretrained for inside an
epoch — the same failure as `load_clap_tower`'s silently-random weights, reached
from the other side and just as invisible. Verify the groups without a GPU:

```powershell
python train.py --data <dir> --head v2 --frontend clap --unfreeze-backbone `
    --grad-checkpointing --device cpu --require-gpu '' --batch 4 --workers 0 `
    --dry-run-batches 2
```

> **`ClapAudioModel` REFUSES HuggingFace's `gradient_checkpointing_enable`**
> ("does not support gradient checkpointing", transformers 5.x, measured
> 2026-09-13) — and CLAP is exactly the front-end a full fine-tune needs it for.
> So `HfTower.enable_checkpointing` falls back to ONE `torch.utils.checkpoint`
> segment around the whole tower: coarser than per-layer, saves more, costs one
> extra forward. The log line says which route was taken. It is a no-op on a
> FROZEN tower (no activations to save) and says that too.

`train.py` and `eval.py` select the GPU **by name** and refuse a card that does
not match `--require-gpu` (default `3080`). torch orders CUDA devices
fastest-first, so the 3080 is torch's `cuda:1` while `nvidia-smi` calls it `0` —
getting that backwards costs the owner their frame rate and is invisible from
inside the process.

### The whole thing, unattended

A full-set run is 1.5 h + 4 h + two evals + an export, on the owner's gaming
box. `runs/pipeline-full.ps1` chains all of it at below-normal priority, writes
`runs/pipeline-full.log` plus one live log per stage, and **exports exactly one
model — whichever front-end won** on a composite the script states in the open
(0.4 family F1 + 0.4 occupied-slot accuracy + 0.2 knob term; types outweigh
knobs because the estimator is a prior the *search* then refines).

`runs/pipeline-v2.ps1` is the v2 stage, same shape and same rules, to be run
**after** `pipeline-full` has finished — both want the 3080 and the same cache
dir, and two trainers on one card is how a 4-hour stage becomes an
out-of-memory crash at hour three. It trains one model (`--head v2
--unfreeze-backbone`), evaluates greedy and beam, and **exports only if v2 beats
v1 on the READINESS rate**, refusing outright if there is no comparable v1
number. `-Data` points it at whichever dataset is current, so it can be aimed at
the data lane's set when that lands — with its OWN `-Cache`, because the fp16
cache is indexed positionally and fingerprinted over the clip paths in order.

```powershell
Start-Process pwsh -WindowStyle Hidden -ArgumentList '-NoProfile','-File', `
    'tools\rig-model\runs\pipeline-full.ps1'

New-Item -ItemType File runs\STOP        # stops it between stages
```

`runs/` is ignored except that one file — note the ignore rule is `runs/*`, not
`runs/`: git never descends into an ignored *directory*, so a negation under
`runs/` can never match.

Its two stage-0 gates run before anything long starts: the dataset pickle
budget, and the LR-horizon arithmetic (`train.py --self-test-schedule`, no GPU,
no dataset).

### The LR horizon follows the TIME cap, not `--epochs`

`--epochs 60 --minutes 90` used to size a one-cycle over 60 epochs and then be
killed at ~epoch 24 — **around the LR peak**, never annealed. Measured on this
box: the LR at the stop point was `2.0e-4` of a `3.0e-4` max, against `3.0e-8`
on a horizon that fits. A whole GPU night for a checkpoint the same wall clock
beats comfortably.

So `--epochs` is a **ceiling** now, and the horizon is re-planned from measured
epoch time:

- epoch 1 is a linear warm-up from `lr/25` to `lr`. That epoch's OWN wall time
  is the estimate — `elapsed/epoch` would charge process start, the cache check
  and the model build to every future epoch and under-plan by ~15 % — while the
  remaining budget is measured from the process start, so startup is paid for
  exactly once;
- `plan_epochs()` sets the horizon to
  `min(--epochs, completed + (time_left // (seconds_per_epoch × 1.03)))` and a
  cosine anneals to `lr/1e4` **on that last epoch**;
- later epochs may only SHRINK the horizon — one that grows keeps pushing the
  cosine's end away, and then the anneal never lands at all;
- best-by-val checkpointing is unchanged, and `run.json` gains
  `schedule.{requested_epochs, planned_epochs, epochs_run, seconds_per_epoch}`.

`--minutes` stays as a hard mid-epoch backstop. The arithmetic has its own
self-test, because it decides how good the shipped checkpoint is and is
invisible in a training log:

```powershell
python train.py --self-test-schedule
```

**Both front-ends share ONE `--cache-dir`.** The full set's fp16 PCM cache is
19.4 GB and ~25 min to render; a second run with its own would pay it again for
byte-identical audio. `train.py` records the dir it used in `run.json`, and
`eval.py` reads it back, so an eval never re-renders the cache into the run
folder.

## The Windows `spawn` rule: the Dataset must be cheap to pickle

Windows has no `fork`. Every `--workers N` loader process is a fresh
interpreter, and torch **pickles the whole `Dataset` down a pipe to each one**.

The first full-set run died on exactly that: `RigClips` kept the fp16 PCM cache
on the instance as an `np.memmap`, which is an `ndarray` *subclass* — pickling
it serialises the 19.4 GB backing store, not the filename — next to 40,413 rows
of expanded labels (573 knobs + 573 mask floats each). The parent died with
`OSError: [Errno 22] Invalid argument` inside `multiprocessing/reduction.dump`
and the child with `UnpicklingError: pickle data was truncated`, **after** the
25-minute cache build.

So the dataset holds **paths, shapes and the vocab, nothing else** (~38 KB
pickled). The PCM memmap, the four label `.npy` arrays and the clip-path list
are opened lazily in whichever process first asks, and `__getstate__` drops
those handles. Never put an array, a memmap, an open file or a per-row list back
on `self` — it reads fine on Linux, where `fork` shares it, and is fatal here.
**`--workers 0` is not the fix.**

Guard, and it is part of `train.py` too (before a single loader is built):

```powershell
python data.py --data <dir> --cache-dir <dir>   # fails over 1 MB pickled
```

## Two traps worth keeping in mind

**Everything here is a fixed-length linear transform, on purpose.** `torch.stft`
has no opset-17 export ("STFT does not currently support complex types") and
`torch.fft.rfft` has none at all, so `LogMel` is two `conv1d`s against
precomputed cos/sin kernels and `ModulationBranch` is a matmul against a 96-row
DFT matrix. That is what lets the mel live *inside* the graph, which is what
stops C# needing a second mel implementation.

**The split is by DI SOURCE.** Every clip is one DI recording through one rig,
so a per-clip split puts the same playing in train and val, and the val number
then measures memorisation of a riff. `data.py` warns when the split leaves val
or test empty — with few distinct DI sources the hash cannot produce a held-out
group, and any metric from such a run is meaningless.

## The crude smoke set

`make_smoke.py` renders through four hand-written stages (gain, `tanh`, one-pole
low-pass, feedback delay). It exists only to prove the pipeline before the data
lane's renders land. Every row is tagged `synthetic-crude` and `train.py` prints
a banner.

**Its knob labels carry no signal by construction**: the toy renderer is driven
by its own hidden parameters, not by the catalogue params in the label, so the
knob head cannot do better than chance on it (MAE ≈ 0.25). Family and slot
labels *are* honest, because module identity does determine which stage runs.
