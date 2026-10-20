using System.Globalization;
using DotCharter.Analysis.BeatMap;

namespace DotCharter.Tests.Analysis;

/// <summary>
///     Align to recording (#792): the cases the corpus probe found the #784 aligner losing, rebuilt as
///     deterministic signals with known beat times. Error bounds only, never a timing.
///     <list type="bullet">
///         <item>A synthetic FULL MIX — the charted guitar (eighth notes on a four-chord loop, one chord a bar,
///         a pushed note every fourth beat) under a hi-hat on every sixteenth, kick and snare, a bass on other
///         pitches and a sustained "voice" — played on or a few percent off the chart's written tempo. Onsets
///         are everywhere and the chroma repeats every four bars: the loose coarse warp wandered off the
///         diagonal there, and a stiff one on the written tempo cuts corners when the tab's tempo is off.</item>
///         <item>The drifting fixture with every sustain zero, as a Guitar Pro import charts plain notes: the
///         chart's pitch classes then vanish 150 ms after each note unless the sounds ring on.</item>
///         <item>A recording that ends well before the chart does: the warp used to squeeze the chart's rest
///         into it.</item>
///     </list>
/// </summary>
public sealed class RecordingAlignerDenseMixTests
{
    /// <summary>The options #784 shipped with: a loose coarse warp on the written tempo, a weak tempo prior, the
    ///     chart's pitch classes only as long as each sound's charted sustain.</summary>
    internal static readonly RecordingAlignmentOptions Shipped784 = new()
    {
        CoarseSkewPenalty = 0.4, TempoSmoothness = 30, CoarsePrescale = false, LetRing = false
    };

    private const int Beats = 96;
    private const double ChartBeatMs = 600;
    private const double LateMs = 1450;

    /// <summary>Measured 2026-10-13 with the defaults: max 3.3–11.6 ms over the five mixes (11.6 on the
    ///     slowest, loudest-voice one); #784 put 4–44 beats more than 50 ms off on the off-tempo ones.</summary>
    private const double MixMaxErrorMs = 20;

