using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Export;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Infrastructure.Recovery;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Session;

namespace DotCharter.Tests.Services.Session;

/// <summary>
///     Ctrl+S must cost the UI thread ONE clone and nothing else heavy.
///     <para>
///         <c>SaveProjectCoreAsync</c> used to run
///         <c>ChartSpacingEnforcer.EnforceProject</c> over the LIVE project, on
///         the UI thread, before taking the snapshot — on a big project that was
///         hundreds of milliseconds between the keypress and the next frame, for
///         a pass whose only consumer is the file being written. It now runs on
///         the save worker over the snapshot. These are STRUCTURE assertions
///         (which thread, which object), never a wall-clock budget — the timing is
///         logged, not asserted.
///     </para>
/// </summary>
public sealed class ProjectSessionSaveThreadingTests : IDisposable
{
    private readonly string _tempDir =
        Directory.CreateTempSubdirectory("dotcharter-savethreading-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    /// <summary>
    ///     The enforcer strips the RS display suffix <c>-arp</c> off a chord
    ///     template's name. If the LIVE project still carries it after the save
    ///     while the serialized snapshot does not, the pass ran over the snapshot
    ///     and not over the UI-thread object. (This used to watch the phrase-name
    ///     sanitizer, which #322 moved out of the model to the export boundary.)
    /// </summary>
    [Fact]
    public async Task Save_EnforcesTheSnapshotOnTheWorker_AndLeavesTheLiveProjectAlone()
    {
        var h = CreateHarness(out var projectPath, out var project);
        var live = project.Arrangements[0];
        live.ChordTemplates.Add(new ChordTemplate { Id = 0, Name = "Cmaj-arp" });

        ProjectSaver.ProjectSaveTiming? timing = null;
        h.Session.Saver.SaveTimed += t => timing = t;

        var (saved, callerThread) = await SaveOnADedicatedThreadAsync(h.Session, projectPath);
        Assert.True(saved);

        // The file got the enforced chart …
        var written = Assert.Single(h.Documents.SavedProjects);
        Assert.Equal("Cmaj", Assert.Single(written.Arrangements[0].ChordTemplates).Name);
        // … the live project was NOT touched by the pass on the way out …
        Assert.Equal("Cmaj-arp", Assert.Single(live.ChordTemplates).Name);
        // … and it was a snapshot, not the live graph, that was serialized.
        Assert.NotSame(live, written.Arrangements[0]);

        // Thread shape: the clone happened on the caller (the UI thread in the
        // app), the enforcer on a worker — the same worker the serializer ran on.
        Assert.NotNull(timing);
        Assert.Equal(callerThread, timing.Value.SnapshotThreadId);
        Assert.NotEqual(callerThread, timing.Value.EnforceThreadId);
        Assert.Equal(h.Documents.SaveChartThreadId, timing.Value.EnforceThreadId);
        Assert.NotEqual(callerThread, h.Documents.SaveChartThreadId);
    }

    /// <summary>
    ///     A save must not go stale on the way to the worker: the snapshot is what
    ///     the file gets, so an edit racing the write cannot reach it. Cheap to pin
    ///     next to the threading shape, and it is the reason the clone stays on the
    ///     UI thread at all.
    /// </summary>
    [Fact]
    public async Task Save_SerializesADeepSnapshot_NotTheLiveGraph()
    {
        var h = CreateHarness(out var projectPath, out var project);
        Assert.True(await h.Session.Saver.SaveProjectCoreAsync(projectPath));

        var written = Assert.Single(h.Documents.SavedProjects);
        Assert.NotSame(project, written);
        Assert.NotSame(project.Arrangements[0], written.Arrangements[0]);
    }

    // ---- harness --------------------------------------------------------------

    /// <summary>
    ///     Stands in for the UI thread: a dedicated, NON-pool thread. Everything
    ///     up to the first <c>await</c> inside the save runs on it synchronously,
    ///     and nothing queued to the thread pool can ever land on it — so
    ///     "ran on the caller" versus "ran on a worker" is a deterministic
    ///     comparison, which it would not be from a pool-hosted xunit thread that
    ///     the <c>Task.Run</c> body could be scheduled back onto.
    /// </summary>
    private static Task<(bool Saved, int CallerThreadId)> SaveOnADedicatedThreadAsync(
        ProjectSessionService session, string projectPath)
    {
        var tcs = new TaskCompletionSource<(bool, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var saved = session.Saver.SaveProjectCoreAsync(projectPath).GetAwaiter().GetResult();
                tcs.SetResult((saved, Environment.CurrentManagedThreadId));
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        })
        { IsBackground = true, Name = "fake-ui-thread" };
        thread.Start();
        return tcs.Task;
    }

