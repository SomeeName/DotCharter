using JuceSharp.Dsp;

namespace DotCharter.Services.Ai.Match.Similarity;

/// <summary>
///     <c>tonefx-v2</c>: a fixed-length, content-invariant description of a guitar
///     tone — what the rig does to the sound, as independent as we can make it of
///     which notes were played.
///     <para>
///         <b>Content invariance is the whole point.</b> The match lane compares a
///         guitar stem lifted off a record (one riff, one performance, one guitar)
///         against a reference DI rendered through a candidate rig (a completely
///         different riff). Anything in the vector that tracks the NOTES rather than
///         the TONE is noise that the search will happily optimise into a wrong
///         answer. So: every statistic is an average over frames, the MFCCs have the
///         per-frame level removed before the DCT (the cepstrum keeps the envelope
///         and drops the harmonic comb), and the only level-dependent numbers are two
///         elements out of <see cref="ToneFeatureLayout.Length" />.
///     </para>
///     <para>
///         <b>Output-level knobs are unidentifiable here BY CONSTRUCTION and that is
///         not a defect to patch.</b> Everything is level-normalised, so a rig whose
///         final <c>level</c> is 0.3 and one at 0.7 are the same tone to this metric —
///         which is correct, because they are the same tone. Level is settled by
///         <c>DcRigLoudnessNormalizer</c> after a rig is chosen, not by the search.
///         Do not add a level term here to make an optimiser's job look easier: it
///         would make every candidate partly graded on gain, which is the one thing
///         the RMS matching in <see cref="LevelMatch" /> exists to prevent.
///     </para>
///     <para>
///         Ten blocks — see <see cref="ToneFeatureLayout" /> for the map and the
///         weights: multi-scale LOG-spaced band statistics (mean and spread),
///         cepstral statistics (mean and spread), spectral shape (mean and spread),
///         dynamics (the compression proxy), a distortion proxy, a MODULATION block
///         (envelope autocorrelation + modulation spectrum, which is the only thing
///         here that can hear a delay or an LFO), and absolute level.
///     </para>
///     <para>
///         <b>Everything runs on <c>JuceSharp.Dsp</c>'s FFT.</b>
///         <see cref="JuceSpectrogramBuilder" /> does the STFT (Hann, magnitudes
///         already normalised by <c>fftSize/2</c>, non-finite samples scrubbed at the
///         input); the mel binning and the DCT are the only new arithmetic, and they
///         are in <see cref="LogFilterBank" />. No new native code, and nothing here
///         ever runs on the audio thread.
///     </para>
///     <para>
///         Deterministic: the same <see cref="MonoAudio" /> always yields the same
///         vector, on any thread, in any order. The instance is stateless and the
///         per-call scratch is local, so one extractor may be shared (it is a
///         singleton in DI).
///     </para>
/// </summary>
public sealed class ToneFeatureExtractor : IToneFeatureExtractor
{
    /// <summary>
    ///     The id every vector carries. <b>Bump it for ANY change</b> to the layout,
    ///     the scaling, the gating threshold or the resampler kernel — a stored
    ///     library of vectors is only comparable within one id, and a silently
    ///     changed extractor is how a search lane starts optimising against a metric
    ///     nobody measured.
    /// </summary>
    public const string ExtractorId = "tonefx-v2";

    /// <summary>Everything is analysed here; the input is resampled if it arrives at
    /// another rate. 48 kHz because that is what the engine renders at and what the
    /// stem cache stores, so the common path resamples nothing.</summary>
    public const int WorkingRate = 48_000;

    /// <summary>Frames quieter than this many dB below the loudest frame are dropped
    /// before any statistic is taken. Silence between phrases is the single largest
    /// source of "the same rig scored differently on a different riff": a gap is not
    /// a tone, and averaging its noise floor in makes a sparse riff read as darker
    /// and more compressed than a busy one.</summary>
    public const double FrameGateDb = 45.0;

