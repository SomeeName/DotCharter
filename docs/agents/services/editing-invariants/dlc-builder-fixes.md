# `Editing/` — the chart-model invariants (deep reference) — Apply DLC Builder fixes and the export findings' Fix

## Apply DLC Builder fixes (#952)

**Every Rocksmith export already applies DLC Builder's build steps to its COPY**
(`Export/DynamicDifficulty/DlcBuilderBuildSteps`, the rules in
[../export-companions.md](../export-companions.md) § DLC Builder's build steps).
The chart's context menu (empty highway ▸ "Apply DLC Builder fixes to the chart",
`Input/TimelineContextMenu`, palette id `tools.applyDlcBuilderFixes`) is the ONE way
to put that result into the PROJECT, for a charter who wants to keep editing it:
`ChartMaintenanceService.ApplyDlcBuilderFixes` (`ChartMaintenanceService.Rocksmith.cs`)
clones the live project, generates the COUNT/p0…/END structure when there is none
(as the export does), runs `DlcBuilderBuildSteps.Apply` with the fixes AND dynamic
difficulty on (whatever the export toggles say; the level-count mode is the
setting's), swaps the result into the clone and records the before/after pair as
ONE `ProjectSnapshotAction`. One Ctrl+Z restores the chart exactly
(`ApplyDlcBuilderFixesTests`).

It REPLACES what it touches: the arrangement's levels, chord templates, phrases,
phrase iterations and event points become DLC Builder's (phrases renamed COUNT,
p0…, NOGUITAR, END when levels are generated). That is why it is never automatic.

"Prepare for Rocksmith" (#184: clean-up, structure, fret-hand positions, improve,
our own 5-level generation) was removed in its favour: it overwrote the project
from the Tools menu, the export dialog, the quality report and the Inspector, and
its levels were not the ones DLC Builder builds. What it kept: the export
findings' Fix below (`Editing/Preparation/ExportFindingFixer`). **Tools ▸ Generate
difficulty levels… (`Editing/Improvement/LevelGenerationService`) stays**: it adds
what the port does not — a level count the charter chooses, and regenerating an
arrangement that already has levels — and Live Play's `DynamicDifficultyLadder`
builds its runtime ladder with it.

## The Fix beside a finding

`Export/Validation/ExportFindingFixes` is the ONE decision per
`ChartExportValidator.Key*`: `Fixes` maps a key to an `ExportFixKind`
(`AutoFix`, `FillFhp`, `GenerateStructure`, `OpenToneDesigner`) and
`NoAutomaticFix` gives every other key a REASON. `ExportFindingFixesTests`
reflects over the constants, so a new finding fails the gate until it is in one
table. `ExportValidationFinding.Fix` reads the table.

`ChartMaintenanceService.FixExportFinding(kind, arrangement)` runs a chart kind on
that arrangement as ONE undo node (`FillFhp` only fills the sounds before a
level's first anchor: nothing placed is moved or removed). A tone kind is not a chart edit: `Presentation/Shared/RocksmithFixes`
switches the editor to the finding's arrangement and opens the Tone designer —
except `KeyToneNotDefined` when `Editing/Tones/ToneKeyRepair.ProposeFor` finds the
one rename that repairs it (#195): then the Fix's tooltip names that rename and
`ChartMaintenanceService.RepairUndefinedToneKey` applies it as ONE undo node, and
the re-check clears the finding. The decision is per PROJECT, so it is made when
the finding is listed, not in the static table.
`RocksmithFixes` is shared by the export dialog and
`Inspector/ExportReadinessViewModel`; a finding whose arrangement name is
ambiguous (two "Lead"s) offers no Fix. Every chart kind has a fixture in
`ApplyDlcBuilderFixesTests.EachFix_ClearsItsFinding_AsOneUndoStep`, and
`EveryChartFix_HasAFixture` fails when a new mapping lacks one.

## Improve chart vs Auto-fix arrangements

Neither is a subset of the other, so both stay (each now has a tooltip saying
so). Auto-fix REPAIRS faults per arrangement (sorting, duplicates, note lengths,
slides, capo frets, phrase-start anchors, template fingers, declared tones);
Improve POLISHES a valid chart (spacing enforcer, trim to audio, extra beats,
END phrase, phrase- AND section-start anchors, chord sustains, link-nexts,
slide hand shapes, hand-shape spacing). They share only the phrase-start
anchor restatement.