    private Harness CreateHarness(out string projectPath, out ChartProject project)
    {
        var projectDir = Path.Combine(_tempDir, "Band - Song");
        Directory.CreateDirectory(projectDir);
        projectPath = Path.Combine(projectDir, "project.rscp");

        project = new ChartProject { AudioFileName = "song.ogg" };
        project.Arrangements.Add(new Arrangement { Name = "Lead Guitar" });
        return new Harness(project, projectPath);
    }

    private sealed class NoOpSaveCompanionService : ISaveCompanionService
    {
        public string? WriteEof(string projectPath, ChartProject snapshot) => null;

        public string? WriteDlcBuilderProject(
            string projectPath, ChartProject snapshot, double? audioLengthMs) => null;

        public Task<string?> BuildPsarcAsync(string projectPath, ChartProject snapshot,
            double? audioLengthMs, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class NoOpCrashRecoveryService : ICrashRecoveryService
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

    /// <summary>
    ///     Records WHAT was handed to the serializer and on WHICH thread; writes
    ///     a placeholder file so the atomic swap has something to swap.
    /// </summary>
    private sealed class RecordingDocumentService : IProjectDocumentService
    {
        public List<ChartProject> SavedProjects { get; } = [];
        public int SaveChartThreadId { get; private set; } = -1;

        public ChartProject LoadChart(string filePath) => throw new NotSupportedException();

        public void SaveChart(string filePath, ChartProject project, double? audioLengthMs = null)
        {
            SaveChartThreadId = Environment.CurrentManagedThreadId;
            SavedProjects.Add(project);
            File.WriteAllText(filePath, "<rscp />");
        }

        public void RefreshArrangementFileManifest(ChartProject project) { }

        public void SaveDlcBuilderXmlBundle(string projectPath, ChartProject project,
            double? audioLengthMs = null)
        { }

        public ChartProject AssembleProject(ChartProject? baseProject, double bpm, string audioFileName,
            IEnumerable<LyricPhrase> lyrics) => baseProject ?? new ChartProject();

        public ChartProject CreateNewProject(string title, string artist, string audioFileName)
            => new() { AudioFileName = audioFileName };
    }

    private sealed class Harness
    {
        public Harness(ChartProject project, string projectPath)
        {
            var logger = new FakeAppLogger();
            var state = new ProjectState();
            var config = new FakeConfigurationService();
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), state, config, FakeLocalization.KeysOnly());
            var selection = new SelectionService();
            var history = new HistoryService(logger);
            var editor = new ChartEditorViewModel(
                state, transport, history, selection,
                new TempoService(logger), new TrackContextRegistry(state, selection), FakeLocalization.KeysOnly());

            editor.ApplyProject(project, resetHistory: true);
            state.CurrentProjectPath = projectPath;
            state.CurrentAudioFileName = project.AudioFileName;

            Session = new ProjectSessionService(
                new FakeAudioPlaybackService(), config, logger, new FakeFileStorageService(),
                Documents, state, transport, editor, new TempoService(logger),
                new NoOpCrashRecoveryService(), history, new NoOpSaveCompanionService(),
                uiDispatcher: new TestUiDispatcher(),
                localization: FakeLocalization.English());
        }

        public RecordingDocumentService Documents { get; } = new();
        public ProjectSessionService Session { get; }
    }
}
