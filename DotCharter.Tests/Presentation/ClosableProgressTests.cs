using DotCharter.Presentation.Shared;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Export;
using System.Reflection;
using ExportTests = DotCharter.Tests.UI.ViewModels.Dialogs.RocksmithExportDialogViewModelTests;

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
        // #607: Close runs on a thread of its own and must be SEEN parked behind
        // the report before the report is released. A pool Task.Run that had not
        // reached Close yet looked the same as a Close that waits, so an
        // unguarded Close passed.
        var close = ParkedCall.Start(() =>
        {
            progress.Close();
            lock (log)
            {
                log.Add("reset");
            }
        });

        try
        {
            Assert.True(await close.WaitUntilParkedOrReturnedAsync("Close()", DeadlockCap, ct),
                "Close returned while a report was still mid-apply: it does not wait the report out.");
        }
        finally
        {
            release.Set();
        }

        await Task.WhenAll(report, close.Returned).WaitAsync(DeadlockCap, ct);
        progress.Report("late report");

        Assert.Equal(["stale report", "reset"], log);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheRocksmithExport_ClosesItsProgressBeforeResettingTheBar(bool cancel)
    {
        using var vm = new ExportTests.Harness(ExportTests.CreateValidProject()).Open();
        using var run = new CancellationTokenSource();
        var reports = 0;
        var progress = new ClosableProgress<RocksmithBuildProgress>(_ => reports++);
        SetField(vm, "_buildCts", run);
        SetField(vm, "_buildProgress", progress);
        vm.IsBuilding = true;
        vm.ProgressMessage = "working";
        vm.ProgressFraction = 0.5;
        var resets = ObserveResets(vm, progress);
        progress.Report(default!);
        if (cancel) run.Cancel();

        EndBuild(vm, run);

        Assert.True(progress.IsClosed);
        Assert.Contains(nameof(vm.ProgressMessage), resets);
        Assert.Contains(nameof(vm.ProgressFraction), resets);
        Assert.False(vm.IsBuilding);
        progress.Report(default!);
        Assert.Equal(1, reports);
        vm.ProgressFraction = 0.25;
        EndBuild(vm, run);
        Assert.Equal(0.25, vm.ProgressFraction);
    }

    [Fact]
    public void ASupersededExport_CannotCloseOrResetTheCurrentRunsProgress()
    {
        using var vm = new ExportTests.Harness(ExportTests.CreateValidProject()).Open();
        using var current = new CancellationTokenSource();
        using var superseded = new CancellationTokenSource();
        var progress = new ClosableProgress<RocksmithBuildProgress>(_ => { });
        SetField(vm, "_buildCts", current);
        SetField(vm, "_buildProgress", progress);
        vm.IsBuilding = true;
        vm.ProgressMessage = "current";
        vm.ProgressFraction = 0.5;

        EndBuild(vm, superseded);

        Assert.False(progress.IsClosed);
        Assert.True(vm.IsBuilding);
        Assert.Equal("current", vm.ProgressMessage);
        Assert.Equal(0.5, vm.ProgressFraction);
        EndBuild(vm, current);
        Assert.True(progress.IsClosed);
        Assert.Equal(0, vm.ProgressFraction);
    }

    [Fact]
    public void TheResetProbe_RejectsAResetWhileTheTargetIsOpen()
    {
        using var vm = new ExportTests.Harness(ExportTests.CreateValidProject()).Open();
        vm.ProgressFraction = 0.5;
        var progress = new ClosableProgress<RocksmithBuildProgress>(_ => { });
        ObserveResets(vm, progress);

        Assert.Throws<Xunit.Sdk.TrueException>(() => vm.ProgressFraction = 0);
    }

    [Fact]
    public void TheRocksmithExport_WiresTheBuilderThroughTheClosableTarget()
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Presentation", "Screens", "Dialogs", "RocksmithExportViewModel.Build.cs");
        Assert.True(File.Exists(path), "The export build owner moved; repoint this progress-adapter wiring check.");
        var source = SourceText.StripComments(File.ReadAllText(path));
        Assert.Contains("new Progress<RocksmithBuildProgress>(buildProgress.Report)", source);
    }

    private static List<string> ObserveResets(RocksmithExportViewModel vm, ClosableProgress<RocksmithBuildProgress> progress)
    {
        var resets = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if ((e.PropertyName == nameof(vm.ProgressMessage) && vm.ProgressMessage.Length == 0) ||
                (e.PropertyName == nameof(vm.ProgressFraction) && vm.ProgressFraction == 0))
            {
                Assert.True(progress.IsClosed, $"The export reset {e.PropertyName} before closing its progress target.");
                resets.Add(e.PropertyName!);
            }
        };
        return resets;
    }

    private static void SetField(RocksmithExportViewModel vm, string name, object value)
    {
        var field = typeof(RocksmithExportViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(field is not null, $"The export's {name} field moved; repoint the cleanup behavior probe.");
        field!.SetValue(vm, value);
    }

    private static void EndBuild(RocksmithExportViewModel vm, CancellationTokenSource run)
    {
        var method = typeof(RocksmithExportViewModel).GetMethod("EndBuild", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(CancellationTokenSource)], null);
        Assert.True(method is not null, "The export's EndBuild(CancellationTokenSource) cleanup moved; repoint this behavior probe.");
        method!.Invoke(vm, [run]);
    }
}