    /// <summary>The RMS every clip is normalised to before the spectral, dynamics and
    /// distortion blocks. The LEVEL block is measured before this and is the only
    /// place absolute loudness survives.</summary>
    public const double AnalysisRmsDbfs = -20.0;

    /// <inheritdoc />
    public string Id => ExtractorId;

    /// <inheritdoc />
    public ToneFeatures Extract(MonoAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);

        var vector = new float[ToneFeatureLayout.Length];
        var work = Prepare(audio, out var rmsDbfs, out var peakDbfs);

        // The level block first: it is the only one measured before normalisation.
        vector[ToneFeatureLayout.LevelOffset] = (float)(Math.Max(rmsDbfs, -80.0) / 40.0);
        vector[ToneFeatureLayout.LevelOffset + 1] = (float)(Math.Max(peakDbfs, -80.0) / 40.0);

        if (work.Length == 0) return new ToneFeatures(vector, ExtractorId);

        WriteMelBlock(work, vector);
        var mid = new JuceSpectrogramBuilder(MidScale, MidScale / 4).Build(work, WorkingRate);
        var kept = GateFrames(mid);
        WriteMfccBlock(mid, kept, vector);
        WriteShapeBlock(mid, kept, vector);
        WriteDistortionBlock(mid, kept, vector);
        WriteDynamicsBlock(work, vector);
        WriteModulationBlock(work, vector);

        for (var i = 0; i < vector.Length; i++)
        {
            if (!float.IsFinite(vector[i]))
                vector[i] = 0f;
        }

