using JuceSharp.Dsp;

namespace DotCharter.Analysis.BeatMap;

/// <summary>
///     What <see cref="RecordingAligner" /> observes in the recording: a fine onset envelope
///     (log-magnitude spectral flux, 1024-point FFT, ~2.7 ms hop) normalised against its local
///     peak level so a quiet verse and a loud chorus weigh the same, and a coarse chroma track
///     (4096-point FFT, one frame per coarse hop) for the coarse warp. Pure and deterministic:
///     the same samples always give the same arrays.
/// </summary>
internal sealed class RecordingAlignmentFeatures
{
    public const int FluxFftSize = 1024;
    public const int ChromaFftSize = 4096;
    private const double WhitenSeconds = 0.25;
    private const double LocalPeakSeconds = 1.5;
    private const double ChromaMinHz = 70.0;
    private const double ChromaMaxHz = 2500.0;

    /// <summary>
    ///     How far a sharp attack sits AFTER the centre of the flux frame that peaks on it: the
    ///     log-flux of a 1024-point Hann window is largest once the attack is past the window's
    ///     middle. Measured on the synthetic pluck (2 ms attack): 4.5 ms. A softer real attack
    ///     peaks a little later still, so this is the smallest correction, not an overshoot.
    /// </summary>
    internal const double FluxPeakLeadMs = 4.5;

    private RecordingAlignmentFeatures(int sampleRate, int hop, float[] onset, float[] smoothOnset,
        double coarseHopMs, float[] coarseOnset, float[][] coarseChroma)
    {
        SampleRate = sampleRate;
        Hop = hop;
        Onset = onset;
        SmoothOnset = smoothOnset;
        CoarseHopMs = coarseHopMs;
        CoarseOnset = coarseOnset;
        CoarseChroma = coarseChroma;
    }

    public int SampleRate { get; }

    public int Hop { get; }

    /// <summary>Normalised onset strength per fine frame, 0..1.</summary>
    public float[] Onset { get; }

    /// <summary><see cref="Onset" /> under a small Gaussian, what the beat placement scores against.</summary>
    public float[] SmoothOnset { get; }

    public double CoarseHopMs { get; }

    /// <summary>Max-pooled <see cref="Onset" /> per coarse frame.</summary>
    public float[] CoarseOnset { get; }

    /// <summary>Unit-length 12-bin chroma per coarse frame; all zeros for silence.</summary>
    public float[][] CoarseChroma { get; }

    public double FineHopMs => Hop * 1000.0 / SampleRate;

    /// <summary>The onset time fine frame <paramref name="k" /> stands for, in ms (its window centre plus <see cref="FluxPeakLeadMs" />).</summary>
    public double FineFrameTimeMs(double k) => (k * Hop + FluxFftSize / 2.0) * 1000.0 / SampleRate + FluxPeakLeadMs;

    /// <summary>The inverse of <see cref="FineFrameTimeMs" />, fractional.</summary>
    public double FineFrameAt(double timeMs) => ((timeMs - FluxPeakLeadMs) * SampleRate / 1000.0 - FluxFftSize / 2.0) / Hop;

    /// <summary><see cref="SmoothOnset" /> at a time, linearly interpolated; zero outside the recording.</summary>
    public float SmoothOnsetAt(double timeMs) => Sample(SmoothOnset, FineFrameAt(timeMs));

    /// <summary>The strongest <see cref="Onset" /> within ±<paramref name="radiusMs" /> of a time.</summary>
    public float PeakOnsetNear(double timeMs, double radiusMs)
    {
        var lo = (int)Math.Floor(FineFrameAt(timeMs - radiusMs));
        var hi = (int)Math.Ceiling(FineFrameAt(timeMs + radiusMs));
        lo = Math.Max(0, lo);
        hi = Math.Min(Onset.Length - 1, hi);
        var best = 0f;
        for (var k = lo; k <= hi; k++) best = Math.Max(best, Onset[k]);
        return best;
    }

    private static float Sample(float[] values, double frame)
    {
        if (frame < 0 || frame > values.Length - 1) return 0f;
        var k = (int)frame;
        if (k >= values.Length - 1) return values[^1];
        var f = (float)(frame - k);
        return values[k] + f * (values[k + 1] - values[k]);
    }

