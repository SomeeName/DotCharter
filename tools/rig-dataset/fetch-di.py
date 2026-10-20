#!/usr/bin/env python3
"""Fetch, licence-check and cut the DI pool for the audio->rig training corpus.

    python fetch-di.py --root ..\\DotCharter-local\\datasets\\rig

What it does, per set:
  1. downloads the Zenodo files it needs (resumable; skips complete ones),
  2. reads the record's licence FROM THE ZENODO API and refuses to prepare a set
     whose licence is not on the allow-list -- a licence in a table in a document
     is a licence nobody re-checked,
  3. extracts only the DI/clean members,
  4. converts to mono 48 kHz WAV, strips silence, cuts 5 s clips into
     <root>/di/<set>/, dropping anything short of a full clip,
  5. writes <root>/LICENSES.md naming every source, its licence and its URL.

Needs: python 3.11+, ffmpeg on PATH. No third-party packages -- this box has no
numpy and no soundfile, and a dataset script that needs a pip install is a
dataset script that does not get re-run.

Sets, and why each is in or out (re-read the records before changing this):

  EGFxSet        CC BY 4.0    IN  -- only Clean.zip. The other 13 zips are the
                                     same notes through REAL PEDALS: that is wet
                                     audio with someone else's labels, and this
                                     corpus renders its own.
  Guitar-TECHS   CC BY 4.0    IN  -- only audio/directinput/*.wav. The micamp
                                     leg is an amp someone else chose.
  EGDB           NOT STATED   OUT -- Google Drive, no licence on the record or
                                     the site. The study says ask the authors
                                     first; a model that ships cannot carry
                                     audio whose terms nobody has read. Revisit
                                     if they answer.
  IDMT-SMT-*     CC BY-NC-ND  OUT -- NC bars a commercial product and ND bars
                                     redistributing a render of it. Fine for the
                                     in-house eval harness (GND_IDMT_ROOT), not
                                     for this.
  GuitarSet      CC BY 4.0    OPT-IN (--only guitarset) -- acoustic through a hex
                                     pickup. The DEBLEEDED hex leg IS a direct
                                     pickup signal and the licence is clean, but
                                     the instrument is wrong: poured unlabelled
                                     into the electric pool it teaches the model
                                     that a piezo-ish transient is what a DI
                                     sounds like. Low weight, or its own
                                     ablation.
  GAPS           see record   OUT -- classical guitar, not DI.
  tonetwist      MIT (repo)   OUT for now -- the AUDIO is per-device Zenodo
                                     records with their own licences, and ~40 %
                                     of its 43 min of DRY material is IDMT or
                                     YouTube-sourced and must be excluded. ~25
                                     min of usable dry after that, for a
                                     per-record licence review. Not worth it
                                     before the owner's own DI exists.
  owner          ours         IN  -- <root>/di/owner/, see the README.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import urllib.request
import zipfile
from pathlib import Path

CLIP_SECONDS = 5
RATE = 48_000
ALLOWED_LICENCES = {"cc-by-4.0", "cc-by-3.0", "cc0-1.0", "mit"}

SETS = {
    "egfxset": {
        "zenodo": "7044411",
        "files": ["Clean.zip", "egfxset_metadata.csv"],
        "member": lambda n: n.lower().endswith(".wav") and "__macosx" not in n.lower(),
        "note": "Clean tones only (690 files, 58 min). The 11.5 h of real-pedal wet "
                "audio in the other 13 zips is deliberately NOT fetched. CONCATENATED "
                "before cutting: every source is ONE 5 s note whose tail is silence, so "
                "trimming each file separately yielded 9 clips out of 690 sources.",
        "concat": True,
    },
    "guitar-techs": {
        "zenodo": "14963133",
        "files": [
            "P1_singlenotes.zip", "P1_chords.zip", "P1_scales.zip", "P1_techniques.zip",
            "P2_singlenotes.zip", "P2_chords.zip", "P2_scales.zip", "P2_techniques.zip",
            "P3_music.zip",
        ],
        "member": lambda n: "/directinput/" in n.replace("\\", "/").lower()
                            and n.lower().endswith(".wav")
                            and "__macosx" not in n.lower(),
        "note": "DI leg only (audio/directinput/*.wav). The micamp leg is someone "
                "else's amp and cabinet.",
    },
    # ---------------------------------------------------------------- optional
    # OPT-IN, and that is the whole point of the flag. GuitarSet's licence is clean
    # (CC BY 4.0) and its hex-pickup leg is a genuine direct pickup signal, so it is
    # the only remaining source the study names that we may legally train on -- but
    # it is an ACOUSTIC guitar, and 3 h of acoustic silently poured into a 5.9 h
    # electric pool would move the pool's centre a long way for a model whose entire
    # job is electric tone. It is fetched only when named, and it belongs at a low
    # weight or in its own ablation.
    "guitarset": {
        "zenodo": "3371780",
        "files": ["audio_hex-pickup_debleeded.zip"],
        "member": lambda n: n.lower().endswith(".wav")
                            and "__macosx" not in n.lower(),
        "note": "OPT-IN (--only guitarset). Hex-pickup DEBLEEDED leg only: a real "
                "per-string direct pickup signal, the closest thing in the set to a "
                "DI. ACOUSTIC guitar, so it is wrong-instrument data by construction "
                "-- keep it at a low weight or in its own ablation, never poured "
                "unlabelled into the electric pool. The mono-mic and mix legs are "
                "someone else's room and someone else's mix and are not fetched.",
        "optional": True,
    },
}


def log(msg: str) -> None:
    print(msg, flush=True)


def zenodo_record(rid: str) -> dict:
    with urllib.request.urlopen(f"https://zenodo.org/api/records/{rid}", timeout=90) as r:
        return json.load(r)


def download(url: str, dest: Path, expect: int | None) -> None:
    if dest.exists() and (expect is None or dest.stat().st_size == expect):
        return
    dest.parent.mkdir(parents=True, exist_ok=True)
    log(f"  downloading {dest.name} ...")
    tmp = dest.with_suffix(dest.suffix + ".part")
    start = tmp.stat().st_size if tmp.exists() else 0
    req = urllib.request.Request(url)
    if start:
        req.add_header("Range", f"bytes={start}-")
    with urllib.request.urlopen(req, timeout=120) as r, open(tmp, "ab" if start else "wb") as f:
        shutil.copyfileobj(r, f, 1 << 20)
    tmp.replace(dest)


def ffmpeg(args: list[str]) -> bool:
    p = subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", *args],
                       capture_output=True, text=True)
    if p.returncode != 0:
        log(f"    ffmpeg: {p.stderr.strip()[:200]}")
    return p.returncode == 0


def cut(src: Path, out_dir: Path, stem: str, concat: bool = False) -> int:
    """mono 48k -> strip silence at both ends -> 5 s segments; drop short ones."""
    out_dir.mkdir(parents=True, exist_ok=True)
    # silenceremove twice around areverse trims BOTH ends; the -50 dB RMS gate is
    # the study's "40 % of frames above a silence gate" in a form ffmpeg can do in
    # one pass. A segment that survives and is a full 5 s is content by construction.
    af = ("silenceremove=start_periods=1:start_silence=0.05:start_threshold=-50dB:detection=rms,"
          "areverse,"
          "silenceremove=start_periods=1:start_silence=0.05:start_threshold=-50dB:detection=rms,"
          "areverse")
    pattern = str(out_dir / f"{stem}_%05d.wav")
    head = ["-f", "concat", "-safe", "0", "-i", str(src)] if concat else ["-i", str(src)]
    ok = ffmpeg([*head, "-ac", "1", "-ar", str(RATE), "-af", af,
                 "-f", "segment", "-segment_time", str(CLIP_SECONDS),
                 "-c:a", "pcm_s16le", pattern])
    if not ok:
        return 0
    # A full 5 s mono 16-bit clip is 480,044 bytes with a 44-byte header; anything
    # materially shorter is the tail of the file, not a clip.
    full = CLIP_SECONDS * RATE * 2
    kept = 0
    # Exactly `{stem}_NNNNN.wav`, so a stem that is a PREFIX of another stem's name
    # cannot have its clips counted twice.
    for p in sorted(out_dir.glob(f"{stem}_[0-9][0-9][0-9][0-9][0-9].wav")):
        if p.stat().st_size < full * 0.98:
            p.unlink()
        else:
            kept += 1
    return kept


def prepare(name: str, spec: dict, root: Path, raw: Path) -> dict:
    log(f"== {name}")
    rec = zenodo_record(spec["zenodo"])
    lic = (rec["metadata"].get("license") or {}).get("id", "unknown").lower()
    title = rec["metadata"]["title"]
    doi = rec["metadata"].get("doi", "")
    if lic not in ALLOWED_LICENCES:
        log(f"  REFUSED: licence '{lic}' is not on the allow-list {sorted(ALLOWED_LICENCES)}")
        return {"name": name, "licence": lic, "title": title, "doi": doi,
                "clips": 0, "seconds": 0.0, "skipped": "licence"}

    sizes = {f["key"]: f["size"] for f in rec["files"]}
    base = f"https://zenodo.org/records/{spec['zenodo']}/files/"
    for key in spec["files"]:
        download(base + key + "?download=1", raw / name / key, sizes.get(key))

    stage = raw / name / "_extracted"
    stage.mkdir(parents=True, exist_ok=True)
    members: list[Path] = []
    for key in spec["files"]:
        if not key.endswith(".zip"):
            continue
        with zipfile.ZipFile(raw / name / key) as z:
            for n in z.namelist():
                if not spec["member"](n):
                    continue
                # PREFIX with the zip: Guitar-TECHS names every player's DI leg
                # `directinput_allchords.wav`, so a flat basename made P2 overwrite
                # P1 and the clip glob below double-counted what was left. Measured:
                # 2,473 clips on disk against 4,167 reported.
                target = stage / f"{Path(key).stem}__{Path(n).name}"
                if not target.exists():
                    with z.open(n) as fi, open(target, "wb") as fo:
                        shutil.copyfileobj(fi, fo, 1 << 20)
                members.append(target)
    log(f"  {len(members)} DI source files")

    # An OPTIONAL set lands OUTSIDE `di/`, and that is not tidiness.
    # `ClipSource.Scan` enumerates `di/` RECURSIVELY, so 3 h of acoustic dropped into
    # `di/guitarset/` would silently become a third of a 5.9 h pool whose entire job
    # is electric tone -- opt-in at fetch time and then not opt-in at all at render
    # time. Point the renderer at `di-optional` deliberately, or not at all.
    out_dir = root / ("di-optional" if spec.get("optional") else "di") / name
    clips = 0
    if spec.get("concat"):
        # One note per file, most of it decay: cutting each separately threw away
        # 681 of 690 sources. Concatenating first turns 58 min of single notes into
        # ~700 clips of continuous timbre material, which is what this set is for.
        lst = raw / name / "_concat.txt"
        lst.write_text("".join(f"file '{m.as_posix()}'\n" for m in sorted(members)),
                       encoding="utf-8")
        clips = cut(lst, out_dir, name, concat=True)
        log(f"  concatenated {len(members)} sources -> {clips} clips")
    else:
        for i, m in enumerate(members, 1):
            stem = re.sub(r"[^A-Za-z0-9_.-]", "_", m.stem)[:60]
            clips += cut(m, out_dir, stem)
            if i % 50 == 0 or i == len(members):
                log(f"  cut {i}/{len(members)} sources -> {clips} clips")

    return {"name": name, "licence": lic, "title": title, "doi": doi,
            "clips": clips, "seconds": clips * CLIP_SECONDS, "note": spec["note"],
            "url": f"https://zenodo.org/records/{spec['zenodo']}"}


def existing_set(name: str, spec: dict, root: Path) -> dict:
    """A row for a set this invocation did not prepare, counted off disk."""
    d = root / ("di-optional" if spec.get("optional") else "di") / name
    n = sum(1 for _ in d.glob("*.wav")) if d.exists() else 0
    lic = "unknown"
    try:
        lic = (zenodo_record(spec["zenodo"])["metadata"].get("license") or {}).get("id", "unknown")
    except Exception:  # noqa: BLE001 - offline is not a reason to lose the row
        pass
    return {"name": name, "licence": lic, "title": name, "doi": "",
            "clips": n, "seconds": n * CLIP_SECONDS, "note": spec["note"],
            "url": f"https://zenodo.org/records/{spec['zenodo']}"}


def owner_pool(root: Path) -> dict:
    d = root / "di" / "owner"
    d.mkdir(parents=True, exist_ok=True)
    n = sum(1 for p in d.glob("*.wav")
            if p.stat().st_size >= CLIP_SECONDS * RATE * 2 * 0.98)
    return {"name": "owner", "licence": "ours", "title": "The owner's own DI recordings",
            "doi": "", "clips": n, "seconds": n * CLIP_SECONDS,
            "note": "Drop mono 48 kHz 16-bit WAV clips of exactly 5 s here, recorded "
                    "DRY off the app's live input (no amp, no cab, no pedals). Any "
                    "file name. Anything shorter than 5 s is ignored.",
            "url": ""}


def write_licences(root: Path, rows: list[dict]) -> None:
    total = sum(r["seconds"] for r in rows)
    lines = [
        "# DI source licences",
        "",
        "Written by `tools/rig-dataset/fetch-di.py`. **Every licence here was read from",
        "the Zenodo record's own API at fetch time**, not copied from a study — the",
        "script refuses to prepare a set whose licence is not on its allow-list.",
        "",
        f"Total prepared DI: **{total / 3600:.2f} h** across {len(rows)} sources.",
        "",
        "| Source | Licence | Clips (5 s) | Hours | What was taken |",
        "|---|---|---:|---:|---|",
    ]
    for r in rows:
        lines.append(f"| {r['name']} | {r['licence']} | {r['clips']:,} | "
                     f"{r['seconds'] / 3600:.2f} | {r.get('note', '')} |")
    lines += ["", "## Records", ""]
    for r in rows:
        if not r.get("url"):
            continue
        lines.append(f"- **{r['name']}** — {r['title']} — <{r['url']}>"
                     + (f" (DOI {r['doi']})" if r["doi"] else ""))
    lines += [
        "",
        "## Deliberately NOT used",
        "",
        "| Source | Licence | Why not |",
        "|---|---|---|",
        "| EGDB | **not stated** | Google Drive, no licence on the record or the site. "
        "A model that ships cannot carry audio whose terms nobody has read. |",
        "| IDMT-SMT-Guitar / -Audio-Effects | CC BY-**NC-ND** | NC bars a commercial "
        "product; ND bars redistributing a render. In-house eval only "
        "(`GND_IDMT_ROOT`). |",
        "| GuitarSet | CC BY 4.0 | OPT-IN, not fetched by default (`--only guitarset`): "
        "acoustic guitar, so wrong-instrument data. Its hex-pickup DEBLEEDED leg is a "
        "genuine direct pickup signal and is licence-clean, which is why it is reachable "
        "at all — use it at a low weight or in its own ablation. |",
        "| GAPS | see record | Classical guitar, not DI. |",
        "| tonetwist AFx | MIT (repo); audio per-device | ~40 % of its dry material is "
        "IDMT- or YouTube-sourced and must be excluded; the rest needs a per-record "
        "licence review for ~25 min of audio. |",
        "| MUSDB18-HQ, MoisesDB | educational-only / CC BY-NC-SA | Both NC. Acceptable "
        "for an in-house model; for a shipped one, use beds rendered from our own "
        "instruments. See the beds note in tools/rig-dataset/README.md. |",
        "",
        "The 35-song local corpus (`DotCharter-local/TestData/EOF`) and any Rocksmith "
        "psarc audio are **evaluation only** and must never be trained on or "
        "redistributed.",
        "",
    ]
    (root / "LICENSES.md").write_text("\n".join(lines), encoding="utf-8")
    log(f"wrote {root / 'LICENSES.md'}")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--root", required=True, type=Path,
                    help="dataset root, e.g. DotCharter-local/datasets/rig")
    ap.add_argument("--only", nargs="*", default=None, help="only these sets")
    args = ap.parse_args()

    if shutil.which("ffmpeg") is None:
        log("error: ffmpeg is not on PATH.")
        return 2

    root: Path = args.root
    raw = root / "_raw"
    root.mkdir(parents=True, exist_ok=True)

    rows = []
    for name, spec in SETS.items():
        # An OPTIONAL set is prepared only when it is NAMED. Its licence is still
        # re-read and still reported, so LICENSES.md says what exists and what was
        # deliberately left out -- a set that vanishes from the licence file is a set
        # nobody re-checks.
        wanted = name in args.only if args.only else not spec.get("optional")
        if not wanted:
            # NOT skipped from the LICENCE FILE, only from the WORK. `--only` used
            # to drop the other sets out of LICENSES.md entirely, which turned a
            # re-cut of one set into a licence file that under-reported the corpus
            # by 0.96 h and named one source where there were two.
            rows.append(existing_set(name, spec, root))
            continue
        try:
            rows.append(prepare(name, spec, root, raw))
        except Exception as e:  # noqa: BLE001 - one bad set must not lose the others
            log(f"  FAILED {name}: {type(e).__name__}: {e}")
    rows.append(owner_pool(root))

    write_licences(root, rows)
    for r in rows:
        log(f"{r['name']:<14} {r['licence']:<12} {r['clips']:>7,} clips  "
            f"{r['seconds'] / 3600:>6.2f} h")
    return 0


if __name__ == "__main__":
    sys.exit(main())
