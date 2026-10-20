"""Does it SOUND close? One table, rows = models, columns = the sound distance.

    python render_eval.py --root <dataset root> --runs runs/a runs/b --limit 200

Every number the trainer reports is a LABEL metric -- family F1, exact chain,
knob MAE. None of them answers the owner's actual question, which is whether the
predicted rig SOUNDS like the true one. This script drives the C# subcommand
that does:

    dotnet run --project tools/rig-dataset -- eval-render \
        --predictions <run>/predictions-test.jsonl --root <dataset root>

...once per run directory, then prints one table so a "sound distance" column
can go straight into the status table beside famF1 and knobMAE.

READ THE COLUMNS THIS WAY:

* `d` is a raw `ToneSimilarity` (`tonefx-v2`) distance and has NO absolute
  scale. Compare it between MODELS on the same rows, never against a threshold.
* `d/floor` is that distance divided by the target's own self-distance -- what
  "the same tone" costs on that material. It is the column that is comparable
  ACROSS rows, and the one to sort models by.
* `beat-gen` is the fraction of rows where the prediction is closer than a fixed
  overdrive -> amp -> cab at catalogue defaults. A model that cannot beat that is
  not contributing.
* THE METRIC IS THE CEILING: `tonefx-v2` measures 1.56x content invariance, so
  two models whose medians differ by a few percent are the same model as far as
  this table can tell.
"""

from __future__ import annotations

import argparse
import json
import math
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
TOOL_PROJECT = REPO / "tools" / "rig-dataset" / "DotCharter.Tools.RigDataset"
PREDICTIONS_NAME = "predictions-test.jsonl"
SUMMARY_NAME = "render-eval.summary.json"


def find_tool(explicit: str | None) -> list[str]:
    """The command that runs `eval-render`.

    A built exe is preferred over `dotnet run` because `dotnet run` rebuilds,
    and a rebuild while two trainings hold the box is exactly the CPU this
    script is supposed not to take.
    """
    if explicit:
        return [explicit]
    for config in ("Release", "Debug"):
        exe = TOOL_PROJECT / "bin" / config / "net10.0" / "win-x64" / "DotCharter.Tools.RigDataset.exe"
        if exe.exists():
            return [str(exe)]
    return ["dotnet", "run", "--project", str(TOOL_PROJECT), "--"]


def num(value) -> float:
    """A summary number, tolerating the `"NaN"` the C# serializer writes.

    `System.Text.Json` emits a named floating-point literal as a STRING, so a
    plain `float(value)` raises on exactly the rows that had no floor -- which
    are the rows a reader most needs to see counted rather than to crash on.
    """
    if value is None:
        return math.nan
    if isinstance(value, str):
        try:
            return float(value)
        except ValueError:
            return math.nan
    return float(value)


def run_one(run_dir: Path, args, tool: list[str]) -> dict | None:
    """Scores one run directory; returns its summary dict, or None."""
    predictions = run_dir / PREDICTIONS_NAME
    # The report goes BESIDE the predictions by default and somewhere else with
    # --out-dir, which is not a nicety: a run directory can belong to a training
    # job that is still writing to it, and this script must never be the reason
    # something appears in one.
    out_dir = (args.out_dir / run_dir.name) if args.out_dir else run_dir
    summary = out_dir / SUMMARY_NAME
    if not predictions.exists():
        print(f"  {run_dir.name}: no {PREDICTIONS_NAME}; skipped.", file=sys.stderr)
        return None

    out_dir.mkdir(parents=True, exist_ok=True)

    if args.reuse and summary.exists():
        print(f"  {run_dir.name}: reusing {summary.name}.", file=sys.stderr)
    else:
        cmd = tool + [
            "eval-render",
            "--predictions", str(predictions),
            "--root", str(args.root),
            "--report", str(out_dir / "render-eval.jsonl"),
            "--summary", str(summary),
            "--threads", str(args.threads),
            "--seed", str(args.seed),
            "--target", args.target,
            "--progress-every", str(args.progress_every),
        ]
        if args.limit:
            cmd += ["--limit", str(args.limit)]
        if args.top_confident:
            cmd += ["--top-confident", str(args.top_confident)]
        if args.refine:
            cmd += ["--refine", "--refine-renders", str(args.refine_renders)]
        print(f"  {run_dir.name}: {' '.join(cmd)}", file=sys.stderr)
        result = subprocess.run(cmd, cwd=str(REPO), check=False)
        if result.returncode != 0:
            print(f"  {run_dir.name}: eval-render exited {result.returncode}; skipped.", file=sys.stderr)
            return None

    with summary.open(encoding="utf-8") as handle:
        return json.load(handle)


COLUMNS = [
    ("model", 34),
    ("n", 5),
    ("med d", 8),
    ("med d/floor", 12),
    ("<1.5x", 7),
    ("<2x", 7),
    ("<3x", 7),
    ("beat-gen", 9),
    ("med gen", 8),
    ("top-N d/fl", 11),
    ("top-N beat", 11),
    ("skip", 5),
    ("s/row", 7),
]


