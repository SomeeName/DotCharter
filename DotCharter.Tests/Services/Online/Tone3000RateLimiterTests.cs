using DotCharter.Tone.Online.Tone3000;

namespace DotCharter.Tests.Services.Online;

/// <summary>Pure sliding-window tests for <see cref="Tone3000RateLimiter" />
/// (injected time — no clocks, per test conventions).</summary>
public sealed class Tone3000RateLimiterTests
{
    [Fact]
    public void UnderBudget_PermitsImmediately()
    {
        var limiter = new Tone3000RateLimiter(maxRequests: 3, windowMs: 1000);

        Assert.Equal(0, limiter.MsUntilNextPermit(0));
        limiter.Record(0);
        limiter.Record(10);
        Assert.Equal(0, limiter.MsUntilNextPermit(20));
    }

    [Fact]
    public void AtBudget_WaitsUntilOldestLeavesWindow()
    {
        var limiter = new Tone3000RateLimiter(maxRequests: 2, windowMs: 1000);
        limiter.Record(100);
        limiter.Record(200);

        // Full at t=300: the oldest (t=100) leaves the window at t=1100.
        Assert.Equal(800, limiter.MsUntilNextPermit(300));
    }

    [Fact]
    public void OldEntries_AreEvicted_FreeingBudget()
    {
        var limiter = new Tone3000RateLimiter(maxRequests: 2, windowMs: 1000);
        limiter.Record(0);
        limiter.Record(1);

        Assert.True(limiter.MsUntilNextPermit(500) > 0);
        Assert.Equal(0, limiter.MsUntilNextPermit(1001)); // t=0 evicted
    }

    [Fact]
    public void DefaultBudget_StaysUnderTheServer100PerMinute()
    {
        var limiter = new Tone3000RateLimiter();
        for (var i = 0; i < 80; i++)
        {
            Assert.Equal(0, limiter.MsUntilNextPermit(i));
            limiter.Record(i);
        }

        // The 81st request inside the same minute must wait — headroom below 100.
        Assert.True(limiter.MsUntilNextPermit(100) > 0);
    }
}
