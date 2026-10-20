namespace DotCharter.Services.Ai.Match.Similarity;

/// <summary>
///     A triangular LOG-SPACED filter bank over a magnitude half-spectrum, plus the
///     DCT-II that turns its log energies into cepstral coefficients.
///     <para>
///         Built here rather than borrowed because <c>JuceSharp.Dsp</c> has no
///         filterbank or cepstral helper — it has <see cref="JuceSharp.Dsp.JuceFft" />,
///         <see cref="JuceSharp.Dsp.JuceSpectrogramBuilder" /> and
///         <see cref="JuceSharp.Dsp.Windows" />, which is the transform half, and
///         this is the perceptual-binning half on top. No new native code.
///     </para>
///     <para>
///         Log-spaced (constant relative bandwidth) triangles normalised to
///         unit PEAK rather than unit AREA: with unit area a high band's response
///         shrinks as the triangle widens, so a bright tone's high bands would read
///         quieter purely because of the binning. Peak normalisation keeps every
///         band's log energy on the same scale, which is what the distance compares.
///     </para>
///     <para>
///         Deterministic and cached per <c>(bands, fftSize, sampleRate, loHz, hiHz)</c>:
///         the same clip always produces the same bank, and the eval renders thousands
///         of clips through it.
///     </para>
/// </summary>
public sealed class LogFilterBank
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        (int Bands, int FftSize, int SampleRate, int LoHz, int HiHz), LogFilterBank> Cache = new();

    private readonly int[] _start;   // first bin of each triangle (inclusive)
    private readonly float[][] _gain; // triangle weights, _gain[b][i] applies to bin _start[b] + i

    private LogFilterBank(int bands, int fftSize, int sampleRate, int loHz, int hiHz)
    {
        Bands = bands;
        _start = new int[bands];
        _gain = new float[bands][];

        var binCount = fftSize / 2;
        var binHz = sampleRate / (double)fftSize;

        // bands + 2 log-spaced edges: triangle b spans edge[b] .. edge[b+2], peaking at edge[b+1].
        var edges = new double[bands + 2];
        var loScale = ToScale(loHz);
        var hiScale = ToScale(Math.Min(hiHz, sampleRate / 2.0));
        for (var i = 0; i < edges.Length; i++)
            edges[i] = FromScale(loScale + (hiScale - loScale) * i / (bands + 1));

        for (var b = 0; b < bands; b++)
        {
            var lo = edges[b];
            var mid = edges[b + 1];
            var hi = edges[b + 2];

            var first = Math.Max(0, (int)Math.Floor(lo / binHz));
            var last = Math.Min(binCount - 1, (int)Math.Ceiling(hi / binHz));
            if (last < first) last = first;

            var gains = new float[last - first + 1];
            for (var i = 0; i < gains.Length; i++)
            {
                var f = (first + i) * binHz;
                double g;
                if (f <= lo || f >= hi) g = 0;
                else if (f <= mid) g = mid > lo ? (f - lo) / (mid - lo) : 1;
                else g = hi > mid ? (hi - f) / (hi - mid) : 1;
                gains[i] = (float)g;
            }

            // A triangle narrower than one bin can land entirely between two bins and
            // come out all-zero — that band would then read as silence for every clip.
            // Give it the nearest bin at unit gain instead.
            var any = false;
            foreach (var g in gains)
                if (g > 0) { any = true; break; }
            if (!any)
            {
                var nearest = Math.Clamp((int)Math.Round(mid / binHz), 0, binCount - 1);
                first = nearest;
                gains = [1f];
            }

            _start[b] = first;
            _gain[b] = gains;
        }
    }

    /// <summary>Number of bands this bank produces.</summary>
    public int Bands { get; }

    /// <summary>The bank for these parameters, built once and shared. Thread-safe.</summary>
    public static LogFilterBank Get(int bands, int fftSize, int sampleRate, int loHz = 30, int hiHz = 16000) =>
        Cache.GetOrAdd((bands, fftSize, sampleRate, loHz, hiHz),
            static k => new LogFilterBank(k.Bands, k.FftSize, k.SampleRate, k.LoHz, k.HiHz));

    /// <summary>
    ///     Bins a magnitude half-spectrum into <see cref="Bands" /> LINEAR band
    ///     powers, ADDING into <paramref name="destPower" /> rather than overwriting.
    ///     <para>
    ///         <b>Accumulating power and taking the logarithm once, at the end, is not
    ///         the same as averaging per-frame logarithms — and the difference is most
    ///         of this extractor's content invariance.</b> A guitar spectrum is a
    ///         harmonic comb: note A puts energy in one set of bands, note B in
    ///         another. Average the LOG spectra and every frame's comb survives into
    ///         the mean, so the mean describes the notes. Average the POWER first and
    ///         the combs of different notes fill each other in, leaving the envelope
    ///         they all share — which is the rig. Measured on two performances through
    ///         one rig, this one change cut the MFCC-mean block's content sensitivity
    ///         by more than half.
    ///     </para>
    /// </summary>
    public void AccumulatePower(ReadOnlySpan<float> magnitude, Span<double> destPower)
    {
        for (var b = 0; b < Bands; b++)
        {
            double sum = 0;
            var gains = _gain[b];
            var start = _start[b];
            for (var i = 0; i < gains.Length; i++)
            {
                var bin = start + i;
                if (bin >= magnitude.Length) break;
                var m = magnitude[bin] * gains[i];
                sum += m * (double)m;
            }

            destPower[b] += sum;
        }
    }

    /// <summary>
    ///     Bins a magnitude half-spectrum into <see cref="Bands" /> band energies, in
    ///     dB (10·log10 of summed POWER, floored 120 dB under unity).
    /// </summary>
    public void LogBands(ReadOnlySpan<float> magnitude, Span<float> destDb)
    {
        for (var b = 0; b < Bands; b++)
        {
            double sum = 0;
            var gains = _gain[b];
            var start = _start[b];
            for (var i = 0; i < gains.Length; i++)
            {
                var bin = start + i;
                if (bin >= magnitude.Length) break;
                var m = magnitude[bin] * gains[i];
                sum += m * (double)m;
            }

            destDb[b] = (float)(10.0 * Math.Log10(Math.Max(sum, 1e-12)));
        }
    }

    /// <summary>
    ///     DCT-II (orthonormal-ish, scaled by <c>sqrt(2/N)</c>) of log band energies →
    ///     <paramref name="destCoeffs" />.Length cepstral coefficients, c0 included.
    /// </summary>
    public static void Dct(ReadOnlySpan<float> logBands, Span<float> destCoeffs)
    {
        var n = logBands.Length;
        var scale = Math.Sqrt(2.0 / n);
        for (var k = 0; k < destCoeffs.Length; k++)
        {
            double sum = 0;
            for (var i = 0; i < n; i++)
                sum += logBands[i] * Math.Cos(Math.PI * k * (i + 0.5) / n);
            destCoeffs[k] = (float)(sum * scale);
        }
    }

    /// <summary>
    ///     The band scale: <b>log2 of frequency</b>, so every band spans the same
    ///     fraction of an octave.
    ///     <para>
    ///         <b>This was an HTK mel scale until the search lane found what that
    ///         costs.</b> Mel is quasi-LINEAR below ~1 kHz, so the bottom band of a
    ///         60 Hz–10 kHz bank covered 60–141 Hz — 1.2 octaves, the entire region a
    ///         low shelf acts on, in ONE number. A search optimising against the
    ///         resulting distance drove an amp's low-gain knob to its minimum while
    ///         the distance kept falling, because a shelf's whole effect was one
    ///         coefficient that a dozen other things also moved. A log scale gives the
    ///         bottom band 60–69 Hz and the shelf six bands to be visible in.
    ///     </para>
    ///     <para>
    ///         It also makes the resolution uniform in the unit a guitarist thinks in.
    ///         Mel spends half its bands below 1 kHz and leaves the 2–6 kHz presence
    ///         region — where a drive's character lives — coarse; log does not.
    ///     </para>
    /// </summary>
    private static double ToScale(double hz) => Math.Log2(Math.Max(hz, 1.0));

    private static double FromScale(double scale) => Math.Pow(2.0, scale);
}
