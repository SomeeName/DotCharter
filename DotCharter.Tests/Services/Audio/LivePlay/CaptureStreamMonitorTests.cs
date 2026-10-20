using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

public sealed class CaptureStreamMonitorTests
{
    private const int Sr = 48000;

    [Fact]
    public void FirstRead_AnchorsWithoutCountingAsDrop()
    {
        var m = new CaptureStreamMonitor(Sr);
        // A large starting position is the expected startup drain, not a drop.
        Assert.Equal(0, m.OnRead(streamPosition: 1_000_000, count: 1600));
        Assert.Equal(0, m.TotalDroppedSamples);
    }

    [Fact]
    public void ContiguousReads_NeverDrop_AndNeverStarve()
    {
        var m = new CaptureStreamMonitor(Sr);
        ulong pos = 500;
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(0, m.OnRead(pos, 1600));
            pos += 1600;
            Assert.False(m.EndTick(detectionActive: true));
        }

        Assert.False(m.IsStarved);
        Assert.Equal(0, m.TotalDroppedSamples);
    }

    [Fact]
    public void DrainBurstAcrossManyReads_IsContiguous_NoDrops()
    {
        // A tick that woke late and drains one ring's worth across several
        // reads (each starting exactly where the previous ended) is NOT a
        // drop — the reader caught up without the ring skipping.
        var m = new CaptureStreamMonitor(Sr);
        ulong pos = 0;
        var reads = new[] { 32768, 32768, 20000, 4000 }; // > one ring, split up
        foreach (var n in reads)
        {
            Assert.Equal(0, m.OnRead(pos, n));
            pos += (ulong)n;
        }

        Assert.Equal(0, m.TickDroppedSamples);
        Assert.False(m.EndTick(detectionActive: true));
        Assert.False(m.IsStarved);
    }

    [Fact]
    public void StreamPositionJump_CountsGapAndStarves()
    {
        var m = new CaptureStreamMonitor(Sr);
        m.OnRead(1000, 1600);      // anchor -> expected next = 2600
        var dropped = m.OnRead(50_000, 1600); // ring skipped ahead
        Assert.Equal(50_000 - 2600, dropped);
        Assert.Equal(dropped, m.TickDroppedSamples);
        Assert.Equal(dropped, m.TotalDroppedSamples);

        Assert.True(m.EndTick(detectionActive: true));
        Assert.True(m.IsStarved);
    }

    [Fact]
    public void MultipleDropsInOneTick_Aggregate()
    {
        var m = new CaptureStreamMonitor(Sr);
        m.OnRead(0, 1000);           // expected next = 1000
        m.OnRead(5000, 1000);        // dropped 4000, expected next = 6000
        m.OnRead(9000, 1000);        // dropped 3000
        Assert.Equal(7000, m.TickDroppedSamples);
        Assert.True(m.EndTick(detectionActive: true));
        Assert.True(m.IsStarved);
        Assert.Equal(7000, m.TotalDroppedSamples);
    }

    [Fact]
    public void Starvation_ClearsAfterCleanTicks_WithHysteresis()
    {
        var m = new CaptureStreamMonitor(Sr, clearAfterCleanTicks: 3);
        m.OnRead(0, 1000);
        m.OnRead(9000, 1000);        // drop
        Assert.True(m.EndTick(true)); // -> starved

        // Clean ticks below the threshold keep it set (no flicker).
        ulong pos = 10_000;
        Assert.False(m.EndTick(true)); Assert.True(m.IsStarved); // clean 1
        m.OnRead(pos, 1000); pos += 1000;
        Assert.False(m.EndTick(true)); Assert.True(m.IsStarved); // clean 2
        m.OnRead(pos, 1000);
        // clean 3 reaches threshold -> clears
        var changed = m.EndTick(true);
        Assert.True(changed);
        Assert.False(m.IsStarved);
    }

    [Fact]
    public void FreshDrop_ResetsCleanTickCountdown()
    {
        var m = new CaptureStreamMonitor(Sr, clearAfterCleanTicks: 3);
        m.OnRead(0, 1000);
        m.OnRead(9000, 1000);
        m.EndTick(true);              // starved

        m.OnRead(10_000, 1000); m.EndTick(true); // clean 1
        m.OnRead(11_000, 1000); m.EndTick(true); // clean 2
        // Another drop resets the countdown.
        m.OnRead(50_000, 1000);
        Assert.False(m.EndTick(true)); // still starved, no edge change
        Assert.True(m.IsStarved);

        // Now it takes a full 3 clean ticks again.
        m.OnRead(51_000, 1000); Assert.False(m.EndTick(true)); Assert.True(m.IsStarved);
        m.OnRead(52_000, 1000); Assert.False(m.EndTick(true)); Assert.True(m.IsStarved);
        m.OnRead(53_000, 1000); Assert.True(m.EndTick(true)); Assert.False(m.IsStarved);
    }

    [Fact]
    public void InactiveSession_ForcesNotStarved()
    {
        var m = new CaptureStreamMonitor(Sr);
        m.OnRead(0, 1000);
        m.OnRead(9000, 1000);        // drop while active-ish
        Assert.True(m.EndTick(true));
        Assert.True(m.IsStarved);

        // Session ends: state forced clear regardless of prior drops.
        Assert.True(m.EndTick(detectionActive: false));
        Assert.False(m.IsStarved);
    }

    [Fact]
    public void DropWhileInactive_NeverStarves()
    {
        var m = new CaptureStreamMonitor(Sr);
        m.OnRead(0, 1000);
        m.OnRead(9000, 1000);        // drop, but detection not active
        Assert.False(m.EndTick(detectionActive: false));
        Assert.False(m.IsStarved);
        // The cumulative counter still records it for diagnostics.
        Assert.Equal(8000, m.TotalDroppedSamples);
    }

    [Fact]
    public void ZeroOrNegativeCount_IsIgnored()
    {
        var m = new CaptureStreamMonitor(Sr);
        Assert.Equal(0, m.OnRead(1000, 0));
        Assert.Equal(0, m.OnRead(1000, -5));
        // Still uninitialized: the next real read anchors without a drop.
        Assert.Equal(0, m.OnRead(9_999_999, 100));
        Assert.Equal(0, m.TotalDroppedSamples);
    }

    [Fact]
    public void SamplesToMs_ConvertsAtSampleRate()
    {
        var m = new CaptureStreamMonitor(48000);
        Assert.Equal(1000.0, m.SamplesToMs(48000), 6);
        Assert.Equal(500.0, m.SamplesToMs(24000), 6);
    }

    [Fact]
    public void BackwardsOrEqualStreamPosition_IsNotADrop()
    {
        // Defensive: a stale/duplicate position must never fabricate a drop.
        var m = new CaptureStreamMonitor(Sr);
        m.OnRead(1000, 1000);        // expected next = 2000
        Assert.Equal(0, m.OnRead(2000, 1000)); // exactly contiguous
        Assert.Equal(0, m.OnRead(1500, 1000)); // behind expected -> clamp to 0
        Assert.Equal(0, m.TotalDroppedSamples);
    }
}
