using DotCharter.Services.Audio.StemSeparation;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     #463: a 6-minute separation showed a bar that barely moved and no log until the end.
///     The reporter turns the separator's per-chunk fraction into "chunk i of N" for the
///     toast and one timed log line per chunk, on an injected clock.
/// </summary>
public sealed class StemChunkProgressReporterTests
{
    private sealed class Clock
    {
        public TimeSpan Now { get; set; }
    }

    private sealed class ChunkSink : IStemChunkProgress
    {
        public List<(int Chunk, int Total)> Chunks { get; } = [];

        public void ReportChunk(int chunk, int total) => Chunks.Add((chunk, total));
    }

    private static (StemChunkProgressReporter Reporter, Clock Clock, ChunkSink Sink, List<string> Log, List<double> Overall) Build()
    {
        var clock = new Clock();
        var sink = new ChunkSink();
        var log = new List<string>();
        var overall = new List<double>();
        var reporter = new StemChunkProgressReporter(() => clock.Now, overall.Add, sink, log.Add);
        return (reporter, clock, sink, log, overall);
    }

    [Fact]
    public void EveryChunk_IsReportedAsIOfN_WithItsTime_AndTheFractionPassesThrough()
    {
        var (reporter, clock, sink, log, overall) = Build();

        for (var k = 0; k < 34; k++)
        {
            clock.Now += TimeSpan.FromMilliseconds(500);
            reporter.Report((k + 1.0) / 34);
        }

        Assert.Equal(34, reporter.Total);
        Assert.Equal(Enumerable.Range(1, 34).Select(i => (i, 34)), sink.Chunks);
        Assert.Equal(34, overall.Count);
        Assert.Equal(1.0, overall[^1], 9);
        Assert.Equal("Stem separation: chunk 1/34 took 500 ms, elapsed 0.5 s.", log[0]);
        Assert.Equal(34, log.Count(l => l.Contains(" took ", StringComparison.Ordinal)));
    }

    [Fact]
    public void ASummary_EveryTenChunks_AndAtTheLast_NamesTheSlowestChunk()
    {
        var (reporter, clock, _, log, _) = Build();

        for (var k = 0; k < 23; k++)
        {
            clock.Now += TimeSpan.FromMilliseconds(k == 14 ? 9800 : 500);
            reporter.Report((k + 1.0) / 23);
        }

        var summaries = log.Where(l => l.Contains("chunks ", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, summaries.Count);
        Assert.StartsWith("Stem separation: chunks 1-10 of 23: 500 ms each on average, slowest 500 ms (chunk 1); elapsed 5.0 s, ~",
            summaries[0], StringComparison.Ordinal);
        Assert.Contains("chunks 11-20 of 23: 1430 ms each on average, slowest 9800 ms (chunk 15)", summaries[1], StringComparison.Ordinal);
        Assert.EndsWith("chunks 21-23 of 23: 500 ms each on average, slowest 500 ms (chunk 21); elapsed 20.8 s.",
            summaries[2], StringComparison.Ordinal);
    }

    /// <summary>A repeated or late fraction must not log a chunk twice or move the count backwards.</summary>
    [Fact]
    public void ARepeatedOrEarlierFraction_IsForwardedButNotCountedAgain()
    {
        var (reporter, clock, sink, log, overall) = Build();

        clock.Now += TimeSpan.FromSeconds(1);
        reporter.Report(1.0 / 4);
        reporter.Report(1.0 / 4);
        clock.Now += TimeSpan.FromSeconds(1);
        reporter.Report(2.0 / 4);
        reporter.Report(1.0 / 4);

        Assert.Equal([(1, 4), (2, 4)], sink.Chunks);
        Assert.Equal(2, reporter.Completed);
        Assert.Equal(4, overall.Count);
        Assert.Equal(2, log.Count(l => l.Contains(" took ", StringComparison.Ordinal)));
    }

    /// <summary>Two chunks in one report (a coarser backend) split the gap between them.</summary>
    [Fact]
    public void ASkippedChunk_SplitsTheGap()
    {
        var (reporter, clock, sink, log, _) = Build();

        clock.Now += TimeSpan.FromSeconds(4);
        reporter.Report(0.5);

        Assert.Equal(2, reporter.Total);
        Assert.Equal([(1, 2)], sink.Chunks);
        clock.Now += TimeSpan.FromSeconds(4);
        reporter.Report(1.0);
        Assert.Equal([(1, 2), (2, 2)], sink.Chunks);
        Assert.All(log.Where(l => l.Contains(" took ", StringComparison.Ordinal)),
            l => Assert.Contains("took 4000 ms", l, StringComparison.Ordinal));
    }

    [Fact]
    public void ZeroAndNaN_AreForwarded_WithoutStartingTheCount()
    {
        var (reporter, _, sink, log, overall) = Build();
        reporter.Report(0);
        reporter.Report(double.NaN);
        Assert.Equal(0, reporter.Total);
        Assert.Empty(sink.Chunks);
        Assert.Empty(log);
        Assert.Equal([0.0, 0.0], overall);
    }

    [Fact]
    public void TheToast_ShowsNoChunkLabel_UntilAChunkIsReported_ThenCarriesIt()
    {
        var reports = new List<(double Value, string? Label)>();
        var toast = new StemSeparationToastProgress((v, l) => reports.Add((v, l)), (i, n) => $"chunk {i} of {n}");
        var reporter = new StemChunkProgressReporter(() => TimeSpan.Zero, p => toast.Report(0.1 + 0.8 * p),
            toast, _ => { });

        toast.Report(0.05);
        reporter.Report(1.0 / 3);
        reporter.Report(2.0 / 3);

        Assert.Equal((0.05, null), reports[0]);
        Assert.Equal("chunk 1 of 3", reports[1].Label);
        Assert.Equal("chunk 2 of 3", reports[2].Label);
        Assert.Equal(0.1 + 0.8 * 2 / 3, reports[2].Value, 9);
    }
}
