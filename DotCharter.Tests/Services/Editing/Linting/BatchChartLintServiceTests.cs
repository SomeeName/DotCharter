using DotCharter.Services.Editing.Fixing;
using DotCharter.Services.Editing.Linting;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Charter;
using DotCharter.Services.Parsing;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Editing.Linting;

public sealed class BatchChartLintServiceTests : IDisposable
{
    private readonly string _tempDir;

    public BatchChartLintServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    [Fact]
    public void Run_ValidateOnly_ReportsIssues_AndNeverTouchesTheFile()
    {
        var path = WriteFixtureProject("chart.rscp");
        var bytesBefore = File.ReadAllBytes(path);

        var result = CreateService().Run(new[] { path },
            cancellationToken: TestContext.Current.CancellationToken);

        var report = Assert.Single(result.Charts);
        Assert.False(report.Failed);
        Assert.False(report.Saved);
        Assert.Equal(0, result.TotalFixes);

        var arrangement = Assert.Single(report.Arrangements);
        // RSCP normalizes arrangement names on load ("Lead Guitar" → "Lead").
        Assert.False(string.IsNullOrWhiteSpace(arrangement.ArrangementName));
        Assert.Contains(arrangement.Issues, i => i.Type == ArrangementIssueType.FretNumberMoreThan24);
        Assert.Empty(arrangement.FixesApplied);

        Assert.Equal(bytesBefore, File.ReadAllBytes(path));
    }

    [Fact]
    public void Run_WithAutoFixes_AppliesFixes_AndReportsPostFixIssues()
    {
        var path = WriteFixtureProject("chart.rscp");

        var result = CreateService().Run(new[] { path }, new ChartLintOptions { ApplyAutoFixes = true },
            cancellationToken: TestContext.Current.CancellationToken);

        var arrangement = Assert.Single(Assert.Single(result.Charts).Arrangements);
        Assert.True(arrangement.FixesApplied.TryGetValue("duplicatesRemoved", out var duplicates) && duplicates >= 1,
            $"Expected duplicatesRemoved fix, got: {string.Join(",", arrangement.FixesApplied.Keys)}");
        Assert.True(result.TotalFixes >= 1);
        // The unfixable lint issue survives the fix pass and is re-reported.
        Assert.Contains(arrangement.Issues, i => i.Type == ArrangementIssueType.FretNumberMoreThan24);
        // Fixes were NOT saved back (flag off): the duplicate is still on disk.
        var reloaded = new RscpChartSerializer().Load(path);
        Assert.Equal(3, reloaded.Arrangements[0].Levels[0].Notes.Count);
    }

    [Fact]
    public void Run_WithAutoFixesAndSave_PersistsTheFixedProject()
    {
        var path = WriteFixtureProject("chart.dcproj");

        var result = CreateService().Run(new[] { path },
            new ChartLintOptions { ApplyAutoFixes = true, SaveFixedProjects = true },
            cancellationToken: TestContext.Current.CancellationToken);

        var report = Assert.Single(result.Charts);
        Assert.True(report.Saved);
        Assert.Equal(1, result.SavedCount);

        var reloaded = new RscpChartSerializer().Load(path);
        var notes = reloaded.Arrangements[0].Levels[0].Notes;
        Assert.Equal(2, notes.Count); // duplicate removed on disk
    }

    [Fact]
    public void Run_BadFile_ReportsFailure_AndContinuesWithTheRest()
    {
        var badPath = Path.Combine(_tempDir, "garbage.rscp");
        File.WriteAllText(badPath, "this is not a chart project at all");
        var goodPath = WriteFixtureProject("good.rscp");
        var missingPath = Path.Combine(_tempDir, "does-not-exist.rscp");

        var result = CreateService().Run(new[] { badPath, missingPath, goodPath },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Charts.Count);
        Assert.Equal(2, result.FailedCount);

        Assert.True(result.Charts[0].Failed);
        Assert.NotNull(result.Charts[0].Error);
        Assert.True(result.Charts[1].Failed);
        Assert.False(result.Charts[2].Failed);
        Assert.NotEmpty(result.Charts[2].Arrangements);
    }

