using System.Runtime.CompilerServices;
using DotCharter.Services.Editing.Quality;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     Inspector design D's Whole project findings: an Arrangement choice (each arrangement, then All arrangements) filters
///     the whole-project check's findings; Jump makes the finding's arrangement the active one first; Fix runs only for a
///     finding of the arrangement on screen whose rule the fix menu repairs, and says why otherwise; the Fixes card's
///     status line reads "Export blocker" only for a finding the check marked as an error.
/// </summary>
public sealed class ProjectFindingsListTests
{
    [Fact]
    public void TheArrangementChoice_FiltersTheFindings()
    {
        var (list, _, lead, rhythm, _) = Create();

        Assert.Equal(["Lead", "Rhythm", "Inspector_Issues_AllArrangements"], list.ArrangementChoices.Select(c => c.Name));
        Assert.Same(lead, list.SelectedArrangement!.Arrangement);
        Assert.Single(list.Items);

        list.SelectedArrangement = list.ArrangementChoices[2];
        Assert.Equal(2, list.Items.Count);
        list.SelectedArrangement = list.ArrangementChoices[1];
        Assert.Same(rhythm, Assert.Single(list.Items).Finding.Arrangement);
    }

    [Fact]
    public void Jump_OpensTheFindingsArrangement_AndFix_WaitsForIt()
    {
        var (list, active, _, rhythm, jumped) = Create();
        list.SelectedArrangement = list.ArrangementChoices[1];
        list.Selected = list.Items[0];

        Assert.False(list.FixCommand.CanExecute(null));
        Assert.Equal("Inspector_Issues_ProjectFixOtherTip", list.FixTip);
        Assert.True(list.JumpCommand.CanExecute(null));

        list.JumpCommand.Execute(null);

        Assert.Same(rhythm, active.Value);
        Assert.Single(jumped);
        Assert.Equal("Inspector_Issues_ProjectFixTip", list.FixTip);
    }

    [Fact]
    public void Fix_NeedsARuleTheFixMenuRepairs()
    {
        var harness = new ChartEditHarness();
        var lead = harness.Project.Arrangements[0];
        lead.ToneBase = "clean";
        lead.ToneA = "lead";
        var at = new FractionalPosition(2);
        lead.ToneChanges.Add(new ToneChange { Position = at, ToneName = "lead" });
        var fixable = new ArrangementIssue(ArrangementIssueType.ToneChangeOnNote, "tone", at.GetPositionMs(harness.State.Beats));
        var fixedOnes = new List<ArrangementIssue>();
        var list = new ProjectFindingsList(FakeLocalization.KeysOnly(), harness.State, () => lead, _ => { }) { FixIssue = fixedOnes.Add };
        list.Show([Finding(lead, fixable, QualitySeverity.Error), Finding(lead, new ArrangementIssue(ArrangementIssueType.NoEndPhrase, "none", 10), QualitySeverity.Warning)], [lead]);

        list.Selected = list.Items.Single(r => r.Finding.Rule == fixable);
        Assert.True(list.FixCommand.CanExecute(null));
        list.FixCommand.Execute(null);
        Assert.Same(fixable, Assert.Single(fixedOnes));

        list.Selected = list.Items.Single(r => r.Finding.Rule != fixable);
        Assert.False(list.FixCommand.CanExecute(null));

        Assert.True(list.Blocks(fixable));
        Assert.False(list.Blocks(list.Items[1].Finding.Rule!));
    }

    private static (ProjectFindingsList List, StrongBox<Arrangement> Active, Arrangement Lead, Arrangement Rhythm, List<ArrangementIssue> Jumped) Create()
    {
        var harness = new ChartEditHarness();
        var lead = harness.Project.Arrangements[0];
        lead.Name = "Lead";
        var rhythm = new Arrangement { Name = "Rhythm" };
        rhythm.Levels.Add(new ChartLevel());
        harness.Project.Arrangements.Add(rhythm);
        var active = new StrongBox<Arrangement>(lead);
        var jumped = new List<ArrangementIssue>();
        var list = new ProjectFindingsList(FakeLocalization.KeysOnly(), harness.State, () => active.Value, a => active.Value = a)
        {
            JumpToIssue = jumped.Add
        };
        list.Show(
        [
            Finding(lead, new ArrangementIssue(ArrangementIssueType.NoEndPhrase, "lead", 100), QualitySeverity.Warning),
            Finding(rhythm, new ArrangementIssue(ArrangementIssueType.NoEndPhrase, "rhythm", 200), QualitySeverity.Warning)
        ], [lead, rhythm]);
        return (list, active, lead, rhythm, jumped);
    }

    private static QualityFinding Finding(Arrangement arrangement, ArrangementIssue rule, QualitySeverity severity) =>
        new(QualityChecks.ChartRules, severity, rule.Code, arrangement, arrangement.Levels[0], rule.TimeMs, null, rule, null, null);
}
