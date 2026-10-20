using JuceSharp.Dsp;

namespace DotCharter.Analysis.Tone;

/// <summary>
///     Reduces one span of mono guitar audio to a <see cref="ToneAudioProfile" />
///     — the measurement half of "generate a tone from the song's own audio".
///     <para>
///         The charter places the tone change; the caller slices the audio between
///         that change and the next one, downmixes it to mono, and hands the span
///         here. Nothing in this file knows about time, charts or gear, and the
///         mapping onto amps and pedals happens a layer up.
///     </para>
///     <para>
///         <b>Deterministic by construction</b> — see
///         <c>DotCharter.Analysis/AGENTS.md</c>. Every accumulation walks a fixed
///         index order into a <see cref="double" />, there is no parallelism, no
///         clock, no randomness and no hash-ordered enumeration. The same span at
///         the same rate yields a bit-identical profile.
///     </para>
///     <para>
///         <b>Mono only.</b> Channel handling belongs to the caller; a stereo
///         interleaved buffer passed here would be analysed as a signal at twice
///         the rate and would report nonsense without failing.
///     </para>
/// </summary>
public static class ToneAudioAnalyzer
{
    /// <summary>
    ///     Analysis frame length in samples (~93 ms at 44.1 kHz). Long enough to
    ///     RESOLVE a low-E fundamental from its second harmonic — at 2048 the
    ///     harmonics of an 82 Hz note sit closer together than the analysis
    ///     window's own main lobe is wide, so no band drawn around one of them
    ///     can avoid swallowing its neighbour — and short enough that a frame is
    ///     one note rather than one phrase.
    /// </summary>
    public const int FrameSize = 4096;

    /// <summary>Frame advance in samples — 75 % overlap.</summary>
    public const int HopSize = 1024;

    /// <summary>
    ///     Transform length. Deliberately twice <see cref="FrameSize" />: the
    ///     autocorrelation behind <see cref="ToneAudioProfile.HarmonicRatio" /> is
    ///     computed through this FFT, and a CIRCULAR correlation of an unpadded
    ///     frame wraps the end of the frame onto its start — which reads as a
    ///     periodicity that is not there.
    /// </summary>
    public const int FftSize = 8192;

    /// <summary>Upper edge of <see cref="ToneAudioProfile.LowEnergy" />.</summary>
    public const double LowBandMaxHz = 250.0;

    /// <summary>Upper edge of <see cref="ToneAudioProfile.MidEnergy" />.</summary>
    public const double MidBandMaxHz = 2000.0;

    /// <summary>Lowest fundamental looked for — below a 7-string's low B.</summary>
    public const double MinFundamentalHz = 50.0;

    /// <summary>Highest fundamental looked for — E6, the top of the neck.</summary>
    public const double MaxFundamentalHz = 1320.0;

    /// <summary>
    ///     Normalised autocorrelation a frame's period peak must reach before the
    ///     frame is called pitched. Noise and percussive frames sit well below it.
    /// </summary>
    public const double MinFundamentalConfidence = 0.35;

    /// <summary>
    ///     What an UNPITCHED frame contributes to
    ///     <see cref="ToneAudioProfile.HarmonicRatio" />.
    ///     <para>
    ///         Deliberately small but NOT zero, and the distinction matters. Zero
    ///         is what an arithmetic accident also produces, so it cannot be told
    ///         apart from "the detector never ran"; worse,
    ///         <see cref="ToneAudioProfile.Drive" /> reads
    ///         <c>1 - HarmonicRatio</c> as "energy smeared between the partials",
    ///         so a failed detection at 0.0 would score as the MOST distorted
    ///         possible signal. 0.05 says "we looked, there is no harmonic series
    ///         here" — which is the honest reading of noise — while leaving the
    ///         driven-vs-clean ordering to the crest and flatness terms that
    ///         actually measured something.
    ///     </para>
    /// </summary>
    public const double UnpitchedHarmonicRatio = 0.05;

    /// <summary>Highest harmonic counted by <see cref="ToneAudioProfile.HarmonicRatio" />.</summary>
    private const int MaxHarmonic = 20;

