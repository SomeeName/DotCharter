using System.Diagnostics;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Audio.Calibration;
using DotCharter.Tests.Presentation.Settings;

namespace DotCharter.Tests.UI;

/// <summary>
///     The latency wizard's Apply over a synthetic clock: both tap rounds are
///     tapped at fixed offsets from each beat, so the measured picture-to-sound
///     offset is known exactly. A trusted recommendation of 0 (the picture
///     already leads the sound) must clear a visual lead an earlier run left,
///     not keep it.
/// </summary>
public sealed class LatencyWizardApplyTests
{
    private const double PeriodMs = 600.0;

    private static long Ms(double ms) => (long)(ms / 1000.0 * Stopwatch.Frequency);

    private static (LatencyWizardViewModel Vm, FakeConfigurationService Config) Build(double existingLeadMs)
    {
        var config = new FakeConfigurationService();
        config.AppConfig.VisualLeadMs = existingLeadMs;
        var vm = new LatencyWizardViewModel(new CalibrationAudio(new FakeAudioPlaybackService()), config,
            new GraphicsStubs.FakeDevice(), FakeLocalization.English(), new FakeAppLogger(), new TestUiDispatcher());
        return (vm, config);
    }

    /// <summary>Fourteen beats on the synthetic clock, a tap after each one past the warm-up, alternating between the two offsets.</summary>
    private static void TapRound(LatencyWizardViewModel vm, Func<long> clock, Action<long> setClock, double first, double second)
    {
        var beat = clock();
        for (var i = 0; i < 14; i++)
        {
            beat += Ms(PeriodMs);
            setClock(beat);
            vm.PumpMetronomeForTests(beat);
            setClock(beat + Ms(i % 2 == 0 ? first : second));
            vm.TapCommand.Execute(null);
        }

        setClock(beat + Ms(PeriodMs / 2));
    }

    private static Task<double?> RunWizard(LatencyWizardViewModel vm, (double, double) visual, (double, double) audio)
    {
        var now = 1_000_000_000L;
        vm.Clock = () => now;
        vm.GoToVisualTapCommand.Execute(null);
        TapRound(vm, () => now, t => now = t, visual.Item1, visual.Item2);
        vm.GoToAudioTapCommand.Execute(null);
        TapRound(vm, () => now, t => now = t, audio.Item1, audio.Item2);
        vm.ComputeResultsCommand.Execute(null);
        Assert.True(vm.ResultsTrusted, "the synthetic taps were not enough for a trusted result; the test proves nothing");
        vm.ApplyResultsCommand.Execute(null);
        return vm.Result;
    }

    [Fact]
    public async Task ATrustedZeroRecommendation_ClearsAnEarlierVisualLead()
    {
        var (vm, config) = Build(existingLeadMs: 40);

        // Picture taps 40 ms after the beat, sound taps 70 ms after: the picture leads, so the lead to apply is 0.
        var applied = await RunWizard(vm, (30, 50), (60, 80)).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal(0.0, applied);
        Assert.Equal(0.0, config.AppConfig.VisualLeadMs);
    }

    /// <summary>The control: the same harness with the picture lagging applies the positive lead it measured.</summary>
    [Fact]
    public async Task APictureThatLags_AppliesThePositiveLead()
    {
        var (vm, config) = Build(existingLeadMs: 0);

        var applied = await RunWizard(vm, (60, 80), (30, 50)).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.NotNull(applied);
        Assert.Equal(30.0, applied!.Value, precision: 0);
        Assert.Equal(applied.Value, config.AppConfig.VisualLeadMs);
    }
}
