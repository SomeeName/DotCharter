"""Trains the rig estimator. CSV log, checkpoints, no tensorboard.

    python train.py --data <dir> --frontend clap --epochs 30 --batch 32

Workers: `--workers N` spawns N loader processes, and on Windows *spawn* means
the `Dataset` is PICKLED to each one. This trainer asserts the pickled size
before it builds a single loader, because the failure mode otherwise is an
`OSError: [Errno 22]` an hour into a run -- see data.py's rule 4. Do not "fix"
a pickling failure with `--workers 0`; fix what is on the Dataset.

Device: the owner games on the RTX 5070 Ti, so training pins the idle RTX 3080.
`torch` enumerates CUDA devices FASTEST-FIRST, not in PCI order -- `nvidia-smi`
calls the 3080 index 0 and torch calls it index 1 -- so the device is selected
by NAME and the run REFUSES to start on a GPU whose name does not match
`--require-gpu`. Getting that backwards costs the owner their frame rate, and
it is invisible from inside the process.
"""

from __future__ import annotations

import argparse
import csv
import json
import math
import os
import pickle
import subprocess
import threading
import sys
import time
from pathlib import Path

import numpy as np
import torch
from torch.utils.data import DataLoader

import metrics as M
import model_v2 as v2
import sensitivity
from data import build_splits
from model import RigEstimator, count_params, multitask_loss
from vocab import DEFAULT_VOCAB_PATH, load_vocab


class PowerGovernor:
    """Closed-loop duty-cycle control that holds a GPU at or under a power target.

    A FIXED duty cycle is open-loop: the wattage it produces depends on the model,
    the batch and the card, so the number that was quiet last night is a guess
    tonight. This measures the card instead and adjusts.

    The card is resolved BY NAME, never by index -- `nvidia-smi` orders by PCI bus
    and torch orders fastest-first, so on this box torch's `cuda:0` is smi's `1`.
    Reading the wrong card's wattage would throttle against a number that has
    nothing to do with the job.

    Asymmetric on purpose: cut hard when over the target (noise is the complaint,
    and a fan that has already spun up takes a while to settle), creep back up
    slowly when under it, so the loop cannot oscillate audibly.

    `control_file` is re-read every 10 s so the target can be lowered on a running
    job without restarting it and losing the epoch.
    """

    DOWN = 0.85     # over target: cut duty 15 % per sample
    UP = 1.02       # under target: recover 2 % per sample
    MIN_DUTY = 0.1
    MAX_DUTY = 1.0

    def __init__(self, gpu_name: str, target_watts: float,
                 control_file: Path | None = None, period: float = 2.0):
        self.gpu_name = gpu_name
        self.target = float(target_watts)
        self.control_file = control_file
        self.period = period
        self.duty = 0.35          # start low: the first sample is one period away
        self.last_power = float("nan")
        self.index = self._resolve_index()
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._run, daemon=True,
                                        name="power-governor")

    def _resolve_index(self) -> str | None:
        try:
            out = subprocess.run(
                ["nvidia-smi", "--query-gpu=index,name", "--format=csv,noheader"],
                capture_output=True, text=True, timeout=10).stdout
        except Exception as exc:                       # nvidia-smi absent or wedged
            print(f"[governor] cannot run nvidia-smi ({exc}); governor DISABLED", flush=True)
            return None
        for line in out.strip().splitlines():
            idx, _, name = line.partition(",")
            if self.gpu_name.lower() in name.strip().lower():
                print(f"[governor] '{self.gpu_name}' is nvidia-smi index {idx.strip()} "
                      f"({name.strip()})", flush=True)
                return idx.strip()
        print(f"[governor] no nvidia-smi GPU matches '{self.gpu_name}'; governor DISABLED",
              flush=True)
        return None

    def _read_power(self) -> float:
        try:
            out = subprocess.run(
                ["nvidia-smi", "--query-gpu=power.draw",
                 "--format=csv,noheader,nounits", "-i", str(self.index)],
                capture_output=True, text=True, timeout=10).stdout
            return float(out.strip().splitlines()[0])
        except Exception:
            return float("nan")

    def _reload_target(self) -> None:
        if not self.control_file:
            return
        try:
            raw = self.control_file.read_text(encoding="utf-8").strip()
            if not raw:
                return
            new = float(raw)
        except Exception:
            return                                     # a half-written file is not an error
        if new > 0 and abs(new - self.target) > 1e-6:
            print(f"[governor] target {self.target:.0f} W -> {new:.0f} W "
                  f"(from {self.control_file})", flush=True)
            self.target = new

    def _run(self) -> None:
        since_reload = 0.0
        while not self._stop.wait(self.period):
            since_reload += self.period
            if since_reload >= 10.0:
                self._reload_target()
                since_reload = 0.0
            power = self._read_power()
            if power != power:                         # NaN: keep the last duty
                continue
            self.last_power = power
            if power > self.target:
                self.duty = max(self.MIN_DUTY, self.duty * self.DOWN)
            elif power < self.target * 0.9:
                self.duty = min(self.MAX_DUTY, self.duty * self.UP)

    def start(self) -> "PowerGovernor":
        if self.index is not None:
            self._thread.start()
        return self

    def stop(self) -> None:
        self._stop.set()


def duty_sleep(device: torch.device, t0: float, duty: float) -> None:
    """Sleep so that the work since `t0` occupies `duty` of the wall clock.

    The sync is not optional: the CUDA queue is asynchronous, so without it this
    measures how fast python got back round the loop rather than how long the
    GPU was busy, and the sleep is computed from roughly nothing.

    Used by the training loop AND the validation loop. Pacing only training is
    the bug this exists to prevent: validation is a full-speed pass over
    thousands of clips through the same 27.5 M tower, so an un-paced one spikes
    the card to full power once an epoch -- which is both the noise the throttle
    was added to remove, and a reading that drives the governor's own duty to the
    floor for the epoch that follows.
    """
    if duty >= 1.0:
        return
    if device.type == "cuda":
        torch.cuda.synchronize(device)
    busy = time.time() - t0
    idle = busy * (1.0 / duty - 1.0)
    if idle > 0:
        time.sleep(min(idle, 5.0))      # cap: a stalled step must not park the run


