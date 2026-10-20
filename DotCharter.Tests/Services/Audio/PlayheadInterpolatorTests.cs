using DotCharter.Tone.Audio.Engine;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The smoothed playhead behind the 250 Hz position tick and the per-frame
///     <c>CurrentTimeMs</c> read, driven with a fake clock at 2× so the whole
///     thing is deterministic: no timer, no engine, no wall clock.
///     <para>
///         The model of the native side: JUCE pumps one 256-frame buffer every
///         5.33 wall-ms at 48 kHz; at tempo 2.0 each buffer consumes 10.67
///         song-ms, and <c>AudiblePosition</c> is the song time of the LAST
///         completed buffer (the stretcher's read-ahead correction makes it
///         exact at the buffer boundary, and it holds still until the next).
///         The timer wakes every 4 ms with ±0.6 ms of pool jitter.
///     </para>
/// </summary>
public sealed class PlayheadInterpolatorTests
{
    private const long TicksPerSecond = 10_000_000; // 100 ns, like Stopwatch on Windows
    private const double TicksPerMs = TicksPerSecond / 1000.0;

    private readonly ITestOutputHelper _out;

    public PlayheadInterpolatorTests(ITestOutputHelper output) => _out = output;

    /// <summary>The audible position the native side would report at wall time t.</summary>
    private static double NativeMsAt(double wallMs, double speed, double bufferWallMs = 256 * 1000.0 / 48_000)
    {
        var buffersDone = Math.Floor(wallMs / bufferWallMs);
        return buffersDone * bufferWallMs * speed;
    }

    private static long Ticks(double wallMs) => (long)Math.Round(wallMs * TicksPerMs);

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(0.5)]
    public void SteadyPlayback_ReportsAMonotonicPlayheadWithLowIncrementVarianceAndNoSnaps(double speed)
    {
        var interp = new PlayheadInterpolator(TicksPerSecond);
        var rng = new Random(20260830);
        var reports = new List<double>();
        var wallMs = 0.0;
        for (var i = 0; i < 2_500; i++) // ~10 s of playback at 250 Hz
        {
            reports.Add(interp.Tick(NativeMsAt(wallMs, speed), Ticks(wallMs), speed));
            wallMs += 4.0 + (rng.NextDouble() - 0.5) * 1.2;
        }

        // Skip the first 100 ticks: the low-pass settles the initial
        // quantization offset there, by design.
        var increments = new List<double>();
        for (var i = 101; i < reports.Count; i++)
        {
            Assert.True(reports[i] >= reports[i - 1],
                $"Playhead went backwards at tick {i}: {reports[i - 1]:F3} -> {reports[i]:F3}");
            increments.Add(reports[i] - reports[i - 1]);
        }

        var mean = increments.Average();
        var stdDev = Math.Sqrt(increments.Sum(x => (x - mean) * (x - mean)) / increments.Count);
        _out.WriteLine($"speed {speed}: mean increment {mean:F3} song-ms/tick, stddev {stdDev:F3}, " +
                       $"cv {stdDev / mean:P1}, snaps {interp.SnapCount}");

        Assert.Equal(0, interp.SnapCount);
        // 4 wall-ms per tick × speed, ±0.6 ms of timer jitter is the ONLY
        // variance we accept; the buffer staircase must not show through.
        Assert.InRange(mean, 4.0 * speed * 0.97, 4.0 * speed * 1.03);
        Assert.True(stdDev / mean < 0.12,
            $"Per-tick increments vary too much (cv {stdDev / mean:P1}) — the native buffer staircase is leaking through.");
    }

    [Fact]
    public void AtDoubleSpeed_TheReportedPlayheadTracksTheAudibleBusWithinOneBuffer()
    {
        const double speed = 2.0;
        var interp = new PlayheadInterpolator(TicksPerSecond);
        var worst = 0.0;
        for (var wallMs = 0.0; wallMs < 10_000; wallMs += 4.0)
        {
            var report = interp.Tick(NativeMsAt(wallMs, speed), Ticks(wallMs), speed);
            if (wallMs < 400) continue;
            worst = Math.Max(worst, Math.Abs(report - wallMs * speed));
        }
        _out.WriteLine($"worst |report - true song time| = {worst:F2} ms");
        // One device buffer at 2× is 10.67 song-ms; the low-pass may not fall
        // behind by more than that (a lagging playhead is a playhead that
        // draws notes after they have sounded).
        Assert.True(worst <= 256 * 1000.0 / 48_000 * speed + 0.5, $"worst error {worst:F2} ms");
    }

    [Fact]
    public void ExtrapolateBetweenTicks_IsContinuousWithTheTickReports()
    {
        const double speed = 2.0;
        var interp = new PlayheadInterpolator(TicksPerSecond);
        for (var wallMs = 0.0; wallMs < 1_000; wallMs += 4.0)
            interp.Tick(NativeMsAt(wallMs, speed), Ticks(wallMs), speed);

        // The last tick was at 996 ms; a render frame 2.5 ms later.
        var last = interp.Tick(NativeMsAt(1_000, speed), Ticks(1_000), speed);
        Assert.True(interp.TryExtrapolate(Ticks(1_002.5), speed, out var frame));
        Assert.Equal(last + 2.5 * speed, frame, 6);

        // ...and exactly AT the tick it IS the tick's report (one consistent
        // sample: no fresh-report-with-stale-timestamp pairing possible).
        Assert.True(interp.TryExtrapolate(Ticks(1_000), speed, out var same));
        Assert.Equal(last, same, 9);
    }

    [Fact]
    public void AnOverlappedTickWithAnOlderTimestamp_NeverStepsBackwards()
    {
        const double speed = 2.0;
        var interp = new PlayheadInterpolator(TicksPerSecond);
        for (var wallMs = 0.0; wallMs < 1_000; wallMs += 4.0)
            interp.Tick(NativeMsAt(wallMs, speed), Ticks(wallMs), speed);

        var a = interp.Tick(NativeMsAt(1_004, speed), Ticks(1_004), speed);
        // A pool thread that read its timestamp BEFORE tick A ran, but got the
        // lock after it: System.Threading.Timer callbacks can overlap.
        var b = interp.Tick(NativeMsAt(1_004, speed), Ticks(1_001), speed);
        Assert.True(b >= a, $"overlapped tick stepped back: {a:F3} -> {b:F3}");
        Assert.True(interp.TryExtrapolate(Ticks(1_006), speed, out var after));
        Assert.True(after >= b);
    }

    [Fact]
    public void ADriftBeyondTheThreshold_SnapsAndCountsOnce()
    {
        var interp = new PlayheadInterpolator(TicksPerSecond);
        interp.Tick(0, Ticks(0), 1.0);
        interp.Tick(4, Ticks(4), 1.0);
        var snapped = interp.Tick(5_000, Ticks(8), 1.0); // a seek the service did not reset for
        Assert.Equal(5_000, snapped, 9);
        Assert.Equal(1, interp.SnapCount);
        Assert.True(interp.IsRunning);

        interp.Reset();
        Assert.False(interp.IsRunning);
        Assert.False(interp.TryExtrapolate(Ticks(12), 1.0, out _));
        Assert.Equal(7_000, interp.Tick(7_000, Ticks(12), 1.0), 9); // first tick of a burst: hard sync
    }
}
