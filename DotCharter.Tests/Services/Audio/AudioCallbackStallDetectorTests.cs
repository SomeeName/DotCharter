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
            Assert.False(d.Observe(Engine, clockValid: true, renderFrames: i * 144_000));
    }

    [Fact]
    public void AFrozenClock_IsStalledAfterTheThreshold_AndRecoversWhenItMoves()
    {
        var d = new AudioCallbackStallDetector();
        Assert.False(d.Observe(Engine, true, 5_000));
        for (var i = 1; i < AudioCallbackStallDetector.StalledTicksBeforeDead; i++)
            Assert.False(d.Observe(Engine, true, 5_000));

        Assert.True(d.Observe(Engine, true, 5_000));
        Assert.True(d.Observe(Engine, true, 5_000));

        Assert.False(d.Observe(Engine, true, 6_000));
    }

    [Fact]
    public void AReplacedEngine_StartsAFreshCount()
    {
        var d = new AudioCallbackStallDetector();
        for (var i = 0; i <= AudioCallbackStallDetector.StalledTicksBeforeDead; i++)
            d.Observe(Engine, true, 5_000);

        var replacement = new object();
        Assert.False(d.Observe(replacement, true, 5_000));
        Assert.False(d.Observe(replacement, true, 5_000 + 144_000));
    }

    [Fact]
    public void AnUnreadableClock_IsNoEvidence()
    {
        var d = new AudioCallbackStallDetector();
        for (var i = 0; i < 10; i++)
            Assert.False(d.Observe(Engine, clockValid: false, renderFrames: 0));
    }
}
