"""Per-head metrics on the test split, plus a RECOVERY table.

    python eval.py --checkpoint runs/clap/best.pt --data <dir>

Per-head metrics say how the model does on its own loss terms. The recovery
table says what the matcher actually gets: for each family, how often a rig
containing it was spotted (recall), and for the modules that were spotted, the
knob MAE against the 0.05 "human expert" yardstick the study takes from
Comunita 2021. A model can post a fine average MAE while being useless on
drives, and only the per-family table shows it.
"""

from __future__ import annotations

import argparse
import json
from collections import defaultdict
from pathlib import Path

import numpy as np
import torch
from torch.utils.data import DataLoader

import metrics as M
import model_v2 as v2
import predictions
import sensitivity
from data import build_splits
from model import RigEstimator
from vocab import DEFAULT_VOCAB_PATH, load_vocab

YARDSTICK = 0.05  # Comunita et al. 2021: expert-indistinguishable knob MAE.


@torch.no_grad()
def run(model, loader, device, vocab, head: str = "v1", beam: int = 0,
        audible: "np.ndarray | None" = None,
        predictions_out=None, data_root=None, cache_dir=None, split="test") -> dict:
    """Per-head metrics, the recovery table, and the READINESS rate.

    `head` selects only how the CHAIN is read -- v1's per-slot argmax, or v2's
    greedy (or beam) decode mapped back onto the same `[N, slots]` array.
    Everything after that point is one code path for both heads on purpose:
    the whole reason v1 stays runnable is to be compared, and two comparisons
    computed by two pieces of code compare nothing.
    """
    model.eval()
    fam_hat, fam_ref = [], []
    slot_hat, slot_ref = [], []
    knob_abs_by_module: dict[str, list[float]] = defaultdict(list)
    knob_all: list[float] = []
    conf_all, conf_correct = [], []
    knob_pred_all, knob_ref_all, knob_mask_all = [], [], []
    exact_chain = 0
    total_clips = 0

    for audio, fam_t, slot_t, knob_t, knob_m in loader:
        audio = audio.to(device)
        if head == "v2":
            z = model.embed(audio)
            r = v2.beam_decode(model, z, k=beam) if beam > 1 else v2.greedy_decode(model, z)
            fam_l = model.head_family(z)
            knob_l = r.knobs                       # already in [0,1]
            conf_l = r.confidence
            sh = v2.tokens_to_slots(r.tokens, vocab.slots).cpu().numpy()
        else:
            fam_l, slot_l, knob_l, conf_l = model(audio)
            knob_l = torch.sigmoid(knob_l)
            conf_l = torch.sigmoid(conf_l)
            sh = slot_l.argmax(dim=-1).cpu().numpy()

        fam_hat.append((torch.sigmoid(fam_l) > 0.5).cpu().numpy())
        fam_ref.append(fam_t.numpy())

        sr_ = slot_t.numpy()
        slot_hat.append(sh)
        slot_ref.append(sr_)
        exact_chain += int((sh == sr_).all(axis=1).sum())
        total_clips += sh.shape[0]

        knob_p = knob_l.float().cpu().numpy()
        knob_r = knob_t.numpy()
        knob_mask = knob_m.numpy()
        knob_pred_all.append(knob_p)
        knob_ref_all.append(knob_r)
        knob_mask_all.append(knob_mask)
        err = np.abs(knob_p - knob_r)
        knob_all.extend(err[knob_mask > 0].tolist())
        for entry in vocab.modules:
            lo, hi = entry.param_offset, entry.param_offset + len(entry.params)
            sub_mask = knob_mask[:, lo:hi]
            if sub_mask.sum() == 0:
                continue
            knob_abs_by_module[entry.id].extend(err[:, lo:hi][sub_mask > 0].tolist())

        conf_all.extend(conf_l.float().cpu().numpy().reshape(-1).tolist())
        conf_correct.extend((sh == sr_).mean(axis=1).tolist())

    fam_hat = np.concatenate(fam_hat)
    fam_ref = np.concatenate(fam_ref)
    slot_hat = np.concatenate(slot_hat)
    slot_ref = np.concatenate(slot_ref)

    per_family = {}
    for i, name in enumerate(vocab.families):
        tp = int(((fam_hat[:, i] == 1) & (fam_ref[:, i] == 1)).sum())
        fp = int(((fam_hat[:, i] == 1) & (fam_ref[:, i] == 0)).sum())
        fn = int(((fam_hat[:, i] == 0) & (fam_ref[:, i] == 1)).sum())
        support = int(fam_ref[:, i].sum())
        prec = tp / max(tp + fp, 1)
        rec = tp / max(tp + fn, 1)
        per_family[name] = {
            "support": support,
            "precision": round(prec, 4),
            "recall": round(rec, 4),
            "f1": round(2 * prec * rec / max(prec + rec, 1e-9), 4),
        }

    micro_tp = int(((fam_hat == 1) & (fam_ref == 1)).sum())
    micro_fp = int(((fam_hat == 1) & (fam_ref == 0)).sum())
    micro_fn = int(((fam_hat == 0) & (fam_ref == 1)).sum())
    p = micro_tp / max(micro_tp + micro_fp, 1)
    r = micro_tp / max(micro_tp + micro_fn, 1)

    occupied = slot_ref != 0
    recovery = {}
    for module_id, errs in sorted(knob_abs_by_module.items()):
        recovery[module_id] = {
            "params": len(vocab.module(module_id).params),
            "observations": len(errs),
            "knob_mae": round(float(np.mean(errs)), 4),
            "within_yardstick": round(float(np.mean(np.asarray(errs) <= YARDSTICK)), 4),
        }

    knob_arr = np.asarray(knob_all) if knob_all else np.zeros(1)
    conf_arr = np.asarray(conf_all)
    corr_arr = np.asarray(conf_correct)
    conf_corr = (
        float(np.corrcoef(conf_arr, corr_arr)[0, 1])
        if conf_arr.size > 1 and conf_arr.std() > 1e-6 and corr_arr.std() > 1e-6
        else float("nan")
    )

    chain = M.chain_metrics(slot_hat, slot_ref)
    knob_pred_cat = np.concatenate(knob_pred_all)
    ready = M.readiness_report(
        slot_hat, slot_ref, knob_pred_cat,
        np.concatenate(knob_ref_all), np.concatenate(knob_mask_all),
        audible=audible)

    # The renderable prediction dump. Written from the SAME arrays the metrics
    # above are computed from, so the rig that gets rendered is provably the rig
    # that was scored -- re-deriving it from a second decode would let the two
    # drift.
    if predictions_out is not None:
        n = predictions.write_predictions(
            predictions_out, data_root, cache_dir, split, vocab,
            slot_hat, knob_pred_cat, np.asarray(conf_all))
        print(f"[eval] wrote {n} prediction rows -> {predictions_out}", flush=True)

    return {
        "clips": int(total_clips),
        "head": head,
        "decode": f"beam{beam}" if (head == "v2" and beam > 1) else
                  ("greedy" if head == "v2" else "argmax"),
        "family_micro_precision": round(p, 4),
        "family_micro_recall": round(r, 4),
        "family_micro_f1": round(2 * p * r / max(p + r, 1e-9), 4),
        "per_family": per_family,
        "slot_accuracy": round(chain["slot_accuracy"], 4),
        "slot_accuracy_occupied": round(chain["slot_accuracy_occupied"], 4),
        "exact_chain_match": round(chain["exact_chain_match"], 4),
        "chain_edit_distance": round(chain["chain_edit_distance"], 4),
        "chain_edit_distance_normalised": round(chain["chain_edit_distance_normalised"], 4),
        # The owner's bar, in numbers: "skip the refinement and be ready to go".
        # A clip counts only when the chain is EXACTLY right AND every knob is
        # within tolerance -- an average MAE hides the one knob that is 0.4 out.
        **ready,
        "knob_mae": round(float(knob_arr.mean()), 4),
        "knob_within_yardstick": round(float((knob_arr <= YARDSTICK).mean()), 4),
        "yardstick": YARDSTICK,
        "confidence_correlation": None if conf_corr != conf_corr else round(conf_corr, 4),
        "recovery": recovery,
    }


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--checkpoint", required=True)
    ap.add_argument("--data", required=True)
    ap.add_argument("--split", default="test", choices=["train", "val", "test"])
    ap.add_argument("--batch", type=int, default=32)
    ap.add_argument("--workers", type=int, default=0)
    ap.add_argument("--device", default="auto")
    # The same guard as train.py, for the same reason: torch's cuda:0 is the
    # RTX 5070 Ti the owner games on. An eval is short, but it is still a GPU.
    ap.add_argument("--require-gpu", default="3080",
                    help="substring the chosen GPU's name must contain; '' disables the check")
    ap.add_argument("--cache-dir", default=None,
                    help="fp16 PCM + label cache; defaults to the run's own run.json entry, "
                         "else <checkpoint dir>/cache")
    ap.add_argument("--vocab", default=str(DEFAULT_VOCAB_PATH))
    ap.add_argument("--json-out", default=None)
    ap.add_argument("--knob-head", default=None, choices=["step", "global"],
                    help="override the knob head recorded in the checkpoint. Normally the "
                         "checkpoint is trusted, but a checkpoint's recorded args can be "
                         "WRONG: round 8 recorded knob_head='global' from an argparse flag "
                         "that never reached the model, so its weights are a step head and "
                         "loading them into a global one fails on missing head_knob_global.* "
                         "keys. Without this, such a run is unevaluable and four hours of GPU "
                         "time are simply lost.")
    ap.add_argument("--predictions-out", default=None,
                    help="where to write the per-clip truth/prediction JSONL that the "
                         "sound-distance eval renders. Defaults to "
                         "<run-dir>/predictions-<split>.jsonl.")
    ap.add_argument("--no-predictions", action="store_true",
                    help="skip the prediction dump (it needs the manifest and the "
                         "split paths file).")
    ap.add_argument("--head", default=None, choices=["v1", "v2"],
                    help="override the checkpoint's own head; normally unnecessary")
    ap.add_argument("--knob-weights", default="auto",
                    help="sensitivity JSON defining which knobs are AUDIBLE, for the "
                         "reported-beside ready_rate_audible / knob_mae_audible numbers "
                         "('auto' = knob_sensitivity.json beside the vocab, '' = off). "
                         "Loaded for BOTH heads regardless of what either trained with: it "
                         "describes the measurement, not the run. It does NOT change "
                         "ready_rate, which is the export gate.")
    ap.add_argument("--beam", type=int, default=0,
                    help="v2 only: beam width (>1 enables beam search; 0/1 = greedy)")
    args = ap.parse_args()

    from train import pick_device

    vocab = load_vocab(args.vocab)
    ckpt = torch.load(args.checkpoint, map_location="cpu", weights_only=False)
    frontend = ckpt.get("frontend", "cnn")
    run_dir = Path(args.checkpoint).parent
    cache_dir = Path(args.cache_dir) if args.cache_dir else None
    if cache_dir is None:
        # A run that shared another run's cache records where it is; without
        # this an eval re-renders the whole 19.4 GB PCM cache into its own dir.
        run_json = run_dir / "run.json"
        if run_json.exists():
            recorded = json.loads(run_json.read_text(encoding="utf-8")).get("cache_dir")
            if recorded and Path(recorded).exists():
                cache_dir = Path(recorded)
    splits = build_splits(Path(args.data), vocab, cache_dir=cache_dir or (run_dir / "cache"))
    ds = splits[args.split]
    if len(ds) == 0:
        raise SystemExit(f"the {args.split} split is empty -- too few distinct DI sources")

    device = pick_device(args.device, args.require_gpu or None)
    # The head comes from the CHECKPOINT, not from a flag: a v2 state dict
    # loaded into a v1 module is a shape error at best, and the reverse reads
    # as a model that simply performs badly.
    head = args.head or ckpt.get("head") or ckpt.get("args", {}).get("head") or "v1"
    if head == "v2":
        ck_args = ckpt.get("args", {})
        model = v2.RigEstimatorV2(
            vocab, frontend=frontend, freeze=True,
            layers=int(ck_args.get("decoder_layers", 3)),
            d_model=int(ck_args.get("decoder_dim", 256)),
            knob_head=(args.knob_head or str(ck_args.get("knob_head", "step")))).to(device)
    else:
        model = RigEstimator(vocab, frontend=frontend, freeze=True).to(device)
    model.load_state_dict(ckpt["model"])

    # The AUDIBLE knob subset, for the reported-beside numbers. It is a property
    # of the MEASUREMENT, not of the run, so it is loaded for BOTH heads from the
    # same file -- including v1, which trains unweighted. Comparing the two heads
    # on an audibility split that only one of them was given would be worse than
    # not reporting it.
    audible = None
    audible_info: dict = {"source": None}
    weights_path = (sensitivity.DEFAULT_SENSITIVITY_PATH
                    if args.knob_weights == "auto" else
                    (Path(args.knob_weights) if args.knob_weights else None))
    if weights_path and Path(weights_path).exists():
        w, info = sensitivity.knob_weights(vocab, weights_path)
        audible = w > sensitivity.FLOOR + 1e-6
        audible_info = {"source": str(weights_path), "floor": sensitivity.FLOOR,
                        "audible": int(audible.sum()), "at_floor": int((~audible).sum()),
                        "degenerate": bool(info.get("degenerate", False))}
        if info.get("degenerate"):
            # Uniform weights make EVERY knob "audible", so the split would be a
            # copy of the strict number wearing a different name.
            audible, audible_info["source"] = None, None
    elif args.knob_weights and args.knob_weights != "auto":
        raise SystemExit(f"--knob-weights {args.knob_weights} does not exist")

    loader = DataLoader(ds, batch_size=args.batch, num_workers=args.workers)
    # Default the dump next to the checkpoint, so every run carries its own.
    pred_out = args.predictions_out
    if pred_out is None and not args.no_predictions:
        pred_out = run_dir / f"predictions-{args.split}.jsonl"
    metrics = run(model, loader, device, vocab, head=head, beam=args.beam, audible=audible,
                  predictions_out=pred_out, data_root=Path(args.data),
                  cache_dir=(cache_dir or (run_dir / 'cache')), split=args.split)
    metrics["audible_split"] = audible_info
    metrics["checkpoint"] = str(args.checkpoint)
    metrics["frontend"] = frontend
    metrics["split"] = args.split

    print(json.dumps({k: v for k, v in metrics.items() if k not in ("recovery", "per_family")},
                     indent=2))
    print("\n== per-family ==")
    print(f"{'family':12} {'support':>8} {'prec':>7} {'recall':>7} {'f1':>7}")
    for name, m in metrics["per_family"].items():
        print(f"{name:12} {m['support']:8d} {m['precision']:7.3f} {m['recall']:7.3f} {m['f1']:7.3f}")

    print(f"\n== recovery (knob MAE vs the {YARDSTICK} yardstick) ==")
    print(f"{'module':16} {'obs':>7} {'mae':>7} {'<=yard':>8}")
    for module_id, m in sorted(metrics["recovery"].items(), key=lambda kv: kv[1]["knob_mae"]):
        print(f"{module_id:16} {m['observations']:7d} {m['knob_mae']:7.3f} {m['within_yardstick']:8.3f}")

    if args.json_out:
        Path(args.json_out).write_text(json.dumps(metrics, indent=2), encoding="utf-8")
        print(f"\n[eval] wrote {args.json_out}")


if __name__ == "__main__":
    main()
