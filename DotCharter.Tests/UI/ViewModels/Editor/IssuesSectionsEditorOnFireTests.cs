using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Editing.Quality;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     Inspector design D shows the Issues tab's Review card beside the others on a wide pane: the Editor on Fire
///     round trip is computed while that card is on screen, without opening its section, and stops when it leaves;
///     opening another section never closes the check the card still shows.
/// </summary>
public sealed class IssuesSectionsEditorOnFireTests
{
    private static ExportReadinessViewModel Readiness()
    {
        var harness = new ChartEditHarness();
        return new ExportReadinessViewModel(harness.State, new CheckpointsViewModelTests.FakeDocumentService(),
            harness.Transport, harness.Editor, FakeLocalization.KeysOnly(), RecordingProxy.Create<IChartMaintenanceService>().Proxy,
            new DotCharter.Presentation.Dialogs.DialogsSlot(), new FakeAppLogger(),
            new ChartQualityReporter(new ArrangementValidationService(new FakeAppLogger())), new TestUiDispatcher());
    }

    [Fact]
    public void TheReviewCardOnScreen_ComputesTheRoundTrip_WithoutOpeningItsSection()
    {
        var readiness = Readiness();
        var sections = new IssuesSectionsViewModel(readiness);
        Assert.False(readiness.EofRoundTrip.IsExpanded);

        sections.ShowEditorOnFire(true);
        Assert.True(readiness.EofRoundTrip.IsExpanded);
        Assert.True(sections.IsFindings);

        sections.Open(IssuesSection.Project);
        Assert.True(readiness.EofRoundTrip.IsExpanded);

        sections.ShowEditorOnFire(false);
        Assert.False(readiness.EofRoundTrip.IsExpanded);
        Assert.True(sections.IsProject);
    }

    [Fact]
    public void OpeningTheReviewSection_StillComputesIt_WhenTheCardIsOffScreen()
    {
        var readiness = Readiness();
        var sections = new IssuesSectionsViewModel(readiness);

        sections.ChooseCommand.Execute(IssuesSection.EditorOnFire);
        sections.ShowEditorOnFire(false);

        Assert.True(readiness.EofRoundTrip.IsExpanded);
        Assert.True(sections.IsEditorOnFire);
    }

    [Fact]
    public void AProjectSwitch_WithTheReviewCardOnScreen_ComputesTheRoundTripAgain()
    {
        var readiness = Readiness();
        var runs = 0;
        readiness.EofRoundTripModel.OffThread = _ => Task.FromResult(new EofRoundTripCheck(null, $"run {++runs}"));
        var sections = new IssuesSectionsViewModel(readiness);
        sections.ShowEditorOnFire(true);
        sections.Open(IssuesSection.Project);
        Assert.Equal(1, runs);
        Assert.Contains("run 1", readiness.EofRoundTrip.Summary, StringComparison.Ordinal);

        readiness.Reset(); // what a project switch calls

        Assert.True(readiness.EofRoundTrip.IsExpanded);
        Assert.True(sections.IsProject);
        Assert.Equal(2, runs);
        Assert.DoesNotContain("run 1", readiness.EofRoundTrip.Summary, StringComparison.Ordinal);
        Assert.Contains("run 2", readiness.EofRoundTrip.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AProjectSwitch_WithTheReviewCardOffScreen_ClearsTheOldRoundTrip()
    {
        var readiness = Readiness();
        var runs = 0;
        readiness.EofRoundTripModel.OffThread = _ => Task.FromResult(new EofRoundTripCheck(null, $"run {++runs}"));
        var sections = new IssuesSectionsViewModel(readiness);
        sections.ShowEditorOnFire(true);
        sections.ShowEditorOnFire(false);
        Assert.Contains("run 1", readiness.EofRoundTrip.Summary, StringComparison.Ordinal);

        readiness.Reset();

        Assert.False(readiness.EofRoundTrip.IsExpanded);
        Assert.Equal("", readiness.EofRoundTrip.Summary);
        Assert.Equal(1, runs);
    }
}
