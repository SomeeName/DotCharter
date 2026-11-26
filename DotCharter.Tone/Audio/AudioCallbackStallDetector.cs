namespace DotCharter.Tone.Audio;

/// <summary>
/// Tells the device watchdog that the driver has stopped calling back. An ASIO
/// driver whose hardware is unplugged often keeps the device "open": JUCE never
/// closes it and the engine's cached sample rate stays non-zero, so
/// <c>SampleRate &gt; 0</c> alone reported a dead Focusrite as alive for minutes.
/// The render clock is advanced by every device callback,
/// paused transport or not, so a clock that has not moved for
/// <see cref="StalledTicksBeforeDead"/> consecutive watchdog ticks means no
/// callback is running. Pure, so it is tested without a device.
/// <para>
/// The clock is ALSO unreadable while the device is open. An unplug makes
/// the ASIO driver post a reset request; JUCE closes the device, re-opens it
/// (which fails without the hardware) and calls <c>audioDeviceAboutToStart</c>
/// again anyway, which rewinds the render clock to "no block rendered yet" and
/// republishes the old sample rate. No callback ever follows. An unreadable
/// clock on an OPEN device is therefore a frozen clock, not missing evidence;
/// only a closed device (sample rate 0, already dead on its own) is no evidence.
/// </para>
/// </summary>
internal sealed class AudioCallbackStallDetector
{
    /// <summary>Consecutive non-advancing ticks (3 s apart) before the engine counts as dead.</summary>
    internal const int StalledTicksBeforeDead = 2;

    // Clock readings that are not a frame count. Distinct, so moving from one to
    // the other counts as progress (a fresh observation) rather than a stall.
    private const long NoBlockRendered = -1;
    private const long NoEvidence = long.MinValue;

    private object? _engine;
    private long _lastFrames = NoEvidence;
    private int _stalledTicks;

    /// <summary>
    /// Feeds one watchdog tick. <paramref name="engine"/> identifies the engine
    /// instance: a replaced engine starts a fresh count. <paramref name="deviceOpen"/>
    /// is whether the engine reports an open device (sample rate &gt; 0). An
    /// unreadable clock (<paramref name="clockValid"/> false) on an open device
    /// counts as a clock stuck before its first block; on a closed device it is
    /// no evidence and resets the count. Returns true while the callback is stalled.
    /// </summary>
    internal bool Observe(object? engine, bool clockValid, long renderFrames, bool deviceOpen)
    {
        var frames = clockValid ? renderFrames : deviceOpen ? NoBlockRendered : NoEvidence;
        if (!ReferenceEquals(engine, _engine) || engine is null || frames == NoEvidence)
        {
            _engine = engine;
            _lastFrames = frames;
            _stalledTicks = 0;
            return false;
        }

        if (frames != _lastFrames)
        {
            _lastFrames = frames;
            _stalledTicks = 0;
            return false;
        }

        _stalledTicks++;
        return _stalledTicks >= StalledTicksBeforeDead;
    }

    /// <summary>
    /// After an engine rebuild, waits (bounded) until the new device has
    /// rendered and then rendered again: a valid clock that moves. A rebuilt
    /// engine whose driver opened but never calls back is not a recovery, and
    /// <see cref="Observe"/> cannot tell on the first tick of a new engine.
    /// <paramref name="readClock"/> is the engine's <c>TryGetRenderClock</c>.
    /// </summary>
    internal static bool WaitForCallbackProgress(
        Func<(bool Valid, long Frames)> readClock,
        Func<long> nowMs,
        Action<int> sleepMs,
        long budgetMs,
        int pollMs)
    {
        var deadline = nowMs() + budgetMs;
        long? first = null;
        while (true)
        {
            var (valid, frames) = readClock();
            if (valid)
            {
                if (first is null) first = frames;
                else if (frames != first) return true;
            }
            if (nowMs() >= deadline) return false;
            sleepMs(pollMs);
        }
    }
}
