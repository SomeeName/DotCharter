# Add a rule to the Issues tab

The Issues tab lists what `ArrangementValidationService` finds. Each finding comes
from one rule class. A rule only reports; it never edits the chart.

## Steps

1. **Name the finding.** Add a value at the END of the `ArrangementIssueType` enum
   in `DotCharter.Core/Domain/Validation/ArrangementIssueType.cs`, with a summary
   comment saying what it flags and how often it was seen on real charts. Then
   give it a short code (like `D11`) in the code table lower in the same file.
2. **Write the rule.** Add a class in `DotCharter.Services/Editing/Validation/Rules/`
   that implements `IArrangementRule`
   (`DotCharter.Services/Editing/Validation/IArrangementRule.cs`). Read the
   arrangement from the context and add `ArrangementIssue` rows. Keep no state
   between calls; one instance is reused. Report one row per real problem, not one
   per difficulty level.
3. **Register it twice.** Add it to the rule list in
   `DotCharter.Services/Editing/Validation/ArrangementValidationService.cs` and to
   `DotCharter.UI/App.ValidationServices.cs`.
4. **Say where it points.** Map the finding to the inspector field it should jump
   to in `DotCharter.UI/ViewModels/Editor/Inspector/InspectorIssueFieldMap.cs`.
5. **Add an auto-fix, if there is a safe one.** See
   `DotCharter.Services/Editing/Fixing/ArrangementAutoFixService.cs`. A fix must be
   a fixed point: running it twice changes nothing more.
6. **Decide what the AI chart pass does with it.** Every production rule must be
   either in the AI pass's rule gate or listed with a reason in
   `DotCharter.Tests/Ai/Chart/AiChartRuleGateCoverageTests.cs`.
7. **Check what the rule reads.** Phrases live in two stores, and a rule that reads
   only one is silent on the other. Read
   `docs/agents/services/editing-invariants/validation-and-fixing.md` first.

## Tests to add

- `DotCharter.Tests/Services/Editing/Validation/<Rule>Tests.cs`: one case that
  fires, one that stays quiet, and one for the auto-fix if you wrote it. Model it
  on `ChordOutsideHandShapeRuleTests.cs` in that folder.
- The coverage test in step 6 fails until you handle the new rule.
- Break the rule on purpose and confirm your test fails.

## Example in the code

`DotCharter.Services/Editing/Validation/Rules/ChordOutsideHandShapeRule.cs` (finding
`D11`) flags a chord outside every hand shape.
