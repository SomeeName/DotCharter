using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Editing.MultiTrack;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Export;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Infrastructure.Recovery;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Session;

namespace DotCharter.Tests.Services.Session;

public sealed class ProjectSessionViewRestoreTests : IDisposable
{
    private readonly string _tempDir =
        Directory.CreateTempSubdirectory("dotcharter-viewrestore-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    private sealed class ViewStubDocumentService(ChartProject toLoad, bool throwOnLoad = false)
        : IProjectDocumentService
    {
        public ChartProject LoadChart(string filePath)
            => throwOnLoad ? throw new InvalidDataException("unreadable chart") : toLoad;

        public void SaveChart(string filePath, ChartProject project, double? audioLengthMs = null) { }

        public void RefreshArrangementFileManifest(ChartProject project) { }

        public void SaveDlcBuilderXmlBundle(string projectPath, ChartProject project,
            double? audioLengthMs = null)
        { }

        public ChartProject AssembleProject(ChartProject? baseProject, double bpm, string audioFileName,
            IEnumerable<LyricPhrase> lyrics) => baseProject ?? new ChartProject();

        public ChartProject CreateNewProject(string title, string artist, string audioFileName)
            => new() { AudioFileName = audioFileName };
    }

    private sealed class ViewNoOpCrashRecoveryService : ICrashRecoveryService
    {
        /// <summary>No store behind this fake, so nothing counts as an internal
        /// snapshot. Settable for the test that needs a real recovery root.</summary>
        public string SnapshotRootDirectory { get; set; } = string.Empty;

        public void Start() { }
        public IReadOnlyList<RecoverableSession> FindRecoverableSessions() => [];
        public void DiscardSession(RecoverableSession session) { }
        public void NotifyProjectSaved(string projectPath) { }
        public void NotifyProjectSwitched() { }
        public void NotifyCleanShutdown() { }
        public Func<bool>? HasUnsavedChanges { get; set; }
        public void NotifyProjectChanged() { }
        public void Dispose() { }
    }

    private sealed class ViewNoOpSaveCompanionService : ISaveCompanionService
    {
        public string? WriteEof(string projectPath, ChartProject snapshot) => null;

        public string? WriteDlcBuilderProject(
            string projectPath, ChartProject snapshot, double? audioLengthMs) => null;

        public Task<string?> BuildPsarcAsync(string projectPath, ChartProject snapshot,
            double? audioLengthMs, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    /// <summary>
    ///     The PREVIOUS session: a library-style project with audio, open and
    ///     clean, exactly as a normal open leaves things. The project the next
    ///     <c>LoadFile</c> will hand back is fixed up front.
    /// </summary>
    private sealed class ViewHarness
    {
        public ViewHarness(ChartProject nextProject, bool throwOnLoad = false,
            string recoveryRoot = "")
        {
            var logger = new FakeAppLogger();
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), State, Config, FakeLocalization.KeysOnly());
            var selection = new SelectionService();
            var history = new HistoryService(logger);
            var editor = new ChartEditorViewModel(
                State, transport, history, selection,
                new TempoService(logger), new TrackContextRegistry(State, selection), FakeLocalization.KeysOnly());

            var previous = new ChartProject { AudioFileName = "previous-song.wav" };
            previous.Arrangements.Add(new Arrangement { Name = "Lead Guitar" });
            editor.ApplyProject(previous, resetHistory: true);
            State.CurrentProjectPath = Path.Combine(Path.GetTempPath(), "Previous", "project.rscp");
            State.CurrentAudioFileName = previous.AudioFileName;
            // What SongBrowserViewModel.LoadSongItem leaves behind after a library
            // song — the only writer of this field.
            State.CurrentSongIdentity = "lib:PreviousSongKey";

            Session = new ProjectSessionService(
                Audio, Config, logger, Storage,
                new ViewStubDocumentService(nextProject, throwOnLoad), State, transport, editor,
                new TempoService(logger),
                new ViewNoOpCrashRecoveryService { SnapshotRootDirectory = recoveryRoot }, history,
                new ViewNoOpSaveCompanionService(), statusBar: StatusBar,
                uiDispatcher: new TestUiDispatcher(),
                localization: FakeLocalization.English(),
                pathRekey: new FakeProjectPathRekey(),
                viewMemory: new ProjectViewMemory(Config, State, new GhostArrangementsService(),
                    new StackedArrangementsService()));
        }

        public FakeAudioPlaybackService Audio { get; } = new();
        public ProjectState State { get; } = new();
        public FakeConfigurationService Config { get; } = new();
        public RecordingStatusBarService StatusBar { get; } = new();
        public FakeFileStorageService Storage { get; } = new();
        public ProjectSessionService Session { get; }
    }

    private static ChartProject ThreeArrangements()
    {
        var project = new ChartProject { AudioFileName = "" };
        foreach (var name in new[] { "Lead", "Rhythm", "Bass" }) project.Arrangements.Add(new Arrangement { Name = name });
        project.Chart = new ChartData { SongName = "Next Song" };
        return project;
    }

    /// <summary>
    ///     The wiring line in <c>LoadAudioAndRestoreViewAsync</c>: opening a project
    ///     asks the view memory to put back the active arrangement and the playhead
    ///     the owner left there. Remove that call and the reopened project opens on
    ///     its first arrangement at 0.
    /// </summary>
    [Fact]
    public async Task LoadFile_RestoresTheSavedActiveArrangementAndPlayhead()
    {
        var project = ThreeArrangements();
        var h = new ViewHarness(project);
        var dir = Path.Combine(_tempDir, "Song");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "project.rscp");
        File.WriteAllText(path, "");
        project.AudioFileName = "song.ogg";
        File.WriteAllText(Path.Combine(dir, project.AudioFileName), "audio");

        // The owner's view of this project is on file (a previous session wrote it).
        var saved = new ProjectViewState { ActiveArrangementName = "Bass", ActiveArrangementIndex = 2, PlayheadMs = 12_345 };
        h.Config.AppConfig.ProjectViews = new Dictionary<string, ProjectViewState> { [ProjectViewMemory.KeyFor(path)] = saved };
        h.Audio.TotalAudioLengthMs = 60_000;

        await h.Session.LoadFile(path);

        Assert.Equal("Bass", h.State.ActiveArrangement?.Name);
        Assert.Equal(12_345, h.State.CurrentTimeMs);
    }
}
