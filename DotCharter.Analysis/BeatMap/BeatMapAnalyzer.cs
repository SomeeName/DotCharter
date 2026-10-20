using JuceSharp.Dsp;

namespace DotCharter.Analysis.BeatMap;

public sealed class BeatMapAnalyzerOptions
{
    public double MinBpm { get; init; } = 60;
    public double MaxBpm { get; init; } = 200;

    /// <summary>Length of one tempo-estimation window.</summary>
    public double WindowSeconds { get; init; } = 6.0;

    /// <summary>Hop between tempo-estimation windows.</summary>
    public double HopSeconds { get; init; } = 2.0;

    /// <summary>Beats per bar assumed for downbeat phase estimation.</summary>
    public int BeatsPerBar { get; init; } = 4;
}

/// <summary>One tracked beat. <paramref name="Bpm"/> is the local tempo at
/// this beat, <paramref name="Confidence"/> combines the onset evidence under
/// the beat with the tempo clarity of its window (0..1).</summary>
public readonly record struct DetectedBeat(double TimeSeconds, double Bpm, float Confidence, bool IsDownbeat);

public sealed class BeatMapAnalysisResult
{
    public static readonly BeatMapAnalysisResult Empty =
        new(Array.Empty<DetectedBeat>(), 0.0);

    public BeatMapAnalysisResult(IReadOnlyList<DetectedBeat> beats, double averageBpm)
    {
        Beats = beats;
        AverageBpm = averageBpm;
    }

    public IReadOnlyList<DetectedBeat> Beats { get; }
    public double AverageBpm { get; }
}

/// <summary>
///     Beat-map auto-detect v2: tempo-drift tracking over an onset envelope.
///     <para>Pipeline: a streamed spectral-flux envelope (1024-point FFT,
///     256-sample hop) is whitened against its local average, then a comb
///     autocorrelation over sliding windows estimates the local beat period —
///     tracking gradual tempo drift — and a forward pass places beats by
///     stepping the drifting period and snapping to nearby envelope peaks.
///     Each beat carries a confidence; downbeats are the bar phase with the
///     most onset energy.</para>
///     <para>Deterministic: identical samples and options always produce the
///     identical result. Memory is bounded by the envelope (4 bytes per 256
///     input samples) — input chunks are never buffered.</para>
/// </summary>
public sealed class BeatMapAnalyzer
{
    private const int FftSize = 1024;
    private const int HopSize = 256;
    private const double WhitenSeconds = 0.5;
    private const double SnapWindowFraction = 0.15;

    public BeatMapAnalysisResult Analyze(ReadOnlyMemory<float> monoSamples, int sampleRate,
        BeatMapAnalyzerOptions? options = null) => Analyze(SliceChunks(monoSamples), sampleRate, options);

    public BeatMapAnalysisResult Analyze(IEnumerable<ReadOnlyMemory<float>> monoChunks, int sampleRate,
        BeatMapAnalyzerOptions? options = null)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        options ??= new BeatMapAnalyzerOptions();
        if (options.MinBpm <= 0 || options.MaxBpm <= options.MinBpm)
            throw new ArgumentException("0 < MinBpm < MaxBpm required.", nameof(options));

        var fps = (double)sampleRate / HopSize;
        var flux = ComputeFluxEnvelope(monoChunks);
        if (flux.Count < (int)(2.0 * fps)) return BeatMapAnalysisResult.Empty;

        var envelope = Whiten(flux, (int)(WhitenSeconds * fps));
        var windows = EstimateWindowPeriods(envelope, fps, options);
        if (windows.Count == 0) return BeatMapAnalysisResult.Empty;

        var beats = TrackBeats(envelope, windows, fps);
        if (beats.Count == 0) return BeatMapAnalysisResult.Empty;

        MarkDownbeats(beats, envelope, fps, options.BeatsPerBar);

        var averageBpm = 0.0;
        foreach (var b in beats) averageBpm += b.Bpm;
        averageBpm /= beats.Count;

