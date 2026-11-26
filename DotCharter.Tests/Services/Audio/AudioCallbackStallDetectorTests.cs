using DotCharter.Tone.Audio;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
/// #273: an unplugged ASIO interface stopped calling back while the engine's
/// cached sample rate stayed non-zero, so the watchdog saw a live engine and
/// playback stayed dead for minutes. The render clock moves on every device
/// callback; a valid clock that stops moving is the dead-driver signal.
/// </summary>
public sealed class AudioCallbackStallDetectorTests
{
    private static readonly object Engine = new();

    [Fact]
    public void AnAdvancingClock_IsNeverStalled()
    {
        var d = new AudioCallbackStallDetector();
        for (var i = 0; i < 10; i++)
            Assert.False(d.Observe(Engine, clockValid: true, renderFrames: i * 144_000, deviceOpen: true));
    }

    [Fact]
    public void AFrozenClock_IsStalledAfterTheThreshold_AndRecoversWhenItMoves()
    {
        var d = new AudioCallbackStallDetector();
        Assert.False(d.Observe(Engine, true, 5_000, deviceOpen: true));
        for (var i = 1; i < AudioCallbackStallDetector.StalledTicksBeforeDead; i++)
            Assert.False(d.Observe(Engine, true, 5_000, deviceOpen: true));

        Assert.True(d.Observe(Engine, true, 5_000, deviceOpen: true));
        Assert.True(d.Observe(Engine, true, 5_000, deviceOpen: true));

        Assert.False(d.Observe(Engine, true, 6_000, deviceOpen: true));
    }

    [Fact]
    public void AReplacedEngine_StartsAFreshCount()
    {
        var d = new AudioCallbackStallDetector();
        for (var i = 0; i <= AudioCallbackStallDetector.StalledTicksBeforeDead; i++)
            d.Observe(Engine, true, 5_000, deviceOpen: true);

        var replacement = new object();
        Assert.False(d.Observe(replacement, true, 5_000, deviceOpen: true));
        Assert.False(d.Observe(replacement, true, 5_000 + 144_000, deviceOpen: true));
    }

    [Fact]
    public void AnUnreadableClockOnAClosedDevice_IsNoEvidence()
    {
        var d = new AudioCallbackStallDetector();
        for (var i = 0; i < 10; i++)
            Assert.False(d.Observe(Engine, clockValid: false, renderFrames: 0, deviceOpen: false));
    }

    /// <summary>
    /// Unplugging an ASIO interface makes the driver request a reset;
    /// JUCE re-opens the (now absent) device and restarts it anyway, which
    /// rewinds the render clock to "no block yet" while the sample rate stays
    /// published. No callback follows, so the clock never becomes readable.
    /// </summary>
    [Fact]
    public void AnOpenDeviceWhoseClockNeverBecomesReadableAgain_IsStalled()
    {
        var d = new AudioCallbackStallDetector();
        Assert.False(d.Observe(Engine, true, 5_000, deviceOpen: true));
        Assert.False(d.Observe(Engine, true, 149_000, deviceOpen: true));

        // The reset: the clock is unreadable from here on, the device still open.
        Assert.False(d.Observe(Engine, clockValid: false, renderFrames: 0, deviceOpen: true));
        for (var i = 1; i < AudioCallbackStallDetector.StalledTicksBeforeDead; i++)
            Assert.False(d.Observe(Engine, clockValid: false, renderFrames: 0, deviceOpen: true));
        Assert.True(d.Observe(Engine, clockValid: false, renderFrames: 0, deviceOpen: true));

        // The replugged device calls back again.
        Assert.False(d.Observe(Engine, true, 512, deviceOpen: true));
    }

    [Fact]
    public void WaitForCallbackProgress_NeedsAReadableClockThatMoves()
    {
        long now = 0;
        var reads = new Queue<(bool, long)>([(false, 0), (true, 512), (true, 512), (true, 1_024)]);
        Assert.True(AudioCallbackStallDetector.WaitForCallbackProgress(
            () => reads.Count > 0 ? reads.Dequeue() : (true, 1_024), () => now, ms => now += ms, budgetMs: 1_000, pollMs: 20));
        Assert.Empty(reads);
    }

    [Fact]
    public void WaitForCallbackProgress_FailsWhenTheRebuiltDeviceNeverCallsBack()
    {
        long now = 0;
        var polls = 0;
        Assert.False(AudioCallbackStallDetector.WaitForCallbackProgress(
            () => { polls++; return (false, 0L); }, () => now, ms => now += ms, budgetMs: 1_000, pollMs: 20));
        Assert.Equal(51, polls);

        now = 0;
        Assert.False(AudioCallbackStallDetector.WaitForCallbackProgress(
            () => (true, 512L), () => now, ms => now += ms, budgetMs: 1_000, pollMs: 20));
    }
}