    /// <summary>
    ///     Half-width, in bins, of a Hann window's main lobe at
    ///     <see cref="FftSize" /> — <c>2 × FftSize / FrameSize</c>, plus margin.
    ///     A harmonic's band must be at least this wide or it measures the tip of
    ///     the lobe rather than the partial, and even a pure sine then reports a
    ///     mediocre <see cref="ToneAudioProfile.HarmonicRatio" />.
    /// </summary>
    private const double MainLobeHalfWidthBins = 4.5;

    /// <summary>dBFS reported for digital silence, instead of -infinity.</summary>
    private const double SilenceDb = -120.0;

    /// <summary>
    ///     Relative floor under the log in the flatness geometric mean, as a
    ///     fraction of the frame's own mean magnitude. A bin can be exactly zero,
    ///     and <c>log(0)</c> would take the whole profile to negative infinity;
    ///     making the floor RELATIVE keeps flatness scale-invariant, which it is
    ///     by definition.
    /// </summary>
    private const double FlatnessFloor = 1e-10;

    /// <summary>
    ///     Measure one span of mono audio.
    /// </summary>
    /// <param name="monoSamples">
    ///     The span between one tone change and the next, mono, nominally -1..1.
    ///     May be shorter than a single frame — including empty — and may contain
    ///     non-finite samples from a decoder glitch; both are handled rather than
    ///     thrown on.
    /// </param>
    /// <param name="sampleRate">Samples per second. Must be positive.</param>
    /// <returns>
    ///     A profile whose every field is finite. A span under
    ///     <see cref="ToneAudioProfile.SilenceRms" /> comes back
    ///     <see cref="ToneAudioProfile.IsSilent" /> with neutral spectral fields —
    ///     no tone may be inferred from it.
    /// </returns>
    public static ToneAudioProfile Analyze(ReadOnlySpan<float> monoSamples, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        // ---------------------------------------------------------- time domain
        //
        // A non-finite sample is neutralised HERE, where it enters. One NaN in a
        // frame is smeared across every FFT bin, and NaN then loses every
        // comparison downstream — so the guards that would drop it are the exact
        // ones it slips past. Counting it as 0 keeps the span's LENGTH honest,
        // which skipping it would not.
        double sumOfSquares = 0;
        double peak = 0;
        for (var i = 0; i < monoSamples.Length; i++)
        {
            var s = monoSamples[i];
            var v = float.IsFinite(s) ? (double)s : 0.0;
            sumOfSquares += v * v;
            var magnitude = Math.Abs(v);
            if (magnitude > peak) peak = magnitude;
        }

        var rms = monoSamples.Length > 0 ? Math.Sqrt(sumOfSquares / monoSamples.Length) : 0.0;
        var peakDb = peak > 0 ? Math.Max(SilenceDb, 20.0 * Math.Log10(peak)) : SilenceDb;

        if (rms < ToneAudioProfile.SilenceRms) return Silent(rms, peakDb);

        // Crest is peak-over-RMS; rms is above SilenceRms here, so the ratio is
        // finite and the log is defined. A silent span never reaches this line,
        // which is what keeps the field out of NaN.
        var crestDb = Math.Max(0.0, 20.0 * Math.Log10(peak / rms));

        // ------------------------------------------------------- spectral domain
        var fft = new JuceFft(FftSize);
        var window = HannWindow(FrameSize);
        var re = new float[FftSize];
        var im = new float[FftSize];
        var magnitudes = new double[FftSize / 2];
        var power = new float[FftSize];

        var binHz = (double)sampleRate / FftSize;
        var nyquistBin = FftSize / 2;

        // Every per-frame feature is averaged weighted by that frame's ENERGY.
        //
        // Averaging the SPECTRA and taking one flatness of the result would be
        // cheaper and is wrong in a way that matters here: a span holding four
        // different clean chords averages into a spectrum with energy everywhere,
        // which reads as flat, which reads as distorted. Per-frame features
        // sidestep that, and the energy weight stops a near-silent frame's
        // garbage spectrum from voting.
        double weightSum = 0;
        double centroidSum = 0, flatnessSum = 0, harmonicSum = 0;
        double lowSum = 0, midSum = 0, highSum = 0;

        double envelopeSum = 0, envelopePeak = 0;
        var frameCount = 0;

        foreach (var (offset, count) in Frames(monoSamples.Length))
        {
            frameCount++;

            // Frame envelope is the RMS of the RAW samples: Sustain asks how much
            // level survives between attacks, and a window would taper that away.
            double frameSquares = 0;
            for (var i = 0; i < count; i++)
            {
                var s = monoSamples[offset + i];
                var v = float.IsFinite(s) ? (double)s : 0.0;
                frameSquares += v * v;
                re[i] = (float)(v * window[i]);
            }

            for (var i = count; i < FftSize; i++) re[i] = 0f;
            Array.Clear(im);

            var envelope = Math.Sqrt(frameSquares / count);
            envelopeSum += envelope;
            if (envelope > envelopePeak) envelopePeak = envelope;

            fft.Forward(re, im);

            // Bin 0 is DC. A recording with an offset would otherwise pile that
            // offset into LowEnergy and drag the centroid toward 0 Hz.
            double magnitudeSum = 0, energySum = 0, weightedFreq = 0;
            double low = 0, mid = 0, high = 0;
            for (var bin = 1; bin < nyquistBin; bin++)
            {
                var m = Math.Sqrt((double)re[bin] * re[bin] + (double)im[bin] * im[bin]);
                magnitudes[bin] = m;
                var energy = m * m;
                var hz = bin * binHz;

                magnitudeSum += m;
                energySum += energy;
                weightedFreq += hz * m;

                if (hz < LowBandMaxHz) low += energy;
                else if (hz < MidBandMaxHz) mid += energy;
                else high += energy;
            }

            if (energySum <= 0 || magnitudeSum <= 0) continue;

            var weight = energySum;
            weightSum += weight;

            centroidSum += weight * (weightedFreq / magnitudeSum);
            flatnessSum += weight * Flatness(magnitudes, nyquistBin, magnitudeSum);
            lowSum += weight * (low / energySum);
            midSum += weight * (mid / energySum);
            highSum += weight * (high / energySum);

            var fundamental = DetectFundamental(fft, re, im, power, count, sampleRate);
            harmonicSum += weight * (fundamental > 0
                ? HarmonicRatio(magnitudes, nyquistBin, magnitudeSum, fundamental, binHz, sampleRate)
                : UnpitchedHarmonicRatio);
        }

        // Every frame was empty (a span of pure DC clears bin 0 upward). Nothing
        // spectral can be said, but the level fields still measured something.
        if (weightSum <= 0 || frameCount == 0)
        {
            return new ToneAudioProfile(rms, peakDb, crestDb, 0, 0, UnpitchedHarmonicRatio,
                0, 0, 0, 0);
        }

        var lowShare = lowSum / weightSum;
        var midShare = midSum / weightSum;
        var highShare = highSum / weightSum;

        // The three bands partition the spectrum, so they sum to 1 already; the
        // renormalisation only removes the accumulated rounding, so that the
        // invariant a caller reads off the doc comment holds exactly.
        var shareSum = lowShare + midShare + highShare;
        if (shareSum > 0)
        {
            lowShare /= shareSum;
            midShare /= shareSum;
            highShare /= shareSum;
        }

        var sustain = envelopePeak > 0
            ? Math.Clamp(envelopeSum / frameCount / envelopePeak, 0, 1)
            : 0.0;

        return new ToneAudioProfile(
            rms,
            peakDb,
            crestDb,
            centroidSum / weightSum,
            Math.Clamp(flatnessSum / weightSum, 0, 1),
            Math.Clamp(harmonicSum / weightSum, 0, 1),
            lowShare,
            midShare,
            highShare,
            sustain);
    }

