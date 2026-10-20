namespace DotCharter.Tests.Services.Audio.Tuner;

/// <summary>
///     Deterministic synthesized signals for the managed tuner-core tests.
///     These signals (and the tolerances asserted over them) are the
///     reference contract that carries over when the 0.18 tuner graduates to
///     native (0.18 Phase 0).
/// </summary>
internal static class TunerTestSignals
{
    /// <summary>Pure sine at <paramref name="frequencyHz"/>.</summary>
    public static float[] Sine(double frequencyHz, int sampleRate, int length,
        double amplitude = 0.25, double phase = 0.0)
    {
        var samples = new float[length];
        var step = 2.0 * Math.PI * frequencyHz / sampleRate;
        for (var n = 0; n < length; n++)
            samples[n] = (float)(amplitude * Math.Sin(phase + step * n));
        return samples;
    }

    /// <summary>
    ///     Sawtooth-ish periodic tone: fundamental plus
    ///     <paramref name="harmonicCount"/> harmonics at 1/k amplitudes,
    ///     peak-normalized to <paramref name="amplitude"/>.
    /// </summary>
    public static float[] Harmonics(double f0, int sampleRate, int length,
        double amplitude = 0.25, int harmonicCount = 8)
    {
        var samples = new double[length];
        var step = 2.0 * Math.PI * f0 / sampleRate;
        for (var k = 1; k <= harmonicCount; k++)
        {
            for (var n = 0; n < length; n++)
                samples[n] += Math.Sin(step * k * n) / k;
        }

        double peak = 0;
        for (var n = 0; n < length; n++) peak = Math.Max(peak, Math.Abs(samples[n]));

        var result = new float[length];
        var scale = peak > 0 ? amplitude / peak : 0;
        for (var n = 0; n < length; n++) result[n] = (float)(samples[n] * scale);
        return result;
    }

    /// <summary>Fundamental plus one strong octave (2nd) harmonic — the
    /// classic octave-error bait.</summary>
    public static float[] SineWithStrongOctave(double f0, int sampleRate, int length,
        double amplitude = 0.25, double octaveRatio = 0.9)
    {
        var samples = new float[length];
        var step = 2.0 * Math.PI * f0 / sampleRate;
        for (var n = 0; n < length; n++)
            samples[n] = (float)(amplitude * (Math.Sin(step * n) + octaveRatio * Math.Sin(2 * step * n)));
        return samples;
    }

    /// <summary>Seeded uniform white noise.</summary>
    public static float[] Noise(int length, int seed = 1234, double amplitude = 0.25)
    {
        var random = new Random(seed);
        var samples = new float[length];
        for (var n = 0; n < length; n++)
            samples[n] = (float)(amplitude * (random.NextDouble() * 2.0 - 1.0));
        return samples;
    }

    /// <summary>Frequency shifted by a signed number of cents.</summary>
    public static double Detune(double frequencyHz, double cents)
        => frequencyHz * Math.Pow(2.0, cents / 1200.0);

    /// <summary>Signed cent distance between a frequency and a reference.</summary>
    public static double CentsBetween(double frequencyHz, double referenceHz)
        => 1200.0 * Math.Log2(frequencyHz / referenceHz);
}
