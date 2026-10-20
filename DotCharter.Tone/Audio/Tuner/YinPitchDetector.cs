namespace DotCharter.Tone.Audio.Tuner;

/// <summary>
///     YIN pitch detector (de Cheveigné &amp; Kawahara 2002) — the fallback
///     engine of the 0.18 tuner suite's managed core (see
///     <see cref="IPitchDetector"/> for the native-graduation framing).
///     <para>
///     Computes the difference function over a fixed integration length
///     (window − maxLag, so every lag integrates the same number of terms),
///     normalizes it cumulatively (CMNDF), and takes the first lag whose
///     CMNDF dips below <see cref="Threshold"/>, walked forward to the local
///     minimum — the classic absolute-threshold step that makes YIN prefer
///     the fundamental over subharmonics. The minimum is refined with 3-point
///     parabolic interpolation.
///     </para>
///     <para>
///     Gating: silence (RMS under <see cref="MinRms"/> after DC removal) and
///     aperiodic windows (best CMNDF too high, i.e. clarity = 1 − d′ below
///     <see cref="ClarityThreshold"/>) report <see cref="PitchReading.None"/>.
///     Sample-rate agnostic; allocation-free per <see cref="Detect"/> call.
///     </para>
/// </summary>
public sealed class YinPitchDetector : IPitchDetector
{
    private readonly int _minLag;
    private readonly int _maxLag;
    private readonly int _integrationLength;

    // Preallocated scratch — Detect never allocates.
    private readonly float[] _work;
    private readonly double[] _cmndf; // difference function, normalized in place

    /// <summary>CMNDF absolute threshold: the first dip below this is the
    /// period estimate. 0.15 is the paper's tuner-grade setting.</summary>
    public double Threshold { get; init; } = 0.15;

    /// <summary>Minimum clarity (1 − CMNDF at the chosen lag) for a reading
    /// to count as pitched. Noise stays far below this.</summary>
    public double ClarityThreshold { get; init; } = 0.7;

    /// <summary>Absolute RMS (after DC removal) below which the window counts
    /// as silence.</summary>
    public float MinRms { get; init; } = 0.005f;

    public int SampleRate { get; }
    public int WindowSize { get; }

    /// <summary>Lowest detectable frequency (may be raised from the requested
    /// value so that the window always spans two full periods).</summary>
    public double MinFrequencyHz { get; }

    /// <summary>Highest detectable frequency.</summary>
    public double MaxFrequencyHz { get; }

    public YinPitchDetector(int sampleRate, int windowSize = 4096,
        double minFrequencyHz = 25.0, double maxFrequencyHz = 1500.0)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (windowSize < 64) throw new ArgumentOutOfRangeException(nameof(windowSize));
        if (minFrequencyHz <= 0 || maxFrequencyHz <= minFrequencyHz)
        {
            throw new ArgumentOutOfRangeException(nameof(minFrequencyHz),
                "Need 0 < minFrequencyHz < maxFrequencyHz.");
        }

        SampleRate = sampleRate;
        WindowSize = windowSize;

        _maxLag = Math.Min((int)Math.Ceiling(sampleRate / minFrequencyHz), windowSize / 2);
        _minLag = Math.Max(2, (int)Math.Floor(sampleRate / maxFrequencyHz));
        if (_minLag >= _maxLag)
        {
            throw new ArgumentException(
                $"Window of {windowSize} samples at {sampleRate} Hz cannot cover " +
                $"{minFrequencyHz}-{maxFrequencyHz} Hz.", nameof(windowSize));
        }

        _integrationLength = windowSize - _maxLag;
        MinFrequencyHz = (double)sampleRate / _maxLag;
        MaxFrequencyHz = maxFrequencyHz;

        _work = new float[windowSize];
        _cmndf = new double[_maxLag + 1];
    }

    public PitchReading Detect(ReadOnlySpan<float> window)
    {
        if (window.Length != WindowSize)
        {
            throw new ArgumentException(
                $"Expected exactly {WindowSize} samples, got {window.Length}.", nameof(window));
        }

        if (!PitchMath.RemoveDcAndGate(window, _work, MinRms))
            return PitchReading.None;

        ComputeCmndf();

        var lagIndex = FindDip();
        if (lagIndex < 0) return PitchReading.None;

        var clarity = Math.Clamp(1.0 - _cmndf[lagIndex], 0.0, 1.0);
        if (clarity < ClarityThreshold) return PitchReading.None;

        double lag;
        if (lagIndex > _minLag && lagIndex < _maxLag)
            PitchMath.ParabolicPeak(_cmndf, lagIndex, out lag, out _);
        else
            lag = lagIndex;

        var frequency = SampleRate / lag;
        if (frequency < MinFrequencyHz || frequency > MaxFrequencyHz) return PitchReading.None;

        return new PitchReading(frequency, clarity);
    }

    /// <summary>Difference function d(τ) over a fixed integration length,
    /// then the cumulative-mean normalization d′(τ) in place.</summary>
    private void ComputeCmndf()
    {
        var x = _work;
        var n = _integrationLength;

        _cmndf[0] = 1.0;
        double runningSum = 0;
        for (var tau = 1; tau <= _maxLag; tau++)
        {
            double d = 0;
            for (var j = 0; j < n; j++)
            {
                var diff = (double)x[j] - x[j + tau];
                d += diff * diff;
            }

            runningSum += d;
            _cmndf[tau] = runningSum > 0 ? d * tau / runningSum : 1.0;
        }
    }

    /// <summary>The absolute-threshold step: first lag whose CMNDF drops
    /// below <see cref="Threshold"/>, walked to its local minimum. Falls back
    /// to the global minimum (clarity gating then decides).</summary>
    private int FindDip()
    {
        for (var tau = _minLag; tau <= _maxLag; tau++)
        {
            if (_cmndf[tau] >= Threshold) continue;
            while (tau + 1 <= _maxLag && _cmndf[tau + 1] < _cmndf[tau]) tau++;
            return tau;
        }

        var best = -1;
        var bestValue = double.MaxValue;
        for (var tau = _minLag; tau <= _maxLag; tau++)
        {
            if (_cmndf[tau] >= bestValue) continue;
            bestValue = _cmndf[tau];
            best = tau;
        }

        return best;
    }
}
