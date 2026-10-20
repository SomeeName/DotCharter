namespace DotCharter.Tests.Audio.Dsp.RsToneConversion.Fidelity;

/// <summary>What the harness measures on one render. All levels in dB;
/// <see cref="BandDb" /> is relative to the 200 Hz-4 kHz midband mean (a SHAPE),
/// <see cref="BandAbsDb" /> the same bands absolute (for "this band rose by").</summary>
public sealed record ToneMeasurement(
    double PeakDbfs,
    double RmsDbfs,
    IReadOnlyList<double> BandDb,
    IReadOnlyList<double> BandAbsDb,
    double CentroidHz,
    double TiltDbPerOctave,
    double TailMs);

/// <summary>Harmonic content of a sine probe.</summary>
public sealed record DriveMeasurement(double FundamentalDb, double ThdPercent, double OddToEvenDb);

/// <summary>
///     Pure measurement over rendered audio: a third-octave spectral envelope
///     (Welch, Hann, 8192-point), its centroid and tilt, peak / RMS, the decay
///     tail after the DI stops, and the harmonic content of a sine probe. No
///     native code, so the measurement itself is unit-tested in the default gate
///     (<see cref="ToneMetricsTests" />).
/// </summary>
public static class ToneMetrics
{
    /// <summary>ISO 266 third-octave centres, 31.5 Hz .. 16 kHz.</summary>
    public static readonly double[] BandCentresHz =
    [
        31.5, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600,
        2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000
    ];

    private const int FrameSize = 8192;

    /// <summary>Measures a render of <see cref="ReferenceDi" />: the spectrum over the
    /// playing part, the tail over the rest.</summary>
    public static ToneMeasurement Measure(float[] output, int sampleRate, int playEndSample)
    {
        var play = output.AsSpan(0, Math.Min(playEndSample, output.Length));
        var peak = 0.0;
        var sumSq = 0.0;
        foreach (var v in play)
        {
            peak = Math.Max(peak, Math.Abs(v));
            sumSq += (double)v * v;
        }

        var psd = WelchPsd(play, sampleRate);
        var bands = ThirdOctaveBands(psd, sampleRate);
        return new ToneMeasurement(
            Db(peak),
            Db(Math.Sqrt(sumSq / Math.Max(1, play.Length))),
            NormaliseToMidband(bands),
            bands,
            Centroid(psd, sampleRate, 50, 16000),
            Tilt(bands, 100, 8000),
            TailMs(output, sampleRate, playEndSample));
    }

    /// <summary>Harmonics 1..10 of a settled sine probe, by exact single-bin DFT over
    /// the last second (1 Hz bins at 48 kHz, so every harmonic is its own bin).</summary>
    public static DriveMeasurement Drive(float[] output, int sampleRate, int hz, float inputPeak)
    {
        var window = sampleRate;
        var start = output.Length - window;
        var amps = new double[11];
        for (var k = 1; k <= 10; k++)
        {
            var w = 2.0 * Math.PI * hz * k / sampleRate;
            double re = 0, im = 0;
            for (var i = 0; i < window; i++)
            {
                var ph = w * (start + i);
                re += output[start + i] * Math.Cos(ph);
                im -= output[start + i] * Math.Sin(ph);
            }

            amps[k] = 2.0 * Math.Sqrt(re * re + im * im) / window;
        }

        double harm = 0, odd = 0, even = 0;
        for (var k = 2; k <= 10; k++)
        {
            harm += amps[k] * amps[k];
            if (k % 2 == 1) odd += amps[k] * amps[k];
            else even += amps[k] * amps[k];
        }

        return new DriveMeasurement(
            Db(amps[1] / inputPeak),
            amps[1] <= 1e-12 ? 0 : 100.0 * Math.Sqrt(harm) / amps[1],
            10 * Math.Log10((odd + 1e-30) / (even + 1e-30)));
    }

    /// <summary>The mean of the bands within [<paramref name="loHz" />, <paramref name="hiHz" />]
    /// of an envelope (already relative to the midband).</summary>
    public static double BandMean(IReadOnlyList<double> bandDb, double loHz, double hiHz)
    {
        double sum = 0;
        var n = 0;
        for (var i = 0; i < BandCentresHz.Length; i++)
        {
            if (BandCentresHz[i] < loHz || BandCentresHz[i] > hiHz) continue;
            sum += bandDb[i];
            n++;
        }

        return n == 0 ? double.NaN : sum / n;
    }

