# tools/tab-corpus

Runs the REAL Guitar Pro importer over a local tab corpus and reports import health and
linked-measure candidates (Phase 0 of linked measures). `TabCorpusReport.cs` is a .NET 10
file-based program over `DotCharter.Services`, so it measures the code of the tree it runs
in. Read-only: it never writes next to a tab.

```powershell
# from Git Bash: hidden, low priority, CPUs 8-15
cmd //c start '""' //b //low //wait //affinity FF00 pwsh -NoProfile -File tools/tab-corpus/Run-TabCorpus.ps1
# or directly
dotnet run tools/tab-corpus/TabCorpusReport.cs -- [--tabs <dir>] --out <dir> [--parallel 6] [--limit N] `
    [--filter <substr>] [--no-roundtrip] [--timeout-s 180]
```

`--tabs` defaults to `DOTCHARTER_TABS_DIR`, `--out` to `DOTCHARTER_TAB_REPORT_DIR`;
`Run-TabCorpus.ps1` falls back to `DotCharter-local\tab-corpus-report` beside the main
checkout and refuses an output folder inside the repository. The program drops itself to
idle priority on CPUs 8-15.

## The corpus is private

The tabs are copyrighted transcriptions. Nothing from them is ever committed — no file,
name, path or excerpt — and issues and PRs carry aggregate numbers only. The output's
worst-first list and `details/` name files; they stay local. Tests use synthetic Guitar Pro
files only (`GpImportHealthProbeTests`) and never need the corpus.

## Per file

The engine is `DotCharter.Services/Import/GuitarPro/Report/GpImportHealthProbe`: every
track imported as "add as new arrangements" does it (the score's tempo map, then
`ChartSpacingEnforcer` with the default settings), then:

- **Import health** — the exception class if the import throws; sounds on a missing
  string, above fret 24, below the capo or off the beat grid; the track → arrangement
  mapping, tuning and capo.
- **Techniques dropped** (`.gp` and `.gpx`) — `GpTechniqueCensus` reads the gpif itself and
  counts notes and techniques per track, weighted by how often each written bar plays;
  the same classes are counted on the import. A shortfall is a dropped technique (an
  excess usually a technique the importer derives, such as legato slides).
- **Round trip** — each track exported with `GpExporter`, re-imported and compared per
  measure with the sounds-only detection key (`MeasureContentOptions.SoundsOnly`).
  `GpRoundTripDiff` explains each unequal measure: the classes of sound-level difference
  in it (missing / extra sound, fret, sustain by size, legato, tap, slap-pop, harmonic…),
  and how many measures carry a class as their ONLY difference — what fixing it can win.
- **Linked measures** — `LinkCandidateDetector` on the hardest level: repeated runs within
  each arrangement, and shared runs between arrangements of one `LinkCompatibility` class.
  **Guitar Pro's repeat signs are the reference:** two measures that play one written bar
  (`GpBarInfo.SourceBarIndex`) must have equal keys and land in one group; "beyond GP" is
  repetition the tab wrote out instead.

## Output

| File | What |
|---|---|
| `summary.md` | aggregates (formats, failure classes, warnings, tracks, techniques, round trip, linking), then the worst-first list |
| `files.csv` | one row per file, with its score and warning codes |
| `arrangements.csv` | one row per track, every count above |
| `details/*.json` | the whole probe report per file |
| `run.log` | console output (`Run-TabCorpus.ps1`) |

The score ranks files: a failed import first, then out-of-range share, round-trip
differences, disagreement with GP's repeats and dropped techniques.
