namespace DotCharter.Tone.Audio.Tuner;

/// <summary>
///     One virtual-strobe measurement (see <see cref="VirtualStrobe"/>).
///     <see cref="HasSignal"/> — the target bin is audible this block;
///     <see cref="IsTracking"/> — a drift estimate exists (needs two
///     consecutive signal blocks). <see cref="WheelTurns"/> is the
///     accumulated strobe-wheel rotation (turns; stationary = in tune).
/// </summary>
public readonly record struct StrobeReading(
    bool HasSignal,
    bool IsTracking,
    double CentsOffset,
    double DriftRadiansPerSecond,
    double Magnitude,
    double WheelTurns);

/// <summary>
///     Virtual strobe tuner: a single-bin windowed DFT at a KNOWN target
///     frequency, measuring the bin's phase drift between consecutive blocks
///     (0.18 Phase 1 — "single-bin DFT phase drift at the known
///     string freq"). A physical strobe disc stands still when the string is
///     on frequency and rotates at the beat rate when it is off; here the
///     demodulated phase is constant when on target and advances at
///     2π·Δf rad/s when off, so <see cref="StrobeReading.CentsOffset"/> falls
///     straight out of the phase slope — far finer resolution near the target
///     than any general pitch detector.
///     <para>
///     Managed slice of the 0.18 tone rig: runs off the existing
///     input-capture tap today and is the validated reference implementation
///     for the Phase-0 native graduation (see <see cref="IPitchDetector"/>
///     for the shared framing). Feed CONTIGUOUS capture blocks of exactly
///     <see cref="BlockSize"/> samples; a Hann window keeps spectral leakage
///     out of the phase estimate. Allocation-free per <see cref="Process"/>.
///     </para>
///     <para>
///     Physics cap: between blocks the phase is only known modulo 2π, so the
///     measurable offset is |Δf| ≤ sampleRate/(2·blockSize) (≈ ±11.7 Hz at
///     48 kHz / 2048). The strobe is a FINE instrument — hand it strings that
///     are already near the target (the guided check's pitch engine does the
///     coarse work) and it will not alias.
///     </para>
/// </summary>
public sealed class VirtualStrobe
{
    private readonly float[] _hann;
    private readonly double _windowSum;

    private double _referencePhase; // reference oscillator phase at the next sample
    private long _totalSamples;
    private bool _hasPrevious;
    private double _previousPhase;
    private double _previousMidpoint;
    private double _wheelTurns;

    /// <summary>Minimum target-bin amplitude (full-scale sinusoid units)
    /// below which the block counts as silence at the target.</summary>
    public double MinMagnitude { get; init; } = 0.003;

    /// <summary>Minimum ratio of target-bin amplitude to the block's total
    /// signal level (1.0 for a pure on-target tone). Rejects energy that is
    /// loud but NOT at the target — a different string, broadband noise.</summary>
    public double MinSelectivity { get; init; } = 0.5;

    public int SampleRate { get; }
    public int BlockSize { get; }
    public double TargetFrequencyHz { get; private set; }

    /// <summary>Accumulated strobe-wheel rotation in turns while tracking.</summary>
    public double WheelTurns => _wheelTurns;

    public VirtualStrobe(int sampleRate, double targetFrequencyHz, int blockSize = 2048)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (blockSize < 64) throw new ArgumentOutOfRangeException(nameof(blockSize));
        if (targetFrequencyHz <= 0 || targetFrequencyHz >= sampleRate / 2.0)
            throw new ArgumentOutOfRangeException(nameof(targetFrequencyHz));

        SampleRate = sampleRate;
        BlockSize = blockSize;
        TargetFrequencyHz = targetFrequencyHz;

