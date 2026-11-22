using Avalonia.Threading;
using DotCharter.UI.ViewModels.Dialogs;

namespace DotCharter.Tests.Perf;

/// <summary>The real dialog queue cancels a displaced confirmation and preserves its replacement's turn.</summary>
[Collection("HeadlessUI")]
public sealed class MessageDialogTurnstileUiPerfSmokeTests
{
    [Fact]
    public async Task DisplacingAStuckConfirmationCancelsItAndQueuesTheNextCaller()
    {
        await BoundedWait.ForAsync(HeadlessSessionFixture.Dispatch(async () =>
        {
            var clock = new StepTimeProvider();
            var stuck = TimeSpan.FromMinutes(3);
            var vm = new MessageDialogViewModel(stuck, clock);
            var first = vm.ShowAsync("first", "first", confirmation: true);
            PumpUntil(() => vm.IsOpen && vm.Title == "first");

            var second = vm.ShowAsync("second", "second", confirmation: true);
            Assert.False(second.IsCompleted);
            Assert.Equal(1, clock.PendingTimers);
            clock.Advance(stuck);
            Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
            PumpUntil(() => vm.IsOpen && vm.Title == "second");
            Assert.Equal(1, vm.StuckDialogsDisplaced);

            var third = vm.ShowAsync("third", "third", confirmation: true);
            Assert.False(third.IsCompleted);
            vm.CancelCommand.Execute(null);
            Assert.False(await second.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
            PumpUntil(() => vm.IsOpen && vm.Title == "third");
            Assert.Equal(1, vm.StuckDialogsDisplaced);
            vm.OkCommand.Execute(null);
            Assert.True(await third.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
        }, TestContext.Current.CancellationToken), "the dialog UI dispatcher", TestContext.Current.CancellationToken);
    }

    private static void PumpUntil(Func<bool> until)
    {
        var deadlock = System.Diagnostics.Stopwatch.StartNew();
        while (!until() && deadlock.Elapsed < TimeSpan.FromSeconds(60))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }

        Assert.True(until(), "the dialog never published its state");
    }
}
