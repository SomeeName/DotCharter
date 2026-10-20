namespace DotCharter.Tests.Audio.Dsp.RsToneConversion.Fidelity;

/// <summary>
///     The fixed reference DI the fidelity harness renders through every tone:
///     generated in code (Karplus-Strong plucked strings over a fixed-seed
///     excitation), so it is our own, shippable, and bit-identical on every run.
///     <para>
///         Layout at 48 kHz: E2 single note (0-1.5 s), E4 single note (1.5-3 s), an
///         E5 power chord E2+B2+E3 (3-5 s), then 3 s of silence for the tail. The
///         whole DI is scaled to a -18 dBFS PEAK — the reference input level
///         <c>DcRigLoudnessNormalizer</c> estimates against, so a rendered peak can
///         be read straight against the normaliser's -12 dBFS target.
///     </para>
/// </summary>
public static class ReferenceDi
{
    public const int SampleRate = 48000;

    /// <summary>-18 dBFS, <c>DcRigLoudnessNormalizer.ReferenceInputPeakDb</c>.</summary>
    public const float PeakLinear = 0.12589254f;

    /// <summary>Where the playing stops and the tail measurement starts.</summary>
    public const int PlayEndSample = 5 * SampleRate;

    public const int TotalSamples = 8 * SampleRate;

    private static readonly Lazy<float[]> Cached = new(Build);

    /// <summary>The DI (mono). Callers must not write to it.</summary>
    public static float[] Samples => Cached.Value;

    /// <summary>A settled sine at <paramref name="hz" /> and -18 dBFS peak, for the
    /// describing-function drive probe: <paramref name="settleSamples" /> of
    /// run-in, then exactly one second (1 Hz DFT bins at 48 kHz).</summary>
    public static float[] Sine(int hz, int settleSamples = SampleRate * 2 / 5)
    {
        var n = settleSamples + SampleRate;
        var x = new float[n];
        var w = 2.0 * Math.PI * hz / SampleRate;
        for (var i = 0; i < n; i++) x[i] = (float)(PeakLinear * Math.Sin(w * i));
        return x;
    }

    /// <summary>
    ///     What an idle guitar input carries (#155): broadband interface noise at
    ///     <paramref name="noiseRmsDbfs" /> RMS plus single-coil mains hum (50 Hz and its
    ///     harmonics up to 750 Hz at 1/n) at <paramref name="humRmsDbfs" /> RMS. Fixed seed,
    ///     so a render of it is bit-identical on every run.
    /// </summary>
    public static float[] IdleNoise(int samples, double noiseRmsDbfs = -78, double humRmsDbfs = -72)
    {
        var rng = new Lcg(0x1D1Eu);
        var noise = new double[samples];
        var hum = new double[samples];
        for (var i = 0; i < samples; i++)
        {
            // The sum of four uniforms: near-Gaussian, like an input stage's hiss.
            noise[i] = rng.NextSigned() + rng.NextSigned() + rng.NextSigned() + rng.NextSigned();
            for (var n = 1; n <= 15; n++) hum[i] += Math.Sin(2 * Math.PI * 50 * n * i / SampleRate + n * 0.7) / n;
        }

        var y = new float[samples];
        var noiseGain = Math.Pow(10, noiseRmsDbfs / 20) / Rms(noise);
        var humGain = Math.Pow(10, humRmsDbfs / 20) / Rms(hum);
        for (var i = 0; i < samples; i++) y[i] = (float)(noise[i] * noiseGain + hum[i] * humGain);
        return y;
    }

    private static double Rms(double[] x) => Math.Sqrt(x.Sum(v => v * v) / Math.Max(1, x.Length));

    private static float[] Build()
    {
        var x = new double[TotalSamples];
        var rng = new Lcg(0x5EED_D1u);

        Pluck(x, rng, 82.41, 0, (int)(1.5 * SampleRate));
        Pluck(x, rng, 329.63, (int)(1.5 * SampleRate), (int)(1.5 * SampleRate));
        foreach (var f in new[] { 82.41, 123.47, 164.81 })
            Pluck(x, rng, f, 3 * SampleRate, 2 * SampleRate);

        // A 10 ms release at the end of the playing, like a palm on the strings —
        // a hard cut would put a click into the tail measurement.
        var fade = SampleRate / 100;
        for (var i = 0; i < fade; i++) x[PlayEndSample - fade + i] *= 1.0 - (double)(i + 1) / fade;
        for (var i = PlayEndSample; i < TotalSamples; i++) x[i] = 0;

        var peak = x.Max(Math.Abs);
        var y = new float[TotalSamples];
        for (var i = 0; i < TotalSamples; i++) y[i] = (float)(x[i] / peak * PeakLinear);
        return y;
    }

    /// <summary>Karplus-Strong: a noise burst through a delay line whose loop
    /// averages two taps (the string's loss), with a one-pole on the excitation
    /// for a pick that is not infinitely bright. A test signal, not a guitar
    /// model: its job is a stable, guitar-shaped spectrum and a decay.</summary>
    private static void Pluck(double[] dst, Lcg rng, double hz, int start, int length)
    {
        var period = Math.Max(2, (int)Math.Round(SampleRate / hz));
        var line = new double[period + 1];
        double lp = 0;
        for (var i = 0; i < line.Length; i++)
        {
            lp += 0.5 * (rng.NextSigned() - lp);
            line[i] = lp;
        }

        // Per-period loss for a 3 s T60 at every pitch (-60 dB in 3 s): without
        // it a fixed loss rings the low E for many seconds and kills a high E in
        // under one.
        var loss = Math.Pow(10, -3.0 / (3.0 * hz));
        var idx = 0;
        for (var n = 0; n < length && start + n < dst.Length; n++)
        {
            var next = (idx + 1) % line.Length;
            var y = line[idx];
            line[idx] = loss * 0.5 * (line[idx] + line[next]);
            idx = next;
            dst[start + n] += y;
        }
    }

    /// <summary>A tiny fixed LCG: the excitation must not depend on the runtime's
    /// <see cref="Random" /> implementation.</summary>
    private sealed class Lcg(uint seed)
    {
        private uint _state = seed;

        public double NextSigned()
        {
            _state = unchecked(_state * 1664525u + 1013904223u);
            return (_state >> 8) / (double)(1 << 24) * 2.0 - 1.0;
        }
    }
}
