namespace DotCharter.Tone.Audio.Tuner;

/// <summary>
///     The 0.18 tuner suite's composite pitch engine: MPM primary
///     (<see cref="MpmPitchDetector"/> — best transient robustness and octave
///     behaviour on plucked strings), YIN fallback
///     (<see cref="YinPitchDetector"/>) for windows MPM's clarity gate
///     rejects. Both gates failing means no pitch — noise or silence never
///     produces a reading.
///     <para>
///     Managed slice of the 0.18 tone rig: runs off the existing input-capture
///     tap today and doubles as the reference implementation for the Phase-0
///     native graduation (see <see cref="IPitchDetector"/>). Allocation-free
///     per <see cref="Detect"/> call.
///     </para>
/// </summary>
public sealed class TunerPitchEngine : IPitchDetector
{
    private readonly MpmPitchDetector _mpm;
    private readonly YinPitchDetector _yin;

    public int SampleRate { get; }
    public int WindowSize { get; }

    /// <summary>Builds MPM + YIN with shared defaults.</summary>
    public TunerPitchEngine(int sampleRate, int windowSize = 4096,
        double minFrequencyHz = 25.0, double maxFrequencyHz = 1500.0)
        : this(new MpmPitchDetector(sampleRate, windowSize, minFrequencyHz, maxFrequencyHz),
            new YinPitchDetector(sampleRate, windowSize, minFrequencyHz, maxFrequencyHz))
    {
    }

    /// <summary>Injectable seam (tests tune the individual gates).</summary>
    public TunerPitchEngine(MpmPitchDetector mpm, YinPitchDetector yin)
    {
        _mpm = mpm ?? throw new ArgumentNullException(nameof(mpm));
        _yin = yin ?? throw new ArgumentNullException(nameof(yin));
        if (mpm.SampleRate != yin.SampleRate || mpm.WindowSize != yin.WindowSize)
            throw new ArgumentException("MPM and YIN must share sample rate and window size.");

        SampleRate = mpm.SampleRate;
        WindowSize = mpm.WindowSize;
    }

    public PitchReading Detect(ReadOnlySpan<float> window)
    {
        var reading = _mpm.Detect(window);
        return reading.HasPitch ? reading : _yin.Detect(window);
    }
}
