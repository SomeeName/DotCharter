using JuceSharp.Dsp;

namespace DotCharter.Analysis.Tuning;

/// <summary>
///     Estimates a recording's A440 reference offset, the way a tuner app's "A440 check" does.
///     Pairs of Hann-windowed FFT frames a quarter frame apart give every spectral peak its
///     instantaneous frequency from the phase advance (exact for a steady partial, unlike bin
///     interpolation, so a low note is as precise as a high one). Each peak folds to cents around
///     its nearest equal-tempered semitone and votes, weighted by its magnitude, into a circular
///     one-cent histogram; the smoothed histogram's peak, refined by the circular mean of the votes
///     near it, is the offset. Confidence is how much of the vote sits near that centre beyond what
///     an even spread would put there, so silence and noise read as no confidence. Pure and
///     deterministic; the caller decides which audio to hand in.
/// </summary>
public static class ReferencePitchEstimator
{
    /// <summary>Below this confidence a measurement is not acted on.</summary>
    public const double DefaultMinConfidence = 0.3;

    private const double MinHz = 100.0;
    private const double MaxHz = 2500.0;
    private const int MaxFrames = 1500;
    private const int MaxPeaksPerFrame = 12;
    private const int MinPeaks = 40;
    private const double PeakFloorRatio = 0.05;
    private const double SilenceRms = 1e-4;
    private const double SteadyRatio = 0.8;
    private const int SmoothCents = 4;
    private const double CentreWindowCents = 15.0;

    /// <summary>Measures <paramref name="mono" />; <see cref="ReferencePitchEstimate.None" /> when nothing steady is heard.</summary>
    public static ReferencePitchEstimate Estimate(ReadOnlySpan<float> mono, int sampleRate, CancellationToken ct = default)
    {
        if (sampleRate <= 0) return ReferencePitchEstimate.None;
        var size = FrameSize(sampleRate);
        var lag = size / 4;
        if (mono.Length < size + lag) return ReferencePitchEstimate.None;

        var votes = new List<(double Cents, double Weight)>();
        var frames = CollectVotes(mono, sampleRate, size, lag, votes, ct);
        if (votes.Count < MinPeaks) return ReferencePitchEstimate.None;

        var (centre, confidence) = Centre(votes);
        return new ReferencePitchEstimate(centre, confidence, votes.Count, (double)frames * size / sampleRate);
    }

    /// <summary>Folds an offset in cents into −50..+50 around the nearest semitone (exactly +50 maps to -50).</summary>
    public static double FoldToSemitone(double cents) =>
        cents - 100.0 * Math.Round(cents / 100.0, MidpointRounding.AwayFromZero);

    /// <summary>About 0.19 s: a power of two, so the FFT runs radix-2.</summary>
    private static int FrameSize(int sampleRate)
    {
        var size = 1024;
        while (size < sampleRate * 0.17) size *= 2;
        return size;
    }

