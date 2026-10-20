"""Dataset over the data lane's `manifest.jsonl` + clips.

Four things here earn their place:

1. **The split is BY DI SOURCE, never by clip.** Every clip is one DI recording
   rendered through one rig, so a random per-clip split puts the *same playing*
   in train and val and the val number then measures memorisation of a riff.
   Grouping by `DiPath` is what makes "unseen playing" true. The split is a
   stable hash of the DI path, so adding clips to a growing dataset does not
   reshuffle which DI a previously-trained model has seen.

2. **The audio cache is a memmap of raw PCM, not of mel.** The mel lives INSIDE
   the exported ONNX graph (see `model.LogMel`) so C# feeds raw samples and
   there is no second mel implementation to drift; caching mel instead would
   pin the cache to one front-end's mel config and silently invalidate on every
   front-end change. Raw fp16 is 480 KB/clip at 5 s/48 kHz -- fine to ~100k
   clips, and `--cache none` streams from disk past that.

3. **Labels come from `vocab.py`, which reads the C#-exported sidecar.** Nothing
   here knows a param range.

4. **THE DATASET OBJECT MUST BE CHEAP TO PICKLE (the Windows `spawn` rule).**
   Windows has no `fork`, so every `DataLoader` worker is a fresh process and
   torch pickles the whole `Dataset` down a pipe to each one. The first version
   of this file kept the fp16 PCM cache on the instance as an `np.memmap` --
   which is an `ndarray` SUBCLASS, so pickling it serialises all 19.4 GB of
   backing store, not the filename -- and kept 40,413 rows of expanded labels
   (573 knobs + 573 mask floats each) beside it. The pipe write died with
   `OSError: [Errno 22] Invalid argument` in the parent and
   `UnpicklingError: pickle data was truncated` in the child, and the run was
   dead before its first batch.

   So: **everything bulk lives on disk and is opened LAZILY, per worker.** The
   instance carries paths, shapes and the vocab, nothing else, and
   `__getstate__` drops the opened handles. `python data.py --data <dir>`
   asserts the pickled dataset is under 1 MB. Never put an array, a memmap, an
   open file or a per-row list back on `self` -- that reads fine on Linux
   (`fork` shares it) and is fatal here.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import pickle
import sys
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np
import torch
from torch.utils.data import Dataset

from vocab import DEFAULT_VOCAB_PATH, Vocab, load_vocab

SPLITS = ("train", "val", "test")

#: Label arrays written beside the audio cache, one memmapped `.npy` each.
LABEL_FIELDS = (
    ("families", np.float32),
    ("slots", np.int16),
    ("knobs", np.float32),
    ("knob_mask", np.uint8),
)

LABEL_CACHE_VERSION = 1


def split_of(di_path: str, val_frac: float = 0.1, test_frac: float = 0.1) -> str:
    """Stable per-DI-source split. Same DI -> same split, forever."""
    h = hashlib.sha256(di_path.replace("\\", "/").lower().encode("utf-8")).digest()
    # 24 bits is plenty and keeps the arithmetic exact.
    u = int.from_bytes(h[:3], "big") / float(1 << 24)
    if u < test_frac:
        return "test"
    if u < test_frac + val_frac:
        return "val"
    return "train"


@dataclass
class Row:
    """One manifest line, still UNEXPANDED.

    `modules` is the raw manifest rig, not the 1,146-float label expansion:
    holding that expansion for every row cost ~1.5 GB of the parent's RAM and
    was half of what made the dataset unpicklable. It is encoded straight into
    the on-disk label cache and then dropped.
    """

    clip_path: Path
    di_path: str
    modules: list = field(default_factory=list)
    seconds: float = 0.0
    sample_rate: int = 0


def _get(d: dict, *names, default=None):
    for n in names:
        if n in d:
            return d[n]
    return default


def read_manifest(manifest: Path, vocab: Vocab, limit: int | None = None) -> list[Row]:
    """Parses manifest.jsonl. Paths in it are relative to the manifest."""
    root = manifest.parent
    rows: list[Row] = []
    bad = 0
    with manifest.open("r", encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                obj = json.loads(line)
            except json.JSONDecodeError:
                bad += 1
                continue
            clip = _get(obj, "ClipPath", "clipPath")
            di = _get(obj, "DiPath", "diPath", default="")
            rig = _get(obj, "Rig", "rig", default={}) or {}
            modules = _get(rig, "Modules", "modules", default=[]) or []
            if not clip:
                bad += 1
                continue
            rows.append(
                Row(
                    clip_path=(root / clip).resolve(),
                    di_path=str(di),
                    modules=modules,
                    seconds=float(_get(obj, "Seconds", "seconds", default=vocab.clip_seconds)),
                    sample_rate=int(_get(obj, "SampleRate", "sampleRate",
                                         default=vocab.sample_rate)),
                )
            )
            if limit and len(rows) >= limit:
                break
    if bad:
        print(f"[data] skipped {bad} unparseable manifest line(s)", file=sys.stderr)
    return rows


def load_clip(path: Path, want_samples: int, want_rate: int) -> np.ndarray:
    """One clip as mono float32, resampled by rate check only, padded/truncated.

    The renderer writes at the model's rate already; a mismatch is a dataset
    bug, so it is reported rather than silently resampled -- a resample here
    would be a second, invisible signal path the exported ONNX does not have.
    """
    import soundfile as sf

    audio, rate = sf.read(str(path), dtype="float32", always_2d=True)
    if rate != want_rate:
        raise ValueError(f"{path}: {rate} Hz, expected {want_rate} Hz")
    mono = audio.mean(axis=1) if audio.shape[1] > 1 else audio[:, 0]
    if mono.shape[0] < want_samples:
        mono = np.pad(mono, (0, want_samples - mono.shape[0]))
    return np.ascontiguousarray(mono[:want_samples])


def _fingerprint(rows: list[Row]) -> str:
    """Cheap identity of a split's row SET AND ORDER.

    The cache is indexed positionally, so a manifest that grew, shrank or was
    reordered invalidates it even at an unchanged row count.
    """
    h = hashlib.sha256()
    for row in rows:
        h.update(str(row.clip_path).lower().encode("utf-8", "replace"))
        h.update(b"\0")
    return h.hexdigest()[:32]


class RigClips(Dataset):
    """Clips + labels for one split.

    Holds ONLY paths, shapes and the vocab -- see the module docstring's rule 4.
    Every bulk handle is opened on first use, in whichever process asks for it.
    """

    def __init__(
        self,
        index_dir: Path,
        split: str,
        count: int,
        vocab: Vocab,
        audio_cache: bool,
        augment: bool = False,
    ) -> None:
        self.index_dir = Path(index_dir)
        self.split = split
        self.count = int(count)
        self.vocab = vocab
        self.n = vocab.n_samples
        self.augment = augment
        self.audio_path: Path | None = (
            self.index_dir / f"{split}.f16" if audio_cache else None
        )
        # Opened lazily, per process. NEVER pickled -- see __getstate__.
        self._audio_mm: np.memmap | None = None
        self._labels: dict | None = None
        self._paths: list[str] | None = None
        self._rng: np.random.Generator | None = None

    # -- pickling ---------------------------------------------------------
    def __getstate__(self) -> dict:
        state = self.__dict__.copy()
        state["_audio_mm"] = None
        state["_labels"] = None
        state["_paths"] = None
        state["_rng"] = None
        return state

    def __setstate__(self, state: dict) -> None:
        self.__dict__.update(state)

    # -- lazy handles -----------------------------------------------------
    def audio(self) -> np.memmap | None:
        if self.audio_path is None:
            return None
        if self._audio_mm is None:
            self._audio_mm = np.memmap(
                self.audio_path, dtype=np.float16, mode="r", shape=(self.count, self.n)
            )
        return self._audio_mm

    def labels(self) -> dict:
        if self._labels is None:
            self._labels = {
                name: np.load(self.index_dir / f"{self.split}.{name}.npy", mmap_mode="r")
                for name, _ in LABEL_FIELDS
            }
        return self._labels

    def paths(self) -> list[str]:
        if self._paths is None:
            text = (self.index_dir / f"{self.split}.paths.txt").read_text(encoding="utf-8")
            self._paths = text.splitlines()
        return self._paths

    def rng(self) -> np.random.Generator:
        # torch gives every worker (and every epoch) a distinct base seed. Using
        # the process-global np.random instead would hand all four workers the
        # SAME augmentation stream -- the classic silent Windows-spawn
        # correlation bug, which no assertion here would ever catch.
        if self._rng is None:
            self._rng = np.random.default_rng(torch.initial_seed() % (2**32))
        return self._rng

    # -- Dataset ----------------------------------------------------------
    def __len__(self) -> int:
        return self.count

    def __getitem__(self, i: int):
        # Every array below is np.array(..., copy=True) on purpose: a row of a
        # read-only memmap is a read-only VIEW, and torch.from_numpy on one
        # yields a tensor whose writes are undefined behaviour (pin_memory and
        # the augmentation both write).
        cache = self.audio()
        if cache is not None:
            audio = np.array(cache[i], dtype=np.float32)
        else:
            audio = load_clip(Path(self.paths()[i]), self.n, self.vocab.sample_rate)

        if self.augment:
            # Deliberately tiny: gain and polarity only. Anything that changes
            # TIMBRE (EQ tilt, room, bleed) changes the label's meaning, so it
            # belongs in the renderer where the label is drawn, not here.
            rng = self.rng()
            audio = audio * float(rng.uniform(0.5, 1.4))
            if rng.random() < 0.5:
                audio = -audio
            np.clip(audio, -1.0, 1.0, out=audio)

        lab = self.labels()
        return (
            torch.from_numpy(np.ascontiguousarray(audio, dtype=np.float32)),
            torch.from_numpy(np.array(lab["families"][i], dtype=np.float32)),
            torch.from_numpy(np.array(lab["slots"][i], dtype=np.int64)),
            torch.from_numpy(np.array(lab["knobs"][i], dtype=np.float32)),
            torch.from_numpy(np.array(lab["knob_mask"][i], dtype=np.float32)),
        )


def _build_label_cache(index_dir: Path, split: str, rows: list[Row], vocab: Vocab) -> int:
    """Writes (or adopts) the on-disk label arrays. Returns the overflow count."""
    index_dir.mkdir(parents=True, exist_ok=True)
    meta_path = index_dir / f"{split}.labels.json"
    paths_path = index_dir / f"{split}.paths.txt"
    want = {
        "version": LABEL_CACHE_VERSION,
        "count": len(rows),
        "families": vocab.n_families,
        "slots": vocab.slots,
        "knobs": vocab.knob_dim,
        "fingerprint": _fingerprint(rows),
    }
    if meta_path.exists() and paths_path.exists():
        try:
            if json.loads(meta_path.read_text(encoding="utf-8")) == want and all(
                (index_dir / f"{split}.{name}.npy").exists() for name, _ in LABEL_FIELDS
            ):
                return 0
        except (json.JSONDecodeError, OSError):
            pass

    shapes = {
        "families": (len(rows), vocab.n_families),
        "slots": (len(rows), vocab.slots),
        "knobs": (len(rows), vocab.knob_dim),
        "knob_mask": (len(rows), vocab.knob_dim),
    }
    arrays = {
        name: np.lib.format.open_memmap(
            index_dir / f"{split}.{name}.npy", mode="w+", dtype=dt, shape=shapes[name]
        )
        for name, dt in LABEL_FIELDS
    }
    overflow = 0
    for i, row in enumerate(rows):
        enc = vocab.encode_rig(row.modules)
        overflow += enc.pop("overflow", 0)
        for name, _ in LABEL_FIELDS:
            arrays[name][i] = enc[name]
    for arr in arrays.values():
        arr.flush()
    del arrays
    paths_path.write_text("\n".join(str(r.clip_path) for r in rows), encoding="utf-8")
    meta_path.write_text(json.dumps(want), encoding="utf-8")
    print(f"[data] {split}: wrote label cache for {len(rows)} rows", flush=True)
    return overflow


def _build_audio_cache(index_dir: Path, split: str, rows: list[Row], vocab: Vocab) -> None:
    """Builds (or adopts) the fp16 PCM memmap.

    The on-disk FORMAT and its `.meta.json` are unchanged from the first
    version, deliberately: a 19.4 GB train cache costs ~25 min to re-render and
    there is no reason to invalidate it while fixing a pickling bug.
    """
    index_dir.mkdir(parents=True, exist_ok=True)
    cache_path = index_dir / f"{split}.f16"
    meta_path = cache_path.with_suffix(".meta.json")
    n = vocab.n_samples
    want = {"count": len(rows), "samples": n, "dtype": "float16"}
    if cache_path.exists() and meta_path.exists():
        try:
            if json.loads(meta_path.read_text(encoding="utf-8")) == want:
                return
        except (json.JSONDecodeError, OSError):
            pass

    mm = np.memmap(cache_path, dtype=np.float16, mode="w+", shape=(len(rows), n))
    for i, row in enumerate(rows):
        mm[i] = load_clip(row.clip_path, n, vocab.sample_rate).astype(np.float16)
        if (i + 1) % 500 == 0:
            print(f"[data] cached {i + 1}/{len(rows)}", flush=True)
    mm.flush()
    del mm
    meta_path.write_text(json.dumps(want), encoding="utf-8")


def build_splits(
    data_dir: Path,
    vocab: Vocab,
    limit: int | None = None,
    cache_dir: Path | None = None,
    audio_cache: bool = True,
    val_frac: float = 0.1,
    test_frac: float = 0.1,
) -> dict[str, RigClips]:
    """Reads the manifest, builds the on-disk index, returns one dataset a split.

    `cache_dir` holds BOTH the fp16 PCM cache and the label arrays; it is where
    the dataset lives as far as a DataLoader worker is concerned. It is no
    longer optional: `audio_cache=False` (`--cache none`) still needs the label
    arrays and the path list on disk, because holding them on the instance is
    what made the dataset unpicklable.
    """
    manifest = data_dir / "manifest.jsonl"
    rows = read_manifest(manifest, vocab, limit=limit)
    if not rows:
        raise SystemExit(f"{manifest} produced no usable rows")

    index_dir = Path(cache_dir) if cache_dir is not None else (data_dir / ".rig-index")

    by_split: dict[str, list[Row]] = {s: [] for s in SPLITS}
    for row in rows:
        by_split[split_of(row.di_path, val_frac, test_frac)].append(row)

    di_sources = {s: len({r.di_path for r in rs}) for s, rs in by_split.items()}
    print(
        "[data] "
        + "  ".join(f"{s}={len(by_split[s])} clips/{di_sources[s]} DI" for s in SPLITS),
        flush=True,
    )
    if not by_split["train"]:
        raise SystemExit("the DI-source split left the train set empty")
    for s in ("val", "test"):
        if not by_split[s]:
            print(
                f"[data] WARNING: {s} split is EMPTY. With very few distinct DI sources the "
                "hash cannot produce a held-out group -- any metric from this run measures "
                "memorisation, not generalisation.",
                file=sys.stderr,
            )

    out: dict[str, RigClips] = {}
    overflow = 0
    for s in SPLITS:
        rs = by_split[s]
        if not rs:
            out[s] = RigClips(index_dir, s, 0, vocab, audio_cache=False, augment=False)
            continue
        overflow += _build_label_cache(index_dir, s, rs, vocab)
        if audio_cache:
            _build_audio_cache(index_dir, s, rs, vocab)
        out[s] = RigClips(index_dir, s, len(rs), vocab,
                          audio_cache=audio_cache, augment=(s == "train"))
        # The raw rig is on disk now; drop the parent's copy.
        for row in rs:
            row.modules = []
    if overflow:
        print(
            f"[data] {overflow} module(s) past slot {vocab.slots} were dropped from slot labels "
            "(the sampler and the sidecar disagree on chain length)",
            file=sys.stderr,
        )
    return out


def main() -> None:
    """`python data.py --data <dir>` -- the Windows-spawn self-test.

    Builds the splits, pickles each dataset exactly as `DataLoader` would and
    fails if one is bigger than `--max-pickle-bytes`. Run it before any long
    training launch: a dataset that got fat again dies AFTER the cache build,
    which on the full set is 25 wasted minutes.
    """
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", required=True)
    ap.add_argument("--cache-dir", default=None)
    ap.add_argument("--vocab", default=str(DEFAULT_VOCAB_PATH))
    ap.add_argument("--limit", type=int, default=None)
    ap.add_argument("--cache", default="auto", choices=["auto", "none"])
    ap.add_argument("--max-pickle-bytes", type=int, default=1 << 20)
    args = ap.parse_args()

    vocab = load_vocab(args.vocab)
    splits = build_splits(
        Path(args.data), vocab, limit=args.limit,
        cache_dir=Path(args.cache_dir) if args.cache_dir else None,
        audio_cache=args.cache != "none",
    )
    worst = 0
    for name, ds in splits.items():
        size = len(pickle.dumps(ds, protocol=pickle.HIGHEST_PROTOCOL))
        worst = max(worst, size)
        print(f"[self-test] {name:5} {len(ds):6d} clips  pickles to {size:,} bytes")
        if len(ds):
            shapes = " ".join(str(tuple(t.shape)) for t in ds[0])
            print(f"[self-test] {name:5} item shapes {shapes}")
    if worst > args.max_pickle_bytes:
        raise SystemExit(
            f"FAIL: a dataset pickles to {worst:,} bytes, over the "
            f"{args.max_pickle_bytes:,} budget. Something bulk is on the instance again -- "
            "on Windows every DataLoader worker pays that through a pipe, and past ~2 GB "
            "the pipe write fails with OSError [Errno 22]."
        )
    print(f"[self-test] OK: worst pickle {worst:,} bytes < {args.max_pickle_bytes:,}")


if __name__ == "__main__":
    main()