    [Fact]
    public void Run_CleanProject_ReportsNothingToFix_AndDoesNotSave()
    {
        var path = WriteFixtureProject("clean.rscp", includeBadFret: false, includeDuplicate: false);

        var result = CreateService().Run(new[] { path },
            new ChartLintOptions { ApplyAutoFixes = true, SaveFixedProjects = true },
            cancellationToken: TestContext.Current.CancellationToken);

        var report = Assert.Single(result.Charts);
        Assert.False(report.Failed);
        Assert.False(report.Saved);
        Assert.Equal(0, result.TotalFixes);
        var arrangement = Assert.Single(report.Arrangements);
        Assert.DoesNotContain(arrangement.Issues, i => i.Type == ArrangementIssueType.FretNumberMoreThan24);
    }

    [Fact]
    public void Run_ReportsIssueCountBeforeAndAfterFixes()
    {
        var path = WriteFixtureProject("chart.rscp");

        var result = CreateService().Run(new[] { path }, new ChartLintOptions { ApplyAutoFixes = true },
            cancellationToken: TestContext.Current.CancellationToken);

        var arrangement = Assert.Single(Assert.Single(result.Charts).Arrangements);
        Assert.True(arrangement.IssuesBeforeFixes >= arrangement.Issues.Count,
            "Auto-fixes must never introduce more issues in this fixture.");
    }

    // ------------------------------------------------- progress + cancellation

    /// <summary>
    ///     The batch is minutes of recursive I/O, so it has to say where it is.
    ///     One report per file BEFORE that file is touched (so a UI names the
    ///     file it is working on, not the one it just left) plus one final
    ///     report — which is what drives the bar to its true end position.
    /// </summary>
    [Fact]
    public void Run_ReportsOneProgressStepPerFile_NamingTheFileAboutToBeLinted()
    {
        var first = WriteFixtureProject("a.rscp");
        var second = WriteFixtureProject("b.rscp");
        var steps = new List<ChartLintProgress>();

        CreateService().Run([first, second], progress: new ImmediateProgress(steps.Add),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, steps.Count);
        Assert.Equal(new ChartLintProgress(first, 0, 2), steps[0]);
        Assert.Equal(new ChartLintProgress(second, 1, 2), steps[1]);
        Assert.Equal(new ChartLintProgress(string.Empty, 2, 2), steps[2]);
        Assert.Equal(0.0, steps[0].Fraction);
        Assert.Equal(0.5, steps[1].Fraction);
        Assert.Equal(1.0, steps[2].Fraction);
    }

    /// <summary>
    ///     A cancel stops the batch on a WHOLE-FILE boundary and the partial
    ///     result says so. The files never reached must be byte-identical: a
    ///     saving run rewrites projects the user never opened, so "it stopped"
    ///     has to mean the rest were not touched at all.
    /// </summary>
    [Fact]
    public void Run_WhenCanceled_StopsAfterTheInFlightFile_AndReportsThePartialHonestly()
    {
        var first = WriteFixtureProject("a.rscp");
        var second = WriteFixtureProject("b.rscp");
        var third = WriteFixtureProject("c.rscp");
        var secondBefore = File.ReadAllBytes(second);
        var thirdBefore = File.ReadAllBytes(third);

        using var cts = new CancellationTokenSource();
        // Cancel from inside the first file's own progress report — the closest
        // a test can get to the user hitting Cancel mid-run.
        var progress = new ImmediateProgress(_ => cts.Cancel());

        var result = CreateService().Run([first, second, third],
            new ChartLintOptions { ApplyAutoFixes = true, SaveFixedProjects = true },
            progress, cts.Token);

        Assert.True(result.Canceled);
        var report = Assert.Single(result.Charts);
        Assert.Equal(first, report.FilePath);
        // The in-flight file finished — including its write.
        Assert.True(report.Saved);
        Assert.Equal(secondBefore, File.ReadAllBytes(second));
        Assert.Equal(thirdBefore, File.ReadAllBytes(third));
    }

    /// <summary>A run nobody cancelled must not claim it was cancelled.</summary>
    [Fact]
    public void Run_WithoutCancellation_IsNotReportedAsCanceled()
    {
        var result = CreateService().Run([WriteFixtureProject("a.rscp")],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Canceled);
        Assert.Single(result.Charts);
    }

