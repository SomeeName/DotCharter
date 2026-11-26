using DotCharter.Services.Editing.Validation.Rules;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tests.Services.Editing.HandShapes;
using DotCharter.Tests.Services.Parsing.Eof;
using DotCharter.Tests.Services.Parsing.GuitarPro;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     D110 (<see cref="AudioCheckRule" />): the spots an import guessed (<see cref="Arrangement.AudioCheckSpots" />)
///     become ONE finding per arrangement with their count, pointing at the first unchecked spot. "Checked" clears
///     that spot, "All checked" clears every one, each one undo step; the spots survive a save.
/// </summary>
public sealed class AudioCheckRuleTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_audio_check_").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static List<BeatMarker> Beats() =>
        [.. Enumerable.Range(0, 16).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    private static Arrangement WithSpots(params int[] beats)
    {
        var arr = ChordHandShapePlannerTests.Arr();
        foreach (var beat in beats)
            arr.AudioCheckSpots.Add(new AudioCheckSpot(new FractionalPosition(beat), AudioCheckReason.DeadNoteSlideLanding));
        return arr;
    }

    private static List<ArrangementIssue> Run(Arrangement arr)
    {
        var issues = new List<ArrangementIssue>();
        new AudioCheckRule().Validate(ArrangementValidationContext.Build(arr, Beats(), null), issues);
        return issues;
    }

    [Fact]
    public void TheCodeIsD110_AWarningThatRunsByDefault()
    {
        Assert.Equal("D110", ArrangementIssueType.NeedsAudioCheck.GetCode());
        Assert.Equal(ArrangementIssueSeverity.Warning, ArrangementIssueType.NeedsAudioCheck.GetSeverity());
        var issues = new ArrangementValidationService(new FakeAppLogger()).Validate(WithSpots(2), Beats(), null);
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.NeedsAudioCheck);
    }

    [Fact]
    public void NoSpots_NoFinding() => Assert.Empty(Run(WithSpots()));

    [Fact]
    public void ManySpots_OneFinding_WithTheCount_AtTheFirstSpot()
    {
        var issue = Assert.Single(Run(WithSpots(6, 2, 4)));
        Assert.Equal(1000.0, issue.TimeMs);
        Assert.Equal("Validation_NeedsAudioCheck_Many", issue.MessageKey);
        Assert.Contains("3", issue.Message);
        Assert.Equal("Validation_NeedsAudioCheck_One", Assert.Single(Run(WithSpots(3))).MessageKey);
    }

    [Fact]
    public void Checked_ClearsTheFirstSpot_AndTheFindingMovesOn_InOneUndoStep()
    {
        var arr = WithSpots(6, 2);
        var history = new HistoryService(new FakeAppLogger());
        var vm = new IssueFixViewModel(history, FakeLocalization.English());

        vm.Offer(Assert.Single(Run(arr)), arr, arr.Levels[0], Beats());
        Assert.True(vm.HasFix);
        Assert.True(vm.HasAlternative);
        Assert.Equal(FakeLocalization.English()["Inspector_IssueFix_AudioChecked"], vm.DefaultLabel);
        vm.ApplyDefaultCommand.Execute(null);

        Assert.Equal(3000.0, Assert.Single(Run(arr)).TimeMs);
        history.Undo();
        Assert.False(history.CanUndo);
        Assert.Equal(2, arr.AudioCheckSpots.Count);
    }

    [Fact]
    public void AllChecked_ClearsEverySpot_InOneUndoStep_AndFixAllNeverChecksUnheard()
    {
        var arr = WithSpots(6, 2, 4);
        var history = new HistoryService(new FakeAppLogger());
        var vm = new IssueFixViewModel(history, FakeLocalization.English());
        var issue = Assert.Single(Run(arr));

        Assert.Null(IssueFixViewModel.DefaultFix(issue, arr, arr.Levels[0], Beats()));
        vm.Offer(issue, arr, arr.Levels[0], Beats());
        Assert.Equal(FakeLocalization.English()["Inspector_IssueFix_AudioAllChecked"], vm.AlternativeLabel);
        vm.ApplyAlternativeCommand.Execute(null);

        Assert.Empty(Run(arr));
        history.Undo();
        Assert.Equal(3, arr.AudioCheckSpots.Count);
    }

    [Fact]
    public void TheSpotsSurviveASave_AndAChartWithoutThemWritesNothing()
    {
        var project = EofExportTestCharts.BuildRepresentativeProject();
        var path = Path.Combine(_dir, "plain.rscp");
        new RscpChartSerializer().Save(path, project);
        Assert.DoesNotContain(RscpAudioCheck.Element, File.ReadAllText(path));

        project.Arrangements[0].AudioCheckSpots.Add(new AudioCheckSpot(new FractionalPosition(3, new Fraction(1, 2)), AudioCheckReason.TempoFromTab));
        new RscpChartSerializer().Save(path, project);
        var loaded = new RscpChartSerializer().Load(path);

        Assert.Equal(project.Arrangements[0].AudioCheckSpots, loaded.Arrangements[0].AudioCheckSpots);
        Assert.Empty(loaded.Arrangements[1].AudioCheckSpots);
    }

    [Theory]
    [InlineData("gpif")]
    [InlineData("gp5")]
    public void AGuessedDeadNoteSlideLanding_IsMarkedByTheImport(string format)
    {
        var arrangement = GpDeadNoteSlideImportTests.Import(format, 4, 0, 5);
        var spot = Assert.Single(arrangement.AudioCheckSpots);
        Assert.Equal(new AudioCheckSpot(arrangement.Levels[0].Notes[0].Position, AudioCheckReason.DeadNoteSlideLanding), spot);
    }
}