    /// <summary>The level of one third-octave band (the nearest centre).</summary>
    public static double BandAt(IReadOnlyList<double> bandDb, double hz)
    {
        var best = 0;
        for (var i = 1; i < BandCentresHz.Length; i++)
        {
            if (Math.Abs(Math.Log(BandCentresHz[i] / hz)) < Math.Abs(Math.Log(BandCentresHz[best] / hz))) best = i;
        }

        return bandDb[best];
    }

    // ----------------------------------------------------------------- internals

    internal static double[] WelchPsd(ReadOnlySpan<float> x, int sampleRate)
    {
        _ = sampleRate;
        var hop = FrameSize / 2;
        var psd = new double[FrameSize / 2 + 1];
        var re = new double[FrameSize];
        var im = new double[FrameSize];
        var frames = 0;
        for (var start = 0; start + FrameSize <= x.Length; start += hop)
        {
            for (var i = 0; i < FrameSize; i++)
            {
                var w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FrameSize - 1));
                re[i] = x[start + i] * w;
                im[i] = 0;
            }

            Fft(re, im);
            for (var k = 0; k < psd.Length; k++) psd[k] += re[k] * re[k] + im[k] * im[k];
            frames++;
        }

        if (frames > 0)
        {
            for (var k = 0; k < psd.Length; k++) psd[k] /= frames;
        }

        return psd;
    }

    internal static double[] ThirdOctaveBands(double[] psd, int sampleRate)
    {
        var binHz = (double)sampleRate / FrameSize;
        var bands = new double[BandCentresHz.Length];
        var edge = Math.Pow(2, 1.0 / 6);
        for (var b = 0; b < BandCentresHz.Length; b++)
        {
            var lo = BandCentresHz[b] / edge;
            var hi = BandCentresHz[b] * edge;
            double e = 0;
            for (var k = 1; k < psd.Length; k++)
            {
                var f = k * binHz;
                if (f >= lo && f < hi) e += psd[k];
            }

            bands[b] = 10 * Math.Log10(e + 1e-30);
        }

        return bands;
    }

    private static double[] NormaliseToMidband(double[] bands)
    {
        var mid = BandMean(bands, 200, 4000);
        return bands.Select(b => b - mid).ToArray();
    }

    private static double Centroid(double[] psd, int sampleRate, double loHz, double hiHz)
    {
        var binHz = (double)sampleRate / FrameSize;
        double num = 0, den = 0;
        for (var k = 1; k < psd.Length; k++)
        {
            var f = k * binHz;
            if (f < loHz || f > hiHz) continue;
            num += f * psd[k];
            den += psd[k];
        }

        return den <= 0 ? 0 : num / den;
    }

    private static double Tilt(IReadOnlyList<double> bands, double loHz, double hiHz)
    {
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        var n = 0;
        for (var i = 0; i < BandCentresHz.Length; i++)
        {
            if (BandCentresHz[i] < loHz || BandCentresHz[i] > hiHz) continue;
            var x = Math.Log2(BandCentresHz[i]);
            sx += x;
            sy += bands[i];
            sxx += x * x;
            sxy += x * bands[i];
            n++;
        }

        var d = n * sxx - sx * sx;
        return d == 0 ? 0 : (n * sxy - sx * sy) / d;
    }

    /// <summary>
    ///     How long the output takes, after the DI stops, to fall 30 dB under its
    ///     level in the last 100 ms of playing (10 ms RMS windows). Capped at the
    ///     render's end — a tail that never gets there reads as the whole tail.
    /// </summary>
    internal static double TailMs(float[] output, int sampleRate, int playEndSample)
    {
        var win = sampleRate / 100;
        double Rms(int from)
        {
            double s = 0;
            var n = 0;
            for (var i = from; i < from + win && i < output.Length; i++, n++) s += (double)output[i] * output[i];
            return n == 0 ? 0 : Math.Sqrt(s / n);
        }

        double reference = 0;
        for (var t = playEndSample - 10 * win; t < playEndSample; t += win) reference += Rms(t);
        reference /= 10;
        if (reference <= 1e-9) return 0;

        var floor = reference * Math.Pow(10, -30.0 / 20);
        for (var t = playEndSample; t + win <= output.Length; t += win)
        {
            if (Rms(t) < floor) return (t - playEndSample) * 1000.0 / sampleRate;
        }

        return (output.Length - playEndSample) * 1000.0 / sampleRate;
    }

    private static double Db(double linear) => 20 * Math.Log10(linear + 1e-20);

    /// <summary>In-place iterative radix-2 FFT; the length must be a power of two.</summary>
    internal static void Fft(double[] re, double[] im)
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
            var ang = -2 * Math.PI / len;
            var wr = Math.Cos(ang);
            var wi = Math.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
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
                    var nr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = nr;
                }
            }
        }
    }
}
