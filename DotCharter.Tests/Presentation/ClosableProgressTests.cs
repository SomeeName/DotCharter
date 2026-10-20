using DotCharter.Presentation.Shared;

namespace DotCharter.Tests.Presentation;

public sealed class ClosableProgressTests
{
    private static readonly TimeSpan DeadlockCap = TimeSpan.FromSeconds(60);

    [Fact]
    public void Reports_AreAppliedUntilClose_AndDroppedAfter()
    {
        var applied = new List<int>();
        var progress = new ClosableProgress<int>(applied.Add);

        progress.Report(1);
        progress.Close();
        progress.Report(2);

        Assert.Equal([1], applied);
        Assert.True(progress.IsClosed);
    }

    [Fact]
    public async Task Close_WaitsOutAReportMidApply_SoAResetAfterItIsTheLastWord()
    {
        var ct = TestContext.Current.CancellationToken;
        var log = new List<string>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var progress = new ClosableProgress<string>(value =>
        {
            entered.Set();
            Assert.True(release.Wait(DeadlockCap, ct), "The test never released the report.");
            lock (log)
            {
                log.Add(value);
            }
        });

        var report = Task.Run(() => progress.Report("stale report"), ct);
        Assert.True(entered.Wait(DeadlockCap, ct), "The report never started.");
        var close = Task.Run(() =>
        {
            progress.Close();
            lock (log)
            {
                log.Add("reset");
            }
        }, ct);

        Assert.False(close.IsCompleted);
        release.Set();
        await Task.WhenAll(report, close).WaitAsync(DeadlockCap, ct);
        progress.Report("late report");

        Assert.Equal(["stale report", "reset"], log);
    }

    [Fact]
    public void TheRocksmithExport_ClosesItsProgressBeforeResettingTheBar()
    {
        var source = SourceText.StripComments(File.ReadAllText(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.Presentation", "Screens", "Dialogs", "RocksmithExportViewModel.Build.cs")));

        Assert.Contains("new Progress<RocksmithBuildProgress>(buildProgress.Report)", source);
        var endBuild = source[source.IndexOf("private void EndBuild()", StringComparison.Ordinal)..];
        var close = endBuild.IndexOf("_buildProgress?.Close();", StringComparison.Ordinal);
        var reset = endBuild.IndexOf("ProgressFraction = 0;", StringComparison.Ordinal);
        Assert.True(close >= 0, "EndBuild no longer closes the build's progress target.");
        Assert.True(close < reset, "EndBuild must close the progress target BEFORE it resets the bar.");
    }
}
