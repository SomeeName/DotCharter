"""Waits for the data lane's renders, then trains on whatever has landed.

The model lane finishes before the data lane does, so this is the hand-off: it
polls `<data>/manifest.jsonl` until it has at least `--min-clips` rows, trains
the requested front-end on that snapshot, evaluates, exports, and then -- if the
dataset has grown by `--regrow-factor` -- does it again. Each round writes to
its own `runs/<tag>-<n>clips/` so no round can clobber another's checkpoints.

    python watch_and_train.py --data <dir> --frontend cnn --min-clips 5000

Stop it by deleting the stop file it prints, or by killing the process.
"""

from __future__ import annotations

import argparse
import subprocess
import sys
import time
from pathlib import Path


def count_rows(manifest: Path) -> int:
    if not manifest.exists():
        return 0
    try:
        with manifest.open("r", encoding="utf-8") as fh:
            return sum(1 for line in fh if line.strip())
    except OSError:
        return 0


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", required=True)
    ap.add_argument("--frontend", default="cnn", choices=["cnn", "clap", "ast"])
    ap.add_argument("--min-clips", type=int, default=5000)
    ap.add_argument("--regrow-factor", type=float, default=2.0)
    ap.add_argument("--epochs", type=int, default=60)
    ap.add_argument("--batch", type=int, default=32)
    ap.add_argument("--minutes", type=float, default=90)
    ap.add_argument("--poll-seconds", type=int, default=120)
    ap.add_argument("--rounds", type=int, default=4)
    ap.add_argument("--runs-dir", default="runs")
    ap.add_argument("--stop-file", default="runs/STOP")
    args = ap.parse_args()

    here = Path(__file__).parent
    manifest = Path(args.data) / "manifest.jsonl"
    stop = Path(args.stop_file)
    stop.parent.mkdir(parents=True, exist_ok=True)
    print(f"[watch] polling {manifest} every {args.poll_seconds}s for >= {args.min_clips} clips")
    print(f"[watch] stop cleanly with:  New-Item -ItemType File '{stop.resolve()}'", flush=True)

    trained_at = 0
    for round_ix in range(args.rounds):
        while True:
            if stop.exists():
                print("[watch] stop file present; exiting", flush=True)
                return
            n = count_rows(manifest)
            need = max(args.min_clips, int(trained_at * args.regrow_factor))
            if n >= need:
                break
            print(f"[watch] {n} clips, waiting for {need}", flush=True)
            time.sleep(args.poll_seconds)

        n = count_rows(manifest)
        tag = f"{args.frontend}-{n}clips"
        out = Path(args.runs_dir) / tag
        print(f"[watch] round {round_ix + 1}: training {args.frontend} on {n} clips -> {out}",
              flush=True)

        rc = subprocess.call([
            sys.executable, str(here / "train.py"),
            "--data", args.data, "--out", str(out), "--frontend", args.frontend,
            "--epochs", str(args.epochs), "--batch", str(args.batch),
            "--minutes", str(args.minutes), "--workers", "4",
        ], cwd=here)
        if rc != 0:
            print(f"[watch] training exited {rc}; stopping", flush=True)
            return

        subprocess.call([
            sys.executable, str(here / "eval.py"),
            "--checkpoint", str(out / "best.pt"), "--data", args.data,
            "--split", "test", "--json-out", str(out / "eval-test.json"),
        ], cwd=here)

        # Only a run on REAL renders may overwrite the shipped model; a crude
        # smoke checkpoint replacing a good one is a silent regression.
        first = next(iter(manifest.open(encoding="utf-8")), "")
        if "synthetic-crude" in first:
            print("[watch] crude smoke set -- NOT exporting over the shipped model", flush=True)
        else:
            import os
            target = Path(os.environ["LOCALAPPDATA"]) / "DotCharter" / "ai-match" / "rig-estimator"
            subprocess.call([
                sys.executable, str(here / "export_onnx.py"),
                "--checkpoint", str(out / "best.pt"), "--out", str(target),
                "--model-id", f"rig-estimator-{tag}",
            ], cwd=here)

        trained_at = n

    print("[watch] all rounds done", flush=True)


if __name__ == "__main__":
    main()
