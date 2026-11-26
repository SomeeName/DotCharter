# tools/edit-replay

How many of a charter's edits would a fresh Guitar Pro import (plus Link all) have saved? (#944)

`EditReplay.cs` is a .NET 10 file-based program over `DotCharter.Services`, like `tools/chart-eval`,
so it always measures the code of the tree it sits in. It is a local dev tool, not part of the app.
Its inputs are the charter's files and stay local: never commit them, name them or quote them.

```powershell
dotnet run tools/edit-replay/EditReplay.cs -- --gp <score.gp> --project <finished.dcproj> --log <app.log> `
    [--bars <first>-<last>] [--arrangement <name>] [--tsv <out.tsv>]
```

The program drops itself to idle priority on CPUs 8-15.

## What it does

1. **Fresh import.** Parses every track of the score with `GpImporter` and the default
   `GpImportSettings`, applies it through the app's own `GpImportApplier` (add new: the score's beat
   map, spacing enforcement), then runs `LinkSuggestionFinder.Find` and `LinkAll`, which is what the
   import dialog's **Link all** does. The app services the apply path needs and the tool has no use
   for (redraw, transport, logging, localization) are inert stubs; the config is a default `AppConfig`.
2. **The charter's session.** The log's `[History] Executed …` lines are the logged edits, per
   arrangement (the project snapshot, the link-suggestions step and beat-map edits are not chart edits
   and are left out); undos and redos are reported beside them. The arrangements the charter edited are
   the log's targets, and the bar range is the first to the last bar any `[Editing]` line names.
   `--arrangement` and `--bars` override both.
3. **Diff and count**, per arrangement, over that bar range, against level 0 of the charter's project
   (matched by arrangement name):

| Category | One edit per |
|---|---|
| Anchor | anchor added or deleted; a fret or width change in place; a move within its bar that keeps fret and width |
| Shape | hand shape added or deleted; a start or end change (a shape paired by start, else by overlap) |
| Grip | paired shape whose template grip (frets and fingers) differs: one grip save |
| Fingering | chord whose fingering differs under NO shape whose grip is already counted (listed always, counted only then) |
| Sustain | note or chord (same position and strings) whose sustains differ |
| Note | note or chord added or deleted, or changed to other strings or frets at the same position |

4. **With links.** An edit made in one placement of a link group is free in every other placement of
   that group where the SAME edit is needed: same category, same position relative to the placement,
   same before and after. A placement where the charter's result differs still costs its own edit.
   Groups chain. The groups are the fresh import's, after Link all.

Output: one summary line per arrangement (`logged`, `needed`, `linked`, with the per-category
breakdown), plus a TSV of every edit (`arrangement, bar, beat in bar, category, kind, counted, fresh,
charter`) on stdout or in `--tsv`.

Sample on synthetic data (the test fixture's shape):

```text
links: 1 groups after Link all; bars 1-4; log: 1 undo(s), 0 redo(s)
arrangement                        total    Anchor     Shape      Grip Fingering   Sustain      Note
Lead                     logged        2
                         needed        9         3         2         2         0         1         1
                         linked        6         2         1         1         0         1         1
```

## Comparing a fix (before and after)

The tool measures the tree it sits in, so a comparison is two runs from two worktrees on the same
inputs: one at the baseline commit and one with the change. A baseline older than this tool does not
have `tools/edit-replay/`; copy the folder into that worktree first (it needs nothing else):

```powershell
git worktree add --detach <tmp>\base <baseline-commit>
Copy-Item -Recurse tools\edit-replay <tmp>\base\tools\
dotnet run <tmp>\base\tools\edit-replay\EditReplay.cs -- <same arguments> --tsv before.tsv
dotnet run tools\edit-replay\EditReplay.cs -- <same arguments> --tsv after.tsv
```

The baseline worktree needs the JuceSharp submodule's sources, as every DotCharter build does.

## Limits

- The counts are a greedy pairing, not a proven minimum: a fresh anchor pairs first with a charter
  anchor at the same position, then with the nearest one of the same fret and width in its bar.
- One grip save is counted per shape whose grip differs, even when two shapes share a template.
- Link propagation is credited where the same edit is needed; it does not charge for an edit a link
  would copy into a placement that needed none.
- The import runs with a default `AppConfig`, not the charter's settings.

## Tests

`DotCharter.Tests/Tools/EditReplay/EditReplayCoreTests` compiles `EditReplayCore.cs` (the pure diff,
counts and log parser) by link and pins every count on a synthetic project pair.
