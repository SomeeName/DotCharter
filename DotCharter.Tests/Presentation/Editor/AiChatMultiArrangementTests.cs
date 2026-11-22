using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Presentation.Screens.Editor;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Ai.Chat;
using DotCharter.Services.Editing.Selection;
using DotCharter.Tone.Ai;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Presentation.Editor;

/// <summary>#478: the arrangement picker is a multi-select with "All arrangements"; one Send runs the task on every checked arrangement.</summary>
public sealed class AiChatMultiArrangementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DotCharterTests", "ai-chat-multi-" + Guid.NewGuid().ToString("N"));
    private readonly FakeConfigurationService _config = new();
    private readonly FakeLocalization _loc = FakeLocalization.English();
    private readonly SelectionService _selection = new();
    private readonly ProjectState _state = new();
    private readonly List<AiChatInstructionRequest> _requests = [];
    private readonly ChartProject _project = new();

    public AiChatMultiArrangementTests()
    {
        Directory.CreateDirectory(_root);
        _config.AppConfig.Ai = new AiProviderSettings(AiProviderKind.ClaudeCli, "claude-sonnet-5-5", null);
        _project.Chart.SongName = "Song";
        foreach (var (name, notes, chords) in new[] { ("Lead", 2, 1), ("Rhythm", 3, 2), ("Bass", 1, 0) })
        {
            var arrangement = new Arrangement { Name = name };
            var level = new ChartLevel();
            for (var i = 0; i < notes; i++) level.Notes.Add(new GuitarNote { StringIndex = 1, Fret = 5 + i });
            for (var i = 0; i < chords; i++) level.Chords.Add(new Chord());
            arrangement.Levels.Add(level);
            _project.Arrangements.Add(arrangement);
        }

        _state.CurrentProjectPath = Path.Combine(_root, "project.rscp");
        _state.ActiveProject = _project;
        _state.ActiveArrangement = _project.Arrangements[0];
    }

    public void Dispose() => BestEffortDelete.Directory(_root);

    private static IAiChatArrangementChoice Row(AiChatViewModel vm, string label) => vm.ArrangementPicker.Choices.Single(c => c.Label == label);

    private AiChatViewModel Build()
    {
        var time = new TestTime();
        var store = new AiChatHistory(new ProjectStateAiChatChartSource(_state), time);
        var vm = new AiChatViewModel(_config, _state, _selection, new AiChatServices(store, new RecordingActions(_requests),
            new FakeAppLogger(), time), new TestUiDispatcher(), _loc);
        vm.SelectedScope = vm.Scopes[2];
        return vm;
    }

    private static async Task Send(AiChatViewModel vm, string text)
    {
        vm.InputText = text;
        await ((IAsyncRelayCommand)vm.SendCommand).ExecuteAsync(null);
    }

    [Fact]
    public void ThePicker_ListsAllFirst_AndDefaultsToTheActiveArrangement()
    {
        var vm = Build();

        Assert.Equal([_loc["AiChat_AllArrangements"], "Lead", "Rhythm", "Bass"], vm.ArrangementPicker.Choices.Select(c => c.Label));
        Assert.True(((AiChatArrangementChoice)vm.ArrangementPicker.Choices[0]).IsAll);
        Assert.Equal([false, true, false, false], vm.ArrangementPicker.Choices.Select(c => c.IsChecked));
        Assert.Equal("Lead", vm.ArrangementPicker.PickerText);
        Assert.Equal("Lead: 2 notes · 1 chord", vm.ScopeSummary);
    }

    [Fact]
    public void Checking_SeveralArrangements_UpdatesThePickerText_AndTheSummaryCoversThem()
    {
        var vm = Build();

        Row(vm, "Rhythm").IsChecked = true;

        Assert.Equal(_loc.Format("AiChat_ArrangementsN", 2), vm.ArrangementPicker.PickerText);
        Assert.Equal("2 arrangements: 5 notes · 3 chords", vm.ScopeSummary);
        Assert.False(vm.ArrangementPicker.Choices[0].IsChecked);
    }

    [Fact]
    public void All_ChecksEveryArrangement_AndUncheckingItFallsBackToTheActiveOne()
    {
        var vm = Build();

        vm.ArrangementPicker.Choices[0].IsChecked = true;
        Assert.All(vm.ArrangementPicker.Choices, c => Assert.True(c.IsChecked));
        Assert.Equal(_loc["AiChat_AllArrangements"], vm.ArrangementPicker.PickerText);
        Assert.Equal("3 arrangements: 6 notes · 3 chords", vm.ScopeSummary);

        vm.ArrangementPicker.Choices[0].IsChecked = false;
        Assert.Equal([false, true, false, false], vm.ArrangementPicker.Choices.Select(c => c.IsChecked));
    }

    [Fact]
    public void TheLastCheckedArrangement_CannotBeUnchecked()
    {
        var vm = Build();

        Row(vm, "Lead").IsChecked = false;

        Assert.True(Row(vm, "Lead").IsChecked);
    }

    [Fact]
    public async Task Send_RunsOneRequestWithAScopePerCheckedArrangement_InChartOrder()
    {
        var vm = Build();
        Row(vm, "Bass").IsChecked = true;
        Row(vm, "Rhythm").IsChecked = true;

        await Send(vm, "Use 1-3 for the octaves");

        var request = Assert.Single(_requests);
        Assert.Equal(["Lead", "Rhythm", "Bass"],
            new[] { request.Scope }.Concat(request.MoreScopes).Select(s => s.Arrangement!.Name));
        Assert.All(request.MoreScopes.Append(request.Scope), s => Assert.Equal(AiChatScopeKind.Arrangement, s.Kind));
    }

    [Fact]
    public async Task Send_WithTheDefault_IsOneScope_OnTheActiveArrangement()
    {
        var vm = Build();

        await Send(vm, "Use 1-3 for the octaves");

        var request = Assert.Single(_requests);
        Assert.Same(_project.Arrangements[0], request.Scope.Arrangement);
        Assert.Empty(request.MoreScopes);
    }

    [Fact]
    public async Task ASelectionScope_ReachesOnlyOneArrangement_AndTheSummarySaysSo()
    {
        var vm = Build();
        _selection.SelectedNotes.Add(_project.Arrangements[0].Levels[0].Notes[0]);
        vm.SelectedScope = vm.Scopes[0];
        Row(vm, "Rhythm").IsChecked = true;

        Assert.Equal(_loc.Format("AiChat_Summary_SelectionOnly", "1 note · 0 chords selected", "Lead"), vm.ScopeSummary);
        await Send(vm, "Use 1-3 for the octaves");

        var request = Assert.Single(_requests);
        Assert.Same(_project.Arrangements[0], request.Scope.Arrangement);
        Assert.Empty(request.MoreScopes);
    }

    [Fact]
    public void ChoosingAnArrangementTheOldWay_ChecksOnlyIt()
    {
        var vm = Build();
        Row(vm, "Rhythm").IsChecked = true;

        vm.SelectedArrangement = vm.Arrangements.Single(a => a.Label == "Bass");

        Assert.Equal([false, false, false, true], vm.ArrangementPicker.Choices.Select(c => c.IsChecked));
        Assert.Equal("Bass", vm.ArrangementPicker.PickerText);
    }

    [Fact]
    public void SwitchingTheActiveArrangement_ResetsThePickerToIt()
    {
        var vm = Build();
        vm.ArrangementPicker.Choices[0].IsChecked = true;

        _state.ActiveArrangement = _project.Arrangements[2];

        Assert.Equal([false, false, false, true], vm.ArrangementPicker.Choices.Select(c => c.IsChecked));
    }

    private sealed class TestTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class RecordingActions(List<AiChatInstructionRequest> requests) : IAiChatChartActions
    {
        public bool IsRunning => false;

        public event EventHandler<AiChatApplied>? Applied
        {
            add { }
            remove { }
        }

        public Task<AiChatInstructionReply> RunInstructionAsync(AiChatInstructionRequest request, CancellationToken ct)
        {
            requests.Add(request);
            return Task.FromResult(new AiChatInstructionReply(AiChartRunStatus.Answered, "ok", null, null));
        }
    }
}