    private static int CollectVotes(ReadOnlySpan<float> mono, int sampleRate, int size, int lag,
        List<(double Cents, double Weight)> votes, CancellationToken ct)
    {
        var fft = new JuceFft(size);
        var window = new float[size];
        for (var i = 0; i < size; i++) window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / size));
        var frameA = new float[size];
        var frameB = new float[size];
        var reA = new float[size];
        var imA = new float[size];
        var reB = new float[size];
        var imB = new float[size];
        var magnitude = new double[size / 2];

        var available = (mono.Length - size - lag) / size + 1;
        var count = Math.Min(available, MaxFrames);
        var stride = (double)(mono.Length - size - lag) / Math.Max(1, count - 1);
        var lowBin = Math.Max(2, (int)Math.Ceiling(MinHz * size / sampleRate));
        var highBin = Math.Min(size / 2 - 3, (int)(MaxHz * size / sampleRate));
        var used = 0;
        for (var f = 0; f < count; f++)
        {
            ct.ThrowIfCancellationRequested();
            var start = count == 1 ? 0 : (int)(f * stride);
            if (!Windowed(mono.Slice(start, size), window, frameA)) continue;
            Windowed(mono.Slice(start + lag, size), window, frameB);
            fft.ForwardReal(frameA, reA, imA);
            fft.ForwardReal(frameB, reB, imB);
            for (var k = 0; k < magnitude.Length; k++) magnitude[k] = Math.Sqrt(reA[k] * reA[k] + imA[k] * imA[k]);
            AddFramePeaks(magnitude, reA, imA, reB, imB, lowBin, highBin, size, lag, sampleRate, votes);
            used++;
        }

        return used;
    }

    /// <summary>Copies the windowed frame; false when it is silent.</summary>
    private static bool Windowed(ReadOnlySpan<float> source, float[] window, float[] target)
    {
        var energy = 0.0;
        for (var i = 0; i < target.Length; i++)
        {
            energy += source[i] * source[i];
            target[i] = source[i] * window[i];
        }

        return Math.Sqrt(energy / target.Length) >= SilenceRms;
    }

    private static void AddFramePeaks(double[] magnitude, float[] reA, float[] imA, float[] reB, float[] imB,
        int lowBin, int highBin, int size, int lag, int sampleRate, List<(double Cents, double Weight)> votes)
    {
        var frameMax = 0.0;
        for (var k = lowBin; k <= highBin; k++) frameMax = Math.Max(frameMax, magnitude[k]);
        if (frameMax <= 0) return;

        var peaks = new List<(int Bin, double Magnitude)>();
        for (var k = lowBin; k <= highBin; k++)
        {
            var m = magnitude[k];
            if (m < frameMax * PeakFloorRatio) continue;
            if (m > magnitude[k - 1] && m >= magnitude[k + 1] && m > magnitude[k - 2] && m >= magnitude[k + 2]) peaks.Add((k, m));
        }

        peaks.Sort((a, b) => b.Magnitude.CompareTo(a.Magnitude));
        for (var p = 0; p < Math.Min(MaxPeaksPerFrame, peaks.Count); p++)
        {
            var k = peaks[p].Bin;
            // A partial that is not steady across the two frames (a pick attack, a drum) does not vote.
            var later = Math.Sqrt(reB[k] * reB[k] + imB[k] * imB[k]);
            if (later < peaks[p].Magnitude * SteadyRatio || later > peaks[p].Magnitude / SteadyRatio) continue;
            var advance = Math.Atan2(imB[k], reB[k]) - Math.Atan2(imA[k], reA[k]) - 2 * Math.PI * k * lag / size;
            var deviation = PrincipalArgument(advance) * size / (2 * Math.PI * lag);
            if (Math.Abs(deviation) > 0.75) continue;
            var hz = (k + deviation) * sampleRate / size;
            var cents = FoldToSemitone(1200.0 * Math.Log2(hz / 440.0));
            votes.Add((cents, peaks[p].Magnitude));
        }
    }

    private static double PrincipalArgument(double phase) => phase - 2 * Math.PI * Math.Round(phase / (2 * Math.PI));

    /// <summary>The smoothed circular histogram's peak, refined by the circular mean of the votes near it.</summary>
    private static (double Centre, double Confidence) Centre(List<(double Cents, double Weight)> votes)
    {
        var histogram = new double[100];
        var total = 0.0;
        foreach (var (cents, weight) in votes)
        {
            histogram[((int)Math.Floor(cents + 50.0) % 100 + 100) % 100] += weight;
            total += weight;
        }

        var best = 0;
        var bestScore = double.MinValue;
        for (var c = 0; c < 100; c++)
        {
            var score = 0.0;
            for (var d = -SmoothCents; d <= SmoothCents; d++)
                score += histogram[((c + d) % 100 + 100) % 100] * (SmoothCents + 1 - Math.Abs(d));
            if (score > bestScore)
            {
                bestScore = score;
                best = c;
            }
        }

        var coarse = best - 50.0 + 0.5;
        double sin = 0, cos = 0, near = 0;
        foreach (var (cents, weight) in votes)
        {
            if (Math.Abs(FoldToSemitone(cents - coarse)) > CentreWindowCents) continue;
            var angle = 2 * Math.PI * cents / 100.0;
            sin += weight * Math.Sin(angle);
            cos += weight * Math.Cos(angle);
            near += weight;
        }

        var centre = near > 0 ? Math.Atan2(sin, cos) * 100.0 / (2 * Math.PI) : coarse;
        var even = (2 * CentreWindowCents + 1) / 100.0;
        var confidence = Math.Clamp((near / total - even) / (1 - even), 0, 1);
        return (FoldToSemitone(centre), confidence);
    }
}
