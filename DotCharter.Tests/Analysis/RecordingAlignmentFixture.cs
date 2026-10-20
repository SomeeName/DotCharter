using DotCharter.Analysis.BeatMap;

namespace DotCharter.Tests.Analysis;

/// <summary>
///     A synthetic chart and a "recording" of it at a drifting tempo with KNOWN beat times (#784):
///     16 bars of 4/4, eighth notes on a pitched riff, written on a straight 120 BPM grid from 0 ms,
///     played starting 800 ms late at ~118 BPM with a ±6 % sinusoidal sway. Deterministic.
/// </summary>
internal sealed class RecordingAlignmentFixture
{
    public const int Beats = 64;
    public const int SectionLength = 16;
    private static readonly int[] Riff = [45, 48, 50, 52, 55, 52, 50, 48, 45, 57, 55, 52, 50, 48, 47, 45];

    public RecordingAlignmentFixture(int? mismatchedSection = null)
    {
        ChartTimes = new double[Beats];
        TrueTimes = new double[Beats];
        var t = 800.0;
        for (var i = 0; i < Beats; i++)
        {
            ChartTimes[i] = i * 500.0;
            TrueTimes[i] = t;
            t += 60_000.0 / 118.0 * (1 + 0.06 * Math.Sin(2 * Math.PI * i / 40.0));
        }

        var events = new List<AlignmentEvent>();
        for (var i = 0; i < Beats; i++)
        {
            for (var half = 0; half < 2; half++)
            {
                // Every fourth bar rests on its last off-beat, so the pattern is not perfectly periodic.
                if (half == 1 && i % 16 == 15) continue;
                var midi = Riff[(2 * i + half) % Riff.Length];
                events.Add(new AlignmentEvent(i + 0.5 * half, 0.5, [midi]));
            }
        }

        Events = events;
        SectionStarts = Enumerable.Range(0, Beats / SectionLength).Select(s => s * SectionLength).ToArray();

        var end = TrueTimes[^1] + 2000;
        Samples = new float[(int)(end / 1000.0 * AnalysisTestSignals.SampleRate)];
        foreach (var e in events)
        {
            var section = (int)e.BeatPosition / SectionLength;
            if (section == mismatchedSection) continue;
            AddPluck(Samples, RecordingAligner.TimeAt(TrueTimes, e.BeatPosition) / 1000.0, Hz(e.Midi[0]));
        }

        if (mismatchedSection is { } wrong)
        {
            // What the recording plays there instead: triplets on other pitches.
            for (var i = wrong * SectionLength; i < (wrong + 1) * SectionLength; i++)
            {
                for (var third = 0; third < 3; third++)
                    AddPluck(Samples, RecordingAligner.TimeAt(TrueTimes, i + third / 3.0) / 1000.0, Hz(61 + (i + third) % 5));
            }
        }
    }

    public double[] ChartTimes { get; }

    public double[] TrueTimes { get; }

    public IReadOnlyList<AlignmentEvent> Events { get; }

    public int[] SectionStarts { get; }

    public float[] Samples { get; }

    public AlignmentBeat[] Grid(Func<int, bool>? isFixed = null, Func<int, double>? timeOf = null) =>
        Enumerable.Range(0, Beats).Select(i => new AlignmentBeat(timeOf?.Invoke(i) ?? ChartTimes[i], isFixed?.Invoke(i) ?? false)).ToArray();

    public RecordingAlignmentResult Align(AlignmentBeat[] grid, CancellationToken ct) =>
        new RecordingAligner().Align(Samples, AnalysisTestSignals.SampleRate, grid, Events, SectionStarts, cancellationToken: ct);

    /// <summary><see cref="AnalysisTestSignals.AddPluck" />'s note with a 20 ms release instead of a cut:
    ///     a note that stops dead is a broadband click, louder in a spectral-flux envelope than its own attack.</summary>
    private static void AddPluck(float[] buffer, double startSeconds, double frequencyHz)
    {
        const int rate = AnalysisTestSignals.SampleRate;
        const double duration = 0.6;
        const double release = 0.02;
        var start = (int)(startSeconds * rate);
        var length = Math.Min((int)(duration * rate), buffer.Length - start);
        for (var n = 0; n < length; n++)
        {
            var t = (double)n / rate;
            var envelope = Math.Exp(-t / 0.25);
            if (t < 0.002) envelope *= t / 0.002;
            if (t > duration - release) envelope *= (duration - t) / release;
            var sample = Math.Sin(2 * Math.PI * frequencyHz * t) + 0.45 * Math.Sin(4 * Math.PI * frequencyHz * t) +
                         0.28 * Math.Sin(6 * Math.PI * frequencyHz * t) + 0.15 * Math.Sin(8 * Math.PI * frequencyHz * t);
            buffer[start + n] += (float)(0.3 * envelope * sample);
        }
    }

    private static double Hz(int midi) => 440.0 * Math.Pow(2, (midi - 69) / 12.0);
}
