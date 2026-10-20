namespace DotCharter.Services.Audio.LivePlay;

/// <summary>
///     Accounts for gaps in the single-consumer input-capture stream that the
///     live-note detector drains once per UI tick.
///     <para>
///     The native capture ring is a fixed SPSC buffer (a fraction of a second
///     deep). The detection reader is <see cref="LivePlayDetectionPump"/>'s
///     dedicated AboveNormal-priority thread (it was a Background-priority UI
///     timer before); when it falls more than one ring behind — typically because the audio thread is
///     saturated running the master plus stem-group stretchers, or the CPU is
///     pegged by a stem-separation job — the ring skips ahead and the reported
///     absolute stream position jumps forward. Every skipped sample is audio the
///     detector never sees, so <see cref="GuitarInputAnalyzer"/> re-anchors and
///     loses its onset history: to the user, "note detection stopped working
///     with stem separation".
///     </para>
///     <para>
///     This monitor watches the reported stream positions, tallies dropped
///     samples, and raises a hysteretic <see cref="IsStarved"/> state so the UI
///     can surface an "input starved by processing" warning distinct from the
///     pure-digital-silence warning. Pure logic, no audio dependencies.
///     </para>
/// </summary>
public sealed class CaptureStreamMonitor
{
    private readonly int _sampleRate;
    private readonly int _clearAfterCleanTicks;
    private bool _initialized;
    private ulong _expected;
    private long _tickDropped;
    private int _cleanTicks;

    /// <param name="sampleRate">Capture sample rate, for the sample→ms helper.</param>
    /// <param name="clearAfterCleanTicks">Consecutive drop-free ticks required
    ///     before <see cref="IsStarved"/> falls back to false (hysteresis so the
    ///     UI warning does not flicker on a single recovered stall). At the 30 Hz
    ///     detection tick, the default ~1.5 s.</param>
    public CaptureStreamMonitor(int sampleRate, int clearAfterCleanTicks = 45)
    {
        _sampleRate = sampleRate > 0 ? sampleRate : 48000;
        _clearAfterCleanTicks = Math.Max(1, clearAfterCleanTicks);
    }

    /// <summary>Total samples the ring skipped (never delivered) since
    /// construction — a cheap cumulative health counter for logs.</summary>
    public long TotalDroppedSamples { get; private set; }

    /// <summary>Samples the ring skipped during the tick currently being
    /// drained. Reset by <see cref="EndTick"/>.</summary>
    public long TickDroppedSamples => _tickDropped;

    /// <summary>True while the capture stream is skipping samples faster than
    /// the reader consumes them. Hysteretic: stays set for a short cooldown
    /// after the last drop.</summary>
    public bool IsStarved { get; private set; }

    /// <summary>Register one streaming read within a tick.
    /// <paramref name="streamPosition"/> is the absolute capture-axis index of
    /// the first returned sample, <paramref name="count"/> the number returned.
    /// Returns the samples the ring skipped immediately before this read (0 when
    /// contiguous). The first call only anchors — a large starting position is
    /// the expected startup drain, not a drop.</summary>
    public long OnRead(ulong streamPosition, int count)
    {
        if (count <= 0) return 0;
        long dropped = 0;
        if (_initialized && streamPosition > _expected)
            dropped = (long)(streamPosition - _expected);
        _initialized = true;
        _expected = streamPosition + (ulong)count;
        if (dropped > 0)
        {
            _tickDropped += dropped;
            TotalDroppedSamples += dropped;
        }
        return dropped;
    }

    /// <summary>Close the current tick and update <see cref="IsStarved"/>.
    /// <paramref name="detectionActive"/> gates the state — starvation is only
    /// meaningful while the detector is actually judging; when inactive the
    /// state is forced clear. Returns true when <see cref="IsStarved"/> changed
    /// so the caller can log only on edges.</summary>
    public bool EndTick(bool detectionActive)
    {
        var was = IsStarved;
        if (!detectionActive)
        {
            IsStarved = false;
            _cleanTicks = 0;
        }
        else if (_tickDropped > 0)
        {
            IsStarved = true;
            _cleanTicks = 0;
        }
        else if (IsStarved && ++_cleanTicks >= _clearAfterCleanTicks)
        {
            IsStarved = false;
            _cleanTicks = 0;
        }

        _tickDropped = 0;
        return IsStarved != was;
    }

    /// <summary>Milliseconds of audio a sample count represents at the capture
    /// sample rate (for human-readable log lines).</summary>
    public double SamplesToMs(long samples) => samples * 1000.0 / _sampleRate;
}
