using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Infrastructure.Recovery;
using DotCharter.Services.Session;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     The crash-recovery dialog's footer contract: rows are PICKED, the first
///     is pre-selected on open, and the ONE primary ("Restore selected",
///     IsDefault in markup) acts on the pick, so Enter gets the newest crash
///     back. Esc is "Ask later" and keeps every file.
/// </summary>
public sealed class RecoveryViewModelTests
{
    private sealed class FakeCrashRecoveryService : ICrashRecoveryService
    {
        /// <summary>No store behind this fake, so nothing counts as an internal
        /// snapshot. Settable for the test that needs a real recovery root.</summary>
        public string SnapshotRootDirectory { get; set; } = string.Empty;

        public List<RecoverableSession> Discarded { get; } = [];
        public IReadOnlyList<RecoverableSession> Sessions { get; set; } = [];

        public void Start() { }
        public IReadOnlyList<RecoverableSession> FindRecoverableSessions() => Sessions;
        public void DiscardSession(RecoverableSession session) => Discarded.Add(session);
        public void NotifyProjectSaved(string projectPath) { }
        public void NotifyProjectSwitched() { }
        public void NotifyCleanShutdown() { }
        public Func<bool>? HasUnsavedChanges { get; set; }
        public void NotifyProjectChanged() { }
        public void Dispose() { }
    }

    /// <summary>Loads succeed: the session service reports the snapshot as the
    /// open project, which is how the VM observes a successful recovery.</summary>
    private sealed class FakeProjectSessionService : IProjectSessionService
    {
        private readonly ProjectState _state;
        public FakeProjectSessionService(ProjectState state) => _state = state;

        public int DirtyMarks { get; private set; }

        public AppConfig AppConfig { get; } = new();
        public void Initialize() { }
        public void LoadAppConfig() { }
        public void SaveAppConfig() { }
        public void FreeAudio() { }

        public Task LoadFile(string filePath)
        {
            _state.CurrentProjectPath = filePath;
            return Task.CompletedTask;
        }

        public Task LoadAudioFile(string filePath, bool generateBeats = true,
            bool preferMemoryPlayback = false) => Task.CompletedTask;

        public Task<bool> AttachAudioAsync(string sourcePath) => Task.FromResult(true);
        public Task LoadSilentTransportAsync(double projectLengthMs) => Task.CompletedTask;
        public Task OpenProject() => Task.CompletedTask;
        public Task<bool> SaveProjectAsync() => Task.FromResult(true);
        public Task<bool> SaveProjectAsAsync() => Task.FromResult(true);
        public IReadOnlyList<RecentProjectEntry> GetRecentProjects() => [];
        public event Action? RecentProjectsChanged { add { } remove { } }
        public bool IsProjectDirty => false;
        public Func<Task<bool>>? ConfirmDiscardAsync { get; set; }
        public Task<bool> MayReplaceOpenProjectAsync() => Task.FromResult(true);
        public void MarkProjectClean() { }
        public void MarkProjectDirty() => DirtyMarks++;
        public event Action? ProjectDirtyChanged { add { } remove { } }
        public void Dispose() { }
    }

    private static RecoverableSession Session(string name, string originalPath = @"C:\charts\x.rscp") => new()
    {
        SnapshotPath = $@"C:\recovery\{name}.rscp",
        SidecarPath = $@"C:\recovery\{name}.rscp.json",
        Metadata = new RecoverySnapshotMetadata
        {
            OriginalProjectPath = originalPath,
            SongName = name,
            TimestampUtc = DateTimeOffset.UtcNow
        }
    };

    /// <summary>Opened the way the launcher opens it: over the sessions the crash check found.</summary>
    private static (RecoveryViewModel vm, FakeCrashRecoveryService crash, FakeProjectSessionService session, ProjectState state)
        Build(params RecoverableSession[] sessions)
    {
        var state = new ProjectState();
        var crash = new FakeCrashRecoveryService { Sessions = sessions };
        var session = new FakeProjectSessionService(state);
        var vm = new RecoveryViewModel(crash, session, state, FakeLocalization.English(), new FakeAppLogger());
        vm.Configure(new RecoveryRequest(sessions));
        return (vm, crash, session, state);
    }

    private static Task Recover(RecoveryViewModel vm) => ((IAsyncRelayCommand)vm.RecoverSelectedCommand).ExecuteAsync(null);

    [Fact]
    public void Open_PreselectsTheFirstSession_SoEnterHasATarget()
    {
        var (vm, _, _, _) = Build(Session("a"), Session("b"));

        Assert.False(vm.Result.IsCompleted);
        Assert.Same(vm.Sessions[0], vm.SelectedSession);
        Assert.True(vm.RecoverSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task RecoverSelected_RestoresThePick_AndMovesSelectionOn()
    {
        var (vm, _, session, state) = Build(Session("a", @"C:\charts\a.rscp"), Session("b"));

        await Recover(vm);

        // The recovered row is gone, the pick moved to the next one, Save
        // points at the ORIGINAL file and the session is dirty (the original
        // does not contain the recovered work).
        Assert.Single(vm.Sessions);
        Assert.Same(vm.Sessions[0], vm.SelectedSession);
        Assert.Equal(@"C:\charts\a.rscp", state.CurrentProjectPath);
        Assert.Equal(1, session.DirtyMarks);
        Assert.False(vm.Result.IsCompleted, "one session is left — the dialog must stay open for it");
    }

    [Fact]
    public void DiscardingTheSelectedRow_MovesSelectionToTheFirstRemaining()
    {
        var (vm, crash, _, _) = Build(Session("a"), Session("b"));

        vm.DiscardCommand.Execute(vm.Sessions[0]);

        Assert.Single(crash.Discarded);
        Assert.Same(vm.Sessions[0], vm.SelectedSession);
        Assert.True(vm.RecoverSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task DiscardAll_DiscardsEverything_ClearsThePick_AndCloses()
    {
        var (vm, crash, _, _) = Build(Session("a"), Session("b"));

        vm.DiscardAllCommand.Execute(null);

        Assert.Equal(2, crash.Discarded.Count);
        Assert.Empty(vm.Sessions);
        Assert.Null(vm.SelectedSession);
        Assert.True(vm.Result.IsCompleted, "nothing left: the dialog must close");
        Assert.True(await vm.Result);
        Assert.False(vm.RecoverSelectedCommand.CanExecute(null),
            "with nothing selected the primary must not be pressable");
    }

    [Fact]
    public async Task Esc_AsksLater_KeepsEveryFile_AndAnswersFalse()
    {
        var (vm, crash, _, _) = Build(Session("a"), Session("b"));

        Assert.True(vm.TryCancel());

        Assert.Empty(crash.Discarded);
        Assert.True(vm.Result.IsCompleted);
        Assert.False(await vm.Result);
    }

    [Fact]
    public void Conforms_to_its_contract()
    {
        var (vm, _, _, _) = Build(Session("a"), Session("b"));
        Assert.Empty(ContractConformanceTests.Violations(typeof(IRecoveryScreen), vm));
    }
}