    private const double MixMeanErrorMs = 3;
    private const double SwayMeanErrorMs = 3;
    private const double SwayMaxErrorMs = 6;
    private static readonly int[][] Chords = [[40, 47, 52], [36, 43, 48], [43, 50, 55], [38, 45, 50]];
    private static readonly int[] Voice = [64, 67, 69, 71, 72, 71, 69, 67];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0.12f, 0.08f, 0.2f, 1.0)]
    [InlineData(0.06f, 0.3f, 0.4f, 1.03)]
    [InlineData(0.03f, 0.6f, 0.6f, 0.97)]
    [InlineData(0.06f, 0.3f, 0.4f, 1.08)]
    [InlineData(0.06f, 0.3f, 0.4f, 0.9)]
    public void AFullMix_OnOrOffTheWrittenTempo_KeepsEveryBeatOnItsBeat(float guitar, float voice, float bass, double ratio)
    {
        var (samples, events, truth) = Build(guitar, voice, bass, ratio);
        var errors = Errors(samples, events, truth, new RecordingAlignmentOptions());
        Log($"mix x{ratio}", errors);
        Assert.True(errors.Average() < MixMeanErrorMs, $"mean beat error {errors.Average():0.0} ms");
        Assert.True(errors.Max() < MixMaxErrorMs, $"max beat error {errors.Max():0.0} ms at beat {Array.IndexOf(errors, errors.Max())}");
    }

    /// <summary>The mixes above are only evidence if they defeat the aligner #792 replaced.</summary>
    [Theory]
    [InlineData(0.06f, 0.3f, 0.4f, 1.03)]
    [InlineData(0.03f, 0.6f, 0.6f, 0.97)]
    [InlineData(0.06f, 0.3f, 0.4f, 1.08)]
    [InlineData(0.06f, 0.3f, 0.4f, 0.9)]
    public void TheOffTempoMixes_DefeatTheShipped784Options(float guitar, float voice, float bass, double ratio)
    {
        var (samples, events, truth) = Build(guitar, voice, bass, ratio);
        var errors = Errors(samples, events, truth, Shipped784);
        Log($"#784 mix x{ratio}", errors);
        Assert.Contains(errors, e => e > 50);
    }

    [Fact]
    public void AChartWithoutSustains_StillFollowsTheSway()
    {
        var fx = new RecordingAlignmentFixture();
        var events = fx.Events.Select(e => e with { SustainBeats = 0 }).ToList();
        var result = new RecordingAligner().Align(fx.Samples, AnalysisTestSignals.SampleRate, fx.Grid(), events, fx.SectionStarts, cancellationToken: Ct);
        var errors = RecordingAlignerTests.Errors(result.BeatTimesMs, fx.TrueTimes);
        Log("no sustains", errors);
        Assert.True(errors.Average() < SwayMeanErrorMs, $"mean beat error {errors.Average():0.0} ms");
        Assert.True(errors.Max() < SwayMaxErrorMs, $"max beat error {errors.Max():0.0} ms at beat {Array.IndexOf(errors, errors.Max())}");
    }

    [Fact]
    public void ARecordingThatEndsBeforeTheChart_StillAlignsTheBeatsItHolds()
    {
        // The chart's last 32 beats (~20 s) run past the end of the recording, further than the warp's
        // 10 s band reaches: the warp must not squeeze them into the recording.
        const int lastHeard = 63;
        var (samples, events, truth) = Build(0.12f, 0.08f, 0.2f, 1.0);
        var cut = (int)((truth[lastHeard] + 300) / 1000.0 * AnalysisTestSignals.SampleRate);
        var errors = Errors(samples[..cut], events, truth, new RecordingAlignmentOptions());
        var heard = errors.Take(lastHeard - 1).ToArray();
        Log("cut", heard);
        Assert.True(heard.Max() < MixMaxErrorMs, $"max beat error {heard.Max():0.0} ms at beat {Array.IndexOf(heard, heard.Max())}");
    }

    [Fact]
    public void LetRing_RingsEachSoundUntilTheNext_UpToTheCap()
    {
        var events = new List<AlignmentEvent>
        {
            new(0, 0, [40]), new(0, 0, [47]), new(0.5, 0.1, [42]), new(0.75, 1.0, [44]), new(3, 0.25, [45])
        };

        var rung = RecordingAligner.LetRing(events, 1);

        // Two sounds at one position both ring to the next position; a charted sustain longer than the gap
        // is kept; a gap longer than the cap rings for the cap; the last sound keeps its own sustain.
        Assert.Equal([0.5, 0.5, 0.25, 1.0, 0.25], rung.Select(e => e.SustainBeats));
        Assert.Equal(events.Select(e => e.BeatPosition), rung.Select(e => e.BeatPosition));
    }

    private static double[] Errors(float[] samples, List<AlignmentEvent> events, double[] truth, RecordingAlignmentOptions options)
    {
        var grid = Enumerable.Range(0, Beats).Select(i => new AlignmentBeat(i * ChartBeatMs, false)).ToArray();
        var result = new RecordingAligner().Align(samples, AnalysisTestSignals.SampleRate, grid, events, [0, 32, 64], options, Ct);
        return RecordingAlignerTests.Errors(result.BeatTimesMs, truth);
    }

    private static (float[] Samples, List<AlignmentEvent> Events, double[] Truth) Build(float guitar, float voice, float bass, double ratio)
    {
        var beatMs = ChartBeatMs * ratio;
        var truth = Enumerable.Range(0, Beats).Select(i => LateMs + i * beatMs).ToArray();
        var samples = new float[(int)((truth[^1] + 3000) / 1000.0 * AnalysisTestSignals.SampleRate)];
        var events = new List<AlignmentEvent>();
        for (var i = 0; i < Beats; i++)
        {
            var chord = Chords[i / 4 % Chords.Length];
            double[] fractions = i % 4 == 3 ? [0.0, 0.75] : [0.0, 0.5];
            foreach (var fraction in fractions)
            {
                events.Add(new AlignmentEvent(i + fraction, 0.5, chord));
                foreach (var midi in chord)
                    AnalysisTestSignals.AddPluck(samples, (truth[i] + fraction * beatMs) / 1000.0, Hz(midi), 0.3, guitar);
            }

            // The rest of the band, which the chart does not chart.
            for (var sixteenth = 0; sixteenth < 4; sixteenth++)
                AddNoise(samples, (truth[i] + sixteenth * beatMs / 4) / 1000.0, 0.025, sixteenth == 0 ? 0.25f : 0.18f, 2 * i + sixteenth);
            if (i % 2 == 0) AnalysisTestSignals.AddPluck(samples, truth[i] / 1000.0, 55, 0.25, 0.5f);
            else AddNoise(samples, truth[i] / 1000.0, 0.12, 0.4f, 7 * i);
            AnalysisTestSignals.AddPluck(samples, (truth[i] + 0.75 * beatMs) / 1000.0, Hz(chord[0] - 12 + 2 * (i % 3)), 0.4, bass);
            AddTone(samples, truth[i] / 1000.0, beatMs / 1000.0, Hz(Voice[i % Voice.Length]), voice);
        }

        return (samples, events, truth);
    }

    /// <summary>A decaying burst of deterministic noise (an LCG seeded per hit).</summary>
    private static void AddNoise(float[] buffer, double startSeconds, double decaySeconds, float amplitude, int seed)
    {
        const int rate = AnalysisTestSignals.SampleRate;
        var start = (int)(startSeconds * rate);
        var length = Math.Min((int)(5 * decaySeconds * rate), buffer.Length - start);
        var state = (uint)(seed * 2654435761u + 12345u);
        for (var n = 0; n < length; n++)
        {
            state = state * 1664525u + 1013904223u;
            var white = (state >> 8) / (float)(1 << 24) * 2 - 1;
            buffer[start + n] += amplitude * (float)Math.Exp(-(double)n / rate / decaySeconds) * white;
        }
    }

    /// <summary>A sustained tone with a soft attack and a slow vibrato: a voice, with no onset to speak of.</summary>
    private static void AddTone(float[] buffer, double startSeconds, double seconds, double hz, float amplitude)
    {
        const int rate = AnalysisTestSignals.SampleRate;
        var start = (int)(startSeconds * rate);
        var length = Math.Min((int)(seconds * rate), buffer.Length - start);
        var phase = 0.0;
        for (var n = 0; n < length; n++)
        {
            var t = (double)n / rate;
            phase += 2 * Math.PI * hz * (1 + 0.01 * Math.Sin(2 * Math.PI * 5 * t)) / rate;
            var envelope = Math.Min(1, t / 0.08) * Math.Min(1, (seconds - t) / 0.05);
            buffer[start + n] += (float)(amplitude * envelope * Math.Sin(phase));
        }
    }

    private static double Hz(int midi) => 440.0 * Math.Pow(2, (midi - 69) / 12.0);

    private static void Log(string label, double[] errors)
    {
        var sorted = errors.Order().ToArray();
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{label}: mean {errors.Average():0.00} ms, median {sorted[sorted.Length / 2]:0.00}, max {sorted[^1]:0.00}, over 50 ms {errors.Count(e => e > 50)}"));
    }
}
