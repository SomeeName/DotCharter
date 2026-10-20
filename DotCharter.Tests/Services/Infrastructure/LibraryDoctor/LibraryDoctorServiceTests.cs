using DotCharter.Services.Infrastructure.LibraryDoctor;
using DotCharter.Services.Library;

namespace DotCharter.Tests.Services.Infrastructure.LibraryDoctor;

public sealed class LibraryDoctorServiceTests
{
    private sealed class FakeProbe : ILibraryPackageProbe
    {
        private readonly Dictionary<string, PackageProbeResult> _byPath;
        public readonly List<string> Probed = [];

        public FakeProbe(Dictionary<string, PackageProbeResult> byPath) => _byPath = byPath;

        public PackageProbeResult Probe(string psarcPath)
        {
            Probed.Add(psarcPath);
            return _byPath.TryGetValue(psarcPath, out var r)
                ? r
                : new PackageProbeResult(true, AudioAvailability.Present);
        }
    }

    private static LibrarySongEntry Entry(string path, string key, string sng = "arr.sng")
        => new()
        {
            PackagePath = path,
            SongKey = key,
            Title = "T",
            Artist = "A",
            TuningSummary = "E A D G B E",
            Arrangements = string.IsNullOrEmpty(sng)
                ? []
                : [new LibraryArrangementEntry { SngPath = sng }]
        };

    [Fact]
    public async Task ProbesEachDistinctPackageOnce()
    {
        var probe = new FakeProbe(new Dictionary<string, PackageProbeResult>(StringComparer.OrdinalIgnoreCase));
        var service = new LibraryDoctorService(new FakeAppLogger(), FakeLocalization.English(), probe);

        // Two songs share one multi-song pack; a third lives alone.
        var report = await service.RunAsync(
        [
            Entry("pack.psarc", "S1"),
            Entry("pack.psarc", "S2"),
            Entry("solo.psarc", "S3")
        ], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, probe.Probed.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(2, probe.Probed.Count); // exactly once per distinct path
        Assert.Equal(3, report.SongsScanned);
        Assert.True(report.IsHealthy);
    }

    [Fact]
    public async Task MissingAudioProbe_SurfacesAsIssue()
    {
        var probe = new FakeProbe(new Dictionary<string, PackageProbeResult>(StringComparer.OrdinalIgnoreCase)
        {
            ["silent.psarc"] = new PackageProbeResult(true, AudioAvailability.Missing)
        });
        var service = new LibraryDoctorService(new FakeAppLogger(), FakeLocalization.English(), probe);

        var report = await service.RunAsync([Entry("silent.psarc", "S1")],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(report.Issues, i => i.Kind == LibraryIssueKind.MissingAudio);
    }

    [Fact]
    public async Task UnreadableProbe_SurfacesAsUnreadablePackage()
    {
        var probe = new FakeProbe(new Dictionary<string, PackageProbeResult>(StringComparer.OrdinalIgnoreCase)
        {
            ["corrupt.psarc"] = new PackageProbeResult(false, AudioAvailability.Unknown, "bad header")
        });
        var service = new LibraryDoctorService(new FakeAppLogger(), FakeLocalization.English(), probe);

        var report = await service.RunAsync([Entry("corrupt.psarc", "S1")],
            cancellationToken: TestContext.Current.CancellationToken);

        var issue = Assert.Single(report.Issues, i => i.Kind == LibraryIssueKind.UnreadablePackage);
        Assert.Contains("bad header", issue.Detail);
    }

    [Fact]
    public async Task EntryWithNoSng_FlagsNoArrangements()
    {
        var service = new LibraryDoctorService(new FakeAppLogger(),
            FakeLocalization.English(),
            new FakeProbe(new Dictionary<string, PackageProbeResult>(StringComparer.OrdinalIgnoreCase)));

        var report = await service.RunAsync([Entry("x.psarc", "S1", sng: "")],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(report.Issues, i => i.Kind == LibraryIssueKind.NoArrangements);
    }

    [Fact]
    public async Task EmptyLibrary_ReturnsEmptyReport()
    {
        var service = new LibraryDoctorService(new FakeAppLogger(), FakeLocalization.English(),
            new FakeProbe(new Dictionary<string, PackageProbeResult>(StringComparer.OrdinalIgnoreCase)));
        var report = await service.RunAsync([], cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(report.IsHealthy);
        Assert.Equal(0, report.SongsScanned);
    }

    [Fact]
    public async Task ReportsProgressPerPackage()
    {
        var service = new LibraryDoctorService(new FakeAppLogger(),
            FakeLocalization.English(),
            new FakeProbe(new Dictionary<string, PackageProbeResult>(StringComparer.OrdinalIgnoreCase)));
        var ticks = new List<LibraryDoctorProgress>();
        var progress = new Progress<LibraryDoctorProgress>(p => ticks.Add(p));

        // Progress<T> posts async; run and then assert the final report instead
        // of racing the callback — but at least confirm no throw and the report.
        var report = await service.RunAsync(
            [Entry("a.psarc", "S1"), Entry("b.psarc", "S2")], progress, TestContext.Current.CancellationToken);

        Assert.Equal(2, report.PackagesScanned);
    }
    private sealed class RecordingProgress : IProgress<LibraryDoctorProgress>
    {
        public readonly List<LibraryDoctorProgress> Reports = [];
        public void Report(LibraryDoctorProgress value) => Reports.Add(value);
    }

    /// <summary>
    ///     Static perf audit 2026-08-29 A1: one <c>Report</c> per package meant
    ///     one dispatcher post + one <c>PropertyChanged</c> + one ProgressBar
    ///     invalidation per package, in a burst, on a warm library — the exact
    ///     shape the package library's scan had already fixed with a
    ///     33 ms gate. The probes here are instant, so a per-package report would
    ///     produce exactly <c>packages</c> entries; the gate produces a handful,
    ///     and the LAST package always reports so the bar reaches 100%.
    /// </summary>
    [Fact]
    public async Task Progress_IsGatedTo33Ms_AndTheLastPackageAlwaysReports()
    {
        const int packages = 2_000;
        var probe = new FakeProbe(new Dictionary<string, PackageProbeResult>(StringComparer.OrdinalIgnoreCase));
        var service = new LibraryDoctorService(new FakeAppLogger(), FakeLocalization.English(), probe);
        var progress = new RecordingProgress();

        var songs = Enumerable.Range(0, packages).Select(i => Entry($"pack{i}.psarc", $"S{i}")).ToList();
        await service.RunAsync(songs, progress, TestContext.Current.CancellationToken);

        Assert.Equal(packages, probe.Probed.Count);
        Assert.NotEmpty(progress.Reports);
        Assert.Equal(new LibraryDoctorProgress(packages, packages), progress.Reports[^1]);
        Assert.True(progress.Reports.Count <= packages / 10,
            $"{progress.Reports.Count} progress reports for {packages} instant probes — the 33 ms gate is not gating.");
        Assert.Equal(33, LibraryDoctorService.ProgressIntervalMs);
    }
}
