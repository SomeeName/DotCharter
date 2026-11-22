namespace DotCharter.Services.Audio.LivePlay;

/// <summary>Freezes the capture-to-song mapping at EOF until final detector evidence is available.</summary>
public sealed class LivePlayCompletion
{
    private ulong _anchorSample;
    private double _anchorMs;
    private int _sampleRate;
    private double _speed;
    private ulong _requiredSample;
    private bool _anchored;
    private bool _syntheticAnchor;
    private long _beganAt;
    private long _progressAt;
    private ulong _progressSample;
    private double _offsetMs;
    private double _lateMs;
    private int _latencySamples;
    private double _stallBudgetMs;
    private ulong _streamEdge;
    private ulong _pauseSample;
    private bool _pauseFixed;
    private bool _restartedWhilePaused;
    public TimeProvider Clock { get; set; } = TimeProvider.System;
    public bool IsPending { get; private set; }
    public double EndMs { get; private set; }
    public bool NeedsCaptureAnchor => _syntheticAnchor;
    public double PendingPlayheadMs => EndMs + Clock.GetElapsedTime(_beganAt).TotalMilliseconds * _speed;

    public static bool CanApplyTransport(double armedEndMs, bool active,
        (int Epoch, long Seek) snapshot, (int Epoch, long Seek) current,
        double boundaryMs = 0, double playheadMs = double.MaxValue, bool isPlaying = false)
        => (armedEndMs > 0 || isPlaying) && active && snapshot == current && playheadMs >= boundaryMs;

    public bool AllowTransport(double armedEndMs, bool active,
        (int Epoch, long Seek) snapshot, (int Epoch, long Seek) current,
        double boundaryMs = 0, double playheadMs = double.MaxValue, bool isPlaying = false)
    {
        if (CanApplyTransport(armedEndMs, active, snapshot, current, boundaryMs, playheadMs, isPlaying)) return true;
        Reset();
        return false;
    }

    /// <summary>Every playback entry arms the active transport's boundary; no separate resume latch.</summary>
    public double Arm(bool active, double songEndMs, double? loopEndMs)
    {
        Reset();
        return active ? Math.Max(0, loopEndMs ?? songEndMs) : 0;
    }

    public void Reset()
    {
        IsPending = false;
        _anchored = false;
        _syntheticAnchor = false;
        _pauseFixed = false;
        _restartedWhilePaused = false;
    }

    public void Observe(ulong sample, double songMs, int sampleRate, double speed, bool discontinuity = false)
    {
        _progressAt = Clock.GetTimestamp();
        _progressSample = sample;
        _streamEdge = sample;
        _pauseFixed = false;
        _restartedWhilePaused = false;
        if (IsPending && !discontinuity && !_syntheticAnchor) return;
        if (IsPending && !_syntheticAnchor) songMs = PendingPlayheadMs - _offsetMs * _speed;
        _anchorSample = sample;
        _anchorMs = songMs;
        _sampleRate = sampleRate;
        _speed = speed > 0 ? speed : 1;
        _anchored = true;
        _syntheticAnchor = false;
        if (IsPending) SetRequiredSample();
    }

    public void Begin(double endMs, ulong sample, int sampleRate, int detectorLatencySamples,
        double lateWindowMs, double inputOffsetMs, double speed)
    {
        if (IsPending) return;
        if (!_anchored)
        {
            Observe(sample, endMs - inputOffsetMs * speed, sampleRate, speed);
            _syntheticAnchor = true;
        }
        EndMs = endMs;
        _offsetMs = inputOffsetMs;
        _lateMs = lateWindowMs;
        _latencySamples = detectorLatencySamples;
        _beganAt = _progressAt = Clock.GetTimestamp();
        _progressSample = sample;
        _stallBudgetMs = lateWindowMs / _speed + Math.Abs(inputOffsetMs) + detectorLatencySamples * 1000.0 / sampleRate;
        SetRequiredSample();
        IsPending = true;
    }

    private void SetRequiredSample()
    {
        var tailMs = Math.Max(0, (EndMs + _lateMs - _anchorMs) / _speed);
        _requiredSample = _anchorSample + (ulong)Math.Ceiling(tailMs * _sampleRate / 1000) + (ulong)_latencySamples;
    }

    public bool HasEvidence(ulong consumedSamples)
    {
        if (!IsPending) return false;
        if (consumedSamples != _progressSample)
        {
            _progressSample = consumedSamples;
            _progressAt = Clock.GetTimestamp();
        }
        return consumedSamples >= _requiredSample || Clock.GetElapsedTime(_progressAt).TotalMilliseconds >= _stallBudgetMs;
    }

    /// <summary>
    ///     A capture read while the transport is stopped. The first one fixes where the pause fell
    ///     on the capture stream: <paramref name="pausedSongMs" /> (the stopped playhead, on the
    ///     detection axis) through the last playing read's mapping, so an attack captured between
    ///     that read and the actual pause still counts as played. A read that starts BEHIND the
    ///     last one means the sample counter restarted (engine rebuild, device change): the old
    ///     mapping does not name these samples, so nothing more is mapped until play resumes.
    /// </summary>
    public void ObservePaused(ulong streamPos, int read, double pausedSongMs)
    {
        var edge = streamPos + (ulong)Math.Max(0, read);
        if (streamPos < _streamEdge) _restartedWhilePaused = true;
        _streamEdge = edge;
        if (!_anchored || _syntheticAnchor || IsPending || _pauseFixed || _restartedWhilePaused) return;
        var pastAnchorMs = Math.Max(0, (pausedSongMs - _anchorMs) / _speed);
        var pause = _anchorSample + (ulong)Math.Floor(pastAnchorMs * _sampleRate / 1000);
        _pauseSample = Math.Min(pause, Math.Max(_anchorSample, edge));
        _pauseFixed = true;
    }

    /// <summary>
    ///     The song time of an attack captured while the transport still ran, read through the
    ///     mapping the last playing read left: the pause-time mapping. The capture's stream sample
    ///     decides, against the pause point <see cref="ObservePaused" /> fixed (the last playing
    ///     read's edge until then). False for an attack captured after the pause (playing during
    ///     it), after the counter restarted, and when no playing read has anchored one.
    /// </summary>
    public bool TryMapCapturedBeforePause(ulong sample, out double songMs)
    {
        songMs = 0;
        var pauseSample = _pauseFixed ? _pauseSample : _anchorSample;
        if (!_anchored || _syntheticAnchor || IsPending || _restartedWhilePaused || sample >= pauseSample) return false;
        songMs = Map(sample);
        return true;
    }

    /// <summary>Uses the playing clock, or the frozen pause mapping for an earlier capture.</summary>
    public bool TryMapDetection(ulong sample, bool isPlaying, double playingSongMs, out double songMs)
    {
        songMs = isPlaying ? playingSongMs : 0;
        return isPlaying || TryMapCapturedBeforePause(sample, out songMs);
    }

    public double Map(ulong sample) => Map((double)sample);
    private double Map(double sample) => _anchorMs + (sample - _anchorSample) * 1000 / _sampleRate * _speed;
}
