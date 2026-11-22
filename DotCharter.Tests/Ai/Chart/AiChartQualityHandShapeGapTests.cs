using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Tests.Services.Editing.HandShapes;
using DotCharter.Tone.Ai;

namespace DotCharter.Tests.Ai.Chart;

/// <summary>
///     The AI chart pass's own quality report (#808) reads the minimum note distance setting for
///     D15 like the Issues tab does: a larger configured gap flags the same shape in both.
/// </summary>
public sealed class AiChartQualityHandShapeGapTests
{
    [Fact]
    public void TheAiReportsD15_MatchesTheIssuesTab_UnderALargerConfiguredGap()
    {
        var config = new FakeConfigurationService
        {
            AppConfig = new AppConfig { MinNoteDistanceMode = MinNoteDistanceMode.BeatFraction, MinNoteDistanceDivisor = 8 }
        };
        var project = new ChartProject();
        for (var i = 0; i < 32; i++) project.Beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120 });
        var arr = ChordHandShapePlannerTests.Arr();
        arr.Levels[0].Chords.Add(ChordHandShapePlannerTests.Chord(0, 0, (0, 3), (1, 5), (2, 5)));
        arr.Levels[0].Chords.Add(ChordHandShapePlannerTests.Chord(2, 0, (0, 5), (1, 7), (2, 7)));
        arr.Levels[0].HandShapes.Add(new HandShape { Position = new FractionalPosition(0, new Fraction(0, 16)), EndPosition = new FractionalPosition(0, new Fraction(31, 16)) });
        arr.Levels[0].HandShapes.Add(new HandShape { Position = new FractionalPosition(0, new Fraction(32, 16)), EndPosition = new FractionalPosition(0, new Fraction(40, 16)) });
        project.Arrangements.Add(arr);

        var issuesTab = new ArrangementValidationService(new FakeAppLogger(), ArrangementValidationService.DefaultRules(config))
            .Validate(arr, project.Beats, null, project).Count(i => i.Type == ArrangementIssueType.HandShapeGap);
        var run = new AiChartPassRun(new ProjectState(), new HistoryService(new FakeAppLogger()), null, FakeLocalization.English(),
            new FakeAppLogger(), new FingeringGenerationService(), new NoModels(), () => new RigConfig(), () => null)
        { AppConfiguration = config };
        var aiReport = run.QualityReporter.Build(project).Findings.Count(f => f.Rule?.Type == ArrangementIssueType.HandShapeGap);

        Assert.Equal(1, issuesTab);
        Assert.Equal(issuesTab, aiReport);
    }

    private sealed class NoModels : IToneDesignModelFactory
    {
        public IToneDesignModel Create(AiProviderSettings settings) => throw new NotSupportedException();

        public bool Supports(AiProviderKind kind) => false;
    }
}
