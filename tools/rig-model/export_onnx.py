"""Exports a checkpoint to ONNX + the sidecar the C# estimator reads.

    python export_onnx.py --checkpoint runs/clap/best.pt --out <dir>

The graph takes RAW MONO PCM, [batch, 240000] float32 at 48 kHz, and returns
PROBABILITIES on four named outputs. Activations are baked in on purpose: if
C# had to know which head wanted a sigmoid and which a softmax, that is four
more places for the two sides to disagree.

The sidecar is the C#-exported `rig_label_vocab.json` plus an `export` block
(model id, opset, input/output names) and, once `eval.py` has run, a `parity`
block holding a fixture clip's exact outputs. `RigEstimatorParityTests` runs
the shipped ONNX on that fixture and compares -- which is what proves the C#
normalisation reproduces the python one rather than merely looking like it.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import time
from pathlib import Path

import numpy as np
import torch

import model_v2 as v2
from model import ExportWrapper, RigEstimator
from vocab import DEFAULT_VOCAB_PATH, load_vocab

INPUT_NAME = "audio"
OUTPUT_NAMES = ["family_presence", "slot_probs", "knob_values", "confidence"]

#: The v2 contract is ADDITIVE. The four v1 outputs keep their names, shapes and
#: meaning -- filled from the decoder's greedy chain, because token `k+2` IS
#: slot class `k`, so v1's `slot_probs` carries the decoder's own distribution
#: and its argmax IS the decoded chain -- and the sequence is published BESIDE
#: them, so `RigLabelVocabulary.SchemaVersion` does NOT move -- bumping it would
#: have made every existing v1 model unloadable to buy nothing.
#:
#: `chain_tokens` is FLOAT32, not int64, and that is part of the contract: the
#: C# reader maps an ONNX output to `float[]`, so an integer-typed output makes
#: any reader that touches it throw. Ids top out at 102 and are exact in fp32.
#: (`RigEstimatorOnnx` was also hardened to read only the four outputs it needs
#: BY NAME -- see `RigEstimatorContractTests`.)
V2_EXTRA_OUTPUTS = ["chain_tokens", "chain_knobs"]
OPSET = 17


def output_names(head: str) -> list[str]:
    """The graph's outputs for a head: v1's four, plus v2's two."""
    return OUTPUT_NAMES + (V2_EXTRA_OUTPUTS if head == "v2" else [])


def build_fixture(n: int, sr: int) -> np.ndarray:
    """The parity fixture: CLOSED FORM, so C# can regenerate it exactly.

    There is deliberately no RNG in here. An earlier version added numpy
    Gaussian noise, which no C# test can reproduce -- it would have forced the
    fixture to ship as a binary blob, and a blob is a thing that can go stale
    against the formula without anyone noticing. Harmonics, a 4 Hz tremolo and
    a re-pluck envelope are enough structure that a model which collapsed to a
    constant still moves the outputs.
    """
    t = np.arange(n, dtype=np.float64) / sr
    x = np.zeros(n, dtype=np.float64)
    for h, f in enumerate([110.0, 220.0, 330.0, 440.0, 660.0], start=1):
        x += (1.0 / h) * np.sin(2 * np.pi * f * t + 0.3 * h)
    x *= 0.6 + 0.4 * np.sin(2 * np.pi * 4.0 * t)        # a 4 Hz tremolo
    x *= np.exp(-np.mod(t, 1.25) * 1.6)                  # re-plucked every 1.25 s
    x /= max(float(np.max(np.abs(x))), 1e-9)
    return (x * 0.7).astype(np.float32)


TARGET_RMS_DBFS = -18.0


