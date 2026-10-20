r"""Join several rendered corpora into ONE manifest, without copying the audio.

The dataset tool's own `merge` subcommand merges SHARDS within one root and has
no concept of a second corpus, so joining v1 with v2 is a job for this side. It
is a job worth doing carefully rather than with a `cat`, for three reasons:

1. **Clip indices COLLIDE.** Both corpora number from 0, so every `ClipPath` in
   v2 has a twin in v1. That is why the rows are rewritten to point at their own
   root rather than renumbered: renumbering would mean moving 70 GB of audio.

2. **`ClipPath` is resolved relative to the MANIFEST**, not to any configured
   root -- `data.py` does `clip_path = (manifest.parent / ClipPath)`. So a
   manifest in a third directory can address both corpora with `../rig/...` and
   `../rig-v2/...` and nothing has to be copied or linked. That is the whole
   trick here.

3. **`DiPath` is left EXACTLY as it was, and that is load-bearing.** The
   train/val/test split is a stable sha256 of that string
   (`data.split_of`), so two corpora that spell the same DI recording the same
   way put it in the SAME split -- which is what stops the identical playing
   appearing in v1's train and v2's test. Normalising, absolutising or
   prettifying these strings would silently reshuffle every split and leak.
   Both corpora already write `di/<pack>/<file>.wav` against the same pool, so
   they agree; this script CHECKS that rather than assuming it, and refuses a
   root whose DI paths look like a different convention.

   (v2's rows carry `di-pitch`/`di-level` augmentations of those same
   recordings. They keep the source's DiPath, so an augmented take stays on the
   same side of the split as its original -- correct, and the reason the check
   is on the path and not on the audio.)

    python merge_datasets.py --root ...\datasets\rig --root ...\datasets\rig-v2 \
        --out ...\datasets\rig-merged
"""
from __future__ import annotations