        return new ToneFeatures(vector, ExtractorId);
    }

    /// <summary>The scale the MFCC, shape and distortion blocks are taken at: long
    /// enough to resolve a low E's partials (11.7 Hz per bin), short enough that a
    /// note change does not smear across the whole frame.</summary>
    private const int MidScale = 4096;

    /// <summary>
    ///     Resample to <see cref="WorkingRate" />, remove DC, measure the absolute
    ///     level, then normalise to <see cref="AnalysisRmsDbfs" />. Returns a buffer
    ///     at least as long as the longest FFT (zero-padded if the clip is shorter),
    ///     or empty for an all-silent input.
    /// </summary>
    private static float[] Prepare(MonoAudio audio, out double rmsDbfs, out double peakDbfs)
    {
        var resampled = MonoResampler.To(audio, WorkingRate).Samples;

        double sum = 0;
        var n = 0;
        foreach (var s in resampled)
            if (float.IsFinite(s)) { sum += s; n++; }
        var dc = n > 0 ? sum / n : 0;

        var work = new float[Math.Max(resampled.Length, ToneFeatureLayout.StftScales[^1])];
        double sq = 0, peak = 0;
        for (var i = 0; i < resampled.Length; i++)
        {
            var s = float.IsFinite(resampled[i]) ? resampled[i] - dc : 0;
            work[i] = (float)s;
            sq += s * s;
            peak = Math.Max(peak, Math.Abs(s));
        }

        var rms = resampled.Length > 0 ? Math.Sqrt(sq / resampled.Length) : 0;
        rmsDbfs = 20.0 * Math.Log10(Math.Max(rms, 1e-9));
        peakDbfs = 20.0 * Math.Log10(Math.Max(peak, 1e-9));

        if (rms < 1e-9) return [];

        var gain = (float)(Math.Pow(10.0, AnalysisRmsDbfs / 20.0) / rms);
        for (var i = 0; i < work.Length; i++) work[i] *= gain;
        return work;
    }

    /// <summary>
    ///     Frame indices worth averaging: everything within
    ///     <see cref="FrameGateDb" /> of the loudest frame. Never empty for a
    ///     non-silent clip — the loudest frame always passes its own gate.
    /// </summary>
    private static int[] GateFrames(JuceSpectrogram spectrogram)
    {
        var frames = spectrogram.Frames;
        if (frames.Length == 0) return [];

        var energy = new double[frames.Length];
        var max = 0.0;
        for (var f = 0; f < frames.Length; f++)
        {
            double e = 0;
            foreach (var m in frames[f]) e += m * (double)m;
            energy[f] = e;
            max = Math.Max(max, e);
        }

        if (max <= 0) return [];
        var floor = max * Math.Pow(10.0, -FrameGateDb / 10.0);

        var kept = new List<int>(frames.Length);
        for (var f = 0; f < frames.Length; f++)
        {
            if (energy[f] >= floor)
                kept.Add(f);
        }

        return [.. kept];
    }

    private static void WriteMelBlock(float[] work, float[] vector)
    {
        var at = ToneFeatureLayout.BandMeanOffset;
        Span<float> db = stackalloc float[ToneFeatureLayout.LogBands];
        foreach (var scale in ToneFeatureLayout.StftScales)
        {
            var spectrogram = new JuceSpectrogramBuilder(scale, scale / 4).Build(work, WorkingRate);
            var kept = GateFrames(spectrogram);
            var bank = LogFilterBank.Get(ToneFeatureLayout.LogBands, scale, WorkingRate,
                (int)AnalysisLowHz, (int)AnalysisHighHz);

            var power = new double[ToneFeatureLayout.LogBands];
            var mean = new double[ToneFeatureLayout.LogBands];
            var sq = new double[ToneFeatureLayout.LogBands];

            foreach (var f in kept)
            {
                // Linear power for the MEAN (see LogFilterBank.AccumulatePower), and
                // per-frame dB for the SPREAD — the spread is a statement about how
                // much the band moves over the take, which only has meaning in dB.
                bank.AccumulatePower(spectrogram.Frames[f], power);
                bank.LogBands(spectrogram.Frames[f], db);
                for (var b = 0; b < db.Length; b++) { mean[b] += db[b]; sq[b] += db[b] * (double)db[b]; }
            }

            var count = Math.Max(kept.Length, 1);
            for (var b = 0; b < ToneFeatureLayout.LogBands; b++)
            {
                var m = mean[b] / count;
                var variance = Math.Max(0, sq[b] / count - m * m);
                var averageDb = 10.0 * Math.Log10(Math.Max(power[b] / count, 1e-12));

                // dB/40 for the mean, dB/20 for the spread: a 20 dB band-to-band
                // swing and a 40 dB level difference are comparable perceptual steps.
                vector[at + b] = (float)(averageDb / 40.0);
                vector[at + ToneFeatureLayout.BandMeanLength + b] = (float)(Math.Sqrt(variance) / 20.0);
            }

            at += ToneFeatureLayout.LogBands;
        }
    }

    /// <summary>
    ///     MFCC mean, std and mean absolute delta over the gated frames.
    ///     <para>
    ///         <b>The per-frame log-mel MEAN is removed before the DCT</b>, which is
    ///         why <c>c0</c> is dropped and coefficients 1..20 are kept: c0 would be
    ///         identically zero, and without the removal it would be a second, loud
    ///         copy of the level block hiding inside the block that is supposed to be
    ///         level-invariant.
    ///     </para>
    ///     <para>
    ///         The delta term is what hears MODULATION and dynamics — a tremolo, a
    ///         phaser, a compressor breathing — which no average over frames can see.
    ///         It is a mean of absolute differences, not of signed ones: the signed
    ///         mean of a periodic sweep is zero.
    ///     </para>
    /// </summary>
    private static void WriteMfccBlock(JuceSpectrogram spectrogram, int[] kept, float[] vector)
    {
        var bank = LogFilterBank.Get(ToneFeatureLayout.CepstralBands, spectrogram.FftSize, WorkingRate,
            (int)AnalysisLowHz, (int)AnalysisHighHz);
        var coefficients = ToneFeatureLayout.MfccCoefficients;

        var power = new double[ToneFeatureLayout.CepstralBands];
        var sq = new double[coefficients];
        var sum = new double[coefficients];
        var delta = new double[coefficients];
        var previous = new float[coefficients + 1];
        var current = new float[coefficients + 1];
        var logMel = new float[ToneFeatureLayout.CepstralBands];
        var havePrevious = false;

        foreach (var f in kept)
        {
            bank.AccumulatePower(spectrogram.Frames[f], power);
            bank.LogBands(spectrogram.Frames[f], logMel);
            Normalise(logMel);
            LogFilterBank.Dct(logMel, current);

            for (var k = 0; k < coefficients; k++)
            {
                var c = current[k + 1];
                sum[k] += c;
                sq[k] += c * (double)c;
                if (havePrevious) delta[k] += Math.Abs(c - previous[k + 1]);
            }

            (previous, current) = (current, previous);
            havePrevious = true;
        }

        var count = Math.Max(kept.Length, 1);
        var deltaCount = Math.Max(kept.Length - 1, 1);

        // The MEAN cepstrum comes from the average POWER spectrum, not from the mean
        // of the per-frame cepstra: those are only the same thing for a signal whose
        // spectrum does not change, and the whole problem is a signal whose spectrum
        // changes with every note.
        var averaged = new float[ToneFeatureLayout.CepstralBands];
        for (var b = 0; b < averaged.Length; b++)
            averaged[b] = (float)(10.0 * Math.Log10(Math.Max(power[b] / count, 1e-12)));
        Normalise(averaged);
        var meanCepstrum = new float[coefficients + 1];
        LogFilterBank.Dct(averaged, meanCepstrum);

        for (var k = 0; k < coefficients; k++)
        {
            var m = sum[k] / count;
            var variance = Math.Max(0, sq[k] / count - m * m);
            vector[ToneFeatureLayout.MfccMeanOffset + k] = (float)(meanCepstrum[k + 1] / 20.0);
            vector[ToneFeatureLayout.MfccVariationOffset + k] = (float)(Math.Sqrt(variance) / 20.0);
            vector[ToneFeatureLayout.MfccVariationOffset + coefficients + k] =
                (float)(delta[k] / deltaCount / 10.0);
        }
    }

    /// <summary>
    ///     Removes a log-mel frame's overall level, so what is left is its SHAPE.
    ///     This is why <c>c0</c> is dropped and coefficients 1..N are kept: c0 would
    ///     be identically zero, and without the removal it would be a second, loud
    ///     copy of the level block hiding inside the block that is supposed to be
    ///     level-invariant.
    /// </summary>
    private static void Normalise(Span<float> logMel)
    {
        double mean = 0;
        foreach (var v in logMel) mean += v;
        mean /= logMel.Length;
        for (var b = 0; b < logMel.Length; b++) logMel[b] = (float)(logMel[b] - mean);
    }

    private static void WriteShapeBlock(JuceSpectrogram spectrogram, int[] kept, float[] vector)
    {
        var frames = spectrogram.Frames;
        var stats = new RunningStat[5];
        float[]? previous = null;
        var (lowBin, highBin) = AnalysisBins(spectrogram);
        var bandBins = highBin - lowBin + 1;

        foreach (var f in kept)
        {
            var mag = frames[f];
            double total = 0, weighted = 0, peak = 0;
            for (var i = lowBin; i <= highBin; i++)
            {
                var p = mag[i] * (double)mag[i];
                total += p;
                weighted += p * spectrogram.BinHz(i);
                peak = Math.Max(peak, p);
            }

            if (total <= 0) { previous = mag; continue; }

            var centroid = weighted / total;

            double spreadAcc = 0, geo = 0;
            // The geometric mean is floored RELATIVE to the frame's own peak, not at
            // an absolute 1e-20: an absolute floor makes flatness a measurement of how
            // quiet the quietest bin happens to be, which is a property of the file's
            // headroom rather than of the tone.
            var floor = peak * 1e-8;
            for (var i = lowBin; i <= highBin; i++)
            {
                var p = mag[i] * (double)mag[i];
                var d = spectrogram.BinHz(i) - centroid;
                spreadAcc += p * d * d;
                geo += Math.Log(Math.Max(p, floor));
            }

            var spread = Math.Sqrt(spreadAcc / total);

            double running = 0, rolloff = centroid;
            for (var i = lowBin; i <= highBin; i++)
            {
                running += mag[i] * (double)mag[i];
                if (running >= 0.85 * total) { rolloff = spectrogram.BinHz(i); break; }
            }

            var flatness = Math.Exp(geo / bandBins) / (total / bandBins);

            double flux = 0;
            if (previous is not null)
            {
                // Normalised by BOTH frames' energy, which bounds it to [0, 1]. With
                // the current frame alone in the denominator the ratio is unbounded:
                // a loud frame followed by a quiet one produced a flux of several
                // hundred, and the resulting std made two encodings of the SAME audio
                // 200× further apart than a clean amp is from a fuzz. A feature with
                // no bound is a feature that will eventually be the whole distance.
                double num = 0, den = 0;
                for (var i = lowBin; i <= highBin; i++)
                {
                    num += Math.Abs(mag[i] - previous[i]);
                    den += mag[i] + previous[i];
                }

                flux = den > 0 ? num / den : 0;
            }

            // log2 octaves above 1 kHz, quartered: one element ≈ one 4-octave move.
            stats[0].Add(Math.Log2(Math.Max(centroid, 20) / 1000.0) / 4.0);
            stats[1].Add(Math.Log2(Math.Max(spread, 20) / 1000.0) / 4.0);
            stats[2].Add(Math.Log2(Math.Max(rolloff, 20) / 1000.0) / 4.0);
            stats[3].Add(Math.Log10(Math.Clamp(flatness, 1e-8, 1.0)) / 4.0);
            stats[4].Add(flux);
            previous = mag;
        }

        for (var i = 0; i < stats.Length; i++)
        {
            vector[ToneFeatureLayout.ShapeOffset + i] = (float)stats[i].Mean;
            vector[ToneFeatureLayout.ShapeVariationOffset + i] = (float)stats[i].StdDev;
        }
    }

    /// <summary>The lowest frequency any statistic looks at. Below a low B there is
    /// nothing but rumble and the separator's low-end artefacts.</summary>
    public const double AnalysisLowHz = 60.0;

    /// <summary>The highest. A guitar cab is 30 dB down by 10 kHz; above it a
    /// measurement is reading the file's format, its codec and its resampler.</summary>
    public const double AnalysisHighHz = 10_000.0;

    private static (int Low, int High) AnalysisBins(JuceSpectrogram spectrogram)
    {
        var bins = spectrogram.BinCount;
        var low = Math.Clamp((int)(AnalysisLowHz * spectrogram.FftSize / WorkingRate), 1, bins - 2);
        var high = Math.Clamp((int)(AnalysisHighHz * spectrogram.FftSize / WorkingRate), low + 1, bins - 1);
        return (low, high);
    }

    /// <summary>
    ///     Two proxies for "how dirty is it", measured on the gated frames: the
    ///     2-6 kHz energy against the 60-800 Hz fundamental energy, and how NOISE-LIKE
    ///     that upper band is.
    ///     <para>
    ///         <b>Both bands are FIXED, and anchoring them to the frame's own
    ///         fundamental centroid was tried and measured WORSE.</b> The idea was that
    ///         a fixed upper band reads pitch as drive — which it does — but a centroid
    ///         that moves with the riff moves the measurement window with it, and the
    ///         window's own contents then vary more than the fixed one's: the block's
    ///         cost between two performances of ONE rig rose from 0.020 to 0.031 while
    ///         its cost between a clean amp and a fuzz FELL from 0.011 to 0.006. Fixed
    ///         bands, lower weight, and let the dynamics block carry the distortion
    ///         signal — it separates clean from fuzz by nineteen to one.
    ///     </para>
    ///     <para>
    ///         <b>This was a peak-picked harmonic-to-noise ratio and that had to go.</b>
    ///         Counting energy on local maxima is the textbook measure and it is
    ///         chaotic in practice: which bins are local maxima flips on magnitude
    ///         changes far below audibility, so resampling one clip 48 -> 44.1 -> 48 kHz
    ///         moved this block by 0.258 of a squared total in which a clean amp and a
    ///         fuzz were only 0.41 apart. A feature that unstable is not measuring the
    ///         tone, and a search would chase it.
    ///     </para>
    ///     <para>
    ///         The two replacements are integrals over wide bands, which is why they
    ///         are stable - and they are complementary: the ratio alone calls a bright
    ///         clean tone distorted, the flatness alone calls a noisy clean recording
    ///         distorted, and a real drive moves both.
    ///     </para>
    /// </summary>
    private static void WriteDistortionBlock(JuceSpectrogram spectrogram, int[] kept, float[] vector)
    {
        var frames = spectrogram.Frames;
        var ratio = default(RunningStat);
        var flatness = default(RunningStat);

        var (analysisLow, analysisHigh) = AnalysisBins(spectrogram);
        var fundamentalHigh = Bin(spectrogram, 800.0, analysisLow + 1, analysisHigh - 1);
        var driveLow = Bin(spectrogram, 2000.0, fundamentalHigh + 1, analysisHigh - 1);
        var driveHigh = Bin(spectrogram, 6000.0, driveLow + 1, analysisHigh);

        foreach (var f in kept)
        {
            var mag = frames[f];

            double fundamental = 0;
            for (var i = analysisLow; i <= fundamentalHigh; i++)
                fundamental += mag[i] * (double)mag[i];

            if (fundamental <= 0) continue;

            double drive = 0, drivePeak = 0;
            for (var i = driveLow; i <= driveHigh; i++)
            {
                var p = mag[i] * (double)mag[i];
                drive += p;
                drivePeak = Math.Max(drivePeak, p);
            }

            if (drive <= 0) continue;
            ratio.Add(10.0 * Math.Log10(drive / fundamental) / 20.0);

            // …and how NOISE-LIKE that region is. Distortion products from a chord are
            // dense intermodulation, not a comb: the two features together separate
            // "bright but clean" from "dirty", which neither does alone.
            double geo = 0;
            var floor = drivePeak * 1e-8;
            var n = 0;
            for (var i = driveLow; i <= driveHigh; i++)
            {
                geo += Math.Log(Math.Max(mag[i] * (double)mag[i], floor));
                n++;
            }

            flatness.Add(Math.Log10(Math.Clamp(Math.Exp(geo / n) / (drive / n), 1e-8, 1.0)) / 4.0);
        }

        var at = ToneFeatureLayout.DistortionOffset;
        vector[at] = (float)ratio.Mean;
        vector[at + 1] = (float)ratio.StdDev;
        vector[at + 2] = (float)flatness.Mean;
        vector[at + 3] = (float)flatness.StdDev;
    }

    private static int Bin(JuceSpectrogram spectrogram, double hz, int min, int max) =>
        Math.Clamp((int)(hz * spectrogram.FftSize / WorkingRate), min, Math.Max(min, max));

    /// <summary>
    ///     The compression proxy: the shape of the frame-RMS distribution, which is
    ///     what a compressor and a distortion both flatten and what mastering
    ///     flattens again on a record. Crest factor alone is not enough — a limiter
    ///     and a fuzz reach the same crest by different routes, and the percentile
    ///     spread tells them apart.
    /// </summary>
    /// <summary>
    ///     What the tone does over TIME: the amplitude envelope's autocorrelation (a
    ///     delay's time and how strongly it repeats) and its modulation spectrum (a
    ///     tremolo's or a chorus's rate and depth).
    ///     <para>
    ///         <b>Every other block in this vector averages over frames, and a delay
    ///         is invisible to an average.</b> A signal plus a delayed copy of itself
    ///         has, to a first approximation, the SAME long-term spectrum as the
    ///         signal — so a metric built only from spectral averages rates a rig with
    ///         a 380 ms delay and one with none as near-identical, and a search over
    ///         it will report a confident match to the wrong tone. This block is the
    ///         answer, and it is cheap: one envelope at 200 Hz and one FFT of it.
    ///     </para>
    ///     <para>
    ///         The envelope is in dB and mean-removed, so the block says nothing about
    ///         level and everything about how the level MOVES.
    ///     </para>
    /// </summary>
    private static void WriteModulationBlock(float[] work, float[] vector)
    {
        const int envelopeRate = 200;
        var hop = WorkingRate / envelopeRate;
        var window = hop * 2;

        var frames = Math.Max(0, (work.Length - window) / hop + 1);
        var at = ToneFeatureLayout.ModulationOffset;
        if (frames < 64) return;

        var envelope = new double[frames];
        for (var f = 0; f < frames; f++)
        {
            double sq = 0;
            var start = f * hop;
            for (var i = start; i < start + window; i++) sq += work[i] * (double)work[i];
            envelope[f] = 20.0 * Math.Log10(Math.Sqrt(sq / window) + 1e-9);
        }

        double mean = 0;
        foreach (var v in envelope) mean += v;
        mean /= frames;
        double variance = 0;
        for (var f = 0; f < frames; f++) { envelope[f] -= mean; variance += envelope[f] * envelope[f]; }
        var deviation = Math.Sqrt(variance / frames);
        if (deviation < 1e-6) return;

        // ── Autocorrelation, 20 ms .. 1200 ms. A delay repeat is a peak at its time;
        //    the peak's HEIGHT is how much of the signal comes back, which is what a
        //    feedback knob sets.
        var minLag = Math.Max(1, 20 * envelopeRate / 1000);
        var maxLag = Math.Min(frames / 2, 1200 * envelopeRate / 1000);
        double bestValue = 0, secondValue = 0;
        var bestLag = minLag;
        var correlations = new double[Math.Max(maxLag + 1, minLag + 1)];
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (var f = 0; f + lag < frames; f++) sum += envelope[f] * envelope[f + lag];
            var value = sum / ((frames - lag) * variance / frames);
            correlations[lag] = value;
            if (value > bestValue) { secondValue = bestValue; bestValue = value; bestLag = lag; }
            else if (value > secondValue)
            {
                secondValue = value;
            }
        }

        var doubleLag = Math.Min(bestLag * 2, maxLag);
        vector[at] = (float)Math.Clamp(bestValue, -1, 1);
        vector[at + 1] = (float)(Math.Log2(bestLag * 1000.0 / envelopeRate / 100.0) / 4.0);
        vector[at + 2] = (float)Math.Clamp(secondValue, -1, 1);
        // The repeat-of-the-repeat. A single slapback has none; a long feedback tail
        // has almost as much there as at the first repeat, and that ratio is the one
        // number a feedback knob actually moves.
        vector[at + 3] = (float)Math.Clamp(doubleLag > bestLag ? correlations[doubleLag] : 0, -1, 1);

        // ── Modulation spectrum: where the envelope's own energy sits in Hz.
        var size = 1;
        while (size < frames) size <<= 1;
        var re = new float[size];
        var im = new float[size];
        var taper = JuceSharp.Dsp.Windows.Hann(frames);
        for (var f = 0; f < frames; f++) re[f] = (float)(envelope[f] * taper[f]);
        new JuceFft(size).Forward(re, im);

        Span<double> bands = stackalloc double[5];
        double[] edges = [0.3, 1.0, 3.0, 6.0, 10.0, 20.0];
        double total = 0, weighted = 0, peak = 0;
        for (var bin = 1; bin < size / 2; bin++)
        {
            var hz = bin * (double)envelopeRate / size;
            if (hz < edges[0] || hz > edges[^1]) continue;
            var power = re[bin] * (double)re[bin] + im[bin] * (double)im[bin];
            total += power;
            weighted += power * Math.Log2(hz);
            peak = Math.Max(peak, power);
            for (var b = 0; b < bands.Length; b++)
                if (hz >= edges[b] && hz < edges[b + 1]) { bands[b] += power; break; }
        }

        if (total <= 0) return;
        for (var b = 0; b < bands.Length; b++) vector[at + 4 + b] = (float)(bands[b] / total);
        vector[at + 9] = (float)Math.Min(deviation / 20.0, 2.0);
        vector[at + 10] = (float)(weighted / total / 4.0);
        // How PEAKY the modulation spectrum is: an LFO is one line, a performance is
        // broadband. Without it a busy strummed riff reads as a tremolo.
        vector[at + 11] = (float)Math.Clamp(peak / total, 0, 1);
    }

    private static void WriteDynamicsBlock(float[] work, float[] vector)
    {
        const int window = 1024;
        const int hop = 256;

        var levels = new List<double>(Math.Max(1, work.Length / hop));
        double peak = 0, totalSq = 0;
        for (var i = 0; i < work.Length; i++) { peak = Math.Max(peak, Math.Abs(work[i])); totalSq += work[i] * (double)work[i]; }
        var overallRms = work.Length > 0 ? Math.Sqrt(totalSq / work.Length) : 0;

        for (var start = 0; start + window <= work.Length; start += hop)
        {
            double sq = 0;
            for (var i = start; i < start + window; i++) sq += work[i] * (double)work[i];
            var rms = Math.Sqrt(sq / window);
            if (rms > 1e-7) levels.Add(20.0 * Math.Log10(rms));
        }

        var at = ToneFeatureLayout.DynamicsOffset;
        vector[at] = (float)(20.0 * Math.Log10(Math.Max(peak, 1e-9) / Math.Max(overallRms, 1e-9)) / 20.0);

        if (levels.Count == 0) return;
        levels.Sort();

        var p10 = Percentile(levels, 0.10);
        var p50 = Percentile(levels, 0.50);
        var p95 = Percentile(levels, 0.95);
        var range = p95 - p10;

        vector[at + 1] = (float)(range / 40.0);
        vector[at + 2] = (float)(range > 1e-6 ? (p50 - p10) / range : 0.5);

        double mean = 0;
        foreach (var v in levels) mean += v;
        mean /= levels.Count;
        double variance = 0;
        foreach (var v in levels) variance += (v - mean) * (v - mean);
        vector[at + 3] = (float)(Math.Sqrt(variance / levels.Count) / 20.0);

        var loud = 0;
        foreach (var v in levels) if (v >= p95 - 6.0) loud++;
        vector[at + 4] = (float)(loud / (double)levels.Count);
    }

    private static double Percentile(List<double> sorted, double q)
    {
        var index = Math.Clamp((int)Math.Round(q * (sorted.Count - 1)), 0, sorted.Count - 1);
        return sorted[index];
    }

    private struct RunningStat
    {
        private double _sum;
        private double _sumSq;
        private int _count;

        public void Add(double value)
        {
            _sum += value;
            _sumSq += value * value;
            _count++;
        }

        public readonly double Mean => _count > 0 ? _sum / _count : 0;

        public readonly double StdDev
        {
            get
            {
                if (_count == 0) return 0;
                var m = Mean;
                return Math.Sqrt(Math.Max(0, _sumSq / _count - m * m));
            }
        }
    }
}
