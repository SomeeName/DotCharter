using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The Issues tab on its own view model (#659), driven directly over a real chart editor
///     and a faked rule set: a validation pass regroups by severity (Important first), the
///     "show easier levels" toggle moves the count, the groups wait while the tab is hidden,
///     and an issue jump switches level first and selects through the host. Before the move
///     this was reachable only through source lints — the inspector it lived on could not be
///     built outside a headless session.
/// </summary>
public sealed class InspectorIssuesViewModelTests
{
    [Fact]
    public void AValidationPass_GroupsBySeverity_ImportantFirst()
    {
        var (_, rules, vm) = Create();

        vm.RecomputeIssues();

        Assert.Equal(1, rules.Calls);
        Assert.Equal(3, vm.IssueCount);
        Assert.Equal(ArrangementIssueSeverity.Important, vm.TopIssueSeverity);
        Assert.Equal([ArrangementIssueSeverity.Important, ArrangementIssueSeverity.Warning],
            vm.IssueGroups.Select(g => g.Severity));
        Assert.Equal([2, 1], vm.IssueGroups.Select(g => g.Count));
        Assert.Equal(vm.Items.Count, vm.IssueGroups.Sum(g => g.Count));
        Assert.False(vm.IssueGroupsAreStale);
    }

    [Fact]
    public void ShowEasierLevels_TogglesTheCount()
    {
        var (_, rules, vm) = Create();
        vm.RecomputeIssues();

        Assert.Equal(1, vm.EasierLevelIssueCount);
        Assert.True(vm.HasEasierLevelIssues);
        Assert.DoesNotContain(vm.Items, i => i.Message == EasyFinding);

        vm.ShowEasierLevelIssues = true;

        Assert.Equal(2, rules.Calls);
        Assert.Equal(4, vm.IssueCount);
        Assert.Contains(vm.Items, i => i.Message == EasyFinding);
        Assert.Equal(4, vm.IssueGroups.Sum(g => g.Count));

        vm.ShowEasierLevelIssues = false;

        Assert.Equal(3, vm.IssueCount);
        Assert.DoesNotContain(vm.Items, i => i.Message == EasyFinding);
    }

    [Fact]
    public void WhileTheTabIsHidden_TheGroupsGoStale_AndTheHostHearsEveryPass()
    {
        var (_, _, vm) = Create();
        var host = new FakeHost { IsIssuesTabSelected = false };
        vm.AttachHost(host);

        vm.RecomputeIssues();

        Assert.Equal(1, host.Recomputed);
        Assert.Equal(3, vm.IssueCount);
        Assert.Empty(vm.IssueGroups);
        Assert.True(vm.IssueGroupsAreStale);

        vm.EnsureIssueGroupsCurrent();

        Assert.False(vm.IssueGroupsAreStale);
        Assert.Equal(3, vm.IssueGroups.Sum(g => g.Count));

        vm.DetachHost(host);
        vm.RecomputeIssues();
        Assert.Equal(1, host.Recomputed);
    }

    [Fact]
    public void AJump_SwitchesToTheOwningLevel_AndSelectsThroughTheHost()
    {
        var (harness, _, vm) = Create();
        var host = new FakeHost();
        vm.AttachHost(host);
        vm.ShowEasierLevelIssues = true;
        var easy = vm.Items.Single(i => i.Message == EasyFinding);
        var arrangement = harness.Project.Arrangements[0];

        vm.JumpToIssue(easy);

        Assert.Same(arrangement.Levels[0], harness.State.ActiveLevel);
        Assert.Equal(1, host.ProgrammaticSelections);
        Assert.Same(easy.Note, Assert.Single(harness.Editor.Selection.SelectedNotes));
    }

    private const string EasyFinding = "on the easy level";

    /// <summary>Two levels, the hardest on screen. The rules answer four findings: two Important
    /// and one Warning on the hardest level's notes or the whole arrangement, and one Warning on
    /// the easy level, which the list sets aside until asked.</summary>
    private static (ChartEditHarness Harness, FakeRules Rules, InspectorIssuesViewModel Vm) Create()
    {
        var harness = new ChartEditHarness();
        var arrangement = harness.Project.Arrangements[0];
        var easy = arrangement.Levels[0];
        easy.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 0, Fret = 3 });
        var hard = new ChartLevel { DifficultyIndex = 1 };
        hard.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 0, Fret = 3 });
        hard.Notes.Add(new GuitarNote { Position = new FractionalPosition(2), StringIndex = 1, Fret = 5 });
        arrangement.Levels.Add(hard);
        harness.State.ActiveLevel = hard;

        var rules = new FakeRules(easy.Notes[0], hard.Notes[0], hard.Notes[1]);
        var vm = new InspectorIssuesViewModel(harness.State, harness.Editor, rules,
            RecordingProxy.Create<DotCharter.Services.Editing.Maintenance.IChartMaintenanceService>().Proxy,
            new ExportReadinessViewModel(harness.State, new CheckpointsViewModelTests.FakeDocumentService(),
                harness.Transport, harness.Editor, FakeLocalization.KeysOnly(),
                RecordingProxy.Create<DotCharter.Services.Editing.Maintenance.IChartMaintenanceService>().Proxy,
                new DotCharter.Presentation.Dialogs.DialogsSlot(), new FakeAppLogger()),
            harness.History, FakeLocalization.KeysOnly(), () => null);
        return (harness, rules, vm);
    }

    private sealed class FakeRules(GuitarNote onEasy, GuitarNote onHard, GuitarNote alsoOnHard)
        : IArrangementValidationService
    {
        public int Calls { get; private set; }

        public IReadOnlyList<ArrangementIssue> Validate(Arrangement? arrangement, IList<BeatMarker> beats,
            double? totalAudioLengthMs = null, ChartProject? project = null)
        {
            Calls++;
            return
            [
                // Severity is derived from the type: NoEndPhrase is a Warning, FirstPhraseNotEmpty Important.
                new ArrangementIssue(ArrangementIssueType.NoEndPhrase, "warning on the hard level", 500, Note: onHard),
                new ArrangementIssue(ArrangementIssueType.NoEndPhrase, EasyFinding, 500, Note: onEasy),
                new ArrangementIssue(ArrangementIssueType.FirstPhraseNotEmpty, "important on the hard level", 1000,
                    Note: alsoOnHard),
                new ArrangementIssue(ArrangementIssueType.FirstPhraseNotEmpty, "the whole arrangement")
            ];
        }
    }

    private sealed class FakeHost : IInspectorIssuesHost
    {
        public bool IsIssuesTabSelected { get; init; } = true;

        public int Recomputed { get; private set; }

        public int ProgrammaticSelections { get; private set; }

        public void OnIssuesRecomputed() => Recomputed++;

        public void SelectProgrammatically(Action select)
        {
            ProgrammaticSelections++;
            select();
        }
    }
}
