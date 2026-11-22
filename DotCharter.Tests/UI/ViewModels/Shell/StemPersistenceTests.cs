using System.Reflection;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Session;
using DotCharter.Tests.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI.ViewModels.Shell;

public sealed class StemPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisteringStems_IsDirtyBeforeSaving_AndWaitsForTheSaveResult(bool throws)
    {
        var project = new ChartProject();
        project.Stems.Add(new Stem { Name = "guitar", Path = "old.wav" });
        var state = new ProjectState { ActiveProject = project };
        var session = new RecordingSession();
        var logger = new FakeAppLogger();
        var ui = new GatedInlineDispatcher();
        using var mixer = new StemMixerViewModel(new ProjectState(), new FakeAudioPlaybackService(),
            FakeLocalization.KeysOnly(), ui: ui);
        var vm = new StemSeparationViewModel(state, session, mixer, null!, ui, logger, FakeLocalization.KeysOnly());

        var registration = Register(vm, project, Path.Combine(Path.GetTempPath(), "stem-persist", "song.dcproj"));
        await session.SaveStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var wasAwaited = !registration.IsCompleted;
        if (throws) session.SaveResult.SetException(new IOException("save refused"));
        else session.SaveResult.SetResult(false);
        await registration;

        Assert.True(session.DirtyAtSave);
        Assert.Equal(SaveInitiator.Unattended, session.Initiator);
        Assert.True(wasAwaited, "Stem registration completed before the save answered.");
        Assert.True(session.IsProjectDirty);
        Assert.Contains(logger.Snapshot(), entry => entry.Level == "WARN" &&
            entry.Message.Contains("auto-save", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(StatusSeverity.Warning, state.MainDisplaySeverity);
        Assert.EndsWith("new.wav", project.Stems[0].Path, StringComparison.Ordinal);
        await StemMixerSettle.Async(() => !mixer.IsRebuilding);
    }

    [Fact]
    public async Task AnUnsavedSession_KeepsTheSeparatedStemsDirtyWithoutSaving()
    {
        var project = new ChartProject();
        var state = new ProjectState { ActiveProject = project };
        var session = new RecordingSession();
        var ui = new GatedInlineDispatcher();
        using var mixer = new StemMixerViewModel(new ProjectState(), new FakeAudioPlaybackService(),
            FakeLocalization.KeysOnly(), ui: ui);
        var vm = new StemSeparationViewModel(state, session, mixer, null!, ui,
            new FakeAppLogger(), FakeLocalization.KeysOnly());

        await Register(vm, project, string.Empty);

        Assert.True(session.IsProjectDirty);
        Assert.False(session.SaveStarted.Task.IsCompleted);
        Assert.Single(project.Stems);
    }

    private static Task Register(StemSeparationViewModel vm, ChartProject project, string projectPath) =>
        (Task)typeof(StemSeparationViewModel).GetMethod("RegisterAndPersistStemsAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm,
            [project, new StemTrack[] { new() { Name = "guitar", Path = Path.Combine(Path.GetTempPath(), "new.wav") } },
                Path.GetTempPath(), projectPath, 0, CancellationToken.None])!;

    private sealed class RecordingSession : IProjectSessionService
    {
        public AppConfig AppConfig { get; } = new();
        public bool IsProjectDirty { get; private set; }
        public bool DirtyAtSave { get; private set; }
        public SaveInitiator? Initiator { get; private set; }
        public TaskCompletionSource SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SaveResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<Task<bool>>? ConfirmDiscardAsync { get; set; }
        public event Action? ProjectDirtyChanged { add { } remove { } }
        public event Action? RecentProjectsChanged { add { } remove { } }
        public void MarkProjectDirty() => IsProjectDirty = true;
        public void MarkProjectClean() => IsProjectDirty = false;
        public bool HasUnsavedWork() => IsProjectDirty;
        public Task<bool> SaveProjectAsync(SaveInitiator initiator)
        {
            DirtyAtSave = IsProjectDirty;
            Initiator = initiator;
            SaveStarted.SetResult();
            return SaveResult.Task;
        }
        public Task<bool> MayReplaceOpenProjectAsync() => Task.FromResult(true);
        public void Initialize() { }
        public void LoadAppConfig() { }
        public void SaveAppConfig() { }
        public void FreeAudio() { }
        public Task LoadFile(string filePath) => Task.CompletedTask;
        public Task LoadAudioFile(string filePath, bool generateBeats = true, bool preferMemoryPlayback = false) => Task.CompletedTask;
        public Task<bool> AttachAudioAsync(string sourcePath) => Task.FromResult(false);
        public Task LoadSilentTransportAsync(double projectLengthMs) => Task.CompletedTask;
        public Task OpenProject() => Task.CompletedTask;
        public Task<bool> SaveProjectAsAsync() => Task.FromResult(false);
        public IReadOnlyList<RecentProjectEntry> GetRecentProjects() => [];
        public void Dispose() { }
    }
}
