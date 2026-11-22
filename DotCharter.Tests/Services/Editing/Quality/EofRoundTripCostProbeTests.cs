using System.Diagnostics;
using System.Globalization;
using System.Text;
using DotCharter.Services.Editing.Quality;
using DotCharter.Services.Parsing.Eof;
using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Tests.Services.Editing.Quality;

/// <summary>
///     The MEASUREMENT behind #685, not a gate: what the quality report's in-memory Editor on
///     Fire round trip costs on every project of the TestData corpus, beside the rest of the
///     report and the snapshot the off-thread run takes. Skips unless
///     <c>DOTCHARTER_EOF_ROUNDTRIP_PROBE=1</c> and the corpus is present; prints a Markdown table.
/// </summary>
public sealed class EofRoundTripCostProbeTests(ITestOutputHelper output)
{
    private const string ProbeVar = "DOTCHARTER_EOF_ROUNDTRIP_PROBE";

    internal static string? FindCorpus()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "TestData", "RsCharter");
            if (Directory.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static double Ms(Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        return watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>One sample after a full collection, so one sample's garbage is not the next one's pause.</summary>
    internal static double Collected(Action action)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return Ms(action);
    }

    internal static double Median(Func<double> sample, int runs = 5)
    {
        var all = Enumerable.Range(0, runs).Select(_ => sample()).Order().ToList();
        return all[runs / 2];
    }

    private sealed record Row(string Song, int Arrangements, int Levels, int Notes, double Cold, double Eof, double Others, double Snapshot);

    internal static int Notes(ChartProject project) => project.Arrangements.Sum(a => a.Levels.Sum(l => l.Notes.Count + l.Chords.Count));

    private static Row Measure(ChartQualityReporter reporter, string song, ChartProject project)
    {
        var cold = Ms(() => EofLossSummary.From(EofExporter.Export(project).Report));
        var eof = Median(() => Ms(() => EofLossSummary.From(EofExporter.Export(project).Report)));
        var others = Median(() => Ms(() => reporter.Build(project)));
        var clone = Median(() => Ms(() => ChartProjectCloner.Clone(project)));
        return new Row(song, project.Arrangements.Count, project.Arrangements.Sum(a => a.Levels.Count), Notes(project), cold, eof,
            others, clone);
    }

    [Fact]
    public void EveryCorpusProject_LogsTheRoundTripCost()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(ProbeVar) != "1", $"{ProbeVar} is not 1");
        var root = FindCorpus();
        Assert.SkipWhen(root == null, "No RsCharter corpus (TestData/ is git-ignored).");

        var serializer = new RscpChartSerializer();
        var reporter = new ChartQualityReporter(new ArrangementValidationService(new FakeAppLogger()));
        var rows = new List<Row>();
        var loaded = new List<(string Song, ChartProject Project)>();
        foreach (var file in Directory.EnumerateFiles(root!, "project.rscp", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}backups{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) continue;
            var project = serializer.Load(file);
            var song = Path.GetFileName(Path.GetDirectoryName(file)) ?? file;
            loaded.Add((song, project));
            rows.Add(Measure(reporter, song, project));
        }

        // The corpus has no difficulty ladder; a Rocksmith import does. The five biggest
        // projects again, every instrument given a generated 20-level ladder.
        foreach (var (song, project) in loaded.OrderByDescending(p => Notes(p.Project)).Take(5))
        {
            var laddered = ChartProjectCloner.Clone(project);
            foreach (var arrangement in laddered.Arrangements.Where(a => a.ArrangementType != ArrangementType.Vocal))
                new LevelGenerationService().TryGenerate(arrangement, 20, laddered.Beats);
            rows.Add(Measure(reporter, song + " (20-level ladder)", laddered));
        }

        Assert.NotEmpty(rows);
        var table = new StringBuilder();
        table.AppendLine("| Project | Arr | Levels | Notes+chords | EOF first (ms) | EOF warm median (ms) | Rest of report (ms) | Snapshot (ms) |");
        table.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var r in rows.OrderByDescending(r => r.Eof))
        {
            table.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {r.Song} | {r.Arrangements} | {r.Levels} | {r.Notes} | {r.Cold:F1} | {r.Eof:F1} | {r.Others:F1} | {r.Snapshot:F1} |"));
        }

        output.WriteLine(table.ToString());
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "dotcharter_eof_roundtrip_cost.md"), table.ToString());
    }

    internal static List<(string Song, ChartProject Project)> LoadCorpus(string root) =>
        Directory.EnumerateFiles(root, "project.rscp", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}backups{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .Select(f => (Song: Path.GetFileName(Path.GetDirectoryName(f)) ?? f, Project: new RscpChartSerializer().Load(f)))
            .ToList();

    internal static ChartProject Laddered(ChartProject project)
    {
        var laddered = ChartProjectCloner.Clone(project);
        foreach (var arrangement in laddered.Arrangements.Where(a => a.ArrangementType != ArrangementType.Vocal))
            new LevelGenerationService().TryGenerate(arrangement, 20, laddered.Beats);
        return laddered;
    }

    /// <summary>
    ///     #690 / #711: the whole report's Recheck on the five biggest projects with a 20-level
    ///     ladder, as the UI thread pays it. Opening and Check again only show the busy row; the
    ///     step after it has painted assembles and copies the chart (a clone the first time, the
    ///     spare a landed check gave back after that); the landing rebinds and projects. The check
    ///     itself runs off the UI thread.
    /// </summary>
    /// <summary>
    ///     #711: the report over a spare restored from ANOTHER chart is the report over a fresh clone,
    ///     on every corpus project and on the five biggest with a 20-level ladder, and the restored
    ///     copy deep-equals the clone.
    /// </summary>
    [Fact]
    public void EveryCorpusChart_ReportsTheSameOverARestoredSpare()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(ProbeVar) != "1", $"{ProbeVar} is not 1");
        var root = FindCorpus();
        Assert.SkipWhen(root == null, "No RsCharter corpus (TestData/ is git-ignored).");

        var reporter = new ChartQualityReporter(new ArrangementValidationService(new FakeAppLogger()));
        var corpus = LoadCorpus(root!);
        var charts = corpus.Concat(corpus.OrderByDescending(p => Notes(p.Project)).Take(5)
            .Select(p => (Song: p.Song + " (20-level ladder)", Project: Laddered(p.Project)))).ToList();
        var copies = new QualityReportCopies();
        var lines = 0;
        foreach (var (song, project) in charts)
        {
            var fresh = ChartProjectCloner.Clone(project);
            var copy = copies.Copy(project, out var reused);
            Assert.True(reused || ReferenceEquals(project, charts[0].Project), $"{song}: no spare to restore");
            Assert.Empty(DeepCompare.Diff(song, fresh, copy, new HashSet<string>()));
            var expected = QualityReportFingerprint.Of(reporter.Build(fresh), fresh);
            Assert.Equal(expected, QualityReportFingerprint.Of(reporter.Build(copy), copy));
            lines += expected.Count;
            copies.Return(copy);
        }

        output.WriteLine($"{charts.Count} charts, {lines} report lines, identical over a restored spare.");
    }

    private static double MedianOf(IEnumerable<double> samples)
    {
        var all = samples.Order().ToList();
        return all[all.Count / 2];
    }
}