def prepare_input(audio: np.ndarray, samples: int) -> np.ndarray:
    """The twin of `RigEstimatorOnnx.PrepareInput`.

    Centre window, then normalise to -18 dBFS RMS -- the level the dataset
    renderer produces, so an un-normalised clip is simply a level the model has
    never seen. The gain cap and the silence guard are here for the same reason
    they are there: a near-silent clip must not be blown up by 60 dB.
    """
    audio = np.asarray(audio, dtype=np.float32)
    if audio.shape[0] > samples:
        start = (audio.shape[0] - samples) // 2
        window = audio[start:start + samples].copy()
    else:
        window = np.zeros(samples, dtype=np.float32)
        window[: audio.shape[0]] = audio

    finite = np.where(np.isfinite(window), window, 0.0).astype(np.float64)
    rms = float(np.sqrt(np.mean(finite * finite)))
    if rms < 1e-8:
        return window
    gain = min(10.0 ** (TARGET_RMS_DBFS / 20.0) / rms, 64.0)
    out = window.astype(np.float64) * gain
    out = np.where(np.isfinite(out), np.clip(out, -1.0, 1.0), 0.0)
    return out.astype(np.float32)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--checkpoint", required=True)
    ap.add_argument("--out", required=True, help="dir to write model.onnx + sidecar")
    ap.add_argument("--vocab", default=str(DEFAULT_VOCAB_PATH))
    ap.add_argument("--model-id", default=None)
    ap.add_argument("--check", action="store_true", default=True)
    ap.add_argument("--head", default=None, choices=["v1", "v2"],
                    help="override the checkpoint's own head; normally unnecessary")
    args = ap.parse_args()

    vocab = load_vocab(args.vocab)
    ckpt = torch.load(args.checkpoint, map_location="cpu", weights_only=False)
    frontend = ckpt.get("frontend", "cnn")
    head = args.head or ckpt.get("head") or ckpt.get("args", {}).get("head") or "v1"
    names = output_names(head)

    if head == "v2":
        ck_args = ckpt.get("args", {})
        model = v2.RigEstimatorV2(vocab, frontend=frontend, freeze=True,
                                  layers=int(ck_args.get("decoder_layers", 3)),
                                  d_model=int(ck_args.get("decoder_dim", 256)),
                                  knob_head=str(ck_args.get("knob_head", "step")))
        model.load_state_dict(ckpt["model"])
        model.eval()
        wrapper = v2.ExportWrapperV2(model).eval()
    else:
        model = RigEstimator(vocab, frontend=frontend, freeze=True)
        model.load_state_dict(ckpt["model"])
        model.eval()
        wrapper = ExportWrapper(model).eval()

    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)
    onnx_path = out_dir / "model.onnx"

    n = vocab.n_samples
    dummy = torch.zeros(1, n)
    torch.onnx.export(
        wrapper, (dummy,), str(onnx_path),
        input_names=[INPUT_NAME], output_names=names,
        # Batch is dynamic; the LENGTH is not. A model exported for 5 s that
        # silently accepted 3 s would read a different mel and answer
        # confidently with nonsense.
        dynamic_axes={INPUT_NAME: {0: "batch"}, **{o: {0: "batch"} for o in names}},
        opset_version=OPSET, do_constant_folding=True, dynamo=False,
    )
    print(f"[export] wrote {onnx_path} ({onnx_path.stat().st_size/1e6:.1f} MB)")

    # -- parity fixture ---------------------------------------------------
    # The fixture goes through prepare_input, so the recorded numbers pin the
    # WHOLE C# path -- window, normalisation and inference -- not just the
    # graph. A C# normalisation that disagreed would otherwise still produce a
    # plausible rig and pass any test that only compared the ONNX outputs.
    raw = build_fixture(n, vocab.sample_rate)
    fixture = prepare_input(raw, n)
    with torch.no_grad():
        torch_out = wrapper(torch.from_numpy(fixture).unsqueeze(0))
    torch_out = [o.numpy() for o in torch_out]

    parity = {
        "fixtureSamples": int(n),
        "targetRmsDbfs": TARGET_RMS_DBFS,
        "fixtureSha256": hashlib.sha256(fixture.tobytes()).hexdigest(),
        "fixtureFirst8": [float(v) for v in fixture[:8]],
        "fixtureRms": float(np.sqrt(np.mean(fixture.astype(np.float64) ** 2))),
    }

    if args.check:
        import onnxruntime as ort

        so = ort.SessionOptions()
        so.intra_op_num_threads = 2
        sess = ort.InferenceSession(str(onnx_path), so, providers=["CPUExecutionProvider"])
        ort_out = sess.run(names, {INPUT_NAME: fixture[None, :]})
        for name, a, b in zip(names, torch_out, ort_out):
            delta = float(np.max(np.abs(a - b)))
            print(f"[export] {name:16} torch-vs-ort max|delta| = {delta:.3e}")
            # `not (delta <= tol)`, never `delta > tol`: a NaN fails every
            # comparison, so `>` would wave through the exact failure this
            # check exists to catch -- a broken graph whose outputs are all
            # NaN. That happened here once already.
            if not (delta <= 2e-3):
                raise SystemExit(f"{name} differs between torch and ORT by {delta}")

        reps = 20
        t0 = time.perf_counter()
        for _ in range(reps):
            sess.run(names, {INPUT_NAME: fixture[None, :]})
        ms = (time.perf_counter() - t0) / reps * 1000.0
        print(f"[export] ORT CPU (2 threads): {ms:.1f} ms per 5 s clip")
        parity["cpuInferenceMs"] = round(ms, 2)

        parity["outputs"] = {
            name: [round(float(v), 6) for v in np.asarray(o).reshape(-1)[:64]]
            for name, o in zip(names, ort_out)
        }
        parity["outputShapes"] = {n_: list(np.asarray(o).shape) for n_, o in zip(names, ort_out)}

    # -- sidecar ----------------------------------------------------------
    sidecar = json.loads(Path(args.vocab).read_text(encoding="utf-8"))
    model_id = args.model_id or f"rig-estimator-{frontend}-{head}-{time.strftime('%Y%m%d')}"
    sidecar["export"] = {
        "modelId": model_id,
        "frontend": frontend,
        "head": head,
        "opset": OPSET,
        "inputName": INPUT_NAME,
        "outputNames": names,
        "paramsTotal": int(sum(p.numel() for p in model.parameters())),
        "checkpointEpoch": int(ckpt.get("epoch", -1)),
        "trainedOnCrudeSmokeSet": bool(ckpt.get("args", {}).get("crude", False)),
    }
    if head == "v2":
        # The token convention, written down where a future C# reader of
        # `chain_tokens` will look. `schemaVersion` deliberately does NOT move:
        # the four v1 outputs are unchanged, so a v1 reader is correct on a v2
        # model, and a bump would only make every v1 model unloadable.
        sidecar["export"]["chain"] = {
            "pad": v2.PAD, "bos": v2.BOS, "eos": v2.EOS,
            "tokenOffset": v2.TOKEN_OFFSET,
            "note": "chain_tokens[i] - tokenOffset is the slot class (0 = <none>); "
                    "EOS ends the chain and PAD fills the rest. chain_knobs[i] is the "
                    "flat knob vector masked to that token's own module.",
            "maxSteps": int(vocab.slots),
            "decode": "greedy (unrolled in the graph); beam search is python-side only",
        }
    sidecar["parity"] = parity
    (out_dir / "model.sidecar.json").write_text(json.dumps(sidecar, indent=2), encoding="utf-8")
    print(f"[export] wrote {out_dir / 'model.sidecar.json'} (modelId={model_id})")

    src_log = Path(args.checkpoint).parent / "log.csv"
    if src_log.exists():
        shutil.copy(src_log, out_dir / "training-log.csv")


if __name__ == "__main__":
    main()