    public static RecordingAlignmentFeatures Compute(ReadOnlySpan<float> mono, int sampleRate, double coarseHopMs,
        CancellationToken ct)
    {
        var hop = Math.Max(64, (int)Math.Round(sampleRate / 375.0));
        var flux = Flux(mono, hop, ct);
        var onset = Normalise(Whiten(flux, Math.Max(3, (int)(WhitenSeconds * sampleRate / hop))),
            Math.Max(3, (int)(LocalPeakSeconds * sampleRate / hop)));
        var smooth = Gaussian(onset, 1.5);

        var coarseFrames = Math.Max(1, (int)Math.Ceiling(mono.Length * 1000.0 / sampleRate / coarseHopMs));
        var coarseOnset = new float[coarseFrames];
        for (var k = 0; k < onset.Length; k++)
        {
            var t = (k * hop + FluxFftSize / 2.0) * 1000.0 / sampleRate + FluxPeakLeadMs;
            var c = (int)(t / coarseHopMs);
            if (c >= 0 && c < coarseFrames) coarseOnset[c] = Math.Max(coarseOnset[c], onset[k]);
        }

        var chroma = Chroma(mono, sampleRate, coarseHopMs, coarseFrames, ct);
        return new RecordingAlignmentFeatures(sampleRate, hop, onset, smooth, coarseHopMs, coarseOnset, chroma);
    }

    private static float[] Flux(ReadOnlySpan<float> mono, int hop, CancellationToken ct)
    {
        const int n = FluxFftSize;
        var frames = mono.Length < n ? 0 : (mono.Length - n) / hop + 1;
        var flux = new float[frames];
        var fft = new JuceFft(n);
        var hann = Hann(n);
        var re = new float[n];
        var im = new float[n];
        var prev = new float[n / 2 + 1];
        var cur = new float[n / 2 + 1];
        for (var f = 0; f < frames; f++)
        {
            if ((f & 1023) == 0) ct.ThrowIfCancellationRequested();
            var start = f * hop;
            var finite = true;
            for (var i = 0; i < n; i++)
            {
                var v = mono[start + i] * hann[i];
                if (!float.IsFinite(v)) finite = false;
                re[i] = v;
            }

            if (!finite)
            {
                // A non-finite sample costs its own frames and nothing after them (BeatMapAnalyzer's rule).
                flux[f] = 0f;
                continue;
            }

            Array.Clear(im);
            fft.Forward(re, im);
            float sum = 0;
            for (var k = 1; k <= n / 2; k++)
            {
                var lm = MathF.Log(1f + 100f * MathF.Sqrt(re[k] * re[k] + im[k] * im[k]));
                var d = lm - prev[k];
                if (f > 0 && d > 0) sum += d;
                cur[k] = lm;
            }

            flux[f] = sum;
            (prev, cur) = (cur, prev);
        }

        return flux;
    }

    private static float[] Hann(int n)
    {
        var w = new float[n];
        for (var i = 0; i < n; i++) w[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / (n - 1));
        return w;
    }

    /// <summary>Subtracts the centred moving average; negatives clamp to zero.</summary>
    private static float[] Whiten(float[] x, int length)
    {
        var result = new float[x.Length];
        var half = length / 2;
        double running = 0;
        var lo = 0;
        var hi = -1;
        for (var i = 0; i < x.Length; i++)
        {
            var wantHi = Math.Min(x.Length - 1, i + half);
            var wantLo = Math.Max(0, i - half);
            while (hi < wantHi) running += x[++hi];
            while (lo < wantLo) running -= x[lo++];
            var mean = running / (hi - lo + 1);
            result[i] = (float)Math.Max(0, x[i] - mean);
        }

        return result;
    }