    /// <summary>
    ///     The samples between two times, clamped to what the buffer actually
    ///     holds — the shape the tone-change caller needs, kept here so every
    ///     caller rounds the edges the same way.
    /// </summary>
    /// <remarks>Returns an empty span when the range is inverted or out of bounds;
    /// <see cref="Analyze" /> reports an empty span as silent.</remarks>
    public static ReadOnlySpan<float> Slice(ReadOnlySpan<float> monoSamples, int sampleRate,
        double startSeconds, double endSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        if (!double.IsFinite(startSeconds) || !double.IsFinite(endSeconds)) return [];

        var start = (long)Math.Round(Math.Max(0.0, startSeconds) * sampleRate);
        var end = (long)Math.Round(Math.Max(0.0, endSeconds) * sampleRate);

        start = Math.Clamp(start, 0, monoSamples.Length);
        end = Math.Clamp(end, 0, monoSamples.Length);

        return end <= start ? [] : monoSamples.Slice((int)start, (int)(end - start));
    }

    /// <summary>
    ///     Nothing usable in the span. The spectral fields are neutral rather than
    ///     guessed, and every one of them is finite — a <c>NaN</c> here is the
    ///     failure that reaches a user, because it survives every downstream
    ///     comparison and lands in a tone the app presents as measured.
    /// </summary>
    private static ToneAudioProfile Silent(double rms, double peakDb) =>
        new(rms, peakDb, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>
    ///     Frame offsets and their real (unpadded) sample counts, in order.
    ///     A span shorter than one frame still yields exactly one frame, so a
    ///     handful of samples is measured rather than refused.
    /// </summary>
    private static IEnumerable<(int Offset, int Count)> Frames(int length)
    {
        if (length <= 0) yield break;

        if (length < FrameSize)
        {
            yield return (0, length);
            yield break;
        }

        for (var offset = 0; offset + FrameSize <= length; offset += HopSize)
            yield return (offset, FrameSize);
    }

    /// <summary>Symmetric Hann, matching <c>BeatMapAnalyzer</c>'s convention.
    /// Without it the rectangular window's sidelobes smear every partial across
    /// the spectrum, which lifts flatness on a CLEAN signal and would make the
    /// driven-vs-clean ordering the feature rests on read backwards.</summary>
    private static double[] HannWindow(int size)
    {
        var window = new double[size];
        for (var i = 0; i < size; i++)
            window[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (size - 1));
        return window;
    }

    /// <summary>
    ///     Geometric mean over arithmetic mean of the magnitude spectrum:
    ///     0 for a pure tone, 1 for white noise.
    /// </summary>
    private static double Flatness(double[] magnitudes, int nyquistBin, double magnitudeSum)
    {
        var count = nyquistBin - 1;
        if (count <= 0) return 0;

        var arithmetic = magnitudeSum / count;
        if (arithmetic <= 0) return 0;

        var floor = arithmetic * FlatnessFloor;
        double logSum = 0;
        for (var bin = 1; bin < nyquistBin; bin++)
            logSum += Math.Log(Math.Max(magnitudes[bin], floor));

        return Math.Exp(logSum / count) / arithmetic;
    }

    /// <summary>
    ///     Fundamental of one frame in Hz, or 0 when none is confident.
    ///     <para>
    ///         Autocorrelation, computed through the FFT already in hand: the
    ///         power spectrum of a real signal is real and even, so its forward
    ///         transform IS the autocorrelation (no inverse transform needed, and
    ///         <see cref="JuceFft" /> exposes none). Autocorrelation is chosen over
    ///         a spectral peak-picker because the frames it must not lie on are
    ///         DISTORTED ones: clipping moves most of the energy off the
    ///         fundamental and onto the upper harmonics, and a peak-picker then
    ///         reports the loudest harmonic as the note. The period of the
    ///         waveform does not move under clipping, so the correlation does not
    ///         either.
    ///     </para>
    ///     <para>
    ///         The correlation is UNBIASED — divided by the number of overlapping
    ///         samples — because zero-padding otherwise makes every long lag look
    ///         weaker than every short one, which biases the answer high.
    ///     </para>
    /// </summary>
    private static double DetectFundamental(JuceFft fft, float[] re, float[] im,
        float[] power, int frameLength, int sampleRate)
    {
        // re/im still hold the frame's spectrum, and both are dead by this point
        // (the magnitudes were read off above), so im doubles as the scratch
        // buffer the second transform needs.
        for (var i = 0; i < FftSize; i++)
            power[i] = re[i] * re[i] + im[i] * im[i];

        var scratch = im;
        Array.Clear(scratch);
        fft.Forward(power, scratch);

        var correlation = power;
        var zeroLag = correlation[0] / frameLength;
        if (!(zeroLag > 0)) return 0;

        var minLag = (int)Math.Ceiling(sampleRate / MaxFundamentalHz);
        var maxLag = (int)Math.Floor(sampleRate / MinFundamentalHz);

        // Beyond half the frame the unbiased normalisation divides by so few
        // samples that noise alone clears the confidence threshold.
        maxLag = Math.Min(maxLag, frameLength / 2);
        if (minLag < 1 || maxLag <= minLag) return 0;

        double best = 0;
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            var value = correlation[lag] / (frameLength - lag) / zeroLag;
            if (value > best) best = value;
        }

        if (best < MinFundamentalConfidence) return 0;

        // The SHORTEST lag that is nearly as strong as the best one, not the
        // strongest lag. An autocorrelation peaks at the period AND at every
        // multiple of it, so taking the maximum lands on a SUBharmonic whenever
        // noise nudges one multiple above another — and a fundamental reported an
        // octave low packs the harmonic bands twice as densely, which inflates
        // HarmonicRatio, which under-reports Drive. Measured: a 3 kHz sine locked
        // onto 1 kHz and scored 0.998.
        var threshold = Math.Max(MinFundamentalConfidence, 0.9 * best);
        var bestLag = -1;
        for (var lag = minLag + 1; lag < maxLag; lag++)
        {
            var value = correlation[lag] / (frameLength - lag) / zeroLag;
            if (value < threshold) continue;

            var previous = correlation[lag - 1] / (frameLength - lag + 1) / zeroLag;
            var next = correlation[lag + 1] / (frameLength - lag - 1) / zeroLag;
            if (value < previous || value < next) continue;

            bestLag = lag;
            best = value;
            break;
        }

        if (bestLag < 0) return 0;

        // Parabolic refinement across the peak, so the harmonic bins below are not
        // walked off by the integer-lag quantisation that octave-errors
        // BeatMapAnalyzer.
        var refined = (double)bestLag;
        if (bestLag > minLag && bestLag < maxLag)
        {
            var a = correlation[bestLag - 1] / (frameLength - bestLag + 1) / zeroLag;
            var b = best;
            var c = correlation[bestLag + 1] / (frameLength - bestLag - 1) / zeroLag;
            var denominator = a - 2 * b + c;
            if (denominator != 0)
            {
                var shift = 0.5 * (a - c) / denominator;
                if (Math.Abs(shift) <= 1) refined += shift;
            }
        }

        var hz = sampleRate / refined;
        return hz is > 0 and <= MaxFundamentalHz and >= MinFundamentalHz ? hz : 0;
    }

