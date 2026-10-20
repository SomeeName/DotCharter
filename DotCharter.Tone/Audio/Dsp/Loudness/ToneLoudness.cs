namespace DotCharter.Tone.Audio.Dsp.Loudness;

/// <summary>A chain's measured output for <see cref="CalibrationPhrase" />:
/// integrated loudness (LUFS, -inf for a chain that renders silence) and the
/// sample peak (dBFS).</summary>
public readonly record struct ToneLoudness(double Lufs, double PeakDbfs)
{
    /// <summary>False for a chain that rendered silence (or a non-finite result):
    /// there is nothing to level, and the estimate stays.</summary>
    public bool IsMeasured => double.IsFinite(Lufs) && double.IsFinite(PeakDbfs);
}