def cell(value: float, kind: str) -> str:
    """Formats one number; `n/a` rather than `nan`, because `nan` reads as a bug."""
    if not math.isfinite(value):
        return "n/a"
    return f"{value * 100:.1f}%" if kind == "pct" else f"{value:.4f}"


def row_for(name: str, summary: dict) -> list[str]:
    """One table row out of one summary."""
    everything = summary.get("All", {})
    top = summary.get("TopConfident", {})
    return [
        name,
        str(summary.get("Scored", 0)),
        cell(num(everything.get("MedianDistance")), "num"),
        cell(num(everything.get("MedianDistanceOverFloor")), "num"),
        cell(num(everything.get("FractionUnder1p5")), "pct"),
        cell(num(everything.get("FractionUnder2")), "pct"),
        cell(num(everything.get("FractionUnder3")), "pct"),
        cell(num(everything.get("FractionBeatsGeneric")), "pct"),
        cell(num(everything.get("MedianGenericDistance")), "num"),
        cell(num(top.get("MedianDistanceOverFloor")), "num"),
        cell(num(top.get("FractionBeatsGeneric")), "pct"),
        str(summary.get("Skipped", 0)),
        cell(num(summary.get("SecondsPerRow")), "num"),
    ]


def print_table(rows: list[list[str]]) -> None:
    """The one table this script exists to print."""
    header = " ".join(name.ljust(width) for name, width in COLUMNS)
    print(header)
    print("-" * len(header))
    for row in rows:
        print(" ".join(str(v).ljust(width) for v, (_, width) in zip(row, COLUMNS)))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--root", required=True, type=Path,
                        help="The dataset root the predictions' clip/di paths are relative to.")
    parser.add_argument("--runs", required=True, nargs="+", type=Path,
                        help=f"Run directories, each holding a {PREDICTIONS_NAME}.")
    parser.add_argument("--limit", type=int, default=200,
                        help="Rows per model; 0 scores every row. 200 at ~2.7 s/row is ~9 min per model.")
    parser.add_argument("--seed", type=int, default=20260914,
                        help="Subsample seed. THE SAME SEED FOR EVERY MODEL or the table compares "
                             "different rows, which at 1.56x content invariance is most of the signal.")
    parser.add_argument("--threads", type=int, default=2,
                        help="Rows in flight. Renders serialise on one engine; keep this small when the "
                             "box is training.")
    parser.add_argument("--target", choices=("clip", "render"), default="clip",
                        help="clip: score against the wet clip on disk (the product question). "
                             "render: re-render the truth rig as the target (the harness's own check).")
    parser.add_argument("--top-confident", type=int, default=0,
                        help="Rows in the top-N-confident block; 0 means a tenth.")
    parser.add_argument("--refine", action="store_true",
                        help="Also run the matcher's refinement from the prediction. SLOW; opt-in.")
    parser.add_argument("--refine-renders", type=int, default=8, help="Render budget per row under --refine.")
    parser.add_argument("--progress-every", type=int, default=50, help="Progress line every N rows.")
    parser.add_argument("--reuse", action="store_true",
                        help=f"Reuse an existing {SUMMARY_NAME} instead of re-scoring.")
    parser.add_argument("--out-dir", type=Path, default=None,
                        help="Write each run's report under <out-dir>/<run name>/ instead of into the run "
                             "directory itself. Use it when a training job owns those directories.")
    parser.add_argument("--tool", default=None, help="Path to DotCharter.Tools.RigDataset.exe.")
    args = parser.parse_args()

    tool = find_tool(args.tool)
    rows, notes, summaries = [], [], []
    for run_dir in args.runs:
        summary = run_one(run_dir, args, tool)
        if summary is None:
            continue
        rows.append(row_for(run_dir.name, summary))
        notes.append(summary.get("Notes", ""))
        summaries.append(summary)

    if not rows:
        print("No run directory produced a summary.", file=sys.stderr)
        raise SystemExit(1)

    print()
    print_table(rows)
    print()
    # From the SUMMARY and not from argv: with --reuse the flags on this command
    # line say nothing about how the cached report was produced, and a footer that
    # claims `target=clip` over a `--target render` run is a caption that lies.
    targets = sorted({s.get("Target", "?") for s in summaries})
    windows = sorted({s.get("SelfWindowSeconds", "?") for s in summaries})
    print(f"target={'/'.join(targets)} self-window={'/'.join(str(w) for w in windows)} s "
          f"rows={'/'.join(str(s.get('Scored', 0)) for s in summaries)} seed={args.seed}")
    print("Lower is better in every distance column. `d/floor` is the comparable one; `d` has no "
          "absolute scale.")
    print("THE METRIC IS THE CEILING: tonefx-v2 measures 1.56x content invariance, so medians within a "
          "few percent of each other are the same result.")
    if notes:
        print()
        print(notes[0])


if __name__ == "__main__":
    main()
