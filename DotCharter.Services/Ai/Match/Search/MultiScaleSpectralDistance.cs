namespace DotCharter.Services.Ai.Match.Search;

/// <summary>
///     A multi-scale spectral term over RAW audio, used as a small tie-breaker
///     beside the feature distance.
///     <para>
///         <b>It is time-AVERAGED, not frame-wise, and that is the whole design.</b>
///         The canonical multi-scale spectral loss (DDSP, every sound-matching
///         paper) compares STFT frames at the same index, which is only meaningful
///         when both signals carry the same notes. Here they never do: the target is
///         the record's guitar and the candidate is OUR reference DI rendered
///         through a rig. A frame-wise loss on those two measures which notes were
///         played, swamping the timbre difference it is supposed to see — and it
///         would reward a rig that happens to line its transients up with the
///         record. Averaging each scale's magnitude spectrum over time first throws
///         the note content away and keeps the long-term spectral shape, which is
///         what "sits in the same slot in the mix" means (see the feasibility
///         study, §7).
///     </para>
///     <para>
///         Three window sizes (512 / 1024 / 2048 at a 4× overlap) because one is not
///         enough: the short window resolves the attack/high end, the long one the
///         low midrange where an amp's body lives. Each scale's averaged spectrum is
///         compared in LOG magnitude, after each is normalised to unit mean energy —
///         a level difference is not a tone difference, and the renderer's loudness
///         normalisation already removes it from the rig side.
///     </para>
/// </summary>
public static class MultiScaleSpectralDistance
{
    internal static readonly int[] Scales = [512, 1024, 2048];

    /// <summary>Distance in <c>[0, ∞)</c>, 0 for two signals with the same long-term
    /// spectrum at every scale. Resamples nothing: both inputs must share a sample
    /// rate (they do — everything downstream of the renderer is at the engine's).</summary>
    public static double Distance(MonoAudio a, MonoAudio b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var total = 0.0;
        var used = 0;
        foreach (var n in Scales)
        {
            var sa = AverageSpectrum(a.Samples, n);
            var sb = AverageSpectrum(b.Samples, n);
            if (sa is null || sb is null) continue;
            var acc = 0.0;
            for (var i = 0; i < sa.Length; i++)
            {
                var la = Math.Log(sa[i] + 1e-8);
                var lb = Math.Log(sb[i] + 1e-8);
                acc += Math.Abs(la - lb);
            }

            total += acc / sa.Length;
            used++;
        }

        return used == 0 ? 0.0 : total / used;
    }

    /// <summary>The time-averaged magnitude spectrum at one scale, normalised to
    /// unit mean. Null when the signal is shorter than one window.</summary>
    internal static double[]? AverageSpectrum(float[] samples, int n)
    {
        if (samples.Length < n) return null;
        var hop = n / 4;
        var bins = n / 2 + 1;
        var acc = new double[bins];
        var window = Hann(n);
        var re = new double[n];
        var im = new double[n];
        var frames = 0;
        for (var start = 0; start + n <= samples.Length; start += hop)
        {
            for (var i = 0; i < n; i++)
            {
                re[i] = samples[start + i] * window[i];
                im[i] = 0.0;
            }

            Fft(re, im);
            for (var k = 0; k < bins; k++)
                acc[k] += Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
            frames++;
        }

        if (frames == 0) return null;
        var mean = 0.0;
        for (var k = 0; k < bins; k++)
        {
            acc[k] /= frames;
            mean += acc[k];
        }

        mean /= bins;
        if (mean <= 1e-12) return acc;
        for (var k = 0; k < bins; k++) acc[k] /= mean;
        return acc;
    }

    private static double[] Hann(int n)
    {
        var w = new double[n];
        for (var i = 0; i < n; i++) w[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (n - 1));
        return w;
    }

    /// <summary>In-place radix-2 FFT. Sizes here are always powers of two
    /// (<see cref="Scales" />); this is analysis-side only and never runs on the
    /// audio thread.</summary>
    private static void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2.0 * Math.PI / len;
            var wr = Math.Cos(ang);
            var wi = Math.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                double cr = 1.0, ci = 0.0;
                for (var k = 0; k < len / 2; k++)
                {
                    var ur = re[i + k];
                    var ui = im[i + k];
                    var vr = re[i + k + len / 2] * cr - im[i + k + len / 2] * ci;
                    var vi = re[i + k + len / 2] * ci + im[i + k + len / 2] * cr;
                    re[i + k] = ur + vr;
                    im[i + k] = ui + vi;
                    re[i + k + len / 2] = ur - vr;
                    im[i + k + len / 2] = ui - vi;
                    var ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
    }
}
