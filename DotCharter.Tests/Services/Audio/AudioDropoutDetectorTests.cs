using DotCharter.Tone.Audio.Diagnostics;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The dropout detector turns the engine's render clock ({rendered frames,
///     QPC tick}) plus a managed QPC "now" into stall edges. These pin the
///     staleness maths and the gap-edge state machine with injected clocks —
///     no device, no threads.
/// </summary>
public class AudioDropoutDetectorTests
{
    private const long Tps = 10_000_000; // 10 MHz QPC-like clock: 1 tick = 100 ns
    private const int SampleRate = 48_000;
    private const int Block = 256; // 48k/256 => 5.333 ms block period, threshold = max(12, 16) = 16 ms

    private static long Ms(double ms) => (long)(ms * Tps / 1000.0);

    [Fact]
    public void HealthyCallback_FreshPublish_NeverFlagsAGap()
    {
        var d = new AudioDropoutDetector();
        // Callback published 2 ms ago — well under one block period.
        var host = Ms(1000);
        var now = host + Ms(2);
        var poll = d.Observe(true, host, now, Tps, SampleRate, Block);

        Assert.False(poll.InGap);
        Assert.False(poll.GapStarted);
        Assert.Equal(2.0, poll.StalenessMs, 3);
    }

    [Fact]
    public void StaleCallback_BeyondThreshold_StartsThenEndsAGap()
    {
        var d = new AudioDropoutDetector();
        var host = Ms(1000);

        // 40 ms since the last publish — the audio thread is clearly stalled.
        var start = d.Observe(true, host, host + Ms(40), Tps, SampleRate, Block);
        Assert.True(start.GapStarted);
        Assert.True(start.InGap);
        Assert.False(start.GapEnded);
        Assert.Equal(40.0, start.StalenessMs, 2);

        // A fresh publish resolves it.
        var newHost = host + Ms(45);
        var end = d.Observe(true, newHost, newHost + Ms(1), Tps, SampleRate, Block);
        Assert.True(end.GapEnded);
        Assert.False(end.InGap);
        Assert.False(end.GapStarted);
    }

    [Fact]
    public void GapStartIsRaisedExactlyOnce_WhileTheStallPersists()
    {
        var d = new AudioDropoutDetector();
        var host = Ms(1000);

        var first = d.Observe(true, host, host + Ms(30), Tps, SampleRate, Block);
        var second = d.Observe(true, host, host + Ms(50), Tps, SampleRate, Block);

        Assert.True(first.GapStarted);
        Assert.False(second.GapStarted); // still the same gap
        Assert.True(second.InGap);
        Assert.True(second.StalenessMs > first.StalenessMs);
    }

    [Fact]
    public void JustUnderTheThreshold_IsNotAGap()
    {
        var d = new AudioDropoutDetector();
        var host = Ms(1000);
        // Threshold at 48k/256 is 16 ms (3 * 5.333). 15 ms is jitter, not a stall.
        var poll = d.Observe(true, host, host + Ms(15), Tps, SampleRate, Block);
        Assert.False(poll.InGap);
        Assert.Equal(16.0, poll.ThresholdMs, 1);
    }

    [Fact]
    public void InvalidClock_ClearsAnActiveGap_AndReportsNothing()
    {
        var d = new AudioDropoutDetector();
        var host = Ms(1000);
        var start = d.Observe(true, host, host + Ms(40), Tps, SampleRate, Block);
        Assert.True(start.InGap);

        // Device closed between devices: clock invalid.
        var closed = d.Observe(false, 0, 0, Tps, SampleRate, Block);
        Assert.True(closed.GapEnded);
        Assert.False(closed.InGap);
        Assert.Equal(0, closed.StalenessMs);
    }

    [Fact]
    public void ClockSkew_NegativeStaleness_IsClampedToZero()
    {
        var d = new AudioDropoutDetector();
        var host = Ms(1000);
        var poll = d.Observe(true, host, host - Ms(1), Tps, SampleRate, Block);
        Assert.Equal(0, poll.StalenessMs);
        Assert.False(poll.InGap);
    }

    [Fact]
    public void SmallBlock_ThresholdRespectsTheFloor_NotThreeBlockPeriods()
    {
        var d = new AudioDropoutDetector();
        var host = Ms(1000);
        // 64-frame block at 48k = 1.33 ms; 3x = 4 ms, but the 12 ms floor wins.
        var poll = d.Observe(true, host, host + Ms(5), Tps, SampleRate, blockFrames: 64);
        Assert.Equal(AudioDropoutDetector.GapFloorMs, poll.ThresholdMs, 3);
        Assert.False(poll.InGap); // 5 ms < 12 ms floor
    }
}