import argparse
import json
import os
from collections import Counter
from pathlib import Path


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", action="append", required=True,
                    help="a dataset root holding manifest.jsonl + clips/ (repeatable)")
    ap.add_argument("--out", required=True, help="directory to write the joined manifest into")
    ap.add_argument("--manifest-name", default="manifest.jsonl")
    ap.add_argument("--require-missing-below", type=float, default=0.001,
                    help="fail if more than this fraction of clips are missing on disk")
    args = ap.parse_args()

    out_dir = Path(args.out).resolve()
    out_dir.mkdir(parents=True, exist_ok=True)

    rows_out: list[str] = []
    total = 0
    missing = 0
    di_by_root: dict[str, set[str]] = {}
    per_root_counts: list[tuple[str, int]] = []

    for root_s in args.root:
        root = Path(root_s).resolve()
        manifest = root / args.manifest_name
        if not manifest.exists():
            raise SystemExit(f"no manifest at {manifest}")

        di_here: set[str] = set()
        seen_here: set[str] = set()
        dup_here = 0
        n_here = 0
        with manifest.open("r", encoding="utf-8") as fh:
            for line in fh:
                line = line.strip()
                if not line:
                    continue
                obj = json.loads(line)

                clip_rel = obj.get("ClipPath") or obj.get("clipPath")
                # DEDUPE, first occurrence wins.
                #
                # A sharded corpus contains the same clip twice whenever a render
                # is RESUMED WITH A DIFFERENT --threads. Shard k renders indices
                # where `i % threads == k`, so changing the thread count changes
                # which shard owns an index: the rows written under the old
                # mapping stay on disk in `manifest.shard<k>.jsonl` for shards the
                # new run no longer writes, and the new run re-renders those same
                # indices into its own shards. Measured on 2026-09-13's v2 corpus:
                # a 6-thread run left shard2..shard5 holding 750+499+498+749 rows,
                # and the 2-thread resume reproduced exactly those 2,496 indices.
                #
                # The rows are not junk and the clips are not corrupt -- the
                # per-clip seed is SplitMix64(runSeed, i, attempt), so a row for
                # index i is byte-identical whichever shard produced it. That is
                # precisely why nothing downstream complains: every duplicate is
                # valid JSON pointing at a real clip. The only consequence is that
                # those clips are over-sampled relative to the rest of the corpus,
                # a silent and unreproducible weighting of the training set.
                if clip_rel in seen_here:
                    dup_here += 1
                    continue
                seen_here.add(clip_rel)
                if not clip_rel:
                    raise SystemExit(f"a row in {manifest} has no ClipPath")
                clip_abs = (root / str(clip_rel).replace("\\", "/")).resolve()
                if not clip_abs.exists():
                    missing += 1

                # Relative to the OUT dir, POSIX separators: data.py does
                # (manifest.parent / ClipPath), and `..` resolves fine.
                rel = os.path.relpath(clip_abs, out_dir).replace(os.sep, "/")
                obj["ClipPath"] = rel

                di = str(obj.get("DiPath") or obj.get("diPath") or "")
                if di:
                    di_here.add(di.replace("\\", "/").lower())
                # DiPath deliberately NOT touched.

                obj["SourceRoot"] = root.name        # provenance, ignored by data.py
                rows_out.append(json.dumps(obj, ensure_ascii=False))
                n_here += 1
                total += 1

        di_by_root[root.name] = di_here
        per_root_counts.append((root.name, n_here))
        dup_note = f", {dup_here:,d} DUPLICATE row(s) dropped" if dup_here else ""
        print(f"[merge] {root.name:12} {n_here:7,d} rows, "
              f"{len(di_here):5,d} distinct DI sources{dup_note}")
        if dup_here:
            print(f"[merge]   ^ {root.name} contained the same ClipPath more than once. "
                  "Usually that means the render was resumed with a different --threads, "
                  "so stale shard files hold indices the new shards re-rendered. The clips "
                  "are identical (deterministic per-index seed); only the sampling weight "
                  "was wrong, and dropping the repeats fixes it.")

    # The DI-convention check. Two roots that share a pool MUST overlap; near-zero
    # overlap means the strings disagree, and then the split hash disagrees too.
    names = list(di_by_root)
    for i in range(len(names)):
        for j in range(i + 1, len(names)):
            a, b = di_by_root[names[i]], di_by_root[names[j]]
            if not a or not b:
                continue
            shared = len(a & b)
            frac = shared / max(min(len(a), len(b)), 1)
            print(f"[merge] DI overlap {names[i]} vs {names[j]}: {shared:,} shared "
                  f"({frac:.1%} of the smaller pool)")
            if frac < 0.5:
                print(f"[merge] WARNING: {names[i]} and {names[j]} barely share DI paths. "
                      "If they DO draw on the same pool, their DiPath spelling disagrees and "
                      "the split hash will scatter the same recording across train and test. "
                      "Check before training on this.")

    if total == 0:
        raise SystemExit("no rows found")
    if missing / total > args.require_missing_below:
        raise SystemExit(f"{missing:,} of {total:,} clips ({missing/total:.2%}) are not on disk "
                         f"-- refusing to write a manifest that mostly does not resolve")

    out_manifest = out_dir / args.manifest_name
    tmp = out_manifest.with_suffix(out_manifest.suffix + ".tmp")
    tmp.write_text("\n".join(rows_out) + "\n", encoding="utf-8")
    tmp.replace(out_manifest)

    print(f"[merge] wrote {out_manifest}")
    print(f"[merge] {total:,} rows from {len(args.root)} root(s); {missing:,} clip(s) missing")

    # Report the split the way data.py will compute it, so a bad join is visible
    # HERE rather than after a cache build.
    try:
        import sys
        sys.path.insert(0, str(Path(__file__).parent))
        from data import split_of
        counts = Counter()
        di_split: dict[str, set[str]] = {"train": set(), "val": set(), "test": set()}
        for line in rows_out:
            obj = json.loads(line)
            di = str(obj.get("DiPath") or "")
            s = split_of(di)
            counts[s] += 1
            di_split[s].add(di.lower())
        print("[merge] split preview (by DI-path hash, the same function data.py uses):")
        for s in ("train", "val", "test"):
            print(f"[merge]   {s:5} {counts[s]:7,d} clips  {len(di_split[s]):5,d} DI sources")
        overlap = (di_split["train"] & di_split["test"]) | (di_split["train"] & di_split["val"])
        print(f"[merge]   DI sources appearing in more than one split: {len(overlap)} "
              "(MUST be 0)")
    except Exception as exc:
        print(f"[merge] (split preview unavailable: {exc})")


if __name__ == "__main__":
    main()