        _hann = new float[blockSize];
        double sum = 0;
        for (var n = 0; n < blockSize; n++)
        {
            var w = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * n / (blockSize - 1)));
            _hann[n] = (float)w;
            sum += w;
        }

        _windowSum = sum;
    }

    /// <summary>Points the strobe at a new target (string change). Tracking
    /// state resets; the wheel position is preserved only across
    /// same-target blocks.</summary>
    public void Retarget(double targetFrequencyHz)
    {
        if (targetFrequencyHz <= 0 || targetFrequencyHz >= SampleRate / 2.0)
            throw new ArgumentOutOfRangeException(nameof(targetFrequencyHz));

        TargetFrequencyHz = targetFrequencyHz;
        _hasPrevious = false;
        _wheelTurns = 0;
    }

    /// <summary>Clears all tracking state (capture gap / seek).</summary>
    public void Reset()
    {
        _hasPrevious = false;
        _wheelTurns = 0;
        _referencePhase = 0;
        _totalSamples = 0;
    }

    /// <summary>
    ///     Demodulates one contiguous block of exactly <see cref="BlockSize"/>
    ///     mono samples against the target-frequency reference oscillator and
    ///     measures the phase drift since the previous signal block.
    /// </summary>
    public StrobeReading Process(ReadOnlySpan<float> block)
    {
        if (block.Length != BlockSize)
        {
            throw new ArgumentException(
                $"Expected exactly {BlockSize} samples, got {block.Length}.", nameof(block));
        }

        var omega = 2.0 * Math.PI * TargetFrequencyHz / SampleRate;
        var phase = _referencePhase;
        double inPhase = 0, quadrature = 0, sumSquares = 0;
        for (var n = 0; n < block.Length; n++)
        {
            var sample = block[n];
            var windowed = sample * _hann[n];
            inPhase += windowed * Math.Cos(phase);
            quadrature -= windowed * Math.Sin(phase);
            sumSquares += (double)sample * sample;

            phase += omega;
            if (phase >= 2.0 * Math.PI) phase -= 2.0 * Math.PI;
        }

        _referencePhase = phase;
        var midpoint = _totalSamples + (block.Length - 1) / 2.0;
        _totalSamples += block.Length;

        // Coherent-gain-corrected amplitude of the target bin, and how much
        // of the block's total level that bin explains (1.0 = pure on-target).
        var magnitude = 2.0 * Math.Sqrt(inPhase * inPhase + quadrature * quadrature) / _windowSum;
        var rms = Math.Sqrt(sumSquares / block.Length);
        var selectivity = rms > 0 ? magnitude / (rms * Math.Sqrt(2.0)) : 0.0;

        if (magnitude < MinMagnitude || selectivity < MinSelectivity)
        {
            // Signal lost at the target — phase continuity is broken, the
            // next signal block starts a fresh track.
            _hasPrevious = false;
            return new StrobeReading(false, false, 0, 0, magnitude, _wheelTurns);
        }

        var signalPhase = Math.Atan2(quadrature, inPhase);
        if (!_hasPrevious)
        {
            _hasPrevious = true;
            _previousPhase = signalPhase;
            _previousMidpoint = midpoint;
            return new StrobeReading(true, false, 0, 0, magnitude, _wheelTurns);
        }

        var deltaPhase = signalPhase - _previousPhase;
        while (deltaPhase > Math.PI) deltaPhase -= 2.0 * Math.PI;
        while (deltaPhase <= -Math.PI) deltaPhase += 2.0 * Math.PI;

        var elapsedSeconds = (midpoint - _previousMidpoint) / SampleRate;
        _previousPhase = signalPhase;
        _previousMidpoint = midpoint;

        var driftRadiansPerSecond = deltaPhase / elapsedSeconds;
        var detected = TargetFrequencyHz + driftRadiansPerSecond / (2.0 * Math.PI);
        var cents = detected > 0
            ? PitchMath.CentsBetween(detected, TargetFrequencyHz)
            : double.NegativeInfinity;

        _wheelTurns += deltaPhase / (2.0 * Math.PI);
        return new StrobeReading(true, true, cents, driftRadiansPerSecond, magnitude, _wheelTurns);
    }
}
