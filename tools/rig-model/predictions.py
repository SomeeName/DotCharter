r"""Write `predictions-test.jsonl`: truth and prediction as renderable presets.

The model is judged on whether it sounds CLOSE, not on whether it named the same
gear. That verdict is made by rendering the predicted rig through the same DI as
the truth and measuring a tone distance, which means every eval has to emit, per
held-out clip, a prediction in the manifest's own preset shape -- not just the
scalar metrics.

One row per clip:

    {"clip": ..., "di": ..., "seconds": [start, len] | null,
     "truth": {"Modules": [...]}, "predicted": {"Modules": [...]},
     "confidence": float}

Both sides go through `Vocab.decode_rig`/the manifest's own keys, so the renderer
cannot tell which is which -- that symmetry is the point. In particular TRUTH is
re-emitted from the manifest verbatim rather than round-tripped through the
label encoding: quantising the truth through `normalise`/`denormalise` would
move it slightly, and every clip's measured distance would then include an error
the model never made.

The ORDER is the dataset's, taken from `<cache>/<split>.paths.txt`, which
`data.py` writes as one absolute clip path per row in exactly the order the
`DataLoader` yields them with `shuffle=False`. That file is the only honest join
key: the manifest can hold several corpora whose clip indices collide, and the
absolute resolved path is what distinguishes them.
"""
from __future__ import annotations

import json
from pathlib import Path


def load_manifest_index(data_root: Path) -> dict[str, dict]:
    """`{resolved-absolute-clip-path: manifest row}` for one dataset root.

    Keyed by the RESOLVED path because a merged manifest addresses several
    corpora with `../` prefixes, and two roots can each hold `clips/00000/
    00000000.wav`.
    """
    manifest = Path(data_root) / "manifest.jsonl"
    if not manifest.exists():
        raise FileNotFoundError(f"{manifest} not found; cannot emit predictions")
    root = manifest.parent
    index: dict[str, dict] = {}
    with manifest.open("r", encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            obj = json.loads(line)
            rel = obj.get("ClipPath") or obj.get("clipPath")
            if not rel:
                continue
            key = str((root / str(rel).replace("\\", "/")).resolve()).lower()
            index[key] = obj
    return index


def _span(row: dict):
    """`[start, length]` in seconds, or None if the row says nothing useful.

    `eval-render`'s CLIP mode needs a window; emitting `null` forces the caller
    into `--target render`, which re-renders from the DI instead of cutting the
    stored clip. Both are legitimate, but a null here silently removes the
    choice, so a span is emitted whenever one can be stated honestly.

    An EXPLICIT span wins if the manifest ever carries one. Failing that, the
    v1/v2 sampler renders a whole fixed-length clip and stores `Seconds` as its
    LENGTH, so the window is unambiguously `[0, Seconds]` -- the clip IS the
    take. That is stated rather than guessed: a sampler that later cuts a window
    out of a longer recording would have to write a real start, and until it
    does, 0 is not an assumption but a fact about how these clips were made.
    """
    for a, b in (("Start", "Length"), ("start", "length"),
                 ("StartSeconds", "LengthSeconds")):
        if a in row and b in row:
            return [float(row[a]), float(row[b])]
    if "Span" in row and isinstance(row["Span"], (list, tuple)) and len(row["Span"]) == 2:
        return [float(row["Span"][0]), float(row["Span"][1])]
    secs = row.get("Seconds", row.get("seconds"))
    if secs is not None:
        try:
            return [0.0, float(secs)]
        except (TypeError, ValueError):
            return None
    return None


def write_predictions(out_path, data_root, cache_dir, split, vocab,
                      pred_slots, pred_knobs, confidence) -> int:
    """Emit one JSONL row per clip. Returns the number written.

    `pred_slots` is `[N, slots]` of slot classes and `pred_knobs` `[N, knob_dim]`
    in `[0,1]` -- whichever head produced them, which is why a v1 argmax and a v2
    greedy decode can be compared on one renderer.
    """
    paths_file = Path(cache_dir) / f"{split}.paths.txt"
    if not paths_file.exists():
        raise FileNotFoundError(
            f"{paths_file} not found; it is written by data.py alongside the fp16 cache "
            "and is the only reliable join between dataset order and manifest rows")
    order = [ln.strip() for ln in paths_file.read_text(encoding="utf-8").splitlines() if ln.strip()]
    if len(order) != len(pred_slots):
        raise ValueError(
            f"{paths_file} has {len(order)} rows but {len(pred_slots)} predictions were made; "
            "the cache and the eval disagree about the split")

    index = load_manifest_index(Path(data_root))
    out_path = Path(out_path)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    missing = 0
    written = 0
    with out_path.open("w", encoding="utf-8") as fh:
        for i, abs_clip in enumerate(order):
            row = index.get(str(Path(abs_clip).resolve()).lower())
            if row is None:
                missing += 1
                continue
            # Truth verbatim from the manifest: NOT re-encoded, see module docstring.
            truth_modules = row.get("Rig", {}).get("Modules") \
                or row.get("rig", {}).get("modules") or []
            fh.write(json.dumps({
                "clip": row.get("ClipPath") or row.get("clipPath"),
                "di": row.get("DiPath") or row.get("diPath"),
                "seconds": _span(row),
                "truth": {"Modules": truth_modules},
                "predicted": {"Modules": vocab.decode_rig(pred_slots[i], pred_knobs[i])},
                "confidence": float(confidence[i]) if confidence is not None else None,
            }, ensure_ascii=False) + "\n")
            written += 1
    if missing:
        print(f"[predictions] WARNING: {missing} of {len(order)} clips were not found in "
              f"{data_root}\\manifest.jsonl; those rows were skipped", flush=True)
    return written
