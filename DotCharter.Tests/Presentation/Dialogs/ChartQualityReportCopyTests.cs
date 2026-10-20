using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing.Quality;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Tone.Diagnostics;
using static DotCharter.Tests.Presentation.Dialogs.ChartQualityReportViewModelTests;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     #711: copying a chart with a difficulty ladder costs tens of milliseconds on the UI thread.
///     The busy row shows first and the copy waits for the step after it has painted; a landed
///     check's copy is the next check's spare, restored into rather than cloned, and a check still
///     running keeps its own. Structure only; the timings are logged and measured by
///     <c>EofRoundTripCostProbeTests</c>.
/// </summary>
public sealed class ChartQualityReportCopyTests
{
    private static bool Assembled(RecordingProxy documents) =>
        documents.Calls.Any(c => c.StartsWith(nameof(IProjectDocumentService.AssembleProject), StringComparison.Ordinal));

    [Fact]
    public async Task TheBusyRowShows_BeforeTheChartIsAssembledOrCopied()
    {
        var (h, _, _) = ChartWithABadFret();
        var painted = new List<Action>();
        var runner = new ChartQualityReportOffThreadTests.HeldRunner();
        var (documents, recorder) = RecordingProxy.Create<IProjectDocumentService>();
        var report = Open(h, checks: Checks(runner.Run, painted.Add), documents: documents);
        IChartQualityReportScreen screen = report;

        Assert.True(screen.IsChecking);
        Assert.False(screen.HasVerdict);
        Assert.Single(painted);
        Assert.False(Assembled(recorder), "the chart was assembled before the busy row painted");
        Assert.Empty(runner.Runs);

        painted[0]();

        Assert.True(Assembled(recorder));
        Assert.Single(runner.Runs);
        await runner.FinishAsync(0);
        await report.Pending;
        Assert.False(screen.IsChecking);
        Assert.True(screen.HasVerdict);
    }

    [Fact]
    public void ACopyQueuedBehindANewerCheck_OrAClosedDialog_IsNeverTaken()
    {
        var (h, _, _) = ChartWithABadFret();
        var painted = new List<Action>();
        var runner = new ChartQualityReportOffThreadTests.HeldRunner();
        var (documents, recorder) = RecordingProxy.Create<IProjectDocumentService>();
        var report = Open(h, checks: Checks(runner.Run, painted.Add), documents: documents);
        IChartQualityReportScreen screen = report;
        screen.RecheckCommand.Execute(null);
        Assert.Equal(2, painted.Count);

        painted[0]();
        Assert.False(Assembled(recorder));
        Assert.Empty(runner.Runs);
        painted[1]();
        Assert.Single(runner.Runs);

        screen.RecheckCommand.Execute(null);
        report.Dispose();
        painted[2]();
        Assert.Single(runner.Runs);
    }

    [Fact]
    public async Task ALandedCheck_LeavesItsCopyForTheNext_ButARunningCheckKeepsItsOwn()
    {
        var (h, hard, bad) = ChartWithABadFret();
        var runner = new ChartQualityReportOffThreadTests.HeldRunner();
        var report = Open(h, checks: Checks(runner.Run));
        IChartQualityReportScreen screen = report;
        var first = Assert.Single((await runner.FinishAsync(0)).Report.Arrangements).Arrangement;
        await report.Pending;
        Assert.NotSame(h.Project.Arrangements[0], first);

        // The chart changes, a committed edit, and the next check reads the change out of the SAME copy.
        h.History.Do(new MutateNotesAction(hard.Notes, [bad], n => n.Fret = 5));
        screen.RecheckCommand.Execute(null);
        Assert.Same(first, Assert.Single((await runner.FinishAsync(1)).Report.Arrangements).Arrangement);
        await report.Pending;
        Assert.DoesNotContain(ChartQualityReportOffThreadTests.Listed(screen), f => f.Message.Contains("25", StringComparison.Ordinal));

        h.History.Undo();
        screen.RecheckCommand.Execute(null); // check 2 holds the spare
        screen.RecheckCommand.Execute(null); // so check 3 cannot have it
        var third = Assert.Single((await runner.FinishAsync(3)).Report.Arrangements).Arrangement;
        await report.Pending;
        var second = Assert.Single((await runner.FinishAsync(2)).Report.Arrangements).Arrangement;
        Assert.NotSame(second, third);
        Assert.Contains(new[] { second, third }, a => ReferenceEquals(a, first));

        // The report landed over a restored copy still names the live chart.
        var row = ChartQualityReportOffThreadTests.Listed(screen).First(f => f.Message.Contains("25", StringComparison.Ordinal));
        h.State.ActiveLevel = h.Project.Arrangements[0].Levels[0];
        row.JumpCommand.Execute(null);
        Assert.Same(hard, h.State.ActiveLevel);
        Assert.Contains(bad, h.Editor.Selection.SelectedNotes);
    }

    public class FailingDocuments : RecordingProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) =>
            method?.Name == nameof(IProjectDocumentService.AssembleProject)
                ? throw new InvalidOperationException("assemble failed")
                : base.Invoke(method, args);
    }

    /// <summary>The copy runs in a posted step nobody awaits, so a throw there must end the busy row, not hide in a task.</summary>
    [Fact]
    public async Task ACopyThatThrows_EndsTheBusyRowWithTheError()
    {
        var (h, _, _) = ChartWithABadFret();
        var documents = System.Reflection.DispatchProxy.Create<IProjectDocumentService, FailingDocuments>();

        var report = Open(h, documents: documents);
        await report.Pending;
        IChartQualityReportScreen screen = report;

        Assert.False(screen.IsChecking);
        Assert.True(screen.HasVerdict);
        Assert.Contains("assemble failed", screen.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAppCopiesOnABackgroundPost()
    {
        var (ui, recorder) = RecordingProxy.Create<IUiDispatcher>();
        var checks = new QualityReportChecks(new ChartQualityReporter(new ArrangementValidationService(new FakeAppLogger())), ui);

        checks.AfterPaint(() => { });

        Assert.Equal(nameof(IUiDispatcher.Post), Assert.Single(recorder.Invocations).Method);
    }
}
