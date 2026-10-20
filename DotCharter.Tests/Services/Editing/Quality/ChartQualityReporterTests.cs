using DotCharter.Analysis.AudioToChart;
using DotCharter.Services.Editing.Linting;
using DotCharter.Services.Editing.Quality;
using DotCharter.Services.Export.Validation;
using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Editing.Quality;

/// <summary>
///     #291: one verdict over the existing checks. The report must neither drop nor double what a
///     check says, merge the faults two checks share into one entry, and say "ready" when nothing
///     would stop the export.
/// </summary>
public sealed class ChartQualityReporterTests
{
    private const double BeatMs = 500;

    private static ChartQualityReporter Reporter() => new(new ArrangementValidationService(new FakeAppLogger()));

    /// <summary>Phrased and anchored notes: nothing the export refuses.</summary>
    private static ChartProject ShippableChart()
    {
        var project = new ChartProject();
        for (var i = 0; i < 64; i++)
            project.Beats.Add(new BeatMarker { Time = i * BeatMs, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0 });

        var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = 6 };
        for (var s = 0; s < 6; s++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel();
        for (var beat = 4; beat < 40; beat++)
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(beat), StringIndex = beat % 6, Fret = 3 + beat % 5 });
        level.Anchors.Add(new FretAnchor(new FractionalPosition(4), 3, 4));
        arrangement.Levels.Add(level);
        arrangement.EventPoints.Add(new EventPoint { Position = FractionalPosition.Zero, Phrase = "COUNT" });
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(4), Phrase = "verse", Section = "verse" });
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(44), Phrase = "END" });
        project.Arrangements.Add(arrangement);
        return project;
    }

    /// <summary>A shippable chart with a fret the game cannot play, which the rules AND the export
    /// gate both report, and an uncovered stretch only the export gate reports.</summary>
    private static ChartProject ChartWithFindings()
    {
        var project = ShippableChart();
        var level = project.Arrangements[0].Levels[0];
        level.Notes[10].Fret = 25;
        level.Anchors.Clear();
        level.Anchors.Add(new FretAnchor(new FractionalPosition(12), 9, 3));
        return project;
    }

    [Fact]
    public void AShippableChart_IsReady()
    {
        var report = Reporter().Build(ShippableChart());

        Assert.True(report.IsReady, string.Join("\n", report.Findings.Where(f => f.Severity == QualitySeverity.Error).Select(f => f.Code)));
        Assert.Equal(0, report.Errors);
        Assert.Single(report.Arrangements);
    }

    /// <summary>Every item each check reports, run on its own, appears in the report exactly once
    /// — on its own, or merged with the same fault from the other check.</summary>
    [Fact]
    public void EveryFindingOfEveryCheck_AppearsExactlyOnce()
    {
        var project = ChartWithFindings();
        var issues = new ArrangementValidationService(new FakeAppLogger()).Validate(project.Arrangements[0], project.Beats, null, project);
        var exports = new ChartExportValidator().Validate(project);
        Assert.NotEmpty(issues);
        Assert.NotEmpty(exports);

        var report = Reporter().Build(project);

        Assert.All(issues, issue => Assert.Single(report.Findings, f => f.Rule == issue));
        Assert.All(exports, export => Assert.Single(report.Findings, f => f.Export == export));
        Assert.False(report.IsReady);
    }

    [Fact]
    public void AFaultTwoChecksShare_IsOneFinding_WithTheRulesElementAndTheExportSeverity()
    {
        var project = ChartWithFindings();
        var level = project.Arrangements[0].Levels[0];
        var note = level.Notes[10];

        var report = Reporter().Build(project);

        var fret = Assert.Single(report.Findings, f => ReferenceEquals(f.Element, note) && f.Rule?.Type == ArrangementIssueType.FretNumberMoreThan24);
        Assert.Equal(QualityChecks.ChartRules | QualityChecks.ExportValidation, fret.Checks);
        Assert.Equal(ChartExportValidator.KeyFretOutOfRange, fret.Export!.MessageKey);
        Assert.Equal(QualitySeverity.Error, fret.Severity);
        Assert.Same(level, fret.Level);
        Assert.Equal(note.Position.GetPositionMs(project.Beats), fret.TimeMs);
    }

    [Fact]
    public void AnElementsLevel_IsTheLevelThatHoldsIt()
    {
        var project = ShippableChart();
        var arrangement = project.Arrangements[0];
        var easy = new ChartLevel { DifficultyIndex = 0 };
        var hard = arrangement.Levels[0];
        hard.DifficultyIndex = 1;
        arrangement.Levels.Insert(0, easy);
        var note = hard.Notes[3];
        note.Fret = 30;

        var findings = Reporter().Build(project).Findings.Where(f => ReferenceEquals(f.Element, note)).ToList();

        Assert.NotEmpty(findings);
        Assert.All(findings, f => Assert.Same(hard, f.Level));
    }

    [Fact]
    public void AudioFindings_AreReported_OnTheLevelTheLintVerifies()
    {
        var project = ShippableChart();
        var arrangement = project.Arrangements[0];
        var heard = new ChartAudioFinding(2_000, 52, 0.9f, 55, 0.8f);
        var outcome = new AudioChartLintOutcome(true, null, 36, 35, [heard]);

        var report = Reporter().Build(project, audio: new Dictionary<Arrangement, AudioChartLintOutcome> { [arrangement] = outcome });

        var finding = Assert.Single(report.Findings, f => f.Checks == QualityChecks.AudioLint);
        Assert.Same(heard, finding.Audio);
        Assert.Same(arrangement.Levels[0], finding.Level);
        Assert.Equal(ChartQualityReporter.AudioLintCode, finding.Code);
        Assert.True(report.Arrangements.Single().AudioChecked);
        Assert.True(report.IsReady, "an audio doubt is a warning, not an export blocker");
    }

    /// <summary>An export finding names its arrangement by name; with two arrangements of that name
    /// it belongs to neither and merges with neither, rather than guessing.</summary>
    [Fact]
    public void ADuplicatedArrangementName_IsNeverGuessed()
    {
        var project = ChartWithFindings();
        var twin = ChartProjectCloner.CloneArrangement(project.Arrangements[0]);
        project.Arrangements.Add(twin);

        var report = Reporter().Build(project);

        Assert.All(report.Findings.Where(f => f.Export != null), f =>
        {
            Assert.Null(f.Rule);
            Assert.Null(f.Arrangement);
        });
    }

    [Fact]
    public void EverySharedFault_PairsARealExportKeyWithARealRule()
    {
        var keys = typeof(ChartExportValidator).GetFields()
            .Where(f => f.IsLiteral && f.Name.StartsWith("Key", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet();

        Assert.All(ChartQualityReporter.SameFault, pair =>
        {
            Assert.Contains(pair.Key, keys);
            Assert.True(Enum.IsDefined(pair.Value));
        });
        Assert.Equal(ChartQualityReporter.SameFault.Count, ChartQualityReporter.SameFault.Values.Distinct().Count());
    }

    /// <summary>
    ///     #638: the Editor on Fire round trip, one row per place, named by what it is and where. It
    ///     is its own call (#685: the report runs it lazily, off the UI thread) and it never touches
    ///     the verdict or the findings.
    /// </summary>
    [Fact]
    public void TheEofRoundTrip_NamesACentOffsetAndAPartialTremoloChord_AndLeavesTheVerdictAlone()
    {
        var project = ShippableChart();
        var lead = project.Arrangements[0];
        lead.CentOffset = -15;
        var chord = new Chord { Position = new FractionalPosition(42) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3, Tremolo = true });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        lead.Levels[0].Chords.Add(chord);

        var before = Reporter().Build(project);
        var check = ChartQualityReporter.CheckEofRoundTrip(project);
        var report = Reporter().Build(project);

        var roundTrip = check.Summary!;
        var cents = Assert.Single(roundTrip.Groups, g => g.Loss == EofLoss.CentOffset);
        Assert.Equal(new EofLossPlace("Lead", null, "-15"), Assert.Single(cents.Places));
        var tremolo = Assert.Single(roundTrip.Groups, g => g.Loss == EofLoss.PartialChordTremolo);
        Assert.Equal(new EofLossPlace("Lead", 42 * BeatMs, ""), Assert.Single(tremolo.Places));
        Assert.All(roundTrip.Groups, g => Assert.Equal(EofConversionKind.Dropped, g.Kind));
        Assert.Equal(before.Findings.Count, report.Findings.Count);
        Assert.Equal(before.IsReady, report.IsReady);
        Assert.Empty(check.Error);
    }
}
