r"""Evaluate an EXPORTED `model.onnx` on a real split — the incumbent's own terms.

`eval.py` takes a `--checkpoint`, so a model whose `.pt` is gone cannot be put in
a comparison table at all. The model the app is loading today is exactly that
case: it was exported on 2026-09-12 from a run directory that lived in an agent
worktree which has since been deleted. Its `training-log.csv` survives inside the
export, but those numbers are on ITS OWN (synthetic) val split and are therefore
not comparable to anything measured on the real held-out test split.

This runs the ONNX graph over a real split and reports the SAME metrics, so
"is the new model better?" becomes a subtraction rather than an argument.

Two decisions make the comparison honest, and both are deliberate:

* **The metrics are `eval.py`'s, not a second copy.** The graph is wrapped in a
  shim that quacks like the torch model and handed to `eval.run` unchanged.
  `eval.py` says it itself -- "two comparisons computed by two pieces of code
  compare nothing" -- and a re-implemented readiness rate is precisely how a
  model gets credited with a number nobody else computes the same way.
* **The audio goes through `export_onnx.prepare_input`, imported rather than
  retyped.** That is the twin of `RigEstimatorOnnx.PrepareInput`, so the model is
  fed what the APP would feed it: centre window, RMS-normalised to -18 dBFS,
  gain capped at 64x, non-finite scrubbed. Evaluating on raw cached audio instead
  would measure the model on a level it never sees in production.

The shim returns LOGITS, because that is what `eval.run` expects: it applies its
own `sigmoid` to the knob and confidence heads and its own `argmax` to the slot
head. The ONNX graph has already applied those activations, so feeding its
probabilities straight in would sigmoid them twice -- which does not crash, and
quietly compresses every knob toward 0.5. `logit(p)` inverts it exactly, and for
the slot head `argmax(log p) == argmax(p)`, so the chain is untouched.

    python eval_onnx.py --model %LOCALAPPDATA%\DotCharter\ai-match\rig-estimator\model.onnx \
        --data <dir> --cache-dir <dir> --split test --json-out incumbent.json
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
import torch
from torch.utils.data import DataLoader

import eval as E
import sensitivity
from data import build_splits
from export_onnx import prepare_input
from vocab import DEFAULT_VOCAB_PATH, load_vocab

OUTPUT_NAMES = ["family_presence", "slot_probs", "knob_values", "confidence"]


def _logit(p: np.ndarray) -> np.ndarray:
    """Invert a sigmoid. Clipped, because p == 0 or 1 is +-inf and NaNs the mean."""
    p = np.clip(p.astype(np.float64), 1e-7, 1.0 - 1e-7)
    return np.log(p / (1.0 - p)).astype(np.float32)


class OnnxAsTorchModel:
    """Presents an ONNX session with `eval.run`'s expectation of a torch model."""

    def __init__(self, session, input_name: str, samples: int):
        self.session = session
        self.input_name = input_name
        self.samples = samples

    def eval(self):          # `eval.run` calls this; nothing to do for a graph
        return self

    def __call__(self, audio: torch.Tensor):
        batch = np.stack([
            prepare_input(row, self.samples)
            for row in audio.detach().cpu().numpy().astype(np.float32)
        ])
        fam, slot, knob, conf = self.session.run(
            OUTPUT_NAMES, {self.input_name: batch})
        # log() not logit() for the slot head: it is a softmax, and argmax is
        # invariant under log, so the decoded chain is bit-identical.
        return (
            torch.from_numpy(_logit(np.asarray(fam))),
            torch.from_numpy(np.log(np.clip(np.asarray(slot), 1e-12, None)).astype(np.float32)),
            torch.from_numpy(_logit(np.asarray(knob))),
            torch.from_numpy(_logit(np.asarray(conf).reshape(-1, 1))),
        )


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", required=True, help="path to model.onnx")
    ap.add_argument("--data", required=True)
    ap.add_argument("--split", default="test", choices=["train", "val", "test"])
    ap.add_argument("--cache-dir", default=None)
    ap.add_argument("--vocab", default=str(DEFAULT_VOCAB_PATH))
    ap.add_argument("--batch", type=int, default=16)
    ap.add_argument("--limit", type=int, default=None)
    ap.add_argument("--json-out", default=None)
    ap.add_argument("--predictions-out", default=None,
                    help="per-clip truth/prediction JSONL for the sound-distance eval; "
                         "defaults to <model dir>/predictions-<split>.jsonl")
    ap.add_argument("--no-predictions", action="store_true",
                    help="skip the prediction dump")
    ap.add_argument("--knob-weights", default="auto",
                    help="sensitivity JSON defining the AUDIBLE knob subset "
                         "('auto' = beside the vocab, '' = off). Reported beside "
                         "ready_rate; it does not change the gate.")
    ap.add_argument("--providers", default="CPUExecutionProvider",
                    help="comma-separated ORT providers. CPU by default: this is a "
                         "comparison harness, and the GPU is usually training.")
    args = ap.parse_args()

    import onnxruntime as ort

    vocab = load_vocab(args.vocab)
    session = ort.InferenceSession(args.model,
                                   providers=args.providers.split(","))
    inputs = session.get_inputs()
    if len(inputs) != 1:
        raise SystemExit(f"expected exactly one graph input, got {[i.name for i in inputs]}")
    got = {o.name for o in session.get_outputs()}
    missing = [n for n in OUTPUT_NAMES if n not in got]
    if missing:
        raise SystemExit(f"{args.model} is missing required output(s) {missing}; "
                         f"it publishes {sorted(got)}")

    samples = int(round(vocab.sample_rate * float(vocab.clip_seconds)))
    print(f"[eval-onnx] {args.model}")
    print(f"[eval-onnx] input '{inputs[0].name}' {inputs[0].shape}, feeding {samples} samples "
          f"prepared exactly as RigEstimatorOnnx.PrepareInput does")
    print(f"[eval-onnx] providers {session.get_providers()}")

    sidecar = Path(args.model).with_suffix(".sidecar.json")
    model_id = "unknown"
    if sidecar.exists():
        try:
            model_id = json.loads(sidecar.read_text(encoding="utf-8"))["export"]["modelId"]
            print(f"[eval-onnx] modelId {model_id}")
        except Exception:
            pass

    splits = build_splits(
        Path(args.data), vocab, limit=args.limit,
        cache_dir=Path(args.cache_dir) if args.cache_dir else None)
    ds = splits[args.split]
    print(f"[eval-onnx] {args.split} split: {len(ds)} clips")

    loader = DataLoader(ds, batch_size=args.batch, shuffle=False, num_workers=0)
    model = OnnxAsTorchModel(session, inputs[0].name, samples)

    # head='v1': the four-output contract IS the v1 shape. A v2 export publishes
    # the same four (its slot_probs are filled from the decoder's own
    # distribution), so this reads either export the way the APP reads it --
    # which is the number that matters here, not the python-side greedy decode.
    # Same audible split as eval.py, so the incumbent sits in the table on
    # identical terms rather than being the one row without the columns.
    audible = None
    if args.knob_weights:
        wp = (sensitivity.DEFAULT_SENSITIVITY_PATH if args.knob_weights == "auto"
              else Path(args.knob_weights))
        if Path(wp).exists():
            w, info = sensitivity.knob_weights(vocab, wp)
            if not info.get("degenerate"):
                audible = w > sensitivity.FLOOR + 1e-6
                print(f"[eval-onnx] audible split: {int(audible.sum())} of {w.size} knobs "
                      f"above the {sensitivity.FLOOR} floor")

    pred_out = args.predictions_out
    if pred_out is None and not args.no_predictions:
        pred_out = Path(args.model).parent / f"predictions-{args.split}.jsonl"
    metrics = E.run(model, loader, torch.device("cpu"), vocab, head="v1", audible=audible,
                    predictions_out=pred_out, data_root=Path(args.data),
                    cache_dir=args.cache_dir, split=args.split)
    metrics["model"] = str(args.model)
    metrics["modelId"] = model_id
    metrics["split"] = args.split

    print(json.dumps({k: v for k, v in metrics.items()
                      if k not in ("recovery", "per_family")}, indent=2))
    if args.json_out:
        Path(args.json_out).write_text(json.dumps(metrics, indent=2), encoding="utf-8")
        print(f"[eval-onnx] wrote {args.json_out}")


if __name__ == "__main__":
    main()
