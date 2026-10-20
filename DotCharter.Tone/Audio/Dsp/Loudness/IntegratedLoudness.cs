namespace DotCharter.Tone.Audio.Dsp.Loudness;

/// <summary>
///     Gated integrated loudness of a mono signal, ITU-R BS.1770-4 style: the
///     K-weighting pre-filter (a +4 dB high shelf near 1.7 kHz, then a 38 Hz
///     high-pass), mean square over 400 ms blocks at a 100 ms hop, an absolute
///     gate at -70 LUFS and a relative gate 10 LU under the absolute-gated
///     mean. One channel, weight 1, so a full-scale 1 kHz sine reads -3.0 LUFS.
///     <para>
///         <b>Why this and not peak or plain RMS</b> (measured): a clean tone
///         has a crest factor near 22 dB and a driven one near 13 dB, so equal PEAKS
///         leave the two ~10 dB apart in loudness; a plain RMS over a phrase counts
///         the silences and the decays, and weights the 80 Hz fundamental of a low E
///         the same as the 3 kHz bite the ear actually hears. The K-weighting and the
///         gates are what the broadcast loudness standards settled on for exactly
///         that question.
///     </para>
///     <para>Pure, allocation-bounded (one block-energy list), deterministic.</para>
/// </summary>
public static class IntegratedLoudness
{
    /// <summary>Returned for a signal with no block above the absolute gate.</summary>
    public const double Silence = double.NegativeInfinity;

    private const double AbsoluteGateLufs = -70.0;
    private const double RelativeGateLu = -10.0;
    private const double BlockSeconds = 0.4;
    private const double HopSeconds = 0.1;

    /// <summary>The integrated loudness (LUFS) of <paramref name="mono" /> at
    /// <paramref name="sampleRate" />, or <see cref="Silence" /> when nothing passes
    /// the absolute gate (including a signal shorter than one 400 ms block).</summary>
    public static double Measure(ReadOnlySpan<float> mono, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);

        var block = (int)Math.Round(BlockSeconds * sampleRate);
        var hop = (int)Math.Round(HopSeconds * sampleRate);
        if (mono.Length < block) return Silence;

        // K-weighted squares, summed per 100 ms hop: a 400 ms block is then four
        // consecutive hops, so the overlapping blocks cost one pass, not four.
        var hops = mono.Length / hop;
        var hopEnergy = new double[hops];
        var shelf = Biquad.KWeightingShelf(sampleRate);
        var highPass = Biquad.KWeightingHighPass(sampleRate);
        for (var h = 0; h < hops; h++)
        {
            double sum = 0;
            var start = h * hop;
            for (var i = start; i < start + hop; i++)
            {
                var y = highPass.Process(shelf.Process(mono[i]));
                sum += y * y;
            }

            hopEnergy[h] = sum;
        }

        var hopsPerBlock = block / hop;
        var blocks = hops - hopsPerBlock + 1;
        if (blocks <= 0) return Silence;

        var z = new double[blocks];
        for (var b = 0; b < blocks; b++)
        {
            double sum = 0;
            for (var k = 0; k < hopsPerBlock; k++) sum += hopEnergy[b + k];
            z[b] = sum / (hopsPerBlock * hop);
        }

        // Absolute gate.
        double absSum = 0;
        var absCount = 0;
        foreach (var e in z)
        {
            if (Lufs(e) <= AbsoluteGateLufs) continue;
            absSum += e;
            absCount++;
        }

        if (absCount == 0) return Silence;

        // Relative gate, 10 LU under the absolute-gated mean.
        var relativeGate = Lufs(absSum / absCount) + RelativeGateLu;
        double relSum = 0;
        var relCount = 0;
        foreach (var e in z)
        {
            var l = Lufs(e);
            if (l <= AbsoluteGateLufs || l <= relativeGate) continue;
            relSum += e;
            relCount++;
        }

        return relCount == 0 ? Silence : Lufs(relSum / relCount);
    }

    /// <summary>Sample peak of <paramref name="mono" /> in dBFS (-inf for silence).</summary>
    public static double PeakDbfs(ReadOnlySpan<float> mono)
    {
        var peak = 0f;
        foreach (var v in mono) peak = MathF.Max(peak, MathF.Abs(v));
        return peak > 0f ? 20.0 * Math.Log10(peak) : double.NegativeInfinity;
    }

    private static double Lufs(double meanSquare)
        => meanSquare > 0 ? -0.691 + 10.0 * Math.Log10(meanSquare) : double.NegativeInfinity;

    /// <summary>Direct form I biquad in double precision; coefficients normalised
    /// by a0. The two K-weighting stages are BS.1770's, re-derived for any sample
    /// rate from their analog prototypes (the libebur128 derivation), so 44.1 kHz
    /// and 48 kHz read the same signal the same.</summary>
    private sealed class Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        private double _x1, _x2, _y1, _y2;

        public double Process(double x)
        {
            var y = b0 * x + b1 * _x1 + b2 * _x2 - a1 * _y1 - a2 * _y2;
            _x2 = _x1;
            _x1 = x;
            _y2 = _y1;
            _y1 = y;
            return y;
        }

        public static Biquad KWeightingShelf(int sampleRate)
        {
            const double f0 = 1681.974450955533;
            const double gainDb = 3.999843853973347;
            const double q = 0.7071752369554196;
            var k = Math.Tan(Math.PI * f0 / sampleRate);
            var vh = Math.Pow(10.0, gainDb / 20.0);
            var vb = Math.Pow(vh, 0.4996667741545416);
            var a0 = 1.0 + k / q + k * k;
            return new Biquad(
                (vh + vb * k / q + k * k) / a0,
                2.0 * (k * k - vh) / a0,
                (vh - vb * k / q + k * k) / a0,
                2.0 * (k * k - 1.0) / a0,
                (1.0 - k / q + k * k) / a0);
        }

        public static Biquad KWeightingHighPass(int sampleRate)
        {
            const double f0 = 38.13547087602444;
            const double q = 0.5003270373238773;
            var k = Math.Tan(Math.PI * f0 / sampleRate);
            var a0 = 1.0 + k / q + k * k;
            return new Biquad(
                1.0, -2.0, 1.0,
                2.0 * (k * k - 1.0) / a0,
                (1.0 - k / q + k * k) / a0);
        }
    }
}
