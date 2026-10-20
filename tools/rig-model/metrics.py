"""Chain metrics, shared by `train.py` and `eval.py` so v1 and v2 compare.

Everything here takes v1-SHAPED arrays -- `[N, slots]` of slot classes with 0 =
`<none>`, and `[N, knob_dim]` in `[0,1]` -- whichever head produced them. That
is the whole point: a sequence decoder and a per-slot argmax are different
models of the same object, and the only honest comparison is on one set of
numbers computed by one piece of code.

**The READINESS RATE is the owner's bar in numbers**: "get to a point where you
can skip the refinement and are ready to go most of the time". A clip is ready
when the chain is EXACTLY right (right modules, right order, right length) and
every knob of every module in it is within `tol` of travel. It is deliberately
an ALL-OR-NOTHING per clip: a rig with six right knobs and a seventh 0.4 out is
a rig the user has to go and fix, and an average MAE hides exactly that.
"""

from __future__ import annotations

import numpy as np

#: "Ready to go": every knob within a tenth of travel. The looser 0.2 is
#: reported beside it because the gap between the two says whether the model is
#: nearly there or not close -- one number cannot.
READY_TOL = 0.1
READY_TOL_LOOSE = 0.2


def trim(chain) -> list[int]:
    """A slot-class row -> the chain, with the trailing `<none>`s removed."""
    out = [int(c) for c in chain]
    while out and out[-1] == 0:
        out.pop()
    return out


def edit_distance(a, b) -> int:
    """Levenshtein between two chains (trailing `<none>`s trimmed first).

    Reported because exact-match is brutal and slot accuracy is too kind: a
    chain that is right but for ONE INSERTED pedal scores 0 on the first and
    can score near 0 on the second (every later slot is shifted), while the
    edit distance says 1 -- which is what a user would say too.
    """
    a, b = trim(a), trim(b)
    if not a:
        return len(b)
    prev = list(range(len(b) + 1))
    for i, x in enumerate(a, start=1):
        cur = [i] + [0] * len(b)
        for j, y in enumerate(b, start=1):
            cur[j] = min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (x != y))
        prev = cur
    return prev[-1]


def chain_metrics(pred_slots: np.ndarray, true_slots: np.ndarray) -> dict:
    """Exact / per-slot / occupied-slot accuracy and edit distance."""
    pred = np.asarray(pred_slots)
    true = np.asarray(true_slots)
    occupied = true != 0
    dists = [edit_distance(p, t) for p, t in zip(pred, true)]
    lengths = [max(len(trim(t)), 1) for t in true]
    return {
        "exact_chain_match": float((pred == true).all(axis=1).mean()),
        "slot_accuracy": float((pred == true).mean()),
        "slot_accuracy_occupied": float((pred[occupied] == true[occupied]).mean())
        if occupied.any() else 0.0,
        "chain_edit_distance": float(np.mean(dists)),
        "chain_edit_distance_normalised": float(np.mean(np.asarray(dists) / np.asarray(lengths))),
    }


def readiness(pred_slots: np.ndarray, true_slots: np.ndarray, pred_knobs: np.ndarray,
              true_knobs: np.ndarray, knob_mask: np.ndarray,
              tol: float = READY_TOL, audible: np.ndarray | None = None) -> float:
    """Fraction of clips that are READY: exact chain AND every knob within `tol`.

    The knob test uses the ROW's own mask, i.e. the params of the modules that
    are actually in the true rig. That is well defined precisely because the
    chain has to be exact first: when it is, the predicted modules and the true
    ones are the same set, so "every knob of every module in the rig" is one
    unambiguous set of indices. A row whose rig has no params at all (an empty
    chain) counts as ready when the chain matches -- there is nothing left to
    get wrong.

    `audible` is an optional `[knob_dim]` boolean selecting the knobs a
    measurement says can actually be HEARD. Passing it answers a different and
    narrower question -- "is every knob that MATTERS right?" -- and is reported
    BESIDE the strict number, never instead of it. See `readiness_report`.
    """
    pred_slots = np.asarray(pred_slots)
    true_slots = np.asarray(true_slots)
    mask = np.asarray(knob_mask) > 0
    if audible is not None:
        mask = mask & np.asarray(audible).astype(bool)[None, :]
    err = np.abs(np.asarray(pred_knobs) - np.asarray(true_knobs))
    # `np.where(mask, err, 0).max()` rather than `err[mask].max()`: a row with
    # an all-zero mask has no elements and `.max()` on it RAISES, which would
    # turn "this rig is empty" into a crash halfway through an eval.
    worst = np.where(mask, err, 0.0).max(axis=1) if err.size else np.zeros(len(pred_slots))
    exact = (pred_slots == true_slots).all(axis=1)
    return float((exact & (worst <= tol)).mean())


def readiness_report(pred_slots, true_slots, pred_knobs, true_knobs, knob_mask,
                     audible: np.ndarray | None = None) -> dict:
    """Both tolerances, named as the report and the pipeline read them.

    **`ready_rate` is the export gate and its meaning never changes**: exact
    chain, and EVERY knob of the rig within tolerance, audible or not.

    When `audible` is given, four more numbers are reported ALONGSIDE it. They
    exist because the strict gate and the v2 knob loss disagree about what a
    knob is worth, and that disagreement is otherwise invisible:

    * v2's knob loss is weighted by measured audibility, so it deliberately lets
      an inaudible knob drift; `ready_rate` then counts that drift as a failure.
      A head can therefore predict the CHAIN far better and still score worse on
      readiness, purely because of what its loss was told to ignore.
    * `ready_rate_audible` asks the question the loss was actually asked -- is
      every knob that can be heard right? -- and `knob_mae_audible` /
      `knob_mae_inaudible` split the error the same way, so "the model is only
      losing on knobs nobody can hear" becomes a number instead of a theory.

    These are REPORTED, never a gate. Which question to ship on is a decision
    for a human, and it should be made with both numbers visible rather than by
    quietly redefining the one the pipeline reads.
    """
    out = {
        "ready_rate": round(readiness(pred_slots, true_slots, pred_knobs, true_knobs,
                                      knob_mask, READY_TOL), 4),
        "ready_rate_loose": round(readiness(pred_slots, true_slots, pred_knobs, true_knobs,
                                            knob_mask, READY_TOL_LOOSE), 4),
        "ready_tolerance": READY_TOL,
        "ready_tolerance_loose": READY_TOL_LOOSE,
    }
    if audible is None:
        return out

    aud = np.asarray(audible).astype(bool)
    mask = np.asarray(knob_mask) > 0
    err = np.abs(np.asarray(pred_knobs) - np.asarray(true_knobs))

    sel_a = mask & aud[None, :]
    sel_i = mask & ~aud[None, :]
    out.update({
        "ready_rate_audible": round(readiness(pred_slots, true_slots, pred_knobs,
                                              true_knobs, knob_mask, READY_TOL, aud), 4),
        "ready_rate_audible_loose": round(readiness(pred_slots, true_slots, pred_knobs,
                                                    true_knobs, knob_mask,
                                                    READY_TOL_LOOSE, aud), 4),
        "knob_mae_audible": round(float(err[sel_a].mean()), 4) if sel_a.any() else None,
        "knob_mae_inaudible": round(float(err[sel_i].mean()), 4) if sel_i.any() else None,
        "audible_knobs": int(aud.sum()),
        "inaudible_knobs": int((~aud).sum()),
    })
    return out
