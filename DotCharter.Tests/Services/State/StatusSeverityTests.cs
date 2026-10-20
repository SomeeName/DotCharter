using DotCharter.Tone.Audio;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Application;

namespace DotCharter.Tests.Services.State;

/// <summary>
///     The status line carries a SEVERITY beside its text since the failure
///     paths were made distinguishable from success. These pin the contract the
///     ~225 writers rely on: a plain assignment is Info, a levelled write raises
///     level-before-text, an Error stays lit until the NEXT write (there is no
///     auto-clear anywhere in the state), and the status-bar view model's class
///     flags follow the state.
/// </summary>
public sealed class StatusSeverityTests
{
    [Fact]
    public void PlainAssignment_IsInfo_AndResetsAPreviousError()
    {
        var state = new ProjectState();
        state.SetMessage("Save failed", StatusSeverity.Error);
        Assert.Equal(StatusSeverity.Error, state.MainDisplaySeverity);

        state.MainDisplayMessage = "Loaded chart";

        Assert.Equal("Loaded chart", state.MainDisplayMessage);
        Assert.Equal(StatusSeverity.Info, state.MainDisplaySeverity);
    }

    [Fact]
    public void OneArgumentSetMessage_IsInfo()
    {
        var state = new ProjectState();
        state.SetMessage("boom", StatusSeverity.Error);
        state.SetMessage("fine");
        Assert.Equal(StatusSeverity.Info, state.MainDisplaySeverity);
    }

    [Fact]
    public void LevelledWrite_RaisesSeverityBeforeMessage_AndNothingWhenUnchanged()
    {
        var state = new ProjectState();
        var raised = new List<string>();
        state.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        state.SetMessage("Export failed: disk full", StatusSeverity.Error);

        Assert.Equal(
            [nameof(ProjectState.MainDisplaySeverity), nameof(ProjectState.MainDisplayMessage)],
            raised.Where(n => n.StartsWith("MainDisplay", StringComparison.Ordinal)).ToList());

        raised.Clear();
        state.SetMessage("Export failed: disk full", StatusSeverity.Error);
        Assert.DoesNotContain(raised, n => n.StartsWith("MainDisplay", StringComparison.Ordinal));

        // Same text, different level: the level still moves (a retry that fails
        // the same way after a success must light up again).
        raised.Clear();
        state.SetMessage("Export failed: disk full", StatusSeverity.Warning);
        Assert.Equal([nameof(ProjectState.MainDisplaySeverity)],
            raised.Where(n => n.StartsWith("MainDisplay", StringComparison.Ordinal)).ToList());
    }

    /// <summary>
    ///     An Error PERSISTS: nothing on the state or in the view model fades it
    ///     on a timer. It goes away only when the next writer replaces the line
    ///     — which is what makes a failed save impossible to miss.
    /// </summary>
    [Fact]
    public void Error_StaysUntilTheNextWrite()
    {
        var state = new ProjectState();
        state.SetMessage("Save failed", StatusSeverity.Error);

        // Anything else on the state may change underneath.
        state.PixelsPerMs++;

        Assert.Equal(StatusSeverity.Error, state.MainDisplaySeverity);
        Assert.Equal("Save failed", state.MainDisplayMessage);

        state.SetMessage("Project saved", StatusSeverity.Success);
        Assert.Equal(StatusSeverity.Success, state.MainDisplaySeverity);
    }

    [Fact]
    public void StatusBarViewModel_ClassFlagsFollowTheState()
    {
        var state = new ProjectState();
        var dispatcher = new InlineDispatcher();
        using var vm = new StatusBarViewModel(
            new StatusBarService(dispatcher), state, new NullAudioPlaybackService(),
            dispatcher, new StubProfiles(), new FakeFileStorageService(), FakeLocalization.English());

        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        Assert.False(vm.HasMainMessageGlyph);
        Assert.False(vm.IsMainMessageError);

        state.SetMessage("Save failed", StatusSeverity.Error);
        Assert.True(vm.IsMainMessageError);
        Assert.False(vm.IsMainMessageWarning);
        Assert.False(vm.IsMainMessageSuccess);
        Assert.True(vm.HasMainMessageGlyph);
        Assert.Equal("Save failed", vm.MainMessage);
        Assert.Contains(nameof(StatusBarViewModel.IsMainMessageError), raised);
        Assert.Contains(nameof(StatusBarViewModel.HasMainMessageGlyph), raised);
        Assert.Contains(nameof(StatusBarViewModel.MainMessage), raised);

        state.SetMessage("Project saved", StatusSeverity.Success);
        Assert.False(vm.IsMainMessageError);
        Assert.True(vm.IsMainMessageSuccess);

        state.SetMessage("Partial", StatusSeverity.Warning);
        Assert.True(vm.IsMainMessageWarning);
        Assert.False(vm.IsMainMessageSuccess);

        state.MainDisplayMessage = "Loaded";
        Assert.False(vm.HasMainMessageGlyph);
        Assert.Equal(StatusSeverity.Info, vm.MainMessageSeverity);
    }

