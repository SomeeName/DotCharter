namespace DotCharter.Tone.Audio.Reamp;

/// <summary>
///     Offline sample-rate conversion for a re-amp take: a Kaiser-windowed sinc
///     (β 8.6, 32 zero crossings each side of the LOWER of the two rates' sinc)
///     evaluated through a finely tabulated kernel, cut off at 97 % of the lower
///     Nyquist. Both directions are band-limited — a downsample LOW-PASSES first,
///     so a 96 kHz take on a 48 kHz device does not fold its top octave back
///     into the band, which is the fault linear interpolation has
///     (<c>JuceAudioDecoder</c>'s convenience path says so in bold).
///     <para>
///         Pure and deterministic; runs on the caller's thread, split across the
///         thread pool for a long take. Never on the audio thread — the engine
///         takes the result at ITS rate.
///     </para>
/// </summary>
public static class DiTakeResampler
{
    private const int ZeroCrossings = 32;
    private const int TableSteps = 512; // kernel samples per input sample
    private const double Beta = 8.6;
    private const double Rolloff = 0.97;

    /// <summary>Returns <paramref name="input" /> at <paramref name="toRate" />;
    /// a copy when the rates agree. Length is round(n × to / from).</summary>
    public static float[] Resample(ReadOnlySpan<float> input, int fromRate, int toRate)
    {
        if (fromRate <= 0) throw new ArgumentOutOfRangeException(nameof(fromRate));
        if (toRate <= 0) throw new ArgumentOutOfRangeException(nameof(toRate));
        if (fromRate == toRate || input.IsEmpty) return input.ToArray();

        var ratio = (double)toRate / fromRate;
        // Cut-off as a fraction of the INPUT Nyquist: the lower of the two rates.
        var cutoff = Math.Min(1.0, ratio) * Rolloff;
        var halfWidth = (int)Math.Ceiling(ZeroCrossings / cutoff); // input samples each side
        var table = BuildKernel(cutoff, halfWidth);

        var src = input.ToArray();
        var outLen = (long)Math.Round(src.Length * ratio);
        var output = new float[outLen];
        const int chunk = 16384;
        var chunks = (int)((outLen + chunk - 1) / chunk);
        Parallel.For(0, chunks, c =>
        {
            var start = (long)c * chunk;
            var end = Math.Min(outLen, start + chunk);
            for (var j = start; j < end; j++)
                output[j] = (float)Tap(src, j / ratio, halfWidth, table);
        });
        return output;
    }

    private static double Tap(float[] src, double t, int halfWidth, float[] table)
    {
        var center = (long)Math.Floor(t);
        var lo = Math.Max(0, center - halfWidth + 1);
        var hi = Math.Min(src.Length - 1, center + halfWidth);
        double acc = 0;
        for (var k = lo; k <= hi; k++)
        {
            var u = Math.Abs(t - k) * TableSteps;
            var i = (int)u;
            if (i >= table.Length - 1) continue;
            var frac = u - i;
            var w = table[i] + (table[i + 1] - table[i]) * frac;
            acc += src[k] * w;
        }
        return acc;
    }

    /// <summary>h(u) = cutoff · sinc(cutoff · u) · kaiser(u / halfWidth), u ≥ 0 in
    /// input samples, tabulated at <see cref="TableSteps" /> per sample.</summary>
    private static float[] BuildKernel(double cutoff, int halfWidth)
    {
        var n = halfWidth * TableSteps + 2;
        var table = new float[n];
        var i0Beta = BesselI0(Beta);
        for (var i = 0; i < n; i++)
        {
            var u = (double)i / TableSteps;
            var r = u / halfWidth;
            if (r >= 1.0) { table[i] = 0; continue; }
            var x = Math.PI * cutoff * u;
            var sinc = x == 0 ? 1.0 : Math.Sin(x) / x;
            var kaiser = BesselI0(Beta * Math.Sqrt(1 - r * r)) / i0Beta;
            table[i] = (float)(cutoff * sinc * kaiser);
        }
        return table;
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, q = x * x / 4;
        for (var k = 1; k < 64; k++)
        {
            term *= q / (k * k);
            sum += term;
            if (term < sum * 1e-17) break;
        }
        return sum;
    }
}
