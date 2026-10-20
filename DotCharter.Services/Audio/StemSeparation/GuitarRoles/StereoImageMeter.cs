namespace DotCharter.Services.Audio.StemSeparation.GuitarRoles;

/// <summary>
///     Whole-signal stereo statistics of a guitar stem: inter-channel correlation,
///     the share of its energy in the SIDE signal, and its level. Streaming and
///     allocation-free, so the cheap pre-check can read a whole stem without
///     holding it in memory.
///     <para>
///         This is the "is there a stereo image at all?" half of the honesty rule.
///         A stem whose two channels are (nearly) the same carries no position to
///         split by; splitting it would hand the user the same guitar twice. Which
///         share of the guitar the centre mask actually takes is the other half,
///         and only the split itself can measure it
///         (<see cref="CentreSideSplitter.LeadShare" />).
///     </para>
/// </summary>
public sealed class StereoImageMeter
{
    private double _sumLl;
    private double _sumRr;
    private double _sumLr;
    private long _frames;

    public long Frames => _frames;

    public void Add(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != right.Length)
            throw new ArgumentException("Both channels must hold the same number of frames.", nameof(right));

        double ll = 0, rr = 0, lr = 0;
        for (var i = 0; i < left.Length; i++)
        {
            double l = left[i];
            double r = right[i];
            ll += l * l;
            rr += r * r;
            lr += l * r;
        }

        _sumLl += ll;
        _sumRr += rr;
        _sumLr += lr;
        _frames += left.Length;
    }

    public StereoImageStats Result()
    {
        var total = _sumLl + _sumRr;
        var correlation = _sumLl > 0 && _sumRr > 0 ? _sumLr / Math.Sqrt(_sumLl * _sumRr) : 1.0;
        // M = (L+R)/2, S = (L-R)/2: E_M + E_S = (E_L + E_R)/2 and E_S = (E_L + E_R - 2 E_LR)/4,
        // so the side share is (E_L + E_R - 2 E_LR) / (2 (E_L + E_R)).
        var sideShare = total > 0 ? Math.Clamp((total - 2 * _sumLr) / (2 * total), 0, 1) : 0;
        var rms = _frames > 0 ? Math.Sqrt(total / (2.0 * _frames)) : 0;
        var rmsDbfs = rms > 0 ? 20 * Math.Log10(rms) : double.NegativeInfinity;
        return new StereoImageStats(Math.Clamp(correlation, -1, 1), sideShare, rmsDbfs, _frames);
    }
}

/// <param name="Correlation">Pearson-style inter-channel correlation over the whole
/// stem (no mean removal: audio has none worth removing). 1 = identical channels.</param>
/// <param name="SideShare">Share of the stem's energy in the side signal
/// <c>(L-R)/2</c>: 0 for mono, 0.5 for two unrelated channels, 1 for anti-phase.</param>
/// <param name="RmsDbfs">Level over both channels, dB full scale.</param>
/// <param name="Frames">Sample frames measured.</param>
public readonly record struct StereoImageStats(double Correlation, double SideShare, double RmsDbfs, long Frames);
