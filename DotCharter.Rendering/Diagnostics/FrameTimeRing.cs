namespace DotCharter.Rendering.Diagnostics;

/// <summary>Percentile summary over the ring's current window.</summary>
public readonly record struct FrameTimeSummary(int Count, double P50Ms, double P99Ms, double MaxMs)
{
    public double ApproxFps => P50Ms > 0 ? 1000.0 / P50Ms : 0;
}

/// <summary>
///     Fixed-size ring of frame times with on-demand percentiles. Add() is
///     allocation-free and unsynchronized by design — call it from the one
///     render thread that owns the renderer (per-frame allocations on the
///     render path caused audible Gen-1 GC clicks before; see
///     Preview3DComposedCanvas). Summarize() sorts into a preallocated scratch and is
///     meant for ~1 Hz HUD/log use, not per-frame.
/// </summary>
public sealed class FrameTimeRing
{
    private readonly double[] _samples;
    private readonly double[] _scratch;
    private int _next;
    private int _count;

    public FrameTimeRing(int capacity = 512)
    {
        _samples = new double[capacity];
        _scratch = new double[capacity];
    }

    /// <summary>Any interval at or above this is an IDLE gap, not a rendered
    /// frame: the damage-driven 2D renderer simply drew nothing for a while.
    /// Recording it poisoned max/p99 (a 124-second "frame" in a field report)
    /// and made the Session Health panel useless for stutter hunts. Real
    /// stalls below one second still register.</summary>
    public const double IdleGapThresholdMs = 1000.0;

    public void Add(double frameMs)
    {
        _samples[_next] = frameMs;
        _next = (_next + 1) % _samples.Length;
        if (_count < _samples.Length) _count++;
    }

    /// <summary>Adds the interval unless it is an idle gap (see
    /// <see cref="IdleGapThresholdMs" />). Returns whether it was recorded —
    /// damage-driven renderers use this so pauses never read as stalls.</summary>
    public bool AddFrameInterval(double frameMs)
    {
        if (frameMs >= IdleGapThresholdMs) return false;
        Add(frameMs);
        return true;
    }

    public void Reset()
    {
        _next = 0;
        _count = 0;
    }

    public FrameTimeSummary Summarize()
    {
        var n = _count;
        if (n == 0) return default;

        Array.Copy(_samples, _scratch, n);
        Array.Sort(_scratch, 0, n);

        return new FrameTimeSummary(
            n,
            _scratch[(int)(n * 0.50)],
            _scratch[Math.Min(n - 1, (int)(n * 0.99))],
            _scratch[n - 1]);
    }

    /// <summary>
    ///     Population standard deviation over the current window (0.16
    ///     composed-path jitter metric: a constant present delay is
    ///     compensable by leading the render clock — jitter is not, so it is
    ///     reported first-class next to the percentiles). Same ~1 Hz usage
    ///     contract as <see cref="Summarize" />.
    /// </summary>
    public double StdDevMs()
    {
        var n = _count;
        if (n < 2) return 0;

        double sum = 0;
        for (var i = 0; i < n; i++) sum += _samples[i];
        var mean = sum / n;

        double sq = 0;
        for (var i = 0; i < n; i++)
        {
            var d = _samples[i] - mean;
            sq += d * d;
        }

        return Math.Sqrt(sq / n);
    }
}