    /// <summary>The rail kept "Loaded Chart: project.rscp" for a whole session (2026-09-23). Routine
    /// news steps aside after a few seconds; a warning or an error stays because it still needs the user.</summary>
    [Fact]
    public void RoutineNews_LeavesTheRail_ButAProblemStays()
    {
        var state = new ProjectState();
        var dispatcher = new InlineDispatcher();
        using var vm = new StatusBarViewModel(
            new StatusBarService(dispatcher), state, new NullAudioPlaybackService(),
            dispatcher, new StubProfiles(), new FakeFileStorageService(), FakeLocalization.English());
        var later = Environment.TickCount64 + StatusBarViewModel.MainMessageFadeMs + 1000;

        state.SetMessage("Loaded Chart: project.rscp", StatusSeverity.Info);
        vm.FadeMainMessage(Environment.TickCount64);
        Assert.Equal("Loaded Chart: project.rscp", vm.MainMessage);
        vm.FadeMainMessage(later);
        Assert.Equal("", vm.MainMessage);

        state.SetMessage("Project saved", StatusSeverity.Success);
        Assert.Equal("Project saved", vm.MainMessage);
        vm.FadeMainMessage(later);
        Assert.Equal("", vm.MainMessage);
        Assert.False(vm.HasMainMessageGlyph);

        state.SetMessage("Save failed", StatusSeverity.Error);
        vm.FadeMainMessage(later);
        Assert.Equal("Save failed", vm.MainMessage);
        Assert.True(vm.HasMainMessageGlyph);
    }

    /// <summary>#638: a message may carry one verb ("Show"). It belongs to that message: the rail
    /// offers it while the message is up, runs it on click, and drops it with the next write.</summary>
    [Fact]
    public void AMessagesVerb_IsOfferedWithIt_AndGoesWithTheNextWrite()
    {
        var state = new ProjectState();
        var dispatcher = new InlineDispatcher();
        using var vm = new StatusBarViewModel(
            new StatusBarService(dispatcher), state, new NullAudioPlaybackService(),
            dispatcher, new StubProfiles(), new FakeFileStorageService(), FakeLocalization.English());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var runs = 0;

        Assert.False(vm.HasMainMessageAction);
        state.SetMessage("EOF project written: a.eof — 3 things could not be kept exactly", StatusSeverity.Warning,
            new StatusMessageAction("Show", () => runs++));

        Assert.True(vm.HasMainMessageAction);
        Assert.Equal("Show", vm.MainMessageActionLabel);
        Assert.Contains(nameof(StatusBarViewModel.HasMainMessageAction), raised);
        vm.RunMainMessageActionCommand.Execute(null);
        Assert.Equal(1, runs);

        // The same text again WITHOUT a verb still takes the verb away.
        state.SetMessage("EOF project written: a.eof — 3 things could not be kept exactly", StatusSeverity.Warning);
        Assert.False(vm.HasMainMessageAction);
        Assert.Null(state.MainDisplayAction);

        state.SetMessage("x", StatusSeverity.Warning, new StatusMessageAction("Show", () => runs++));
        state.MainDisplayMessage = "Loaded";
        Assert.False(vm.HasMainMessageAction);
        vm.RunMainMessageActionCommand.Execute(null);
        Assert.Equal(1, runs);
    }

    /// <summary>Snap / selection / zoom and the transport are the editor's; on the Library and in
    /// Settings they were a line of meaningless editor state (review, 2026-09-23).</summary>
    [Fact]
    public void TheEditorReadouts_FollowTheSurface()
    {
        var dispatcher = new InlineDispatcher();
        using var vm = new StatusBarViewModel(
            new StatusBarService(dispatcher), new ProjectState(), new NullAudioPlaybackService(),
            dispatcher, new StubProfiles(), new FakeFileStorageService(), FakeLocalization.English());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.True(vm.IsEditorSurface, "a host without a shell must keep showing the readouts");
        vm.IsEditorSurface = false;
        Assert.False(vm.ShowsEditReadouts);
        Assert.Contains(nameof(StatusBarViewModel.ShowsEditReadouts), raised);

        var root = TestPaths.RepoRoot();
        var shell = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(root, "DotCharter.UI", "ViewModels", "Shell", "MainWindowViewModel.cs")));
        Assert.Contains("StatusBar.IsEditorSurface = value == ChartEditorTabIndex;", shell, StringComparison.Ordinal);
        var rail = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(root, "DotCharter.UI", "Views", "Shell", "StatusBar.axaml")));
        Assert.Contains("IsVisible=\"{Binding ShowsEditReadouts}\"", rail, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsEditorSurface}\"", rail, StringComparison.Ordinal);
        Assert.DoesNotContain("IsVisible=\"{Binding HasEditReadouts}\"", rail, StringComparison.Ordinal);
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public void PostNormal(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
    }

    private sealed class StubProfiles : IPlayerProfileService
    {
        private readonly PlayerProfile _active = new() { Id = Guid.NewGuid(), Name = "Ana" };
        public IReadOnlyList<PlayerProfile> Profiles => new[] { _active };
        public PlayerProfile ActiveProfile => _active;
        public event Action<PlayerProfile>? ActiveProfileChanged { add { } remove { } }
        public string GetDataDirectory(Guid id) => Path.Combine(Path.GetTempPath(), "dotcharter-status-tests", id.ToString("N"));
        public void InvalidateCache() { }
        public PlayerProfile Create(string name, string? accentColor = null) => _active;
        public void Rename(Guid id, string newName) { }
        public void SetLeftyFlip(Guid id, bool lefty) { }
        public void SetTuningMatch(Guid id, string myTuningId, bool playInMyTuning) { }
        public bool SwitchTo(Guid id) => false;
        public bool Delete(Guid id) => false;
        public bool SetAvatar(Guid id, string sourceImagePath) => false;
        public string? GetAvatarPath(Guid id) => null;
        public bool ExportProfile(Guid id, string zipPath) => false;
        public PlayerProfile? ImportProfile(string zipPath) => null;
    }
}