    /// <summary>
    ///     The whole-batch fix tally the bulk-overwrite report is built from —
    ///     "what did that actually change?" answered per fixer, not as one total.
    /// </summary>
    [Fact]
    public void FixClasses_TotalsEachFixerAcrossTheWholeBatch()
    {
        var result = CreateService().Run(
            [WriteFixtureProject("a.rscp"), WriteFixtureProject("b.rscp")],
            new ChartLintOptions { ApplyAutoFixes = true },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.FixClasses.TryGetValue("duplicatesRemoved", out var removed));
        Assert.Equal(2, removed); // one duplicate per fixture file
        Assert.Equal(result.TotalFixes, result.FixClasses.Values.Sum());
    }

    /// <summary>Reports on the calling thread — the service's own reporting is
    /// what is under test here, not a dispatcher.</summary>
    private sealed class ImmediateProgress(Action<ChartLintProgress> onReport) : IProgress<ChartLintProgress>
    {
        public void Report(ChartLintProgress value) => onReport(value);
    }

    // ---------------------------------------------------------------- fixture

    private static BatchChartLintService CreateService(IChartSerializer? serializer = null,
        ISaveCompanionService? companions = null)
    {
        return new BatchChartLintService(
            serializer ?? new RscpChartSerializer(),
            new ArrangementValidationService(new FakeAppLogger()),
            new ArrangementAutoFixService(),
            companions ?? RealCompanions());
    }

    private static readonly ChartLintOptions SaveFixes = new() { ApplyAutoFixes = true, SaveFixedProjects = true };

    private static SaveCompanionService RealCompanions() => new(new FakeConfigurationService(), new FakeAppLogger());

    /// <summary>A project file the way Charter writes it: no DotCharter element anywhere, and no
    /// ownership stamp, so DotCharter never considers it its own.</summary>
    private string WriteCharterProject(string fileName)
    {
        var path = Path.Combine(_tempDir, fileName);
        var doc = CharterRscpProjection.Build(BuildFixtureProject(), out _);
        using var stream = File.Create(path);
        RscpChartSerializer.WriteDocument(stream, doc);
        return path;
    }

    /// <summary>
    ///     A Charter <c>.rscp</c> in a folder batch lint walks: the fixed project is written as the
    ///     <c>.dcproj</c> beside it, and the <c>.rscp</c> stays byte for byte what Charter wrote.
    /// </summary>
    [Fact]
    public void ACharterRscp_IsNeverRewritten_TheFixedProjectIsWrittenAsADcproj()
    {
        var rscp = WriteCharterProject("chart.rscp");
        var before = File.ReadAllBytes(rscp);
        Assert.False(CharterRscp.IsWrittenByDotCharter(rscp));

        var report = Assert.Single(CreateService(companions: RealCompanions()).Run(
            [rscp], SaveFixes, cancellationToken: TestContext.Current.CancellationToken).Charts);

        Assert.True(report.Saved, report.Error);
        Assert.Equal(before, File.ReadAllBytes(rscp));
        var dcproj = Path.Combine(_tempDir, "chart.dcproj");
        Assert.True(File.Exists(dcproj));
        Assert.True(CharterRscp.IsWrittenByDotCharter(dcproj));
        Assert.Equal(2, new RscpChartSerializer().Load(dcproj).Arrangements[0].Levels[0].Notes.Count); // the fix is in it
        Assert.False(File.Exists(rscp + ".bak"));
        Assert.False(File.Exists(rscp + ".lint-saving"));
    }

    /// <summary>An older DotCharter <c>.rscp</c> follows the session's first save: kept as <c>.rscp.bak</c>,
    /// with the Charter companion taking the name.</summary>
    [Fact]
    public void AnOlderDotCharterRscp_IsKeptAsABackup_AndTheCharterCompanionTakesItsName()
    {
        var rscp = WriteFixtureProject("chart.rscp");
        var before = File.ReadAllBytes(rscp);

        var report = Assert.Single(CreateService(companions: RealCompanions()).Run(
            [rscp], SaveFixes, cancellationToken: TestContext.Current.CancellationToken).Charts);

        Assert.True(report.Saved, report.Error);
        Assert.Equal(before, File.ReadAllBytes(rscp + ".bak"));
        Assert.True(File.Exists(Path.Combine(_tempDir, "chart.dcproj")));
        Assert.True(File.Exists(rscp));
        Assert.False(CharterRscp.IsWrittenByDotCharter(rscp));
    }

