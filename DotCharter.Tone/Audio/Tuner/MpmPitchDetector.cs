namespace DotCharter.Tone.Audio.Tuner;

/// <summary>
///     McLeod Pitch Method (MPM) detector — the primary pitch engine of the
///     0.18 tuner suite's managed core (see <see cref="IPitchDetector"/> for
///     the native-graduation framing).
///     <para>
///     Computes the Normalized Square Difference Function (NSDF), collects the
///     key maxima between its positive zero crossings, and picks the FIRST
///     maximum that reaches <see cref="PeakThreshold"/> × the highest maximum —
///     McLeod's documented octave-error guard (a subharmonic peak at 2·T can
///     never shadow the true period peak at T, because T comes first). A second
///     guard (<see cref="OctaveGuardRatio"/>) corrects residual sub-octave
///     picks: if an earlier maximum sits at ~half the chosen lag with nearly
///     the chosen peak's strength, the higher octave wins. The chosen peak is
///     refined by 3-point parabolic interpolation, which for the cosine-shaped
///     NSDF peak of a clean tone keeps the bias far below one cent across the
///     guitar range.
///     </para>
///     <para>
///     Gating: windows whose (DC-removed) RMS is below <see cref="MinRms"/>,
///     or whose winning peak clarity is below <see cref="ClarityThreshold"/>,
///     report <see cref="PitchReading.None"/> — noise and silence must never
///     move a tuner needle.
///     </para>
///     <para>
///     Sample-rate agnostic (everything derives from the constructor's rate);
///     allocation-free per <see cref="Detect"/> call after construction.
///     </para>
/// </summary>
public sealed class MpmPitchDetector : IPitchDetector
{
    private readonly int _minLag;
    private readonly int _maxLag;

    // Preallocated scratch — Detect never allocates.
    private readonly float[] _work;            // DC-removed copy of the window
    private readonly double[] _nsdf;           // [0.._maxLag]
    private readonly double[] _candidateLags;  // interpolated peak lags
    private readonly double[] _candidateValues; // interpolated peak values

    /// <summary>Fraction of the highest NSDF maximum a peak must reach to be
    /// eligible; the FIRST eligible peak wins (McLeod's k, the octave guard).</summary>
    public double PeakThreshold { get; init; } = 0.9;

    /// <summary>Minimum clarity (winning NSDF peak height, ~0..1) for a
    /// reading to count as pitched. Noise sits far below this.</summary>
    public double ClarityThreshold { get; init; } = 0.8;

    /// <summary>Absolute RMS (after DC removal) below which the window counts
    /// as silence. Matches the existing tuner dialog's floor.</summary>
    public float MinRms { get; init; } = 0.005f;

    /// <summary>Sub-octave rescue: an earlier maximum at ~half the chosen lag
    /// whose value is at least this fraction of the chosen peak's replaces it
    /// (the true fundamental was one octave up).</summary>
    public double OctaveGuardRatio { get; init; } = 0.9;

    /// <summary>Relative lag tolerance for the half-lag octave guard.</summary>
    public double OctaveGuardLagTolerance { get; init; } = 0.06;

    public int SampleRate { get; }
    public int WindowSize { get; }

    /// <summary>Lowest detectable frequency (may be raised from the requested
    /// value so that the window always spans two full periods).</summary>
    public double MinFrequencyHz { get; }

    /// <summary>Highest detectable frequency.</summary>
    public double MaxFrequencyHz { get; }

