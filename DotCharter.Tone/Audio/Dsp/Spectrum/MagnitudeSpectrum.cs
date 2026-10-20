namespace DotCharter.Tone.Audio.Dsp.Spectrum;

/// <summary>
///     A long-term magnitude spectrum, managed and pure: Hann-windowed 4096-point
///     frames (half overlap), power averaged over the frames (Welch), folded into
///     log-spaced bands from 20 Hz to 20 kHz and returned in dB re full scale.
///     Analysis of an offline render, not DSP on the audio path: nothing here runs
///     in the engine.
/// </summary>
public static class MagnitudeSpectrum
{
    public const int FrameSize = 4096;
    public const double MinHz = 20;
    public const double MaxHz = 20_000;

    /// <summary>The floor a silent band reads.</summary>
    public const double FloorDb = -120;

    /// <summary>The lower edge of band <paramref name="band" /> of
    /// <paramref name="bands" /> (log-spaced); <c>band == bands</c> is the top edge.</summary>
    public static double BandEdgeHz(int band, int bands) => MinHz * Math.Pow(MaxHz / MinHz, (double)band / bands);

    /// <summary>The spectrum of <paramref name="samples" /> in
    /// <paramref name="bands" /> bands. Shorter than one frame: zero-padded.</summary>
    public static double[] Compute(ReadOnlySpan<float> samples, int sampleRate, int bands)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bands, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        var power = new double[FrameSize / 2 + 1];
        var window = new double[FrameSize];
        var windowEnergy = 0.0;
        for (var i = 0; i < FrameSize; i++)
        {
            window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FrameSize - 1));
            windowEnergy += window[i] * window[i];
        }

        var re = new double[FrameSize];
        var im = new double[FrameSize];
        var frames = 0;
        const int hop = FrameSize / 2;
        for (var start = 0; frames == 0 || start + FrameSize <= samples.Length; start += hop)
        {
            for (var i = 0; i < FrameSize; i++)
            {
                var s = start + i < samples.Length ? samples[start + i] : 0f;
                re[i] = s * window[i];
                im[i] = 0;
            }

            Fft(re, im);
            for (var k = 0; k < power.Length; k++) power[k] += re[k] * re[k] + im[k] * im[k];
            frames++;
        }

        // One-sided power, normalised so a full-scale sine reads about 0 dB in its band.
        var scale = 4.0 / (frames * windowEnergy * FrameSize);
        var result = new double[bands];
        var binHz = (double)sampleRate / FrameSize;
        for (var b = 0; b < bands; b++)
        {
            var lo = BandEdgeHz(b, bands);
            var hi = BandEdgeHz(b + 1, bands);
            var first = Math.Clamp((int)Math.Ceiling(lo / binHz), 1, power.Length - 1);
            var last = Math.Clamp((int)Math.Floor(hi / binHz), 1, power.Length - 1);
            // A band narrower than one bin (the low end) reads the bin it falls in.
            if (last < first) last = first = Math.Clamp((int)Math.Round((lo + hi) / 2 / binHz), 1, power.Length - 1);
            var sum = 0.0;
            for (var k = first; k <= last; k++) sum += power[k];
            var p = sum * scale;
            result[b] = p > 0 ? Math.Max(FloorDb, 10 * Math.Log10(p)) : FloorDb;
        }

        return result;
    }

    // In-place iterative radix-2 FFT; the length must be a power of two.
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
            var angle = -2 * Math.PI / len;
            var wr = Math.Cos(angle);
            var wi = Math.Sin(angle);
            for (var i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    var a = i + k;
                    var b = a + len / 2;
                    var tr = re[b] * cr - im[b] * ci;
                    var ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                    var next = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = next;
                }
            }
        }
    }
}
