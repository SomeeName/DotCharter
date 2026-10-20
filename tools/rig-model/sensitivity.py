"""Per-knob AUDIO sensitivity weights for the v2 knob loss.

A plain MAE over the 573-dim knob vector treats every knob as equally worth
getting right, and that is false in a way the ear notices immediately: an amp's
`gain` moves the tone across the room, a reverb's `predelay` at the same travel
moves it by nothing anyone can name. v1 trained both at weight 1.0, so most of
the knob gradient went into params whose value cannot be heard — and the owner's
bar ("skip the refinement and be ready to go") is an AUDIBLE bar.

So each (module, param) carries a measured weight: render one reference DI
through a default rig with that param at 0.1 and at 0.9, and take the tone
similarity distance between the two renders. That is literally "how much does
this knob move the sound", measured with the same distance the matcher scores
with. The measurement is `tools/rig-dataset`'s `knob-sensitivity` subcommand and
its product is `knob_sensitivity.json` beside the vocab; this module only reads
it and turns it into a weight vector aligned with the vocab's flat knob layout.

**The floor is load-bearing.** Weights are normalised by the corpus's 95th
percentile (not the max -- one outlier module would then flatten everything
else) and clamped into `[FLOOR, 1]`. A knob whose measured distance is zero is
still trained, at `FLOOR`: "the A/B render did not move the distance" is not the
same claim as "this param does nothing", and a zero weight is an untrainable
knob for ever. Without the floor the v2 model would predict garbage for every
param the measurement happened to miss, and nothing in the metrics would say so.

The file is OPTIONAL. With no file every weight is 1.0 and the v2 knob loss is
v1's masked MAE exactly, which is also what keeps the two comparable.
"""

from __future__ import annotations

import json
from pathlib import Path

import numpy as np

#: No knob is ever ignored. See the module docstring.
FLOOR = 0.1

#: Normalise against this percentile rather than the max: a single very loud
#: module (an amp's gain, a bitcrusher's rate) would otherwise push every other
#: knob towards the floor and the weighting would degrade to "the amp matters".
NORMALISE_PERCENTILE = 95.0

SCHEMA_VERSION = 1

DEFAULT_SENSITIVITY_PATH = Path(__file__).with_name("knob_sensitivity.json")


def load_raw(path: str | Path) -> dict:
    """Reads the sensitivity JSON and checks its schema version."""
    blob = json.loads(Path(path).read_text(encoding="utf-8"))
    got = blob.get("schemaVersion")
    if got != SCHEMA_VERSION:
        raise ValueError(
            f"knob_sensitivity.json schemaVersion {got} != {SCHEMA_VERSION}; "
            "re-measure it (tools/rig-dataset knob-sensitivity)"
        )
    return blob


def knob_weights(vocab, path: str | Path | None = None,
                 floor: float = FLOOR) -> tuple[np.ndarray, dict]:
    """The `[knob_dim]` weight vector, aligned with the vocab's flat layout.

    Returns `(weights, info)`. `info` carries the provenance the trainer logs
    and `run.json` records -- an unweighted run must be identifiable from its
    artefacts alone, because a weighted and an unweighted loss are not the same
    number and comparing them across runs is exactly the mistake this dict is
    here to prevent.
    """
    w = np.ones(vocab.knob_dim, dtype=np.float32)
    if path is None:
        return w, {"source": None, "method": "uniform", "floor": 1.0}

    p = Path(path)
    if not p.exists():
        raise FileNotFoundError(
            f"{p} not found. Measure it with\n"
            "  dotnet run --project tools/rig-dataset/DotCharter.Tools.RigDataset "
            "-- knob-sensitivity --out tools/rig-model/knob_sensitivity.json\n"
            "or train without --knob-weights (every weight 1.0)."
        )

    blob = load_raw(p)
    modules = blob.get("modules", {})
    contexts = blob.get("contexts", {})
    # Modules the measurement could not reach at all -- they render, but every
    # knob read 0.0 because an offline A/B never supplies the footswitch, the
    # trigger or the capture path they need (`freeze`, `looper`, `namamp` on
    # the 2026-09-13 sweep). That is a GAP IN THE MEASUREMENT, not a verdict,
    # and putting them at the floor with the genuinely-inaudible level knobs
    # would quietly stop training three whole modules' params. They get the
    # MEDIAN weight instead, i.e. "no evidence, so assume typical".
    inert = set(blob.get("inert", []))
    raw = np.zeros(vocab.knob_dim, dtype=np.float64)
    ctx_of = np.zeros(vocab.knob_dim, dtype=object)
    seen = missing = 0
    for entry in vocab.modules:
        params = modules.get(entry.id, {})
        for j, spec in enumerate(entry.params):
            ix = entry.param_offset + j
            ctx_of[ix] = contexts.get(entry.id, "solo")
            if spec.id in params:
                raw[ix] = max(0.0, float(params[spec.id]))
                seen += 1
            else:
                missing += 1

    # Normalise PER CONTEXT. A module measured solo and one measured behind a
    # default amp+cab are not on the same distance scale -- the sweep says so
    # itself -- so one global percentile would systematically rank every
    # `ampcab` module against the wrong yardstick. Within a context the
    # comparison is apples to apples, and the weight is a RELATIVE quantity
    # anyway.
    scale = 0.0
    scaled = np.zeros_like(raw)
    scales: dict[str, float] = {}
    for ctx in set(ctx_of.tolist()):
        sel = np.array([c == ctx for c in ctx_of])
        positive = raw[sel & (raw > 0)]
        s = float(np.percentile(positive, NORMALISE_PERCENTILE)) if positive.size else 0.0
        scales[str(ctx)] = s
        if s > 0.0:
            scaled[sel] = raw[sel] / s
            scale = max(scale, s)
    if scale <= 0.0:
        # Every measurement is zero -- an all-floor weight vector is a uniform
        # one scaled down, i.e. a silently WEAKER knob loss, so fall back to
        # uniform and say so rather than pretending the file carried signal.
        return w, {"source": str(p), "method": blob.get("method", "?"),
                   "degenerate": True, "floor": 1.0,
                   "note": "every sensitivity was 0; falling back to uniform weights"}

    w = np.clip(scaled, floor, 1.0).astype(np.float32)

    n_inert = 0
    if inert:
        median = float(np.median(w))
        for entry in vocab.modules:
            if entry.id in inert:
                lo = entry.param_offset
                w[lo: lo + len(entry.params)] = median
                n_inert += len(entry.params)

    return w, {
        "source": str(p),
        "method": blob.get("method", "?"),
        "referenceDi": blob.get("referenceDi"),
        "floor": floor,
        "scalePercentile": NORMALISE_PERCENTILE,
        "scalesByContext": scales,
        "paramsMeasured": seen,
        "paramsMissing": missing,
        "inertModules": sorted(inert),
        "paramsAtMedianForInertModules": n_inert,
        "atFloor": int((w <= floor + 1e-6).sum()),
        "mean": float(w.mean()),
    }


def describe(vocab, weights: np.ndarray, n: int = 10) -> list[tuple[str, str, float]]:
    """`(module, param, weight)` sorted DESCENDING -- for the report's top/bottom."""
    rows = []
    for entry in vocab.modules:
        for j, spec in enumerate(entry.params):
            rows.append((entry.id, spec.id, float(weights[entry.param_offset + j])))
    rows.sort(key=lambda r: -r[2])
    return rows if n <= 0 else rows[:n] + rows[-n:]