    public MpmPitchDetector(int sampleRate, int windowSize = 4096,
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

        // The window must span >= 2 periods of the lowest frequency for the
        // NSDF period peak to exist with usable support.
        _maxLag = Math.Min((int)Math.Ceiling(sampleRate / minFrequencyHz), windowSize / 2);
        _minLag = Math.Max(2, (int)Math.Floor(sampleRate / maxFrequencyHz));
        if (_minLag >= _maxLag)
        {
            throw new ArgumentException(
                $"Window of {windowSize} samples at {sampleRate} Hz cannot cover " +
                $"{minFrequencyHz}-{maxFrequencyHz} Hz.", nameof(windowSize));
        }

        MinFrequencyHz = (double)sampleRate / _maxLag;
        MaxFrequencyHz = maxFrequencyHz;

        _work = new float[windowSize];
        _nsdf = new double[_maxLag + 1];
        _candidateLags = new double[_maxLag / 2 + 2];
        _candidateValues = new double[_maxLag / 2 + 2];
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

        ComputeNsdf();

        var candidateCount = CollectKeyMaxima();
        if (candidateCount == 0) return PitchReading.None;

        var chosen = ChooseCandidate(candidateCount);
        var clarity = Math.Clamp(_candidateValues[chosen], 0.0, 1.0);
        if (clarity < ClarityThreshold) return PitchReading.None;

        var lag = _candidateLags[chosen];
        var frequency = SampleRate / lag;
        if (frequency < MinFrequencyHz || frequency > MaxFrequencyHz) return PitchReading.None;

        return new PitchReading(frequency, clarity);
    }

    /// <summary>NSDF: n(τ) = 2·Σ x[j]x[j+τ] / Σ (x[j]² + x[j+τ]²). The
    /// denominator is updated incrementally (O(1) per lag).</summary>
    private void ComputeNsdf()
    {
        var x = _work;
        var w = WindowSize;

        double m = 0;
        for (var j = 0; j < w; j++) m += 2.0 * x[j] * x[j];

        _nsdf[0] = 1.0;
        for (var tau = 1; tau <= _maxLag; tau++)
        {
            m -= (double)x[tau - 1] * x[tau - 1] + (double)x[w - tau] * x[w - tau];

            double acf = 0;
            var limit = w - tau;
            for (var j = 0; j < limit; j++) acf += (double)x[j] * x[j + tau];

            _nsdf[tau] = m > 0 ? 2.0 * acf / m : 0.0;
        }
    }

    /// <summary>Collects the parabolic-interpolated maximum of every positive
    /// NSDF region after the first negative-going zero crossing. Returns the
    /// candidate count (stored in the preallocated candidate arrays).</summary>
    private int CollectKeyMaxima()
    {
        var count = 0;
        var i = 1;

        // Skip the trivial lag-0 lobe (NSDF starts at 1 and falls).
        while (i < _maxLag && _nsdf[i] > 0) i++;

        while (i < _maxLag)
        {
            // Advance to the next positive-going crossing.
            while (i < _maxLag && _nsdf[i] <= 0) i++;
            if (i >= _maxLag) break;

            // Track the raw maximum of this positive region.
            var bestLag = i;
            var best = _nsdf[i];
            while (i < _maxLag && _nsdf[i] > 0)
            {
                if (_nsdf[i] > best)
                {
                    best = _nsdf[i];
                    bestLag = i;
                }

                i++;
            }

            if (bestLag < _minLag || bestLag >= _maxLag) continue;

            PitchMath.ParabolicPeak(_nsdf, bestLag, out var lag, out var value);
            _candidateLags[count] = lag;
            _candidateValues[count] = value;
            count++;
        }

        return count;
    }

    /// <summary>First-above-threshold pick plus the half-lag sub-octave guard.</summary>
    private int ChooseCandidate(int count)
    {
        double nMax = 0;
        for (var c = 0; c < count; c++)
        {
            if (_candidateValues[c] > nMax)
                nMax = _candidateValues[c];
        }

        var threshold = PeakThreshold * nMax;
        var chosen = 0;
        for (var c = 0; c < count; c++)
        {
            if (_candidateValues[c] < threshold) continue;
            chosen = c;
            break;
        }

        // Sub-octave rescue: an earlier, nearly-as-strong maximum at ~half
        // the chosen lag means the chosen peak was the 2·T subharmonic.
        for (var c = 0; c < chosen; c++)
        {
            var halfError = Math.Abs(_candidateLags[c] * 2.0 - _candidateLags[chosen]);
            if (halfError > _candidateLags[chosen] * OctaveGuardLagTolerance) continue;
            if (_candidateValues[c] < OctaveGuardRatio * _candidateValues[chosen]) continue;
            chosen = c;
            break;
        }

        return chosen;
    }
}
