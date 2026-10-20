namespace DotCharter.Tests.Audio.Dsp.RsToneConversion.Fidelity;

/// <summary>
///     The fidelity harness's measurements, checked on signals whose answer is
///     known in closed form — so a calibration number is never a bug in the ruler.
/// </summary>
public sealed class ToneMetricsTests
{
    private const int Sr = ReferenceDi.SampleRate;

    [Fact]
    public void Fft_PutsASineInItsBin()
    {
        var re = new double[64];
        var im = new double[64];
        for (var i = 0; i < 64; i++) re[i] = Math.Cos(2 * Math.PI * 5 * i / 64);
        ToneMetrics.Fft(re, im);
        Assert.Equal(32, Math.Sqrt(re[5] * re[5] + im[5] * im[5]), 6);
        Assert.Equal(0, Math.Sqrt(re[6] * re[6] + im[6] * im[6]), 6);
    }

    [Fact]
    public void Envelope_OfASine_PeaksInItsThirdOctaveBand()
    {
        var x = Sine(1000, 0.5f, Sr * 2);
        var m = ToneMetrics.Measure(x, Sr, x.Length);
        Assert.Equal(m.BandDb.Max(), ToneMetrics.BandAt(m.BandDb, 1000));
        Assert.InRange(m.CentroidHz, 950, 1050);
        Assert.Equal(20 * Math.Log10(0.5), m.PeakDbfs, 1);
        Assert.Equal(20 * Math.Log10(0.5 / Math.Sqrt(2)), m.RmsDbfs, 1);
    }

    [Fact]
    public void Tilt_OfPinkishNoise_IsNegative_AndOfWhiteNoise_IsRising()
    {
        // White noise has equal power per Hz, so a third-octave band (whose width
        // grows with frequency) gains +1 dB per third, i.e. +3 dB/octave.
        var white = Noise(Sr * 4, 7);
        var w = ToneMetrics.Measure(white, Sr, white.Length);
        Assert.InRange(w.TiltDbPerOctave, 2.5, 3.5);

        // One-pole low-passed at ~100 Hz: -6 dB/oct per Hz above the corner, so
        // the third-octave tilt is about 3 - 6 = -3 dB/oct.
        var red = new float[white.Length];
        var a = Math.Exp(-2 * Math.PI * 100.0 / Sr);
        double y = 0;
        for (var i = 0; i < red.Length; i++)
        {
            y = (1 - a) * white[i] + a * y;
            red[i] = (float)y;
        }

        var r = ToneMetrics.Measure(red, Sr, red.Length);
        Assert.InRange(r.TiltDbPerOctave, -3.5, -2.0);
    }

    [Fact]
    public void Drive_OfACleanSine_IsZero_AndOfAHardClip_IsLarge()
    {
        var clean = Sine(220, ReferenceDi.PeakLinear, Sr + Sr / 2);
        var c = ToneMetrics.Drive(clean, Sr, 220, ReferenceDi.PeakLinear);
        Assert.InRange(c.ThdPercent, 0, 0.01);
        Assert.Equal(0, c.FundamentalDb, 2);

        // Symmetric hard clip at half the peak: odd harmonics only, ~20 % THD.
        var clip = clean.Select(v => Math.Clamp(v, -ReferenceDi.PeakLinear / 2, ReferenceDi.PeakLinear / 2)).ToArray();
        var d = ToneMetrics.Drive(clip, Sr, 220, ReferenceDi.PeakLinear);
        Assert.InRange(d.ThdPercent, 15, 30);
        Assert.True(d.OddToEvenDb > 40, $"a symmetric clip has no even harmonics (odd/even {d.OddToEvenDb:F1} dB)");
    }

    [Fact]
    public void Tail_OfAnExponentialDecay_IsItsMinus30DbTime()
    {
        // A tone that decays by 60 dB/s after the "DI" stops reaches -30 dB 500 ms
        // later; the 10 ms RMS windows quantise the answer to a window.
        var playEnd = Sr;
        var x = new float[Sr * 3];
        for (var i = 0; i < x.Length; i++)
        {
            var env = i < playEnd ? 1.0 : Math.Pow(10, -3.0 * (i - playEnd) / Sr);
            x[i] = (float)(0.25 * env * Math.Sin(2 * Math.PI * 440 * i / Sr));
        }

        Assert.InRange(ToneMetrics.TailMs(x, Sr, playEnd), 480, 520);

        // …and a signal that stops dead has no tail at all.
        var dry = x.Select((v, i) => i < playEnd ? v : 0f).ToArray();
        Assert.InRange(ToneMetrics.TailMs(dry, Sr, playEnd), 0, 10);
    }

    [Fact]
    public void ReferenceDi_IsDeterministic_AtTheNormaliserReferenceLevel()
    {
        var di = ReferenceDi.Samples;
        Assert.Equal(ReferenceDi.TotalSamples, di.Length);
        Assert.Equal(ReferenceDi.PeakLinear, di.Max(Math.Abs), 5);
        Assert.All(di.Skip(ReferenceDi.PlayEndSample), v => Assert.Equal(0f, v));

        // A pluck, not a drone: the E2 note's last 100 ms is well under its first.
        static double Rms(float[] x, int from, int n) => Math.Sqrt(x.Skip(from).Take(n).Average(v => (double)v * v));
        Assert.True(Rms(di, 0, Sr / 10) > 4 * Rms(di, (int)(1.4 * Sr), Sr / 10));

        // Generated in code from a fixed seed: the same bits every time.
        Assert.Equal(0.0204, Math.Round(di[1000], 4));
    }

    private static float[] Sine(int hz, float amp, int n)
    {
        var x = new float[n];
        for (var i = 0; i < n; i++) x[i] = (float)(amp * Math.Sin(2 * Math.PI * hz * i / Sr));
        return x;
    }

    private static float[] Noise(int n, int seed)
    {
        var rng = new Random(seed);
        var x = new float[n];
        for (var i = 0; i < n; i++) x[i] = (float)(rng.NextDouble() * 2 - 1) * 0.1f;
        return x;
    }
}
