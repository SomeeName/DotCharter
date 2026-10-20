namespace DotCharter.Tone.Audio.Dsp.Loudness;

/// <summary>
///     The guitar phrase a tone's loudness is MEASURED with: generated in code
///     (Karplus-Strong plucks over a fixed-seed excitation), so it ships, needs no
///     asset and is bit-identical on every machine and every run — which is what
///     lets a measurement be cached by the chain alone.
///     <para>
///         Content, at 48 kHz, scaled to a -18 dBFS PEAK (the walker's
///         <c>DcRigLoudnessNormalizer.ReferenceInputPeakDb</c>, a hot guitar at healthy
///         interface gain): eight palm-muted low-E eighths, single notes on the A, D
///         and G strings, a strummed open E chord, two A5 power-chord hits and a
///         four-note lead line high on the neck — 6.6 s of playing in a 7 s buffer, the mix of
///         sustain, attack and register that a song's part actually has.
///     </para>
///     <para>
///         <b>Deliberately NOT the fidelity harness's reference DI</b>
///         (<c>DotCharter.Tests/.../Fidelity/ReferenceDi</c>: three sustained
///         notes/chords): the harness judges the normalisation by rendering THAT one,
///         so a tone levelled with this phrase and judged with the other is a real
///         measurement of how well the levelling generalises, not a tautology.
///     </para>
/// </summary>
public static class CalibrationPhrase
{
    public const int SampleRate = 48_000;

    /// <summary>-18 dBFS.</summary>
    public const float PeakLinear = 0.12589254f;

    /// <summary>Buffer length: 6.6 s of playing, then silence.</summary>
    public const int TotalSamples = 7 * SampleRate;

    private static readonly Lazy<float[]> Cached = new(Build);

    /// <summary>The phrase (mono). Shared — callers must copy before writing.</summary>
    public static ReadOnlySpan<float> Samples => Cached.Value;

    private static float[] Build()
    {
        var x = new double[TotalSamples];
        var rng = new Lcg(0xCA1B_0A7Eu);
        var t = 0.0;

        // Palm-muted low-E eighths at 140 bpm: short, damped, percussive.
        for (var i = 0; i < 8; i++, t += 0.214)
            Pluck(x, rng, 82.41, t, 0.17, t60Seconds: 0.25, amplitude: 0.8);

        // Single notes on the A, D and G strings.
        foreach (var hz in new[] { 110.0, 146.83, 196.0 })
        {
            Pluck(x, rng, hz, t, 0.45, t60Seconds: 2.5, amplitude: 0.9);
            t += 0.45;
        }

        // A strummed open E (E2 B2 E3 G#3 B3 E4), 12 ms between strings.
        var strum = new[] { 82.41, 123.47, 164.81, 207.65, 246.94, 329.63 };
        for (var s = 0; s < strum.Length; s++)
            Pluck(x, rng, strum[s], t + s * 0.012, 1.4, t60Seconds: 3.0, amplitude: 0.7);
        t += 1.4;

        // Two A5 power-chord hits (A2 E3 A3).
        for (var hit = 0; hit < 2; hit++, t += 0.45)
        {
            foreach (var hz in new[] { 110.0, 164.81, 220.0 })
                Pluck(x, rng, hz, t, 0.42, t60Seconds: 2.0, amplitude: 0.85);
        }

        // A lead line: B3 D4 E4 G4.
        foreach (var hz in new[] { 246.94, 293.66, 329.63, 392.0 })
        {
            Pluck(x, rng, hz, t, 0.3, t60Seconds: 2.5, amplitude: 1.0);
            t += 0.3;
        }

        var peak = 0.0;
        foreach (var v in x) peak = Math.Max(peak, Math.Abs(v));
        var y = new float[TotalSamples];
        for (var i = 0; i < TotalSamples; i++) y[i] = (float)(x[i] / peak * PeakLinear);
        return y;
    }

    /// <summary>One Karplus-Strong note from <paramref name="startSeconds" /> for
    /// <paramref name="lengthSeconds" />, decaying -60 dB in <paramref name="t60Seconds" />,
    /// with a 10 ms release at its end (a palm or the next pick, never a click).</summary>
    private static void Pluck(double[] dst, Lcg rng, double hz, double startSeconds, double lengthSeconds,
        double t60Seconds, double amplitude)
    {
        var start = (int)Math.Round(startSeconds * SampleRate);
        var length = (int)Math.Round(lengthSeconds * SampleRate);
        var period = Math.Max(2, (int)Math.Round(SampleRate / hz));
        var line = new double[period + 1];
        double lp = 0;
        for (var i = 0; i < line.Length; i++)
        {
            lp += 0.5 * (rng.NextSigned() - lp);
            line[i] = lp;
        }

        var loss = Math.Pow(10, -3.0 / (t60Seconds * hz));
        var fade = SampleRate / 100;
        var idx = 0;
        for (var n = 0; n < length && start + n < dst.Length; n++)
        {
            var next = (idx + 1) % line.Length;
            var y = line[idx];
            line[idx] = loss * 0.5 * (line[idx] + line[next]);
            idx = next;
            var release = n >= length - fade ? (double)(length - n) / fade : 1.0;
            dst[start + n] += amplitude * y * release;
        }
    }

    /// <summary>A tiny fixed LCG: the phrase must not depend on the runtime's
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
