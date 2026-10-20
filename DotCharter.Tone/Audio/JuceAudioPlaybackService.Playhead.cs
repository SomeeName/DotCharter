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

    private double ApplyLatencyCompensation(double rawMs)
    {
        var comp = EstimatedOutputLatencyMs + AudioDelayMs;
        var adj = rawMs - comp;
        return adj < 0.0 ? 0.0 : adj;
    }

    public double CurrentTimeMs
    {
        get
        {
            var src = _source;
            if (src == null) return _lastKnownPosition.Ms;
            try
            {
                // Prefer the smooth interpolated clock so that consumers
                // polling at render rate (e.g. the chart playhead at 60+
                // FPS) get sub-tick precision instead of being snapped to
                // the 10 ms timer cadence. At high playback speeds (2x+)
                // a 10 ms snap would otherwise look visibly jittery.
                var speed = _playbackSpeed <= 0 ? 1.0 : _playbackSpeed;
                if (_playhead.TryExtrapolate(System.Diagnostics.Stopwatch.GetTimestamp(), speed, out var extrap,
                        out var epoch))
                {
                    var lat = ApplyLatencyCompensation(extrap);
                    var len = _lastKnownLengthMs;
                    if (len > 0 && lat > len) lat = len;
                    if (lat < 0) lat = 0;
                    // Monotonic during playback: the timer's low-pass
                    // already prevents back-correction, but defend
                    // against the rare case where latency compensation
                    // shifts (e.g. mirror device opens mid-playback).
                    // Only within one epoch: a sample from before a seek
                    // reports the seek's position instead.
                    return _lastKnownPosition.Clamp(lat, epoch);
                }
                // `epoch` was read before the native position below, so a
                // seek landing in between makes this reading the stale one.
                var raw = src.AudiblePosition.TotalMilliseconds;
                var adj = ApplyLatencyCompensation(raw);
                var len2 = _lastKnownLengthMs;
                if (len2 > 0 && adj > len2) adj = len2;
                return _lastKnownPosition.Publish(adj, epoch);
            }
            catch (ObjectDisposedException)
            {
                // Source was disposed by LoadAudioAsync/FreeAudio while a
                // UI/render-thread caller was reading CurrentTimeMs. Return
                // the last known good value; the next tick will pick up the
                // fresh source.
                return _lastKnownPosition.Ms;
            }
            catch
            {
                return _lastKnownPosition.Ms;
            }
        }
    }

    public double AudioDelayMs { get; set; }

    // Output latency reported by the active device (samples) converted to ms
    // and combined with any user-tunable AudioDelayMs. Subtracted from the
    // raw audible position so the on-screen playhead matches what's hitting
    // the speakers right now (audio in the device buffer hasn't been heard
    // yet, so the playhead would otherwise lead the audio).
    //
    // Read on every 250 Hz position tick and once per frame by both renderers
    // (through CurrentTimeMs), so the two P/Invokes behind it are cached per
    // engine instance for OutputLatencyCache.RefreshInterval — a rebuilt engine
    // misses on its reference, and an in-place Reconfigure invalidates.
    public double EstimatedOutputLatencyMs
    {
        get
        {
            var eng = _engine;
            if (eng == null) return 0.0;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_latencyCache.TryGet(eng, now, out var cached)) return cached;
            try
            {
                var sr = eng.SampleRate;
                var ms = sr <= 0 ? 0.0 : eng.OutputLatencySamples * 1000.0 / sr;
                _latencyCache.Store(eng, now, ms);
                return ms;
            }
            catch { return 0.0; }
        }
    }

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
