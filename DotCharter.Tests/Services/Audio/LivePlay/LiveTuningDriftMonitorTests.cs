using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     The Live Play tuning-drift monitor, driven the way the detection worker
///     drives it: <c>BeginCheck</c> at a monotonic millisecond clock, the window
///     filled with what the capture tap would hold, <c>CompleteCheck</c> with the
///     sample count read. The cents arithmetic and the watchdog's own decay and
///     hysteresis are pinned by <c>TuningDriftWiringTests</c> and
///     <c>TuningDriftWatchdogTests</c>; this pins the cadence, the input gates
///     and the hint the HUD is handed.
/// </summary>
public sealed class LiveTuningDriftMonitorTests
{
    private const int SampleRate = 48_000;

    private static readonly FakeLocalization Strings = FakeLocalization.English();

    [Fact]
    public void Sustained_flat_playing_raises_the_flat_hint_exactly_once()
    {
        var logger = new FakeAppLogger();
        var monitor = new LiveTuningDriftMonitor(Strings, logger);
        var flatHz = 110.0 * Math.Pow(2, -15.0 / 1200.0);

        var hints = Play(monitor, flatHz, checks: 24);

        Assert.Equal([Strings["LivePlay_TuningDrift_Flat"]], hints);
        Assert.Contains(logger.Entries, e => e.Message.StartsWith("Tuning drift: DriftFlat", StringComparison.Ordinal));
    }

    [Fact]
    public void Sustained_sharp_playing_raises_the_sharp_hint()
    {
        var monitor = new LiveTuningDriftMonitor(Strings, new FakeAppLogger());
        var sharpHz = 110.0 * Math.Pow(2, 15.0 / 1200.0);

        var hints = Play(monitor, sharpHz, checks: 24);

        Assert.Equal(Strings["LivePlay_TuningDrift_Sharp"], hints[^1]);
    }

    [Fact]
    public void An_authored_cent_offset_is_not_drift()
    {
        var monitor = new LiveTuningDriftMonitor(Strings, new FakeAppLogger());
        monitor.Reset(centOffset: 15.0);

        var hints = Play(monitor, 110.0 * Math.Pow(2, 15.0 / 1200.0), checks: 24);

        Assert.Equal([string.Empty], hints);
    }

    [Fact]
    public void Checks_are_paced_at_four_per_second()
    {
        var monitor = new LiveTuningDriftMonitor(Strings, new FakeAppLogger());

        Assert.NotNull(monitor.BeginCheck(1_000, SampleRate));
        Assert.Null(monitor.BeginCheck(1_000 + LiveTuningDriftMonitor.DriftCheckIntervalMs - 1, SampleRate));
        Assert.NotNull(monitor.BeginCheck(1_000 + LiveTuningDriftMonitor.DriftCheckIntervalMs, SampleRate));
    }

    [Fact]
    public void No_input_asks_for_no_window()
    {
        var monitor = new LiveTuningDriftMonitor(Strings, new FakeAppLogger());

        Assert.Null(monitor.BeginCheck(0, sampleRate: 0));
    }

    [Fact]
    public void A_short_read_is_never_evidence()
    {
        var monitor = new LiveTuningDriftMonitor(Strings, new FakeAppLogger());
        var flatHz = 110.0 * Math.Pow(2, -15.0 / 1200.0);

        for (var i = 0; i < 24; i++)
        {
            var window = monitor.BeginCheck(i * LiveTuningDriftMonitor.DriftCheckIntervalMs, SampleRate);
            Assert.NotNull(window);
            FillSine(window!, flatHz);
            Assert.Null(monitor.CompleteCheck(window!.Length - 1));
        }
    }

    [Fact]
    public void Reset_drops_the_evidence_of_the_previous_run()
    {
        var monitor = new LiveTuningDriftMonitor(Strings, new FakeAppLogger());
        var flatHz = 110.0 * Math.Pow(2, -15.0 / 1200.0);
        Assert.Equal(Strings["LivePlay_TuningDrift_Flat"], Play(monitor, flatHz, checks: 24)[^1]);

        monitor.Reset(centOffset: 0.0);
        var hints = Play(monitor, flatHz, checks: 1, startMs: 100_000);

        Assert.Empty(hints);
    }

    /// <summary>Runs <paramref name="checks" /> due checks of a steady sine and
    /// returns every hint the monitor answered with, in order.</summary>
    private static List<string> Play(LiveTuningDriftMonitor monitor, double frequencyHz, int checks, double startMs = 0)
    {
        var hints = new List<string>();
        for (var i = 0; i < checks; i++)
        {
            var window = monitor.BeginCheck(startMs + i * LiveTuningDriftMonitor.DriftCheckIntervalMs, SampleRate);
            Assert.NotNull(window);
            FillSine(window!, frequencyHz);
            if (monitor.CompleteCheck(window!.Length) is { } hint)
            {
                hints.Add(hint);
            }
        }

        return hints;
    }

    private static void FillSine(float[] window, double frequencyHz)
    {
        for (var n = 0; n < window.Length; n++)
        {
            window[n] = (float)(0.5 * Math.Sin(2.0 * Math.PI * frequencyHz * n / SampleRate));
        }
    }
}
