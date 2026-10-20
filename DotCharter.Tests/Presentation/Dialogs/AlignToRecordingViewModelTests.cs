using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
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
    }

    private (ChartEditHarness Harness, AlignToRecordingViewModel Vm) Open(RecordingAlignmentFixture fx)
    {
        var (h, service, audio) = RecordingAlignmentProject.Build(fx, _dir);
        return (h, new AlignToRecordingViewModel(h.State, service, new Playback(audio), Basics()));
    }

    private static DialogBasics Basics() => new(FakeLocalization.English(), new FakeAppLogger());

    /// <summary>The fake engine with a song "loaded": re-implements the one member the dialog reads.</summary>
    private sealed class Playback(string path) : FakeAudioPlaybackService, IPlaybackTransport
    {
        public string CurrentAudioPath => path;
    }
}
