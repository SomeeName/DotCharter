using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     The prompt every caller opens the hosted latency wizard through: an applied
///     lead reaches <c>CalibrationApplied</c> (the Graphics tab's cached slider
///     listens), a wizard left without applying raises nothing, and a host that
///     cannot open it is logged rather than thrown at a fire-and-forget caller.
/// </summary>
public sealed class LatencyCalibrationLauncherTests
{
    private sealed class AnsweringDialogs(Func<double?> answer) : IDialogs
    {
        public int Opens { get; private set; }

        public bool HasOpenDialog => false;

        public event EventHandler<DialogChange>? Changed { add { } remove { } }

        public Task<TResult?> ShowAsync<TContract, TResult>(Action<TContract>? init = null)
            where TContract : class, IDialogScreen<TResult>
        {
            Assert.Equal(typeof(ILatencyWizardScreen), typeof(TContract));
            Opens++;
            return Task.FromResult((TResult?)(object?)answer());
        }

        public Task<bool> MessageAsync(string title, string message, bool isConfirmation = false) => Task.FromResult(false);

        public bool CloseTop() => false;
    }

    [Fact]
    public async Task An_applied_lead_is_announced()
    {
        var launcher = new LatencyCalibrationLauncher(new AnsweringDialogs(() => 12.5), new FakeAppLogger());
        var heard = new List<double>();
        launcher.CalibrationApplied += (_, lead) => heard.Add(lead);

        await launcher.OpenAsync();

        Assert.Equal([12.5], heard);
    }

    [Fact]
    public async Task Leaving_without_applying_announces_nothing()
    {
        var dialogs = new AnsweringDialogs(() => null);
        var launcher = new LatencyCalibrationLauncher(dialogs, new FakeAppLogger());
        var heard = 0;
        launcher.CalibrationApplied += (_, _) => heard++;

        await launcher.OpenAsync();

        Assert.Equal(1, dialogs.Opens);
        Assert.Equal(0, heard);
    }

    [Fact]
    public async Task A_host_that_cannot_open_it_is_logged_not_thrown()
    {
        var logger = new FakeAppLogger();
        var launcher = new LatencyCalibrationLauncher(new DialogsSlot(), logger);

        await launcher.OpenAsync();

        Assert.Contains(logger.Entries, e => e.Message.Contains("latency wizard", StringComparison.OrdinalIgnoreCase));
    }
}
