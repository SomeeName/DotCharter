// Partial: the interpolated, latency-compensated playhead — CurrentTimeMs, RawPlayheadMs, the output-latency estimate and the device readbacks.

namespace DotCharter.Tone.Audio;

public sealed partial class JuceAudioPlaybackService
{
    // Native AudiblePosition only updates as fast as JUCE pumps audio
    // callbacks (~5 ms at 48 kHz/256-frame buf) and only at the wall-clock
    // rate: at PlaybackSpeed=2.0 the raw playhead moves in twice-as-large
    // steps, which reads as 2D/3D stutter. The 250 Hz tick feeds the native
    // reading into PlayheadInterpolator (extrapolate by elapsed × speed,
    // low-pass toward native) and CurrentTimeMs extrapolates between ticks
    // from its ONE immutable sample, so a render-thread read can never pair a
    // fresh report with a stale timestamp.
    private readonly Engine.PlayheadInterpolator _playhead = new();

    // Cached last-known length + position, used so CurrentTimeMs can keep
    // returning a sensible value during a race with LoadAudioAsync (which
    // disposes _source on a worker thread while the render thread is still
    // reading the playhead). Without this, the renderer throws
    // ObjectDisposedException and brings the whole app down. The position is
    // stamped with the interpolator's epoch, so a read that straddles a seek
    // cannot put the pre-seek position back.
    private readonly Engine.PlayheadFloor _lastKnownPosition = new();

    private double _lastKnownLengthMs;

    // The same playhead BEFORE latency compensation: the song time the engine is
    // currently writing INTO the output buffer, i.e. one output latency ahead of
    // what the user hears. Refreshed by the position tick, and it is what the
    // click scheduler anchors on — see TryScheduleClick.
    private double _rawPlayheadMs;

    private readonly Func<double, double> _latencyCompensator;

    private double ApplyLatencyCompensation(double rawMs) =>
        Engine.PlayheadReadback.CompensateLatency(rawMs, EstimatedOutputLatencyMs + AudioDelayMs, _playbackSpeed);

    public double CurrentTimeMs =>
        Engine.PlayheadReadback.Read(_source, _playhead, _lastKnownPosition, _playbackSpeed,
            _lastKnownLengthMs, _latencyCompensator, System.Diagnostics.Stopwatch.GetTimestamp());

    public double AudioDelayMs { get; set; }

    public double EstimatedOutputLatencyMs =>
        Engine.PlayheadReadback.EstimateOutputLatency(_engine, _latencyCache, System.Diagnostics.Stopwatch.GetTimestamp());

    private readonly Engine.OutputLatencyCache _latencyCache = new();

    public double MeasuredOutputLatencyMs => EstimatedOutputLatencyMs;

    public int ActiveSampleRate => SafeRead(e => e.SampleRate);

    public int ActualBufferFrames => SafeRead(e => e.BufferFrames);

    /// <inheritdoc />
    public bool TryGetCallbackLoad(out AudioCallbackLoad load)
    {
        load = default;
        var engine = _engine;
        if (engine == null) return false;
        try
        {
            if (!engine.TryGetCallbackLoad(out var native)) return false;
            load = new AudioCallbackLoad(native.Average, native.Peak);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public double RawPlayheadMs => _rawPlayheadMs;

    public long OutputFramesWritten => -1;
}