#: A Dataset over this is bulk on the instance, which Windows pays per worker.
PICKLE_BUDGET_BYTES = 1 << 20

#: The warm-up starts here, as a fraction of --lr. OneCycleLR's div_factor=25.
WARMUP_START_FRACTION = 1.0 / 25.0
#: ...and the anneal ends here. OneCycleLR's final_div_factor=1e4.
FINAL_LR_FRACTION = 1.0 / 1e4


def plan_epochs(
    requested: int,
    completed: int,
    elapsed_seconds: float,
    seconds_per_epoch: float,
    minutes_cap: float | None,
    safety: float = 1.03,
) -> int:
    """How many epochs IN TOTAL this run can finish inside its time cap.

    A schedule sized from `--epochs` while the run is stopped by `--minutes` is
    a wasted GPU night: the first full-set CNN run would have been cut at epoch
    ~25 of 60, which on a one-cycle is barely past the LR PEAK -- the model
    never anneals, and the checkpoint is materially worse than the same wall
    clock spent on a schedule that fit. So the horizon is re-planned from
    MEASURED epoch time and the anneal lands on whatever fits.

    Always >= `completed` (the epochs already paid for cannot be un-run) and
    never > `requested`. `safety` buys a few percent against an epoch that
    runs longer than the average -- overshooting the cap truncates the anneal,
    which is the whole failure being fixed.
    """
    if completed < 0 or requested < 1:
        raise ValueError("requested must be >= 1 and completed >= 0")
    if not minutes_cap or seconds_per_epoch <= 0:
        return requested
    remaining = minutes_cap * 60.0 - elapsed_seconds
    fits = int(remaining // (seconds_per_epoch * safety)) if remaining > 0 else 0
    return max(completed, min(requested, completed + max(0, fits)))


def lr_at(step: int, warmup_steps: int, total_steps: int, max_lr: float) -> float:
    """Linear warm-up over epoch 1, then cosine to ~0 over the PLANNED horizon.

    Deliberately not `OneCycleLR`: that object's horizon is fixed at
    construction, and the horizon here is not known until an epoch has been
    timed. Setting the group lr by hand is the smallest thing that can be
    re-planned, and it is pure arithmetic, so `--self-test-schedule` can check
    it without a GPU.
    """
    if step < warmup_steps:
        t = (step + 1) / max(1, warmup_steps)
        return max_lr * (WARMUP_START_FRACTION + (1.0 - WARMUP_START_FRACTION) * t)
    span = max(1, total_steps - warmup_steps)
    t = min(1.0, (step - warmup_steps) / span)
    cos = 0.5 * (1.0 + math.cos(math.pi * t))
    return max_lr * (FINAL_LR_FRACTION + (1.0 - FINAL_LR_FRACTION) * cos)


def self_test_schedule() -> None:
    """`python train.py --self-test-schedule` -- the planning arithmetic.

    Pure functions, no GPU, no dataset: this is the part that decides how good
    the shipped checkpoint is, and it is invisible in a training log.
    """
    # No cap -> the request stands.
    assert plan_epochs(60, 1, 200.0, 200.0, None) == 60
    # 90 min cap, 214 s/epoch, one epoch done: 5400-214 = 5186 s left,
    # 5186 // (214*1.03) = 23 more -> 24 total, not 60.
    assert plan_epochs(60, 1, 214.0, 214.0, 90) == 24, plan_epochs(60, 1, 214.0, 214.0, 90)
    # A cap that comfortably covers the request does not inflate it.
    assert plan_epochs(10, 1, 60.0, 60.0, 600) == 10
    # Out of time -> what is already done, never less.
    assert plan_epochs(60, 7, 5400.0, 214.0, 90) == 7
    assert plan_epochs(60, 7, 99999.0, 214.0, 90) == 7
    # Never below `completed`, never above `requested`.
    assert plan_epochs(5, 5, 10.0, 1.0, 600) == 5
    # An unmeasurable epoch time falls back to the request rather than to 0.
    assert plan_epochs(60, 1, 10.0, 0.0, 90) == 60

    # The anneal must actually LAND. This is the property the OneCycle bug
    # violated: at the planned last step the lr is at the floor, not the peak.
    warm, total, mx = 100, 1000, 3e-4
    assert abs(lr_at(0, warm, total, mx) - mx * (WARMUP_START_FRACTION
               + (1 - WARMUP_START_FRACTION) / warm)) < 1e-12
    assert abs(lr_at(warm - 1, warm, total, mx) - mx) < 1e-12       # peak at the seam
    assert lr_at(total - 1, warm, total, mx) < mx * 0.01            # floor at the end
    mid = lr_at(warm + (total - warm) // 2, warm, total, mx)
    assert mx * 0.4 < mid < mx * 0.6                                # monotone cosine
    prev = float("inf")
    for s in range(warm, total):
        cur = lr_at(s, warm, total, mx)
        assert cur <= prev + 1e-15, f"lr rose after warm-up at step {s}"
        prev = cur
    # A re-plan that SHRINKS the horizon still reaches the floor.
    assert lr_at(400 - 1, warm, 400, mx) < mx * 0.01
    print("[self-test] schedule planning OK")


def build_model(vocab, args):
    """The v1 slot head or the v2 chain decoder, over the same front-end."""
    if args.head == "v2":
        return v2.RigEstimatorV2(vocab, frontend=args.frontend, freeze=args.freeze,
                                 layers=args.decoder_layers, d_model=args.decoder_dim,
                                 knob_head=args.knob_head)
    return RigEstimator(vocab, frontend=args.frontend, freeze=args.freeze)


def backbone_parameters(model) -> list:
    """The PRETRAINED tower's parameters, or `[]` for the from-scratch CNN.

    `model.tower` is an `HfTower` wrapper; the pretrained weights are the
    `tower.tower` inside it. The CNN control has nothing pretrained, so
    `--unfreeze-backbone` is a no-op there and says so rather than quietly
    building a second group with the same parameters at a tenth of the LR.
    """
    inner = getattr(model.tower, "tower", None)
    return list(inner.parameters()) if inner is not None else []


def build_param_groups(model, args) -> list[dict]:
    """Two groups when the backbone is unfrozen: heads at `lr`, backbone at `lr/10`.

    A pretrained tower fine-tuned at the head LR forgets what it was pretrained
    for within an epoch -- which is the same failure as `load_clap_tower`'s
    silently-random weights, reached from the other side and just as invisible:
    the loss goes down, the front-end is no longer the thing that was measured
    to be worth choosing. Each group carries an `lr_scale`, and the scheduler
    multiplies the planned LR by it, so the time-aware horizon still owns the
    SHAPE of the schedule and this only owns the ratio.
    """
    backbone = {id(p) for p in backbone_parameters(model)}
    head_params = [p for p in model.parameters() if p.requires_grad and id(p) not in backbone]
    back_params = [p for p in model.parameters() if p.requires_grad and id(p) in backbone]
    groups = [{"params": head_params, "lr_scale": 1.0, "name": "heads"}]
    if back_params:
        groups.append({"params": back_params, "lr_scale": args.backbone_lr_scale,
                       "name": "backbone"})
    return groups


def enable_grad_checkpointing(model) -> str | None:
    """Turns on gradient checkpointing over the pretrained tower.

    `HfTower.enable_checkpointing` owns the two routes and the reason there
    are two: `ClapAudioModel` REFUSES HuggingFace's own
    `gradient_checkpointing_enable`, and CLAP is the front-end a full
    fine-tune needs it for. Returns the route taken, or None for a front-end
    with no pretrained tower (the CNN control has no activations worth
    trading).
    """
    fn = getattr(model.tower, "enable_checkpointing", None)
    return fn() if fn is not None else None


def pick_device(requested: str, require_gpu: str | None) -> torch.device:
    if requested == "cpu":
        return torch.device("cpu")
    if not torch.cuda.is_available():
        print("[train] CUDA unavailable; falling back to CPU", file=sys.stderr)
        return torch.device("cpu")

    names = [torch.cuda.get_device_name(i) for i in range(torch.cuda.device_count())]
    if requested != "auto":
        idx = int(requested)
    elif require_gpu:
        matches = [i for i, n in enumerate(names) if require_gpu.lower() in n.lower()]
        if not matches:
            raise SystemExit(
                f"no visible GPU matches '{require_gpu}'. Visible: {names}. "
                "If CUDA_VISIBLE_DEVICES is set, the wanted card may be masked out."
            )
        idx = matches[0]
    else:
        idx = 0

    name = names[idx]
    if require_gpu and require_gpu.lower() not in name.lower():
        raise SystemExit(
            f"cuda:{idx} is '{name}', not '{require_gpu}'. Refusing to train on it -- "
            "torch orders devices fastest-first, so this is most likely the card the owner "
            "is gaming on. Pass --require-gpu '' to override."
        )
    print(f"[train] device cuda:{idx} = {name}", flush=True)
    return torch.device(f"cuda:{idx}")


@torch.no_grad()
def evaluate(model, loader, device, vocab, head: str = "v1",
             knob_weight: torch.Tensor | None = None,
             pace=None,
             tail_weight: float = 0.0, tail_tol: float = v2.TAIL_TOL) -> dict:
    """Per-head losses plus the CHAIN metrics both heads are compared on.

    The chain numbers (exact match, slot accuracy, edit distance, the readiness
    rate) all come from `metrics.py` on v1-SHAPED arrays, whichever head
    produced them: a per-slot argmax and a greedy decode are two models of the
    same object, and comparing them on two different pieces of code is how a
    comparison stops meaning anything.
    """
    model.eval()
    n = 0
    agg = {"loss": 0.0, "fam": 0.0, "slot": 0.0, "knob": 0.0, "conf": 0.0}
    fam_tp = fam_fp = fam_fn = 0
    knob_abs = 0.0
    knob_n = 0.0
    pred_slots, true_slots, pred_knobs, true_knobs, masks = [], [], [], [], []

    for audio, fam_t, slot_t, knob_t, knob_m in loader:
        batch_t0 = time.time()
        audio = audio.to(device, non_blocking=True)
        tgt = (fam_t.to(device), slot_t.to(device), knob_t.to(device), knob_m.to(device))
        b = audio.shape[0]

        if head == "v2":
            # The front-end runs ONCE and both the teacher-forced loss and the
            # free-running decode hang off that one embedding. Calling
            # `model(audio, ...)` and then `model.embed(audio)` would run a
            # 27.5 M tower twice per validation batch for identical numbers.
            z = model.embed(audio)
            mem = model.decoder.build_memory(z)
            tok_t = v2.chain_targets(tgt[1])
            tok_logit, knob_logit = model.decoder(v2.decoder_inputs(tok_t), mem)
            fam_logit = model.head_family(z)
            # The tail term is included here too: `best.pt` is chosen on this
            # number, so leaving it out would select the checkpoint on a
            # different objective from the one being trained.
            _, parts = v2.multitask_loss_v2(
                (fam_logit, tok_logit, knob_logit), (tgt[0], tok_t, tgt[2], tgt[3]),
                knob_weight=knob_weight, param_mask=model.param_mask,
                tail_weight=tail_weight, tail_tol=tail_tol)
            # The reported chain is the one INFERENCE produces, never the
            # teacher-forced one: a teacher-forced accuracy is a different
            # (and much kinder) number, and reporting it is how a decoder
            # looks fixed while its free-running chains are still wrong.
            r = v2.greedy_decode(model, z)
            slot_hat = v2.tokens_to_slots(r.tokens, vocab.slots)
            knob_p = r.knobs
        else:
            pred = model(audio)
            _, parts = multitask_loss(pred, tgt)
            fam_logit = pred[0]
            slot_hat = pred[1].argmax(dim=-1)
            knob_p = torch.sigmoid(pred[2])

        for k in agg:
            agg[k] += parts[k] * b
        n += b

        fam_hat = (torch.sigmoid(fam_logit) > 0.5).float()
        fam_tp += int(((fam_hat == 1) & (tgt[0] == 1)).sum())
        fam_fp += int(((fam_hat == 1) & (tgt[0] == 0)).sum())
        fam_fn += int(((fam_hat == 0) & (tgt[0] == 1)).sum())

        knob_abs += float(((knob_p - tgt[2]).abs() * tgt[3]).sum())
        knob_n += float(tgt[3].sum())

        pred_slots.append(slot_hat.cpu().numpy())
        true_slots.append(tgt[1].cpu().numpy())
        pred_knobs.append(knob_p.float().cpu().numpy())
        true_knobs.append(tgt[2].cpu().numpy())
        masks.append(tgt[3].cpu().numpy())

        if pace is not None:
            pace(batch_t0)

    out = {k: v / max(n, 1) for k, v in agg.items()}
    prec = fam_tp / max(fam_tp + fam_fp, 1)
    rec = fam_tp / max(fam_tp + fam_fn, 1)
    out["family_f1"] = 2 * prec * rec / max(prec + rec, 1e-9)
    out["family_precision"] = prec
    out["family_recall"] = rec
    out["knob_mae"] = knob_abs / max(knob_n, 1.0)

    ps, ts = np.concatenate(pred_slots), np.concatenate(true_slots)
    pk, tk, mk = np.concatenate(pred_knobs), np.concatenate(true_knobs), np.concatenate(masks)
    chain = M.chain_metrics(ps, ts)
    out["slot_acc"] = chain["slot_accuracy"]
    out["slot_acc_occupied"] = chain["slot_accuracy_occupied"]
    out["exact_chain"] = chain["exact_chain_match"]
    out["edit_distance"] = chain["chain_edit_distance"]
    out.update(M.readiness_report(ps, ts, pk, tk, mk))
    return out


def main() -> None:
    ap = argparse.ArgumentParser()
    # Not `required`: --self-test-schedule is pure arithmetic and must run
    # without a dataset, on any box, in the pipeline's stage 0.
    ap.add_argument("--data", default=None, help="dir holding manifest.jsonl + clips")
    ap.add_argument("--out", default="runs/latest")
    ap.add_argument("--frontend", default="cnn", choices=["cnn", "clap", "ast"])
    # v1 stays the DEFAULT and stays the control. The v2 decoder is the change
    # being measured, and a change that also moves the baseline measures nothing.
    ap.add_argument("--head", default="v1", choices=["v1", "v2"],
                    help="v1 = per-slot softmaxes (the control); v2 = the chain decoder")
    ap.add_argument(
        "--knob-head", default="step", choices=["step", "global"],
        help="v2 knob regression: per-decoder-STEP (default, and the original) or a GLOBAL "
             "head over the pooled trunk, which is v1's arrangement transplanted onto the v2 "
             "chain decoder. Round 2b measured the knob ceiling as belonging to the HEAD and "
             "not to the audio: v1's global head reached knob_mae 0.1342 where v2's per-step "
             "head stalled at 0.2163, on identical data, tower and corpus. The ONNX contract "
             "is unchanged either way -- knob_values is still [B, knob_dim], filled from "
             "greedy_decode, and no reader can tell which head produced it.")
    ap.add_argument("--decoder-layers", type=int, default=3, help="v2 decoder depth")
    ap.add_argument("--decoder-dim", type=int, default=256, help="v2 decoder d_model")
    ap.add_argument("--freeze", action="store_true", default=True)
    ap.add_argument("--finetune", dest="freeze", action="store_false",
                    help="unfreeze the pretrained tower (needs a much lower lr)")
    ap.add_argument("--unfreeze-backbone", dest="freeze", action="store_false",
                    help="full fine-tune: the pretrained tower trains too, in its own "
                         "param group at --backbone-lr-scale x the head LR")
    ap.add_argument("--backbone-lr-scale", type=float, default=0.1,
                    help="the unfrozen backbone's LR as a fraction of --lr (default 1/10)")
    ap.add_argument("--grad-checkpointing", action="store_true",
                    help="trade compute for memory in the tower; needed for batch 24 "
                         "on 10 GB with an unfrozen CLAP")
    ap.add_argument(
        "--knob-tail-weight", type=float, default=0.0,
        help="extra weight on a HINGE over |knob error| - --knob-tail-tol, added to the v2 "
             "knob loss (0 = off, and off is bit-identical to before). Readiness is "
             "all-or-nothing across every knob of a rig, so it is decided by the WORST "
             "knob, not the mean: measured on round 2 vs 2b, a 38 %% lower mean knob error "
             "bought NO improvement in the chance a clip passes given an exact chain. This "
             "term pays only on knobs already outside the tolerance, which is where "
             "readiness is actually lost. v2 head only.")
    ap.add_argument(
        "--knob-tail-tol", type=float, default=v2.TAIL_TOL,
        help="the tolerance the tail hinge measures from; defaults to the readiness "
             "tolerance so the loss chases the metric being reported.")
    ap.add_argument("--knob-weights", default=None,
                    help="knob_sensitivity.json; 'auto' takes the one beside the vocab if "
                         "it exists. Weights the knob loss by measured AUDIBILITY")
    ap.add_argument("--dry-run-batches", type=int, default=0,
                    help="run N train batches, print the param groups and their LRs, exit")
    ap.add_argument("--epochs", type=int, default=30)
    ap.add_argument("--batch", type=int, default=32)
    ap.add_argument("--lr", type=float, default=3e-4)
    ap.add_argument("--weight-decay", type=float, default=1e-2)
    ap.add_argument("--workers", type=int, default=4)
    ap.add_argument("--limit", type=int, default=None, help="cap manifest rows (smoke runs)")
    ap.add_argument("--device", default="auto")
    ap.add_argument("--require-gpu", default="3080",
                    help="substring the chosen GPU's name must contain; '' disables the check")
    ap.add_argument("--cache", default="auto", choices=["auto", "none"])
    ap.add_argument("--cache-dir", default=None,
                    help="where the fp16 PCM + label cache lives; defaults to <out>/cache. "
                         "POINT TWO RUNS OVER THE SAME DATA AT ONE DIR -- the full set's "
                         "audio cache is 19.4 GB and takes ~25 min to render")
    ap.add_argument("--minutes", type=float, default=None, help="stop after this long")
    ap.add_argument(
        "--throttle", type=float,
        default=float(os.environ.get("DOTCHARTER_TRAIN_THROTTLE", "1.0") or 1.0),
        help="duty cycle in (0, 1]. 1.0 = full speed (default). 0.5 sleeps after each "
             "optimizer step for as long as the step itself took, halving GPU "
             "utilisation -- and therefore fan noise and power draw -- at the cost of "
             "wall time. Also settable as DOTCHARTER_TRAIN_THROTTLE. Nothing about the "
             "MODEL changes: the same steps run in the same order with the same LR, so "
             "a throttled run and a full-speed one differ only in how long they take.")
    ap.add_argument(
        "--max-watts", type=float,
        default=float(os.environ.get("DOTCHARTER_TRAIN_MAX_WATTS", "0") or 0),
        help="hold the training GPU at or under this power draw by adjusting the duty "
             "cycle in a closed loop (0 = off). A fixed --throttle is open-loop and the "
             "watts it produces depend on model, batch and card; this measures the card. "
             "Also settable as DOTCHARTER_TRAIN_MAX_WATTS.")
    ap.add_argument(
        "--resume", nargs="?", const="auto", default=None,
        help="resume from a checkpoint ('auto' = <out>/last.pt). Restores the model, the "
             "OPTIMIZER and GradScaler state, the global step, the best score and the "
             "planned horizon, and continues at the next epoch. Restoring the model alone "
             "is not a resume: AdamW's moments and the LR position are most of the "
             "training state, and dropping them restarts the warm-up on a converged model.")
    ap.add_argument(
        "--watts-file", default=None,
        help="file holding just a number of watts, re-read every 10 s, so the target can "
             "be lowered on a RUNNING job without losing the epoch to a restart.")
    ap.add_argument("--vocab", default=str(DEFAULT_VOCAB_PATH))
    ap.add_argument("--seed", type=int, default=1234)
    ap.add_argument("--self-test-schedule", action="store_true",
                    help="check the time-aware LR planning arithmetic and exit (no GPU)")
    args = ap.parse_args()

    if args.self_test_schedule:
        self_test_schedule()
        return
    if not args.data:
        raise SystemExit("--data is required (only --self-test-schedule runs without it)")

    torch.manual_seed(args.seed)
    np.random.seed(args.seed)

    vocab = load_vocab(args.vocab)
    data_dir = Path(args.data)
    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)

    # Warn loudly if this is the crude smoke set: every number below then
    # describes a toy renderer, not the JUCE rig.
    first = next(iter((data_dir / "manifest.jsonl").open(encoding="utf-8")), "")
    crude = "synthetic-crude" in first
    if crude:
        print("=" * 72)
        print("[train] THIS IS THE CRUDE SYNTHETIC SMOKE SET (synthetic-crude).")
        print("[train] Metrics from it prove the PIPELINE runs. They are not a")
        print("[train] quality signal and must not be reported as one.")
        print("=" * 72, flush=True)

    # The index dir is ALWAYS on disk, `--cache none` included: it holds the
    # label arrays and the clip path list as well as the fp16 PCM, and all
    # three have to be memmapped rather than carried on the Dataset. On Windows
    # every DataLoader worker is a fresh process handed the Dataset by PICKLE,
    # and an instance holding the cache killed the first full run outright --
    # see data.py's rule 4.
    cache_dir = Path(args.cache_dir) if args.cache_dir else (out_dir / "cache")
    splits = build_splits(data_dir, vocab, limit=args.limit,
                          cache_dir=cache_dir,
                          audio_cache=args.cache != "none")
    for name, ds in splits.items():
        if len(ds):
            size = len(pickle.dumps(ds, protocol=pickle.HIGHEST_PROTOCOL))
            if size > PICKLE_BUDGET_BYTES:
                raise SystemExit(
                    f"[train] the {name} dataset pickles to {size:,} bytes, over the "
                    f"{PICKLE_BUDGET_BYTES:,} budget. Every worker pays that through a pipe; "
                    "past ~2 GB the write fails with OSError [Errno 22] after the cache build. "
                    "Something bulk is on the Dataset again -- see data.py rule 4."
                )
            print(f"[train] {name} dataset pickles to {size:,} bytes", flush=True)

    device = pick_device(args.device, args.require_gpu or None)
    model = build_model(vocab, args).to(device)
    total = count_params(model)
    trainable = sum(p.numel() for p in model.parameters() if p.requires_grad)
    print(f"[train] head={args.head} frontend={args.frontend} params={total/1e6:.2f} M "
          f"(trainable {trainable/1e6:.2f} M, frozen={args.freeze})", flush=True)
    if args.head == "v2":
        print(f"[train] chain decoder: {args.decoder_layers} layers x d={args.decoder_dim}, "
              f"{count_params(model.decoder)/1e6:.2f} M params, "
              f"{model.n_tokens} tokens, max chain {vocab.slots}", flush=True)

    checkpointing = None
    if args.grad_checkpointing:
        checkpointing = enable_grad_checkpointing(model)
        print("[train] gradient checkpointing: "
              + (f"ENABLED ({checkpointing} route)" if checkpointing
                 else "this front-end has no pretrained tower to checkpoint"), flush=True)
        if checkpointing and args.freeze:
            print("[train] NOTE: the tower is FROZEN, so checkpointing it saves nothing "
                  "and costs a second forward. Pass --unfreeze-backbone or drop "
                  "--grad-checkpointing.", file=sys.stderr, flush=True)

    # The audibility weights. Loaded ONCE, recorded in run.json: a weighted and
    # an unweighted knob loss are not the same number, and a run whose weighting
    # cannot be established from its own artefacts cannot be compared to
    # anything.
    weights_path = args.knob_weights
    if weights_path == "auto":
        weights_path = (str(sensitivity.DEFAULT_SENSITIVITY_PATH)
                        if sensitivity.DEFAULT_SENSITIVITY_PATH.exists() else None)
    knob_w_np, knob_w_info = sensitivity.knob_weights(vocab, weights_path)
    knob_weight = torch.from_numpy(knob_w_np).to(device) if weights_path else None
    print(f"[train] knob weighting: {knob_w_info}", flush=True)
    if knob_weight is not None and args.head == "v1":
        print("[train] NOTE: --knob-weights is ignored by the v1 head, which is the "
              "unweighted CONTROL by design.", file=sys.stderr, flush=True)
        knob_weight = None

    loaders = {
        s: DataLoader(
            splits[s], batch_size=args.batch, shuffle=(s == "train"),
            num_workers=args.workers, pin_memory=(device.type == "cuda"),
            drop_last=False, persistent_workers=args.workers > 0 and len(splits[s]) > 0,
        )
        for s in splits if len(splits[s]) > 0
    }

    groups = build_param_groups(model, args)
    params = [p for g in groups for p in g["params"]]
    opt = torch.optim.AdamW(groups, lr=args.lr, weight_decay=args.weight_decay)
    for g in opt.param_groups:
        n_p = sum(p.numel() for p in g["params"])
        print(f"[train] param group '{g['name']}': {n_p/1e6:.2f} M params, "
              f"lr = {g['lr_scale']:g} x --lr", flush=True)
    if args.freeze is False and len(opt.param_groups) == 1:
        print("[train] NOTE: --unfreeze-backbone had nothing to unfreeze -- this "
              "front-end has no PRETRAINED tower (the CNN control trains from scratch "
              "anyway).", file=sys.stderr, flush=True)
    # The LR horizon is TIME-AWARE: epoch 1 is the warm-up, and the anneal is
    # planned from the epoch time measured there. See plan_epochs().
    steps_per_epoch = max(1, len(loaders["train"]))
    warmup_steps = steps_per_epoch
    planned_epochs = args.epochs
    total_steps = steps_per_epoch * planned_epochs
    global_step = 0
    scaler = torch.amp.GradScaler("cuda", enabled=device.type == "cuda")

    # ---- resume -------------------------------------------------------------
    start_epoch = 1
    best_resumed = None
    if args.resume:
        ckpt_path = (out_dir / "last.pt") if args.resume == "auto" else Path(args.resume)
        if not ckpt_path.exists():
            raise SystemExit(f"--resume: no checkpoint at {ckpt_path}")
        state = torch.load(ckpt_path, map_location=device, weights_only=False)

        # A resume across architectures is a silently wrong model, not an error,
        # so it is refused rather than coerced.
        for key, mine in (("head", args.head), ("frontend", args.frontend)):
            theirs = state.get(key)
            if theirs is not None and theirs != mine:
                raise SystemExit(
                    f"--resume: {ckpt_path} was trained with {key}={theirs!r}, this run asks "
                    f"for {mine!r}. Refusing -- loading one head's weights into the other is a "
                    "shape error at best and a silently wrong model at worst.")

        model.load_state_dict(state["model"])
        if "optimizer" in state:
            opt.load_state_dict(state["optimizer"])
        if "scaler" in state:
            scaler.load_state_dict(state["scaler"])
        global_step = int(state.get("global_step", 0))
        start_epoch = int(state.get("epoch", 0)) + 1
        if state.get("best") is not None:
            best_resumed = float(state["best"])

        # The horizon is deliberately NOT restored. "Later epochs may only SHRINK
        # the horizon" is a rule about ONE run -- it stops the cosine's end being
        # pushed away for ever so the anneal never lands. A resume is a new
        # budget with a new --epochs ceiling and a fresh --minutes, and carrying
        # the old horizon across means a resume can never EXTEND a run, which is
        # most of the reason to resume one. So it re-plans from the first epoch
        # after the resume, exactly as a fresh run re-plans from its epoch 1.
        planned_epochs = args.epochs
        total_steps = steps_per_epoch * planned_epochs

        stale = [k for k in ("optimizer", "scaler", "global_step") if k not in state]
        print(f"[train] RESUMED from {ckpt_path} at epoch {state.get('epoch')} -> "
              f"continuing at epoch {start_epoch}, global_step {global_step}, "
              f"horizon re-planned against --epochs {planned_epochs}", flush=True)
        if stale:
            print(f"[train] WARNING: this checkpoint predates resume support and carries no "
                  f"{', '.join(stale)}. The MODEL is restored but the optimizer moments and "
                  "the LR position are not, so the schedule restarts its warm-up on an "
                  "already-trained model. Expect a loss bump.", flush=True)

    log_path = out_dir / "log.csv"
    fields = ["epoch", "split", "loss", "fam", "slot", "knob", "conf",
              "family_f1", "family_precision", "family_recall",
              "slot_acc", "slot_acc_occupied", "exact_chain", "edit_distance",
              "ready_rate", "ready_rate_loose", "knob_mae", "seconds"]
    # Append on resume: "w" would truncate the history of the run being resumed,
    # which is the one artefact that shows the whole curve.
    resuming_log = bool(args.resume) and log_path.exists()
    log_file = log_path.open("a" if resuming_log else "w", newline="", encoding="utf-8")
    writer = csv.DictWriter(log_file, fieldnames=fields)
    if not resuming_log:
        writer.writeheader()

    (out_dir / "run.json").write_text(json.dumps({
        "args": vars(args), "params_total": total, "params_trainable": trainable,
        "crude_smoke_set": crude,
        "head": args.head,
        "knob_weighting": knob_w_info,
        "grad_checkpointing": checkpointing,
        "cache_dir": str(cache_dir.resolve()),
        "clips": {s: len(splits[s]) for s in splits},
    }, indent=2, default=str))

    best = float("inf") if best_resumed is None else best_resumed
    started = time.time()
    throttle = float(args.throttle)
    if not (0.0 < throttle <= 1.0):
        raise SystemExit(f"--throttle must be in (0, 1], got {throttle}")

    governor = None
    if args.max_watts > 0 and device.type == "cuda":
        # Match the SAME name the device was selected by, so the governor cannot
        # end up metering the other card.
        gov_name = args.require_gpu or torch.cuda.get_device_name(device)
        governor = PowerGovernor(
            gov_name, args.max_watts,
            Path(args.watts_file) if args.watts_file else None).start()
        if governor.index is None:
            governor = None
        else:
            print(f"[train] POWER GOVERNOR: holding '{args.require_gpu}' at or under "
                  f"{args.max_watts:g} W by duty cycle, clamped to "
                  f"[{PowerGovernor.MIN_DUTY}, {PowerGovernor.MAX_DUTY}]"
                  + (f", target re-read from {args.watts_file} every 10 s"
                     if args.watts_file else ""), flush=True)
    def current_duty() -> float:
        """The duty in force right now: the governor's, floored by any fixed --throttle."""
        return min(throttle, governor.duty) if governor else throttle

    # Validation is paced too. It is a full-speed pass over thousands of clips
    # through the same tower, so leaving it un-paced spikes the card once an
    # epoch AND feeds the governor a reading that drives the next epoch's duty
    # to the floor.
    pacer = (lambda t0: duty_sleep(device, t0, current_duty())) \
        if (governor or throttle < 1.0) else None

    if throttle < 1.0:
        print(f"[train] THROTTLE {throttle:g}: sleeping {1.0 / throttle - 1.0:.2f}x each "
              "step's own wall time after it. Utilisation, power and fan noise scale "
              "with this; the model and the schedule do not change, only wall time.",
              flush=True)

    stop = False
    for group in opt.param_groups:  # step 1 belongs at the warm-up FLOOR, not the peak
        group["lr"] = lr_at(0, warmup_steps, total_steps, args.lr) * group["lr_scale"]
    print(f"[train] schedule: warm-up over epoch 1 ({steps_per_epoch} steps), then cosine. "
          f"Horizon {planned_epochs} epochs until epoch 1 has been timed"
          + (f"; cap {args.minutes} min" if args.minutes else "; no time cap"), flush=True)

    epoch = start_epoch - 1          # so the run.json tail is right if the loop never runs
    for epoch in range(start_epoch, args.epochs + 1):
        model.train()
        t0 = time.time()
        seen = 0
        agg = {"loss": 0.0, "fam": 0.0, "slot": 0.0, "knob": 0.0, "conf": 0.0}

        for audio, fam_t, slot_t, knob_t, knob_m in loaders["train"]:
            step_t0 = time.time()
            audio = audio.to(device, non_blocking=True)
            tgt = (fam_t.to(device), slot_t.to(device), knob_t.to(device), knob_m.to(device))
            opt.zero_grad(set_to_none=True)
            with torch.amp.autocast("cuda", enabled=device.type == "cuda"):
                if args.head == "v2":
                    # Teacher forcing: the decoder sees the TRUE prefix, so a
                    # wrong slot 2 does not also destroy slots 3..7's gradient.
                    tok_t = v2.chain_targets(tgt[1])
                    pred = model(audio, v2.decoder_inputs(tok_t))
                    loss, parts = v2.multitask_loss_v2(
                        pred, (tgt[0], tok_t, tgt[2], tgt[3]),
                        knob_weight=knob_weight, param_mask=model.param_mask,
                        tail_weight=args.knob_tail_weight,
                        tail_tol=args.knob_tail_tol)
                else:
                    loss, parts = multitask_loss(model(audio), tgt)
            scaler.scale(loss).backward()
            scaler.unscale_(opt)
            torch.nn.utils.clip_grad_norm_(params, 5.0)
            scaler.step(opt)
            scaler.update()

            # Duty-cycle throttle. The GPU queue is asynchronous, so the step's
            # wall time is only honest AFTER a sync -- without one this measures
            # how fast python got back round the loop and the sleep is computed
            # from roughly nothing. The sync costs a stall we are deliberately
            # paying for anyway.
            duty_sleep(device, step_t0, current_duty())

            global_step += 1
            for group in opt.param_groups:
                group["lr"] = lr_at(global_step, warmup_steps, total_steps,
                                    args.lr) * group["lr_scale"]
            b = audio.shape[0]
            for k in agg:
                agg[k] += parts[k] * b
            seen += b

            if args.dry_run_batches and global_step >= args.dry_run_batches:
                print(f"[dry-run] {global_step} batch(es) done. Param groups:", flush=True)
                for group in opt.param_groups:
                    n_p = sum(p.numel() for p in group["params"])
                    print(f"[dry-run]   {group['name']:9} {n_p/1e6:7.2f} M  "
                          f"lr={group['lr']:.3e}  (scale {group['lr_scale']:g})")
                print(f"[dry-run] loss parts {parts}", flush=True)
                log_file.close()
                return

            if args.minutes and (time.time() - started) / 60.0 > args.minutes:
                print(f"[train] time cap {args.minutes} min reached", flush=True)
                stop = True
                break

        tr = {k: v / max(seen, 1) for k, v in agg.items()}
        tr.update(epoch=epoch, split="train", seconds=round(time.time() - t0, 1))
        writer.writerow({k: tr.get(k, "") for k in fields})

        line = f"[{epoch:3d}] train loss {tr['loss']:.4f}"
        if "val" in loaders:
            va = evaluate(model, loaders["val"], device, vocab, head=args.head,
                          knob_weight=knob_weight, pace=pacer,
                          tail_weight=args.knob_tail_weight,
                          tail_tol=args.knob_tail_tol)
            va.update(epoch=epoch, split="val", seconds=round(time.time() - t0, 1))
            writer.writerow({k: va.get(k, "") for k in fields})
            line += (f" | val loss {va['loss']:.4f}  famF1 {va['family_f1']:.3f}"
                     f"  slotAcc {va['slot_acc']:.3f}/{va['slot_acc_occupied']:.3f}"
                     f"  exact {va['exact_chain']:.3f}  ready {va['ready_rate']:.3f}"
                     f"  knobMAE {va['knob_mae']:.4f}")
            score = va["loss"]
        else:
            score = tr["loss"]
        log_file.flush()
        if governor:
            line += (f"  [gov {governor.last_power:.0f}W/{governor.target:.0f}W "
                     f"duty {governor.duty:.2f}]")
        print(line + f"  ({tr['seconds']}s)", flush=True)

        # Re-plan the anneal from MEASURED epoch time (train + val + checkpoint
        # write, i.e. wall clock, because that is what the cap measures). The
        # horizon only ever SHRINKS after the first plan: a growing one would
        # keep pushing the cosine's end away and the anneal would never land.
        elapsed = time.time() - started
        # This EPOCH's own wall time, not elapsed/epoch: epoch 1 also carries
        # process start, the cache check and the model build, and charging
        # those to every future epoch under-plans the horizon by ~15 %. The
        # remaining BUDGET is still measured from `started`, so that startup
        # is paid for exactly once.
        seconds_per_epoch = time.time() - t0
        fits = plan_epochs(args.epochs, epoch, elapsed, seconds_per_epoch, args.minutes)
        # The FIRST epoch of this process sets the horizon; later ones may only
        # shrink it. On a resume that anchor is the resumed-to epoch, not 1 --
        # otherwise the first post-resume epoch is treated as "running long" and
        # min()s the horizon down against a plan made for a different budget.
        new_plan = fits if epoch == start_epoch else min(planned_epochs, fits)
        if new_plan != planned_epochs:
            why = (f"measured epoch {epoch}" if epoch == start_epoch
                   else "epochs running long")
            print(f"[train] re-planned horizon {planned_epochs} -> {new_plan} epochs "
                  f"({why}: {seconds_per_epoch:.0f}s/epoch, "
                  f"{(args.minutes or 0) * 60 - elapsed:.0f}s left). The anneal now lands "
                  f"ON the last epoch that fits instead of being cut at the LR peak.",
                  flush=True)
            planned_epochs = new_plan
            total_steps = steps_per_epoch * planned_epochs
        if epoch >= planned_epochs:
            print(f"[train] planned horizon of {planned_epochs} epoch(s) reached", flush=True)
            stop = True

        if score < best:
            best = score
            torch.save({
                "model": model.state_dict(), "frontend": args.frontend,
                # `head` is written into every checkpoint, so eval and export
                # never have to guess which architecture a .pt describes -- a
                # v2 state dict loaded into a v1 module is a shape error at
                # best and a silently wrong model at worst.
                "head": args.head,
                "epoch": epoch, "score": score, "args": vars(args),
            }, out_dir / "best.pt")
        # last.pt carries the FULL training state, not just the weights: AdamW's
        # moments and the LR position are most of what a run has learned about
        # how to proceed, and a "resume" that drops them restarts the warm-up on
        # an already-converged model. best.pt deliberately stays weights-only --
        # it is the artefact eval and export consume, and doubling its size with
        # optimizer moments would slow every load of it for nothing.
        torch.save({"model": model.state_dict(), "frontend": args.frontend,
                    "head": args.head,
                    "epoch": epoch, "args": vars(args),
                    "optimizer": opt.state_dict(),
                    "scaler": scaler.state_dict(),
                    "global_step": global_step,
                    "best": best,
                    "planned_epochs": planned_epochs}, out_dir / "last.pt")
        if stop:
            break

    log_file.close()
    run_json = out_dir / "run.json"
    blob = json.loads(run_json.read_text(encoding="utf-8"))
    blob["schedule"] = {
        "requested_epochs": args.epochs,
        "planned_epochs": planned_epochs,
        "epochs_run": epoch,
        "steps_per_epoch": steps_per_epoch,
        "minutes_cap": args.minutes,
        "seconds_per_epoch": round((time.time() - started) / max(epoch, 1), 1),
    }
    run_json.write_text(json.dumps(blob, indent=2, default=str))
    print(f"[train] done in {(time.time()-started)/60:.1f} min; best={best:.4f}; "
          f"ran {epoch}/{planned_epochs} planned (of {args.epochs} requested); "
          f"checkpoints in {out_dir}", flush=True)


if __name__ == "__main__":
    main()