    /// <summary>A <c>.dcproj</c> of that name is somebody's project: nothing is overwritten, nothing is lost.</summary>
    [Fact]
    public void ADcprojAlreadyBesideTheRscp_IsNeverOverwritten()
    {
        var rscp = WriteCharterProject("chart.rscp");
        var dcproj = Path.Combine(_tempDir, "chart.dcproj");
        File.WriteAllText(dcproj, "someone else's project");
        var before = File.ReadAllBytes(rscp);

        var report = Assert.Single(CreateService(companions: RealCompanions()).Run(
            [rscp], SaveFixes, cancellationToken: TestContext.Current.CancellationToken).Charts);

        Assert.False(report.Saved);
        Assert.Contains("already exists", report.Error);
        Assert.Equal("someone else's project", File.ReadAllText(dcproj));
        Assert.Equal(before, File.ReadAllBytes(rscp));
        Assert.False(File.Exists(rscp + ".lint-saving"));
    }

    /// <summary>A report-only run writes no file at all.</summary>
    [Fact]
    public void AReportOnlyRun_OverACharterRscp_WritesNoDcproj()
    {
        var rscp = WriteCharterProject("clean.rscp");
        var before = File.ReadAllBytes(rscp);
        var service = CreateService(companions: RealCompanions());

        service.Run([rscp], new ChartLintOptions { ApplyAutoFixes = false }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(before, File.ReadAllBytes(rscp));
        Assert.False(File.Exists(Path.Combine(_tempDir, "clean.dcproj")));
    }

    /// <summary>
    ///     Batch lint rewrites <c>.dcproj</c> files IN PLACE across a whole folder
    ///     tree, headlessly, on projects the user never opened — the largest blast
    ///     radius of any writer in the repo. It used to hand the real project path
    ///     straight to the serializer, which opens <c>FileMode.Create</c>
    ///     (truncate) and then fsyncs, on the documented assumption that it is the
    ///     STAGING half of a write-then-rename. A throw mid-write therefore left a
    ///     truncated project, made durable, with nothing to swap back.
    /// </summary>
    [Fact]
    public void AFailedSaveLeavesTheOriginalProjectIntact()
    {
        var path = WriteFixtureProject("chart.dcproj");
        var bytesBefore = File.ReadAllBytes(path);

        var report = Assert.Single(
            CreateService(new ThrowingSerializer()).Run(
                new[] { path }, new ChartLintOptions { ApplyAutoFixes = true, SaveFixedProjects = true },
                cancellationToken: TestContext.Current.CancellationToken).Charts);

        Assert.False(report.Saved);
        Assert.Contains("Save failed", report.Error);

        // The whole claim: the project on disk is byte-for-byte what it was.
        Assert.Equal(bytesBefore, File.ReadAllBytes(path));
        // …and no staging leftover.
        Assert.False(File.Exists(path + ".lint-saving"));
    }

    /// <summary>Writes a partial file, then fails — the exact truncation shape.</summary>
    private sealed class ThrowingSerializer : IChartSerializer
    {
        private readonly RscpChartSerializer _real = new();

        public ChartProject Load(string filePath) => _real.Load(filePath);

        public void Save(string filePath, ChartProject project, double? audioLengthMs = null)
        {
            File.WriteAllText(filePath, "<?xml version='1.0'?><partial");
            throw new IOException("disk full");
        }
    }

    /// <summary>A small project: 8 beats, one lead arrangement with a normal
    /// note, optionally a fret-26 note (unfixable lint issue) and a duplicate
    /// note at the same (position, string) (auto-fixable).</summary>
    private string WriteFixtureProject(string fileName, bool includeBadFret = true, bool includeDuplicate = true)
    {
        var path = Path.Combine(_tempDir, fileName);
        new RscpChartSerializer().Save(path, BuildFixtureProject(includeBadFret, includeDuplicate));
        return path;
    }

    private static ChartProject BuildFixtureProject(bool includeBadFret = true, bool includeDuplicate = true)
    {
        var project = new ChartProject { AudioFileName = "song.wav", Bpm = 120.0 };
        project.Chart.Artist = "Artist";
        project.Chart.SongName = "Song";
        for (var i = 0; i < 8; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, IsMeasure = i % 4 == 0 });

        var arrangement = new Arrangement { Name = "Lead Guitar", ArrangementType = ArrangementType.Lead };
        for (var s = 0; s < 6; s++) arrangement.TuningPitches.Add(new TuningOffset(0));

        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 2, Fret = 5 });
        if (includeDuplicate)
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 2, Fret = 7 });
        if (includeBadFret)
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(3), StringIndex = 0, Fret = 26 });
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        return project;
    }
}
