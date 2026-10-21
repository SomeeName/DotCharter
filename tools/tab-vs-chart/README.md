# tools/tab-vs-chart

Compares the REAL Guitar Pro import of a tab with a FINISHED chart of the same song — the
answer key (#91, level 2a of #82). Deterministic: no audio, no model, no network.
`TabVsChart.cs` is a .NET 10 file-based program over `DotCharter.Services`, so it measures the
importer of the tree it runs in. Read-only on every source; it writes only to its output folder.

```powershell
# from Git Bash: hidden, low priority, CPUs 8-15
cmd //c start '""' //b //low //wait //affinity FF00 pwsh -NoProfile -File tools/tab-vs-chart/Run-TabVsChart.ps1 [-MatchOnly] [-Filter <substr>]
# or directly
dotnet run tools/tab-vs-chart/TabVsChart.cs -- --csv <cdlc.csv> --tabs <dir> --charts <dir> --out <dir> `
    [--overrides <tsv>] [--since 2024-07-20] [--focus "artist;artist"] [--filter <substr>] [--match-only] [--parallel 4]
```

| Variable | What |
|---|---|
| `DOTCHARTER_CDLC_CSV` | the CustomsForge "my CDLC" CSV export (columns `ID`, `Title`, `Artist ID`, …, `Created At`, `Updated At`) |
| `DOTCHARTER_TABS_DIR` | the Guitar Pro corpus (`.gp`, `.gpx`, `.gp5`, `.gp4`, `.gp3`, any depth) |
| `DOTCHARTER_CHART_PROJECTS_DIR` | the EOF / RS2 XML project folders, one per song, named `Artist - Title` |
| `DOTCHARTER_TAB_CHART_OVERRIDES` | the manual match file (default `DotCharter-local\level2-overrides.tsv`) |
| `DOTCHARTER_LEVEL2_REPORT_DIR` | the report (default `DotCharter-local\level2-report`) |
| `DOTCHARTER_LEVEL2_FOCUS` | artists compared and reported first, `;`-separated |

The defaults sit beside the MAIN checkout (also from a worktree); `Run-TabVsChart.ps1` refuses
an output folder inside the repository. The program drops itself to idle priority on CPUs 8-15.

## The sources are private

The tabs are copyrighted transcriptions and the charts are the owner's. Nothing from them is
ever committed — no file, name, path or excerpt — and issues and PRs carry aggregate numbers
only. The report, the override file and any launcher that sets the variables stay local. Tests
use synthetic files only (`Services/Import/GuitarPro/Report/AnswerKey/TabChart*Tests`): a tiny
Guitar Pro file from `LegacyGpFixtureBuilder` and a tiny RS XML written and read back by
`XmlChartSerializer`.

## Pipeline

The engine is `DotCharter.Services/Import/GuitarPro/Report/AnswerKey/`; nothing in the app calls it.

1. **Match** (`TabChartMatching`). A CSV row is known-good when its `Updated At` is on or after
   `--since` (the owner's rule; `Created At` does not count). Its title is matched to a chart
   folder (`Artist - Title`, fuzzy: NFKD-folded, punctuation-free character bigrams, with and
   without bracketed parts), whose artist then picks between tabs of the same title; `.gp` beats
   `.gpx` beats `.gp5`. Below 0.72 a row is unmatched. The override file
   (`id <TAB> tab <TAB> chart-folder`, `-` for "match it", or `id <TAB> skip`) fixes the rest —
   a title the tab spells in kana or kanji, a typo in a folder name. `matching.csv` lists every
   row and why it did not match.
2. **Import** each tab as "add as new arrangements" does: every track, the score's tempo map
   (`GpBeatMapBuilder`, repeats unfolded by `GpMasterBarSequence`), `ChartSpacingEnforcer`.
3. **Answer key**: every single-level `song` XML of the folder (no DD, no vocals/showlights),
   each distinct part once, read through the importer (`TabChartSections.LoadChart`): EOF `moveR#`
   phrases are moved by `RsXmlPhraseMover`, the importer's own rule (DLC Builder's `PhraseMover`),
   so the harness holds no resolution of its own and the two always agree. Sections only label
   measures and differences; no score depends on them.
4. **Align** (`TabChartAligner`): the tab has no audio sync, so measures are aligned by content —
   a semi-global DP over measure pairs (free leading and trailing gaps absorb a count-in, a
   pickup, a cut intro or outro; 0.35 per measure skipped inside), scored by onset slots (0.4)
   and the better of pitch and string+fret (0.6). When the DP's mean similarity over sounding
   pairs is under 0.15, the offset between the first sounding measures is used alone. Bars are
   then compared by TIME (`TabChartBarring`): equal bars beat for beat; where the two bar the
   passage differently, bars are taken on from the shorter side until the music they cover is
   equally long and the span is compared at each sound's offset from its start (nothing is
   stretched; 6/8 against 3/4 is the same length). A pickup (first aligned pair that cannot be
   equalised) aligns at the bar line that ends it.
5. **Score** (`TabChartComparer`), per chart arrangement against the tab track of the same
   instrument that agrees best, per measure:
   - **onsets** (±0.06 beat), **sounds** exact (string + fret) and by pitch, as F1. When the
     tab and the chart count different strings (a five-string bass against four, seven
     strings against six) the tab's sounds are first re-spelled onto the chart's strings by
     sounding pitch (`TabChartStrings`);
   - **techniques** on matched sounds: pitched slide, slide out (unpitched), slide in (the
     ornament the importer materialises and EOF charts: a linked pitched sixteenth one to
     three frets off its landing note, which follows at once; a longer or wider legato slide is
     a note), a legato slide linked into the next note is recorded but not scored, bend, hammer-on, pull-off, tap, palm
     mute, fret-hand mute, harmonic, pinch harmonic, vibrato, tremolo — both / import only /
     chart only;
   - **chords**: same shape where both strike two or more sounds; same template name;
   - **FHP** and **fingering** with `tools/chart-eval`'s definitions: the anchor governing a
     fretted sound is the last one at or before it; a same-shape chord counts for fingering when
     the chart's template fingers every fretted string;
   - **#76 links**: `LinkCandidateDetector` on the import against the chart's real repeats
     (equal sounds-only measure content) through the alignment — precision (linked pairs the
     chart repeats) and recall (chart repeats the detector linked at the same offset).

## Output

| File | What |
|---|---|
| `summary.md` | matching counts and the unmatched list, aggregate agreement per role (focus artists and all), techniques, difference classes with counts, alignment and `moveR#` counts, then the worst-first list |
| `arrangements.csv` | one row per compared arrangement, every count |
| `matching.csv` | every known-good row: matched, or why not, with scores |
| `songs/<slug>.md` | per song: the arrangements, difference classes, worst measures with their sections |
| `songs/<slug>.json` | per song: every count, measure and difference (with its section) — the input of level 2b |
| `run.log` | console output and the importer commit measured |

Difference classes: `missing-onset`, `extra-onset`, `onset-shift`, `same-pitch-other-string`,
`octave`, `wrong-pitch`, `chord-note-missing`, `chord-note-extra`, `chord-shape`, `chord-name`,
`fhp`, `fingering`, `technique:<name>:missing|extra`, `unaligned-tab-measure`,
`unaligned-chart-measure`. "Missing" means the chart has it and the import does not.

The chart's chord NAMES are mostly EOF's auto-names, so `chord-name` measures EOF parity rather
than musical correctness.
