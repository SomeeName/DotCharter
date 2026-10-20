using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Fixing;
using DotCharter.Services.Editing.FretAnchors;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Editing.Phrases;
using DotCharter.Services.Editing.Quality;
using DotCharter.Services.Editing.Refret;
using DotCharter.Tests.Services.Editing.Maintenance;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     #690: the Chart quality report's checks cost 200–450 ms on a chart with a difficulty ladder, so
///     they run on a copy of the chart, on the injected off-thread runner. The dialog opens before
///     they run, a check that lands after a newer one is dropped, and a Fix still edits the live
///     chart on the calling thread as one undo step. Structure only; the timings are logged.
/// </summary>
public sealed class ChartQualityReportOffThreadTests
{
    /// <summary>Every check is handed out and held until the test finishes it on a pool thread, so a
    /// report that checked on the calling thread would show its findings before the test lets it.</summary>
    internal sealed class HeldRunner
    {
        public List<(Func<QualityReportCheck> Work, TaskCompletionSource<QualityReportCheck> Done)> Runs { get; } = [];

        public Task<QualityReportCheck> Run(Func<QualityReportCheck> work)
        {
            var done = new TaskCompletionSource<QualityReportCheck>();
            Runs.Add((work, done));
            return done.Task;
        }

        /// <summary>Runs check <paramref name="run" /> on a pool thread and lands it; returns it as built, naming the copy.</summary>
        public async Task<QualityReportCheck> FinishAsync(int run)
        {
            var (work, done) = Runs[run];
            var check = await Task.Run(work);
            done.SetResult(check);
            return check;
        }
    }

    internal static List<IQualityFinding> Listed(IChartQualityReportScreen screen) => [.. screen.Groups.SelectMany(g => g.Items)];

    [Fact]
    public async Task TheDialogOpensBeforeTheChecksRun_TheyRunOnTheInjectedRunner_AndGoToSelectsTheLiveNote()
    {
        var (h, hard, bad) = ChartQualityReportViewModelTests.ChartWithABadFret();
        var runner = new HeldRunner();
        var report = ChartQualityReportViewModelTests.Open(h, checks: ChartQualityReportViewModelTests.Checks(runner.Run));
        IChartQualityReportScreen screen = report;

        Assert.Single(runner.Runs);
        Assert.True(screen.IsChecking);
        Assert.False(screen.HasVerdict);
        Assert.False(screen.HasFindings);
        Assert.Empty(screen.Arrangements);

        await runner.FinishAsync(0);
        await report.Pending;

        Assert.False(screen.IsChecking);
        Assert.True(screen.HasVerdict);
        Assert.False(screen.IsReady);
        Assert.Equal("Lead", Assert.Single(screen.Arrangements).Name);
        var row = Listed(screen).First(f => f.Message.Contains("25", StringComparison.Ordinal));

        // The check ran on a copy; the row it lands with names the LIVE chart's level and note.
        h.State.ActiveLevel = h.Project.Arrangements[0].Levels[0];
        row.JumpCommand.Execute(null);
        Assert.Same(hard, h.State.ActiveLevel);
        Assert.Contains(bad, h.Editor.Selection.SelectedNotes);
    }

    [Fact]
    public async Task ACheckThatLandsAfterANewerRecheck_IsDropped()
    {
        var (h, _, bad) = ChartQualityReportViewModelTests.ChartWithABadFret();
        var runner = new HeldRunner();
        var report = ChartQualityReportViewModelTests.Open(h, checks: ChartQualityReportViewModelTests.Checks(runner.Run));
        IChartQualityReportScreen screen = report;
        var stale = report.Pending;

        bad.Fret = 5;
        screen.RecheckCommand.Execute(null);
        Assert.Equal(2, runner.Runs.Count);
        var current = report.Pending;
        await runner.FinishAsync(1);
        await current;

        Assert.False(screen.IsChecking);
        Assert.DoesNotContain(Listed(screen), f => f.Message.Contains("25", StringComparison.Ordinal));
        var verdict = screen.Verdict;

        // The first check saw fret 25. It lands last, and changes nothing.
        await runner.FinishAsync(0);
        await stale;

        Assert.False(screen.IsChecking);
        Assert.Equal(verdict, screen.Verdict);
        Assert.DoesNotContain(Listed(screen), f => f.Message.Contains("25", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A level with notes and no fret-hand positions: the export gate's Fix fills them. It edits
    ///     the live chart where it is pressed, as one undo step, and only the check after it goes to
    ///     the runner; while that runs, the previous rows do nothing.
    /// </summary>
    [Fact]
    public async Task AFix_EditsTheLiveChartAsOneUndoStep_AndTheRecheckGoesThroughTheRunner()
    {
        var (h, hard, _) = ChartQualityReportViewModelTests.ChartWithABadFret();
        var runner = new HeldRunner();
        var report = ChartQualityReportViewModelTests.Open(h, checks: ChartQualityReportViewModelTests.Checks(runner.Run),
            maintenance: Maintenance(h));
        IChartQualityReportScreen screen = report;
        await runner.FinishAsync(0);
        await report.Pending;
        static bool IsFhp(IQualityFinding f) => f.HasFix && f.Message.Contains("fret-hand-position", StringComparison.Ordinal);
        var row = Listed(screen).First(IsFhp);
        var jump = Listed(screen).First(f => f.Message.Contains("25", StringComparison.Ordinal));
        var steps = h.History.GetUndoSteps().Count;
        Assert.Empty(hard.Anchors);

        row.FixCommand.Execute(null);

        Assert.NotEmpty(hard.Anchors);
        Assert.Equal(steps + 1, h.History.GetUndoSteps().Count);
        Assert.Equal(2, runner.Runs.Count);
        Assert.True(screen.IsChecking);
        var easy = h.Project.Arrangements[0].Levels[0];
        h.State.ActiveLevel = easy;
        jump.JumpCommand.Execute(null);
        Assert.Same(easy, h.State.ActiveLevel);
        Assert.Empty(h.Editor.Selection.SelectedNotes);

        await runner.FinishAsync(1);
        await report.Pending;
        Assert.False(screen.IsChecking);
        Assert.DoesNotContain(Listed(screen), IsFhp);

        h.History.Undo();
        Assert.Empty(hard.Anchors);
    }

    private static ChartMaintenanceService Maintenance(ChartEditHarness h)
    {
        var logger = new FakeAppLogger();
        var anchors = new FretAnchorGenerationService();
        return new ChartMaintenanceService(h.Services.Config, new ChartImprovementService(), new LevelSquashService(),
            new LevelGenerationService(), new PhraseIterationPropagationService(h.History, new EditingScope(h.State), logger),
            anchors, new ChartPowerToolsTests.NoOpProjectSessionService(), h.History, logger, h.State, h.Editor,
            new ArrangementAutoFixService(), new RefretService(anchors, new FingeringGenerationService()),
            FakeLocalization.English());
    }
}