    /// <summary>
    ///     Share of the frame's magnitude sitting in narrow bands around
    ///     <paramref name="fundamentalHz" /> and its harmonics.
    /// </summary>
    private static double HarmonicRatio(double[] magnitudes, int nyquistBin, double magnitudeSum,
        double fundamentalHz, double binHz, int sampleRate)
    {
        if (magnitudeSum <= 0 || fundamentalHz <= 0) return 0;

        var nyquistHz = sampleRate / 2.0;
        double harmonic = 0;
        var lastBin = 0;

        for (var k = 1; k <= MaxHarmonic; k++)
        {
            var centre = k * fundamentalHz;
            if (centre >= nyquistHz) break;

            // Half a semitone either side, never narrower than the transform can
            // resolve and never wide enough for two harmonics' bands to touch.
            var halfWidthHz = Math.Min(0.45 * fundamentalHz,
                Math.Max(MainLobeHalfWidthBins * binHz, 0.03 * centre));

            var from = Math.Max(1, (int)Math.Ceiling((centre - halfWidthHz) / binHz));
            var to = Math.Min(nyquistBin - 1, (int)Math.Floor((centre + halfWidthHz) / binHz));

            // Bands are built non-overlapping above, but integer rounding can still
            // put two ends on one bin; counting it twice would let the ratio exceed 1.
            if (from <= lastBin) from = lastBin + 1;

            for (var bin = from; bin <= to; bin++) harmonic += magnitudes[bin];
            if (to >= from) lastBin = to;
        }

        return Math.Clamp(harmonic / magnitudeSum, 0, 1);
    }
}
