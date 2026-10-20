using System.Globalization;
using DotCharter.Analysis.BeatMap;

namespace DotCharter.Tests.Analysis;

/// <summary>
///     Align to recording (#784), the analysis half, on <see cref="RecordingAlignmentFixture" />:
///     a straight 120 BPM chart against a recording that starts 800 ms late at ~118 BPM with a
///     ±6 % sway. The ground truth is known exactly, so every assertion is an ERROR BOUND on a
///     deterministic signal — never a timing.
/// </summary>
public sealed class RecordingAlignerTests
{
    /// <summary>Per-beat error bounds. The onset envelope runs on ~2.7 ms frames, so a beat is placed
    ///     to about one frame. Measured 2026-10-13 (#792 defaults): mean 0.75 ms, p90 1.38, max 1.62 from a
    ///     start 1.27 s off on average; the coarse pass alone is ~±40 ms, so these bounds fail without pass 2.</summary>
    private const double MeanErrorBoundMs = 3;

    private const double MaxErrorBoundMs = 6;

    private static readonly RecordingAlignmentFixture Clean = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void ADriftingRecording_AlignsBackWithinAFewMillisecondsPerBeat()
    {
        var before = Errors(Clean.ChartTimes, Clean.TrueTimes);
        var result = Clean.Align(Clean.Grid(), Ct);
        var after = Errors(result.BeatTimesMs, Clean.TrueTimes);
        Log("before", before);
        Log("after", after);

        Assert.True(before.Max() > 500, "the fixture must start far off, or the test proves nothing");
        Assert.True(after.Average() < MeanErrorBoundMs, $"mean beat error {after.Average():0.0} ms");
        Assert.True(after.Max() < MaxErrorBoundMs, $"max beat error {after.Max():0.0} ms");
        Assert.All(result.Sections, s => Assert.True(s.Confidence > 0.8, $"section at beat {s.StartBeat}: {s.Confidence:0.00}"));
    }

    [Fact]
    public void LockedBeats_KeepTheirTimeExactly_AndTheRestAlignsAroundThem()
    {
        // A grid already near the recording (a straight line through it, up to ~100 ms off), with
        // beat 20 locked where it really is and beat 40 locked 30 ms off: both must stay put.
        var slope = (Clean.TrueTimes[^1] - Clean.TrueTimes[0]) / (RecordingAlignmentFixture.Beats - 1);
        var locked = new Dictionary<int, double> { [20] = Clean.TrueTimes[20], [40] = Clean.TrueTimes[40] + 30 };
        var result = Clean.Align(Clean.Grid(locked.ContainsKey,
            i => locked.TryGetValue(i, out var t) ? t : Clean.TrueTimes[0] + i * slope), Ct);

        foreach (var (beat, time) in locked) Assert.Equal(time, result.BeatTimesMs[beat]);
        var errors = Errors(result.BeatTimesMs, Clean.TrueTimes);
        Log("locked", errors);
        Assert.True(errors.Where((_, i) => i != 40).Max() < MaxErrorBoundMs,
            "every other beat still aligns, the neighbours of the deliberately wrong lock included");
    }

    [Fact]
    public void APartialRange_MovesOnlyItsRange()
    {
        // Bars 5–8 only. The grid outside is first put where it really is (an earlier full run),
        // so the range has correct neighbours — the realistic re-run after an edit.
        const int first = 16;
        const int last = 32;
        var start = Clean.Grid(timeOf: i => i is >= first and < last ? Clean.ChartTimes[i] + 900 : Clean.TrueTimes[i]);
        var result = Clean.Align(Clean.Grid(i => i is < first or >= last, i => start[i].TimeMs), Ct);

        for (var i = 0; i < RecordingAlignmentFixture.Beats; i++)
        {
            if (i is < first or >= last) Assert.Equal(start[i].TimeMs, result.BeatTimesMs[i]);
        }

        var inside = Errors(result.BeatTimesMs, Clean.TrueTimes).Skip(first).Take(last - first).ToArray();
        Log("range", inside);
        Assert.True(inside.Max() < MaxErrorBoundMs, $"max error inside the range {inside.Max():0.0} ms");
    }

    [Fact]
    public void AMismatchedSection_ScoresAClearlyLowerConfidence()
    {
        const int wrong = 2;
        var result = new RecordingAlignmentFixture(mismatchedSection: wrong).Align(Clean.Grid(), Ct);
        var scores = result.Sections.Select(s => s.Confidence).ToArray();
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join(" ", scores.Select(s => s.ToString("0.00", CultureInfo.InvariantCulture))));

        Assert.Equal(4, scores.Length);
        Assert.True(scores[wrong] < RecordingAligner.LowConfidence, $"the mismatched section scored {scores[wrong]:0.00}");
        Assert.All(scores.Where((_, i) => i != wrong), s => Assert.True(s >= RecordingAligner.LowConfidence, $"a matching section scored {s:0.00}"));
    }

    [Fact]
    public void ACancelledRun_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Clean.Align(Clean.Grid(), cts.Token));
    }

    [Fact]
    public void AllFixedOrNoAudio_ReturnsTheGridUnchanged()
    {
        var fixedGrid = Clean.Align(Clean.Grid(_ => true), Ct);
        Assert.Equal(Clean.ChartTimes, fixedGrid.BeatTimesMs);

        var silent = new RecordingAligner().Align(new float[100], AnalysisTestSignals.SampleRate, Clean.Grid(), Clean.Events, [0], cancellationToken: Ct);
        Assert.Equal(Clean.ChartTimes, silent.BeatTimesMs);
    }

    [Fact]
    public void BeatsOutOfOrder_AreRejected() =>
        Assert.Throws<ArgumentException>(() => new RecordingAligner().Align(Clean.Samples, AnalysisTestSignals.SampleRate,
            [new AlignmentBeat(500, false), new AlignmentBeat(400, false)], Clean.Events, [0], cancellationToken: Ct));

    internal static double[] Errors(IReadOnlyList<double> aligned, double[] truth) =>
        aligned.Select((t, i) => Math.Abs(t - truth[i])).ToArray();

    private static void Log(string label, double[] errors)
    {
        var sorted = errors.Order().ToArray();
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{label}: mean {errors.Average():0.00} ms, median {sorted[sorted.Length / 2]:0.00}, p90 {sorted[(int)(0.9 * (sorted.Length - 1))]:0.00}, max {sorted[^1]:0.00} (beat {Array.IndexOf(errors, sorted[^1])})"));
    }
}
