namespace DotCharter.Tone.Audio;

/// <summary>
/// Notices the moment the engine reports that its device closed, so an unplug
/// is declared lost within a second instead of after the watchdog's six-second
/// stall heartbeat.
/// <para>
/// When an ASIO interface is unplugged, JUCE stops the device and the engine
/// publishes a sample rate of 0 for a moment. The driver then asks for a reset,
/// JUCE re-opens the absent device and publishes the old rate again, but no
/// callback ever follows, so the render clock stays unreadable. The three-second
/// watchdog tick almost always misses that short zero; the health monitor's
/// 20 ms poll sees it, and feeds it in here.
/// </para>
/// <para>
/// A closed device is not always a lost one: changing the buffer size in the
/// driver's own panel also closes and re-opens it. So a closure only counts
/// once <see cref="ConfirmMs"/> has passed without the clock running again.
/// Pure (the time is passed in), so it is tested without a device.
/// </para>
/// </summary>
internal sealed class AudioDeviceClosedDetector
{
    /// <summary>How long a closed device has to stay silent before it counts as lost.</summary>
    internal const long ConfirmMs = 750;

    private object? _engine;
    private bool _wasOpen;
    private long _closedAtMs = -1;
    private long? _firstFramesAfterReopen;

    /// <summary>
    /// Feeds one poll. Returns true exactly once per closure, when the device
    /// closed and has not started calling back again within <see cref="ConfirmMs"/>.
    /// A new engine instance starts fresh.
    /// </summary>
    internal bool Observe(object? engine, int sampleRate, bool clockValid, long renderFrames, long nowMs)
    {
        if (!ReferenceEquals(engine, _engine) || engine is null)
        {
            _engine = engine;
            _wasOpen = engine is not null && sampleRate > 0;
            _closedAtMs = -1;
            _firstFramesAfterReopen = null;
            return false;
        }

        if (_closedAtMs < 0)
        {
            if (sampleRate > 0)
            {
                _wasOpen = true;
                return false;
            }
            if (!_wasOpen) return false;
            _wasOpen = false;
            _closedAtMs = nowMs;
            _firstFramesAfterReopen = null;
            return false;
        }

        if (CallsBackAgain(sampleRate, clockValid, renderFrames))
        {
            _closedAtMs = -1;
            _wasOpen = true;
            return false;
        }

        if (nowMs - _closedAtMs < ConfirmMs) return false;
        _closedAtMs = -1;
        return true;
    }

    // The device re-opened and its clock moved between two readings: a reset
    // that worked, not an unplug.
    private bool CallsBackAgain(int sampleRate, bool clockValid, long renderFrames)
    {
        if (sampleRate <= 0 || !clockValid)
        {
            _firstFramesAfterReopen = null;
            return false;
        }
        if (_firstFramesAfterReopen is not { } first)
        {
            _firstFramesAfterReopen = renderFrames;
            return false;
        }
        return renderFrames != first;
    }
}
