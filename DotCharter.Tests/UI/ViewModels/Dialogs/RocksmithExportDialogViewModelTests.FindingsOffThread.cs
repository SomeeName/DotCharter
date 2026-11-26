using DotCharter.Services.Export.Validation;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

/// <summary>#1004: opening the dialog must not run the validator (Library lookup included) on the calling thread.</summary>
public sealed partial class RocksmithExportDialogViewModelTests
{
    private static readonly ExportValidationFinding Stale = new("", ExportFindingSeverity.Warning, "Stale");
    private static readonly ExportValidationFinding Fresh = new("", ExportFindingSeverity.Error, "Fresh");

    [Fact]
    public void Opening_does_not_run_the_validator_and_Export_waits_for_the_findings()
    {
        var pending = new List<TaskCompletionSource<IReadOnlyList<ExportValidationFinding>>>();
        var harness = new Harness(CreateValidProject())
        {
            RunValidation = _ =>
            {
                var source = new TaskCompletionSource<IReadOnlyList<ExportValidationFinding>>();
                pending.Add(source);
                return source.Task;
            }
        };

        var vm = harness.Open();

        Assert.Single(pending);
        Assert.True(vm.IsCheckingFindings);
        Assert.Empty(vm.ValidationItems);
        Assert.False(vm.ExportCommand.CanExecute(null));
        Assert.True(vm.IsExportBlocked);
        Assert.Contains("Checking", vm.ExportBlockedReason);

        pending[0].SetResult([]);

        Assert.False(vm.IsCheckingFindings);
        Assert.DoesNotContain("Checking", vm.ExportBlockedReason);
    }

    [Fact]
    public async Task The_default_runner_validates_on_a_worker_thread()
    {
        // The caller is a dedicated thread (never a pool thread), so the pool can not hand it the queued work
        // while it is blocked or idle: the ids differ only if the work really left the calling thread.
        var workerThread = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new Harness(CreateValidProject())
        {
            RunValidation = work => Task.Run(() =>
            {
                workerThread.TrySetResult(Environment.CurrentManagedThreadId);
                return work();
            })
        };
        var callerThread = 0;
        DotCharter.Presentation.Screens.Dialogs.RocksmithExportViewModel? vm = null;
        await Task.Factory.StartNew(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            vm = harness.Open();
        }, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var worker = await workerThread.Task.WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        await vm!.FindingsReady.WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);

        Assert.NotEqual(callerThread, worker);
        Assert.False(vm.IsCheckingFindings);
    }

    [Fact]
    public async Task A_newer_request_drops_the_stale_result()
    {
        var pending = new List<TaskCompletionSource<IReadOnlyList<ExportValidationFinding>>>();
        var harness = new Harness(CreateValidProject())
        {
            RunValidation = _ =>
            {
                var source = new TaskCompletionSource<IReadOnlyList<ExportValidationFinding>>();
                pending.Add(source);
                return source.Task;
            }
        };
        var vm = harness.Open();
        var first = vm.FindingsReady;
        vm.Revalidate();
        var second = vm.FindingsReady;

        pending[1].SetResult([Fresh]);
        await second;
        pending[0].SetResult([Stale]);
        await first;

        Assert.Equal(1, vm.ErrorCount);
        Assert.Equal(0, vm.WarningCount);
        Assert.False(vm.IsCheckingFindings);
    }
}
