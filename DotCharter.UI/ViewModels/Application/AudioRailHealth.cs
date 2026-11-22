namespace DotCharter.UI.ViewModels.Application;

/// <summary>What is wrong with audio, if anything. <see cref="None" /> is the quiet state: one health dot.</summary>
internal enum AudioRailProblem
{
    None,
    NoDevice,
    DeviceLost,
    Dropouts
}

/// <summary>
///     The status rail's audio health: the always-on "Audio: 48 kHz · 128 fr · buffer 2,7 ms" collapses to
///     one dot with the detail in its tooltip, and the full text only shows while something is wrong: no device
///     or no sample rate, a lost device, or dropouts in the last <see cref="DropoutMemoryMs" />.
///     Pure: the caller passes the clock, so a test needs no timer.
/// </summary>
internal sealed class AudioRailHealth
{
    /// <summary>How long one dropout keeps the rail in its problem state.</summary>
    internal const long DropoutMemoryMs = 30_000;

    private long _seenDropouts;
    private long _lastDropoutAtMs = long.MinValue;

    internal AudioRailProblem Evaluate(int sampleRate, int bufferFrames, bool deviceLost, long dropoutCount, long nowMs)
    {
        if (dropoutCount > _seenDropouts) _lastDropoutAtMs = nowMs;
        _seenDropouts = dropoutCount;

        if (deviceLost) return AudioRailProblem.DeviceLost;
        if (sampleRate <= 0 || bufferFrames <= 0) return AudioRailProblem.NoDevice;
        return _lastDropoutAtMs != long.MinValue && nowMs - _lastDropoutAtMs < DropoutMemoryMs
            ? AudioRailProblem.Dropouts
            : AudioRailProblem.None;
    }
}
