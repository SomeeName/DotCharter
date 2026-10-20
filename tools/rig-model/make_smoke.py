"""A CRUDE synthetic smoke set, to prove the pipeline before real data exists.

**These clips are not the product.** The data lane renders through the real
JUCE rig; this renders through four hand-written stages (gain, soft clip,
one-pole low-pass, feedback delay) chosen only because they are trivially
correct. A model trained on this learns THIS toy renderer, so every metric it
produces is a pipeline check, never a quality claim. Every manifest row is
tagged `synthetic-crude` in `Augmentations` so a later reader cannot mistake it
for a real render, and `train.py` prints a warning when it sees that tag.

Usage:
    python make_smoke.py --out <dir> --count 300
"""

from __future__ import annotations

import argparse
import json
import math
import random
from pathlib import Path

import numpy as np
import soundfile as sf

from vocab import DEFAULT_VOCAB_PATH, load_vocab

TAG = "synthetic-crude"

# The toy renderer only knows four behaviours, so it may only claim modules
# whose audible effect it can actually produce. Claiming a `convreverb` it
# cannot render would teach the model a label with no signal behind it.
CRUDE_MODULES = {
    "gain": "level",
    "boost": "level",
    "volume": "level",
    "overdrive": "clip",
    "distortion": "clip",
    "fuzz": "clip",
    "bigmuff": "clip",
    "rat": "clip",
    "ds1": "clip",
    "cab": "lowpass",
    "tilt": "lowpass",
    "delay": "delay",
    "slapback": "delay",
    "tapedelay": "delay",
}


def make_di(rng: random.Random, n: int, sr: int) -> np.ndarray:
    """A plucked-string-ish DI: a few decaying harmonic bursts."""
    out = np.zeros(n, dtype=np.float32)
    t = np.arange(n) / sr
    for _ in range(rng.randint(3, 7)):
        f0 = rng.uniform(80, 440)
        start = rng.randint(0, max(1, n - sr // 2))
        dur = rng.randint(sr // 4, sr)
        end = min(n, start + dur)
        seg = np.arange(end - start) / sr
        env = np.exp(-seg * rng.uniform(1.5, 6.0))
        wave = np.zeros_like(seg)
        for h in range(1, 7):
            wave += (1.0 / h) * np.sin(2 * math.pi * f0 * h * seg + rng.uniform(0, 6.28))
        out[start:end] += (wave * env).astype(np.float32)
    out += rng.uniform(0.0005, 0.003) * np.random.randn(n).astype(np.float32)
    peak = float(np.max(np.abs(out))) or 1.0
    return (out / peak * 0.5).astype(np.float32)


def render_crude(di: np.ndarray, chain: list[tuple[str, dict]], sr: int) -> np.ndarray:
    x = di.astype(np.float64).copy()
    for type_id, params in chain:
        kind = CRUDE_MODULES[type_id]
        if kind == "level":
            x = x * (10.0 ** (params["_db"] / 20.0))
        elif kind == "clip":
            drive = 1.0 + params["_drive"] * 40.0
            x = np.tanh(x * drive) * (0.6 + 0.4 * params["_level"])
        elif kind == "lowpass":
            a = math.exp(-2.0 * math.pi * params["_cut"] / sr)
            y = np.empty_like(x)
            prev = 0.0
            for i in range(x.shape[0]):
                prev = (1 - a) * x[i] + a * prev
                y[i] = prev
            x = y
        elif kind == "delay":
            d = max(1, int(params["_time"] * sr))
            fb, mix = params["_fb"], params["_mix"]
            y = x.copy()
            for i in range(d, y.shape[0]):
                y[i] += fb * y[i - d]
            x = (1 - mix) * x + mix * y
    peak = float(np.max(np.abs(x))) or 1.0
    if peak > 0.99:
        x = x / peak * 0.99
    return x.astype(np.float32)


def sample_chain(rng: random.Random, vocab, max_len: int):
    """A short chain of crude-renderable modules, with real catalogue params."""
    ids = [m for m in CRUDE_MODULES if vocab.module(m) is not None]
    n = rng.choices([1, 2, 3, 4], weights=[0.30, 0.40, 0.22, 0.08])[0]
    n = min(n, max_len)
    picked = rng.sample(ids, k=min(n, len(ids)))
    chain, manifest = [], []
    for type_id in picked:
        entry = vocab.module(type_id)
        # Catalogue-real values for every param, so the LABELS are honest even
        # though the audio is not.
        params = {p.id: rng.uniform(p.min, p.max) for p in entry.params}
        hidden = {
            "_db": rng.uniform(-6, 12),
            "_drive": rng.uniform(0.05, 1.0),
            "_level": rng.uniform(0.3, 1.0),
            "_cut": rng.uniform(1500, 9000),
            "_time": rng.uniform(0.06, 0.5),
            "_fb": rng.uniform(0.1, 0.55),
            "_mix": rng.uniform(0.2, 0.6),
        }
        chain.append((type_id, hidden))
        manifest.append({"ModuleTypeId": type_id, "Params": params, "Bypassed": False})
    return chain, manifest


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--count", type=int, default=300)
    ap.add_argument("--di-sources", type=int, default=40,
                    help="distinct DI recordings; the split groups by these")
    ap.add_argument("--seed", type=int, default=20260912)
    ap.add_argument("--vocab", default=str(DEFAULT_VOCAB_PATH))
    args = ap.parse_args()

    vocab = load_vocab(args.vocab)
    out = Path(args.out)
    (out / "clips").mkdir(parents=True, exist_ok=True)
    (out / "di").mkdir(parents=True, exist_ok=True)
    rng = random.Random(args.seed)
    np.random.seed(args.seed)
    n = vocab.n_samples
    sr = vocab.sample_rate

    dis = []
    for i in range(args.di_sources):
        p = out / "di" / f"di_{i:04d}.wav"
        audio = make_di(rng, n, sr)
        sf.write(str(p), audio, sr, subtype="PCM_16")
        dis.append((p, audio))

    manifest = out / "manifest.jsonl"
    with manifest.open("w", encoding="utf-8") as fh:
        for i in range(args.count):
            di_path, di_audio = dis[rng.randrange(len(dis))]
            chain, rig_modules = sample_chain(rng, vocab, vocab.slots)
            wet = render_crude(di_audio, chain, sr)
            clip = out / "clips" / f"clip_{i:06d}.wav"
            sf.write(str(clip), wet, sr, subtype="PCM_16")
            fh.write(json.dumps({
                "ClipPath": str(clip.relative_to(out)).replace("\\", "/"),
                "DiPath": str(di_path.relative_to(out)).replace("\\", "/"),
                "Rig": {"Modules": rig_modules},
                "Augmentations": [TAG],
                "SampleRate": sr,
                "Seconds": vocab.clip_seconds,
                "Seed": args.seed + i,
            }) + "\n")
            if (i + 1) % 50 == 0:
                print(f"[smoke] {i + 1}/{args.count}", flush=True)

    print(f"[smoke] wrote {args.count} CRUDE clips to {out} -- pipeline proof only, not a quality signal")


if __name__ == "__main__":
    main()