    /// <summary>Divides by the local peak level (sliding max over <paramref name="length" />), floored
    ///     at a tenth of the song's 98th percentile so silence does not normalise its noise up to 1.</summary>
    private static float[] Normalise(float[] x, int length)
    {
        if (x.Length == 0) return x;
        var sorted = (float[])x.Clone();
        Array.Sort(sorted);
        var floor = Math.Max(1e-6f, 0.1f * sorted[(int)(0.98 * (sorted.Length - 1))]);
        var result = new float[x.Length];
        var half = length / 2;
        var deque = new int[x.Length];
        int head = 0, tail = 0, next = 0;
        for (var i = 0; i < x.Length; i++)
        {
            var wantHi = Math.Min(x.Length - 1, i + half);
            while (next <= wantHi)
            {
                while (tail > head && x[deque[tail - 1]] <= x[next]) tail--;
                deque[tail++] = next++;
            }

            while (deque[head] < i - half) head++;
            var peak = Math.Max(floor, x[deque[head]]);
            result[i] = Math.Min(1f, x[i] / peak);
        }

        return result;
    }

    private static float[] Gaussian(float[] x, double sigma)
    {
        var radius = (int)Math.Ceiling(3 * sigma);
        var kernel = new float[2 * radius + 1];
        float total = 0;
        for (var i = -radius; i <= radius; i++)
        {
            kernel[i + radius] = (float)Math.Exp(-0.5 * i * i / (sigma * sigma));
            total += kernel[i + radius];
        }

        var result = new float[x.Length];
        for (var i = 0; i < x.Length; i++)
        {
            float sum = 0;
            for (var j = -radius; j <= radius; j++)
            {
                var k = i + j;
                if (k >= 0 && k < x.Length) sum += kernel[j + radius] * x[k];
            }

            result[i] = sum / total;
        }

        return result;
    }

    private static float[][] Chroma(ReadOnlySpan<float> mono, int sampleRate, double hopMs, int frames, CancellationToken ct)
    {
        const int n = ChromaFftSize;
        var fft = new JuceFft(n);
        var hann = Hann(n);
        var re = new float[n];
        var im = new float[n];
        var pitchClass = new int[n / 2 + 1];
        for (var k = 0; k <= n / 2; k++)
        {
            var hz = (double)k * sampleRate / n;
            pitchClass[k] = hz is < ChromaMinHz or > ChromaMaxHz
                ? -1
                : (int)(((Math.Round(69 + 12 * Math.Log2(hz / 440.0)) % 12) + 12) % 12);
        }

        var result = new float[frames][];
        var bins = new double[12];
        for (var f = 0; f < frames; f++)
        {
            if ((f & 255) == 0) ct.ThrowIfCancellationRequested();
            var centre = (long)Math.Round((f + 0.5) * hopMs * sampleRate / 1000.0);
            var start = centre - n / 2;
            for (var i = 0; i < n; i++)
            {
                var s = start + i;
                var v = s >= 0 && s < mono.Length ? mono[(int)s] : 0f;
                re[i] = float.IsFinite(v) ? v * hann[i] : 0f;
            }

            Array.Clear(im);
            fft.Forward(re, im);
            Array.Clear(bins);
            for (var k = 1; k <= n / 2; k++)
            {
                if (pitchClass[k] < 0) continue;
                bins[pitchClass[k]] += Math.Log(1.0 + 10.0 * Math.Sqrt(re[k] * re[k] + im[k] * im[k]));
            }

            result[f] = UnitChroma(bins, 0.5);
        }

        return result;
    }

    /// <summary>Scales a 12-bin profile to unit length after subtracting its mean (so broadband noise
    ///     reads as nothing); zero when there is less than <paramref name="minEnergy" /> in it.</summary>
    internal static float[] UnitChroma(double[] bins, double minEnergy)
    {
        var mean = 0.0;
        for (var i = 0; i < 12; i++) mean += bins[i];
        mean /= 12;
        var v = new float[12];
        double norm = 0;
        for (var i = 0; i < 12; i++)
        {
            var d = Math.Max(0, bins[i] - mean);
            v[i] = (float)d;
            norm += d * d;
        }

        if (norm < minEnergy * minEnergy) return new float[12];
        var inv = (float)(1.0 / Math.Sqrt(norm));
        for (var i = 0; i < 12; i++) v[i] *= inv;
        return v;
    }
}
