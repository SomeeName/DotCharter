namespace DotCharter.Tone.Audio;

/// <summary>
/// Tells the device watchdog that the driver has stopped calling back. An ASIO
/// driver whose hardware is unplugged often keeps the device "open": JUCE never
/// closes it and the engine's cached sample rate stays non-zero, so
/// <c>SampleRate &gt; 0</c> alone reported a dead Focusrite as alive for minutes.
/// The render clock is advanced by every device callback,
/// paused transport or not, so a VALID clock that has not moved for
/// <see cref="StalledTicksBeforeDead"/> consecutive watchdog ticks means no
/// callback is running. Pure, so it is tested without a device.
/// </summary>
internal sealed class AudioCallbackStallDetector
{
    /// <summary>Consecutive non-advancing ticks (3 s apart) before the engine counts as dead.</summary>
    internal const int StalledTicksBeforeDead = 2;

    private object? _engine;
    private long _lastFrames = -1;
    private int _stalledTicks;

    /// <summary>
    /// Feeds one watchdog tick. <paramref name="engine"/> identifies the engine
    /// instance: a replaced engine starts a fresh count. An unreadable clock
    /// (<paramref name="clockValid"/> false) is no evidence either way and
    /// resets the count. Returns true while the callback is stalled.
    /// </summary>
    internal bool Observe(object? engine, bool clockValid, long renderFrames)
    {
        if (!ReferenceEquals(engine, _engine) || engine is null || !clockValid)
        {
            _engine = engine;
            _lastFrames = clockValid ? renderFrames : -1;
            _stalledTicks = 0;
            return false;
        }

        if (renderFrames != _lastFrames)
        {
            _lastFrames = renderFrames;
            _stalledTicks = 0;
            return false;
        }

        _stalledTicks++;
        return _stalledTicks >= StalledTicksBeforeDead;
    }
}
