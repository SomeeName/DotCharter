using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing.Tempo.RecordingAlignment;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tests.Analysis;
using DotCharter.Tests.Services.Editing.Tempo;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>Tempo map ▸ Align to recording (#784): the dialog over the real service, chart editor and history.</summary>
public sealed class AlignToRecordingViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-784-vm-" + Guid.NewGuid().ToString("N"));

    public AlignToRecordingViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Analyze_ThenApply_WritesOneUndoStep_AndAnswersTrue()
    {
        var (h, vm) = Open(new RecordingAlignmentFixture());
        Assert.False(vm.ApplyCommand.CanExecute(null));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.AnalyzeCommand).ExecuteAsync(null);

        Assert.True(vm.HasResult, vm.StatusText);
        Assert.Equal(4, vm.Sections.Count);
        Assert.All(vm.Sections, s => Assert.True(s.IsApplied));
        Assert.False(string.IsNullOrEmpty(vm.SummaryText));
        Assert.False(h.History.CanUndo);

        vm.ApplyCommand.Execute(null);

        Assert.True(await vm.Result);
        Assert.Single(h.History.GetUndoSteps());
        Assert.NotEqual(0, h.Project.Beats[0].Time);
    }

    [Fact]
    public async Task AnalysisResolvesDeclaredStemsFromTheProjectFolderWhenTheMixLivesElsewhere()
    {
        var (h, service, audio) = RecordingAlignmentProject.Build(new RecordingAlignmentFixture(), _dir);
        var projectDir = Directory.CreateDirectory(Path.Combine(_dir, "chart")).FullName;
        var folder = Directory.CreateDirectory(Path.Combine(projectDir, "stems", "silence-2")).FullName;
        var padded = Path.Combine(folder, "guitar.wav");
        File.Copy(audio, padded);
        h.State.CurrentProjectPath = Path.Combine(projectDir, "chart.dcproj");
        h.Project.Stems.Add(new Stem { Name = "guitar", Path = Path.GetRelativePath(projectDir, padded) });
        var vm = new AlignToRecordingViewModel(h.State, service, new Playback(audio),
            new DialogBasics(FakeLocalization.KeysOnly(), new FakeAppLogger()));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.AnalyzeCommand).ExecuteAsync(null);

        Assert.True(vm.HasResult, vm.StatusText);
        Assert.StartsWith("Dialog_AlignToRecording_SummaryStems(", vm.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UntickingEverySection_DisablesApply_AndChangingTheRangeDropsTheResult()
    {
        var (_, vm) = Open(new RecordingAlignmentFixture());
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.AnalyzeCommand).ExecuteAsync(null);

        foreach (var row in vm.Sections) row.IsApplied = false;
        Assert.False(vm.ApplyCommand.CanExecute(null));

        vm.IsFromBar = true;
        Assert.True(vm.IsFromBar);
        Assert.False(vm.IsWholeSong);
        Assert.False(vm.HasResult);
        Assert.Empty(vm.Sections);
    }

    [Fact]
    public async Task Cancel_ClosesWithoutTouchingTheProject()
    {
        var (h, vm) = Open(new RecordingAlignmentFixture());
        var before = h.Project.Beats.Select(b => b.Time).ToList();

        Assert.True(vm.TryCancel());

        Assert.False(await vm.Result);
        Assert.Equal(before, h.Project.Beats.Select(b => b.Time));
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void WithoutAudio_TheDialogSaysSo()
    {
        var (h, service, _) = RecordingAlignmentProject.Build(new RecordingAlignmentFixture(), _dir);
        var vm = new AlignToRecordingViewModel(h.State, service, new Playback(""), Basics());

        Assert.True(vm.StatusIsProblem);
        Assert.Equal(FakeLocalization.English()["Dialog_AlignToRecording_NoAudio"], vm.StatusText);
    }

    [Fact]
    public async Task OnAChartWithoutNotes_TheDialogSaysWhatTheToolNeeds_AtOpenAndOnAnalyze()
    {
        var (h, service, audio) = RecordingAlignmentProject.Build(new RecordingAlignmentFixture(), _dir);
        h.Project.Arrangements[0].Levels[0].Notes.Clear();
        var needsNotes = FakeLocalization.English()["Dialog_AlignToRecording_NeedsNotes"];

        var vm = new AlignToRecordingViewModel(h.State, service, new Playback(audio), Basics());
        Assert.True(vm.StatusIsProblem);
        Assert.Equal(needsNotes, vm.StatusText);

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.AnalyzeCommand).ExecuteAsync(null);

        Assert.False(vm.HasResult);
        Assert.Empty(vm.Sections);
        Assert.True(vm.StatusIsProblem);
        Assert.Equal(needsNotes, vm.StatusText);
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task ARangeWithoutNotes_IsNamedAsTheRange_NotAsTheChart()
    {
        var (h, vm) = Open(new RecordingAlignmentFixture());
        var notes = h.Project.Arrangements[0].Levels[0].Notes;
        foreach (var note in notes.Where(n => n.Position.BeatId >= 48).ToList()) notes.Remove(note);
        Assert.False(vm.StatusIsProblem);

        vm.FromBar = 13;
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.AnalyzeCommand).ExecuteAsync(null);

        Assert.False(vm.HasResult);
        Assert.True(vm.StatusIsProblem);
        Assert.Equal(FakeLocalization.English()["Dialog_AlignToRecording_RangeNeedsNotes"], vm.StatusText);
    }

    [Fact]
    public async Task ASectionWithoutNotes_IsListedWithItsReason_AndCanNeverBeTicked()
    {
        var (h, vm) = Open(new RecordingAlignmentFixture());
        var notes = h.Project.Arrangements[0].Levels[0].Notes;
        foreach (var note in notes.Where(n => n.Position.BeatId is >= 32 and < 48).ToList()) notes.Remove(note);

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.AnalyzeCommand).ExecuteAsync(null);

        Assert.True(vm.HasResult, vm.StatusText);
        Assert.Equal(4, vm.Sections.Count);
        var silent = vm.Sections[2];
        Assert.False(silent.CanApply);
        Assert.False(silent.IsApplied);
        Assert.False(silent.IsLow);
        Assert.Equal(FakeLocalization.English()["Dialog_AlignToRecording_NoNotes"], silent.SkipReason);
        Assert.Equal("", silent.ConfidenceText);
        Assert.Equal("", silent.ShiftText);
        silent.IsApplied = true;
        Assert.False(silent.IsApplied);

        Assert.All(vm.Sections.Where(s => s != silent), s =>
        {
            Assert.True(s.CanApply);
            Assert.True(s.IsApplied);
            Assert.Equal("", s.SkipReason);
        });
        Assert.False(vm.IsWeakMatch);
        Assert.True(vm.ShowSections);
        Assert.StartsWith(FakeLocalization.English().Format("Dialog_AlignToRecording_SummaryMix", 3, 3), vm.SummaryText);
    }

    [Fact]
    public async Task WhenNoSectionMatches_TheResultIsOneLine_AndTheRowsStartCollapsed()
    {
        var (h, _, audio) = RecordingAlignmentProject.Build(new RecordingAlignmentFixture(), _dir);
        var vm = new AlignToRecordingViewModel(h.State, new NothingMatches(), new Playback(audio), Basics());
        Assert.False(vm.IsWeakMatch);
        Assert.False(vm.ShowSections);

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.AnalyzeCommand).ExecuteAsync(null);

        Assert.True(vm.HasResult);
        Assert.True(vm.IsWeakMatch);
        Assert.True(vm.StatusIsProblem);
        Assert.Equal(FakeLocalization.English()["Dialog_AlignToRecording_WeakMatch"], vm.StatusText);
        Assert.False(vm.ShowSections);
        Assert.Equal(FakeLocalization.English().Format("Dialog_AlignToRecording_ShowSections", 4), vm.SectionsToggleText);
        Assert.Equal(4, vm.Sections.Count);
        Assert.All(vm.Sections, s => Assert.False(s.IsApplied));
        Assert.False(vm.ApplyCommand.CanExecute(null));

        vm.ShowSections = true;
        Assert.True(vm.ShowSections);
        vm.Sections[0].IsApplied = true;
        Assert.True(vm.ApplyCommand.CanExecute(null));

        vm.IsFromBar = true;
        Assert.False(vm.IsWeakMatch);
        Assert.False(vm.ShowSections);
    }

    [Fact]
    public async Task AMatchingRecording_ShowsItsRows_AndIsNotAWeakMatch()
    {
        var (_, vm) = Open(new RecordingAlignmentFixture());

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.AnalyzeCommand).ExecuteAsync(null);

        Assert.False(vm.IsWeakMatch);
        Assert.True(vm.ShowSections);
        Assert.False(vm.StatusIsProblem);
    }

    [Fact]
    public void Conforms_to_its_contract() =>
        Assert.Empty(ContractConformanceTests.Violations(typeof(IAlignToRecordingScreen), Open(new RecordingAlignmentFixture()).Vm));

    [Fact]
    public void TheOffer_AppearsAfterAnImport_OnlyWhenTheAlignmentCouldRun()
    {
        var (h, _, audio) = RecordingAlignmentProject.Build(new RecordingAlignmentFixture(), _dir);
        var logger = new FakeAppLogger();

        Assert.False(new AlignToRecordingOffer(new DialogsSlot(), h.State, new Playback(""), FakeLocalization.English(), logger)
            .Offer("Guitar Pro import"));
        Assert.Null(h.State.MainDisplayAction);

        Assert.True(new AlignToRecordingOffer(new DialogsSlot(), h.State, new Playback(audio), FakeLocalization.English(), logger)
            .Offer("Guitar Pro import"));
        Assert.Equal(FakeLocalization.English()["Status_AlignToRecordingOffer"], h.State.MainDisplayMessage);
        Assert.Equal(FakeLocalization.English()["Action_AlignToRecording"], h.State.MainDisplayAction!.Label);

        h.Project.IsBeatMapLocked = true;
        Assert.False(new AlignToRecordingOffer(new DialogsSlot(), h.State, new Playback(audio), FakeLocalization.English(), logger)
            .Offer("Guitar Pro import"));

        h.Project.IsBeatMapLocked = false;
        h.Project.Arrangements[0].Levels[0].Notes.Clear();
        Assert.False(new AlignToRecordingOffer(new DialogsSlot(), h.State, new Playback(audio), FakeLocalization.English(), logger)
            .Offer("Guitar Pro import"));
    }

    private (ChartEditHarness Harness, AlignToRecordingViewModel Vm) Open(RecordingAlignmentFixture fx)
    {
        var (h, service, audio) = RecordingAlignmentProject.Build(fx, _dir);
        return (h, new AlignToRecordingViewModel(h.State, service, new Playback(audio), Basics()));
    }

    private static DialogBasics Basics() => new(FakeLocalization.English(), new FakeAppLogger());

    /// <summary>An analysis in which every section has notes and none reaches the confidence threshold:
    ///     the wrong audio, or a tab in another key.</summary>
    private sealed class NothingMatches : IRecordingAlignmentService
    {
        public Task<RecordingAlignmentAnalysis> AnalyzeAsync(ChartProject project, RecordingAlignmentRange range, bool keepAnchors,
            string? audioPath, CancellationToken cancellationToken) =>
            AnalyzeAsync(project, range, keepAnchors, audioPath, cancellationToken, null);

        public Task<RecordingAlignmentAnalysis> AnalyzeAsync(ChartProject project, RecordingAlignmentRange range, bool keepAnchors,
            string? audioPath, CancellationToken cancellationToken, string? projectPath)
        {
            var plan = RecordingAlignmentPlan.Build(project, range, keepAnchors);
            var sections = plan.Sections
                .Select((span, i) => new RecordingAlignmentSectionResult(i, span, 0.2, 31, 40, span.EndBeat - span.StartBeat))
                .ToList();
            return Task.FromResult(new RecordingAlignmentAnalysis(
                new RecordingAlignmentProposal(plan, plan.OldTimes, sections, RecordingAlignmentSource.Mix), RecordingAlignmentFailure.None));
        }

        public RecordingAlignmentApplyStatus Apply(ChartProject project, RecordingAlignmentProposal proposal,
            IReadOnlySet<int> acceptedSections) => RecordingAlignmentApplyStatus.NoChange;
    }

    /// <summary>The fake engine with a song "loaded": re-implements the one member the dialog reads.</summary>
    private sealed class Playback(string path) : FakeAudioPlaybackService, IPlaybackTransport
    {
        public string CurrentAudioPath => path;
    }
}