        return new BeatMapAnalysisResult(beats, averageBpm);
    }

    private static IEnumerable<ReadOnlyMemory<float>> SliceChunks(ReadOnlyMemory<float> samples)
    {
        var offset = 0;
        while (offset < samples.Length)
        {
            var n = Math.Min(8192, samples.Length - offset);
            yield return samples.Slice(offset, n);
            offset += n;
        }
    }

    // ------------------------------------------------------------ envelope

    private static List<float> ComputeFluxEnvelope(IEnumerable<ReadOnlyMemory<float>> chunks)
    {
        var fft = new JuceFft(FftSize);
        var hann = new float[FftSize];
        for (var i = 0; i < FftSize; i++)
            hann[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / (FftSize - 1));

        var window = new float[FftSize];
        var re = new float[FftSize];
        var im = new float[FftSize];
        var logMag = new float[FftSize / 2 + 1];
        var prevLogMag = new float[FftSize / 2 + 1];
        var havePrev = false;
        var fill = 0;

        var flux = new List<float>();

        foreach (var chunk in chunks)
        {
            var span = chunk.Span;
            var offset = 0;
            while (offset < span.Length)
            {
                var copy = Math.Min(FftSize - fill, span.Length - offset);
                span.Slice(offset, copy).CopyTo(window.AsSpan(fill));
                fill += copy;
                offset += copy;
                if (fill < FftSize) continue;

                var frameFinite = true;
                for (var i = 0; i < FftSize; i++)
                {
                    var windowed = window[i] * hann[i];
                    if (!float.IsFinite(windowed)) frameFinite = false;
                    re[i] = windowed;
                }

                if (!frameFinite)
                {
                    // A non-finite input sample — a decoder glitch, a denormal
                    // blow-up — must cost its own four overlapping frames and
                    // NOTHING after them. The FFT smears it across every bin, and
                    // the resulting NaN log-magnitude then becomes the PREVIOUS
                    // frame's spectrum, so the poison would otherwise outlive the
                    // sample that caused it. Today it happens to stop anyway,
                    // because `d > 0` is false for NaN — but that is an accident
                    // of a comparison three lines from a `sum +=`, and one
                    // `MathF.Max(0f, d)` "cleanup" (Max(0, NaN) IS NaN) turns a
                    // 23 ms dropout into a whole track that returns no beat map.
                    // Emit a silent frame and keep the last good spectrum.
                    flux.Add(0f);
                    Array.Copy(window, HopSize, window, 0, FftSize - HopSize);
                    fill = FftSize - HopSize;
                    continue;
                }

                Array.Clear(im);
                fft.Forward(re, im);

                float sum = 0;
                for (var k = 1; k <= FftSize / 2; k++)
                {
                    var mag = MathF.Sqrt(re[k] * re[k] + im[k] * im[k]);
                    var lm = MathF.Log(1f + mag);
                    if (havePrev)
                    {
                        var d = lm - prevLogMag[k];
                        if (d > 0) sum += d;
                    }
                    logMag[k] = lm;
                }

                flux.Add(havePrev ? sum : 0f);
                (prevLogMag, logMag) = (logMag, prevLogMag);
                havePrev = true;

                Array.Copy(window, HopSize, window, 0, FftSize - HopSize);
                fill = FftSize - HopSize;
            }
        }

        return flux;
    }

    /// <summary>Subtract a moving local average so sustained energy does not
    /// mask rhythmic accents; negative values clamp to zero.</summary>
    private static float[] Whiten(List<float> flux, int averageLength)
    {
        var n = flux.Count;
        var result = new float[n];
        averageLength = Math.Max(3, averageLength);
        var half = averageLength / 2;

        double running = 0;
        var count = 0;
        for (var i = 0; i < Math.Min(n, half); i++)
        {
            running += flux[i];
            count++;
        }

        for (var i = 0; i < n; i++)
        {
            if (i + half < n)
            {
                running += flux[i + half];
                count++;
            }

            if (i - half - 1 >= 0)
            {
                running -= flux[i - half - 1];
                count--;
            }

            var average = count > 0 ? running / count : 0.0;
            var v = flux[i] - (float)average;
            result[i] = v > 0 ? v : 0f;
        }

        return result;
    }

    // ------------------------------------------------------- tempo windows

    private readonly record struct TempoWindow(int CenterHop, double PeriodHops, float Confidence);

    private static List<TempoWindow> EstimateWindowPeriods(float[] envelope, double fps,
        BeatMapAnalyzerOptions options)
    {
        var windowLength = Math.Max((int)(options.WindowSeconds * fps), (int)(4 * fps / (options.MinBpm / 60.0)));
        windowLength = Math.Min(windowLength, envelope.Length);
        var hop = Math.Max(1, (int)(options.HopSeconds * fps));

        // Rounded INWARD, both of them. A lag is an integer number of hops, so a
        // floor on minLag admits a period FASTER than MaxBpm and a ceil on maxLag
        // one SLOWER than MinBpm — i.e. the analyzer could return exactly the
        // tempo its options excluded. Measured: at 44.1 kHz / hop 256 the envelope
        // runs at 172.27 fps, so MinBpm=75 is 137.8 hops and the ceil made 138
        // reachable — 74.898 BPM out of a [75, 78] request. MinBpm/MaxBpm are a
        // contract and nothing downstream re-checks them, so a beat map outside
        // the range is worse than none.
        var minLag = Math.Max(1, (int)Math.Ceiling(60.0 / options.MaxBpm * fps));
        var maxLag = (int)Math.Floor(60.0 / options.MinBpm * fps);
        maxLag = Math.Min(maxLag, windowLength / 2);
        if (maxLag <= minLag) return [];

        var acLimit = Math.Min(windowLength - 1, maxLag * 3);
        var ac = new double[acLimit + 1];
        var windows = new List<TempoWindow>();

        for (var start = 0; start + windowLength <= envelope.Length || start == 0; start += hop)
        {
            var length = Math.Min(windowLength, envelope.Length - start);
            if (length < windowLength / 2 && start > 0) break;

            for (var lag = minLag; lag <= acLimit; lag++)
            {
                double v = 0;
                for (var i = start; i + lag < start + length; i++)
                    v += envelope[i] * envelope[i + lag];
                ac[lag] = v;
            }

            double best = 0;
            var bestLag = -1;
            double sum = 0;
            var candidates = 0;
            for (var lag = minLag; lag <= maxLag; lag++)
            {
                var score = ac[lag];
                if (lag * 2 <= acLimit) score += 0.5 * ac[lag * 2];
                if (lag * 3 <= acLimit) score += 0.33 * ac[lag * 3];
                sum += score;
                candidates++;
                if (score > best)
                {
                    best = score;
                    bestLag = lag;
                }
            }

            if (bestLag > 0 && best > 0)
            {
                // Parabolic refinement around the winning lag.
                var period = (double)bestLag;
                if (bestLag > minLag && bestLag < maxLag)
                {
                    var y0 = ac[bestLag - 1];
                    var y1 = ac[bestLag];
                    var y2 = ac[bestLag + 1];
                    var denom = y0 - 2 * y1 + y2;
                    if (Math.Abs(denom) > 1e-12)
                    {
                        var delta = 0.5 * (y0 - y2) / denom;
                        if (Math.Abs(delta) < 1) period += delta;
                    }
                }

                // The refinement moves the period by up to a whole hop, which is
                // the other way out of the requested range. Clamping here is what
                // makes the bound hold END TO END: the median-of-three smoothing
                // below picks one of three in-range values and PeriodAt only ever
                // interpolates between two, so every beat's Bpm is then inside
                // [MinBpm, MaxBpm] by construction.
                period = Math.Clamp(period, minLag, maxLag);

                var mean = sum / Math.Max(1, candidates);
                var confidence = (float)Math.Clamp(1.0 - mean / best, 0.0, 1.0);
                windows.Add(new TempoWindow(start + length / 2, period, confidence));
            }

            if (start + windowLength >= envelope.Length) break;
        }

        // Median-of-three smoothing kills isolated octave errors without
        // damping genuine gradual drift.
        if (windows.Count >= 3)
        {
            var smoothed = new List<TempoWindow>(windows.Count);
            for (var i = 0; i < windows.Count; i++)
            {
                if (i == 0 || i == windows.Count - 1)
                {
                    smoothed.Add(windows[i]);
                    continue;
                }

                var a = windows[i - 1].PeriodHops;
                var b = windows[i].PeriodHops;
                var c = windows[i + 1].PeriodHops;
                var median = Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
                smoothed.Add(windows[i] with { PeriodHops = median });
            }

            return smoothed;
        }

        return windows;
    }

    private static double PeriodAt(List<TempoWindow> windows, double hopPosition)
    {
        if (hopPosition <= windows[0].CenterHop) return windows[0].PeriodHops;
        for (var i = 1; i < windows.Count; i++)
        {
            if (hopPosition > windows[i].CenterHop) continue;
            var previous = windows[i - 1];
            var next = windows[i];
            var t = (hopPosition - previous.CenterHop) / Math.Max(1.0, next.CenterHop - previous.CenterHop);
            return previous.PeriodHops + t * (next.PeriodHops - previous.PeriodHops);
        }

        return windows[^1].PeriodHops;
    }

    private static float WindowConfidenceAt(List<TempoWindow> windows, double hopPosition)
    {
        if (hopPosition <= windows[0].CenterHop) return windows[0].Confidence;
        for (var i = 1; i < windows.Count; i++)
        {
            if (hopPosition > windows[i].CenterHop) continue;
            var previous = windows[i - 1];
            var next = windows[i];
            var t = (hopPosition - previous.CenterHop) / Math.Max(1.0, next.CenterHop - previous.CenterHop);
            return (float)(previous.Confidence + t * (next.Confidence - previous.Confidence));
        }

        return windows[^1].Confidence;
    }

    // -------------------------------------------------------- beat tracking

    private static List<DetectedBeat> TrackBeats(float[] envelope, List<TempoWindow> windows, double fps)
    {
        var beats = new List<DetectedBeat>();
        var evidence = new List<bool>();
        var initialPeriod = windows[0].PeriodHops;

        // First beat: strongest onset within the first 1.5 periods.
        var searchEnd = Math.Min(envelope.Length - 1, (int)(initialPeriod * 1.5));
        var position = 0;
        for (var i = 1; i <= searchEnd; i++)
        {
            if (envelope[i] > envelope[position])
                position = i;
        }

        if (envelope[position] <= 0f) return beats;

        double current = position;
        while (current < envelope.Length)
        {
            var period = PeriodAt(windows, current);
            var snapWindow = Math.Max(1, (int)(period * SnapWindowFraction));
            var center = (int)Math.Round(current);
            var lo = Math.Max(0, center - snapWindow);
            var hi = Math.Min(envelope.Length - 1, center + snapWindow);

            var peak = center <= hi && center >= lo ? center : lo;
            for (var i = lo; i <= hi; i++)
            {
                if (envelope[i] > envelope[peak])
                    peak = i;
            }

            var snapped = envelope[peak] > 0f ? peak : current;

            // Local salience of the onset under the beat vs its surroundings.
            var statLo = Math.Max(0, (int)(snapped - period / 2));
            var statHi = Math.Min(envelope.Length - 1, (int)(snapped + period / 2));
            double localSum = 0;
            for (var i = statLo; i <= statHi; i++) localSum += envelope[i];
            var localMean = localSum / Math.Max(1, statHi - statLo + 1);
            var peakValue = envelope[Math.Min(envelope.Length - 1, (int)Math.Round(snapped))];
            var salience = peakValue > 0
                ? (float)(peakValue / (peakValue + localMean + 1e-12))
                : 0f;

            var windowConfidence = WindowConfidenceAt(windows, snapped);
            var confidence = Math.Clamp(salience * (0.5f + 0.5f * windowConfidence), 0f, 1f);

            var localPeriod = PeriodAt(windows, snapped);
            beats.Add(new DetectedBeat(
                snapped / fps,
                60.0 * fps / localPeriod,
                confidence,
                IsDownbeat: false));
            evidence.Add(peakValue > 0f);

            current = snapped + localPeriod;
        }

        // Beats predicted into trailing silence carry no onset evidence —
        // drop them (mid-song silent beats stay: they are followed by
        // evidence-backed beats).
        var lastEvidence = evidence.LastIndexOf(true);
        if (lastEvidence < beats.Count - 1)
            beats.RemoveRange(lastEvidence + 1, beats.Count - 1 - lastEvidence);

        return beats;
    }

    private static void MarkDownbeats(List<DetectedBeat> beats, float[] envelope, double fps, int beatsPerBar)
    {
        if (beatsPerBar <= 1 || beats.Count == 0) return;

        var phaseEnergy = new double[beatsPerBar];
        for (var i = 0; i < beats.Count; i++)
        {
            var hop = (int)Math.Round(beats[i].TimeSeconds * fps);
            if (hop >= 0 && hop < envelope.Length)
                phaseEnergy[i % beatsPerBar] += envelope[hop];
        }

        var bestPhase = 0;
        for (var p = 1; p < beatsPerBar; p++)
        {
            if (phaseEnergy[p] > phaseEnergy[bestPhase])
                bestPhase = p;
        }

        for (var i = bestPhase; i < beats.Count; i += beatsPerBar)
            beats[i] = beats[i] with { IsDownbeat = true };
    }
}
