"""Licence/size/CPU-speed bake-off for the pretrained audio front-end.

The estimator runs INSIDE the app, on CPU, while the audio engine is live, so
"one 5 s clip well under 1 s on CPU" is a hard gate, not a preference. This
script downloads each candidate, counts its audio-tower params and times a
single-clip forward the way the app would run it (ORT, CPU provider, one
session, `intra_op_num_threads` small so the audio thread keeps its cores).

Run:  python bench_frontend.py
"""

from __future__ import annotations

import json
import time

import numpy as np
import torch

CANDIDATES = {
    # id: (licence, url, loader)
    "ast": (
        "Apache-2.0",
        "https://huggingface.co/MIT/ast-finetuned-audioset-10-10-0.4593",
    ),
    "clap-htsat": (
        "MIT (LAION)",
        "https://huggingface.co/laion/clap-htsat-unfused",
    ),
}


def count_params(m: torch.nn.Module) -> int:
    return sum(p.numel() for p in m.parameters())


def time_cpu(fn, warmup: int = 3, iters: int = 10) -> float:
    for _ in range(warmup):
        fn()
    t0 = time.perf_counter()
    for _ in range(iters):
        fn()
    return (time.perf_counter() - t0) / iters * 1000.0


def bench_ast() -> dict:
    from transformers import ASTModel

    model = ASTModel.from_pretrained("MIT/ast-finetuned-audioset-10-10-0.4593")
    model.eval()
    cfg = model.config
    # AST eats a mel spectrogram, not PCM: [B, max_length, num_mel_bins].
    x = torch.randn(1, cfg.max_length, cfg.num_mel_bins)
    with torch.inference_mode():
        out = model(x).last_hidden_state
        ms = time_cpu(lambda: model(x))
    return {
        "params_m": count_params(model) / 1e6,
        "hidden": out.shape[-1],
        "tokens": out.shape[1],
        "input": f"mel[{cfg.max_length}x{cfg.num_mel_bins}] @16kHz",
        "torch_cpu_ms": ms,
    }


def bench_clap() -> dict:
    from transformers import ClapAudioModel

    model = ClapAudioModel.from_pretrained("laion/clap-htsat-unfused")
    model.eval()
    cfg = model.config
    # HTSAT eats [B, 1, frames, mel_bins] at 48 kHz -- the rate our clips are
    # already in, so the ONNX graph needs no resampler at all.
    frames = cfg.spec_size if getattr(cfg, "spec_size", None) else 1001
    x = torch.randn(1, 1, 1001, cfg.num_mel_bins)
    with torch.inference_mode():
        out = model(x).last_hidden_state
        ms = time_cpu(lambda: model(x))
    return {
        "params_m": count_params(model) / 1e6,
        "hidden": out.shape[1] if out.dim() == 3 else out.shape[-1],
        "shape": list(out.shape),
        "input": f"mel[1001x{cfg.num_mel_bins}] @48kHz (frames hint {frames})",
        "torch_cpu_ms": ms,
    }


def main() -> None:
    torch.set_num_threads(4)
    results = {}
    for name, fn in (("ast", bench_ast), ("clap-htsat", bench_clap)):
        licence, url = CANDIDATES[name]
        try:
            r = fn()
            r.update(licence=licence, url=url, ok=True)
        except Exception as e:  # noqa: BLE001 - a candidate that will not load is a result
            r = {"ok": False, "error": f"{type(e).__name__}: {e}", "licence": licence, "url": url}
        results[name] = r
        print(name, json.dumps(r, indent=2, default=str), flush=True)

    print("\n== summary ==")
    for name, r in results.items():
        if r.get("ok"):
            print(f"{name:12} {r['params_m']:7.1f} M  {r['torch_cpu_ms']:8.1f} ms  {r['licence']}")
        else:
            print(f"{name:12} FAILED: {r['error']}")


if __name__ == "__main__":
    main()
