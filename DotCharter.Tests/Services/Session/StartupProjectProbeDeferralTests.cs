using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Export;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Infrastructure.Recovery;
using DotCharter.Services.Session;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.Services.Session;

/// <summary>
///     <b>The startup project's existence probe is not on the first-frame path
///     (#194).</b> <c>ProjectSessionService.Initialize()</c> runs inside
///     <c>MainWindowViewModel</c>'s constructor — the <c>vmGraph=</c> startup phase,
///     on the UI thread, before the window exists — and it used to call
///     <c>File.Exists(LastProjectPath)</c> there. The owner's last project lives on
///     a spinning disk: one R2R boot read <c>vmGraph=336</c> against 45 ms for every
///     other, with the whole gap between "Initializing audio engine" and the view
///     model's "initialized" log line — i.e. on that probe.
///     <para>
///         Behavioural: the probe is a seam that records its calls and threads, and
///         the dispatcher QUEUES posts instead of running them, so "nothing ran
///         during Initialize" is observable rather than inferred.
///     </para>
/// </summary>
public sealed class StartupProjectProbeDeferralTests
{
    [Fact]
    public async Task Initialize_NeverProbesTheStartupProject_ItQueuesTheProbeOntoAWorker()
    {
        var h = new Harness(@"Z:\nowhere\project.rscp");
        // Each probe records whether it ran INSIDE the reload's own synchronous call
        // on the calling thread — i.e. on the UI thread, which is the cost this is
        // about. A thread-id comparison alone is not enough: once the test awaits,
        // its thread can go back to the pool and pick the worker item up itself.
        var probes = new List<bool>();
        var callingThread = Environment.CurrentManagedThreadId;
        var insideReload = false;
        var session = h.Build(path =>
        {
            lock (probes) probes.Add(Volatile.Read(ref insideReload) && Environment.CurrentManagedThreadId == callingThread);
            return false;
        });

        session.Initialize();

        Assert.Empty(probes);
        Assert.Empty(h.Editor.Arrangements);
        var reload = Assert.Single(h.Dispatcher.Queued);

        // The first frame is up; the reload runs. A missing project still ends on
        // the default one, exactly as before — just after first paint.
        Volatile.Write(ref insideReload, true);
        reload();
        Volatile.Write(ref insideReload, false);
        await WaitUntil(() => h.Editor.Arrangements.Count > 0);

        var ranOnTheCallingThread = Assert.Single(probes);
        Assert.False(ranOnTheCallingThread,
            "the startup project's existence probe ran synchronously on the dispatcher thread inside the " +
            "reload — a parked disk would stall the UI there instead of a worker.");
        Assert.Equal("Lead Guitar", h.Editor.Arrangements[0].Name);
    }

    [Fact]
    public void Initialize_WithNoStartupProject_AppliesTheDefaultProjectAtOnce_AndQueuesNothing()
    {
        var h = new Harness("");
        var probed = false;
        var session = h.Build(_ => probed = true);

        session.Initialize();

        Assert.False(probed);
        Assert.Empty(h.Dispatcher.Queued);
        Assert.Equal("Lead Guitar", Assert.Single(h.Editor.Arrangements).Name);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        // A bound on a WAIT, not a timing assertion: the work is a Task.Run and one
        // continuation, so it lands in microseconds; ten seconds only keeps a
        // regression from hanging the gate.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(5, TestContext.Current.CancellationToken);
        Assert.True(condition(), "the queued startup reload never applied a project.");
    }

    private sealed class Harness
    {
        private readonly FakeAppLogger _logger = new();
        private readonly ProjectState _state = new();
        private readonly FakeConfigurationService _config = new();
        private readonly TransportViewModel _transport;
        private readonly HistoryService _history;

        public Harness(string lastProjectPath)
        {
            _config.AppConfig.LastProjectPath = lastProjectPath;
            _transport = new TransportViewModel(new FakeAudioPlaybackService(), _state, _config, FakeLocalization.KeysOnly());
            var selection = new SelectionService();
            _history = new HistoryService(_logger);
            Editor = new ChartEditorViewModel(
                _state, _transport, _history, selection,
                new TempoService(_logger), new TrackContextRegistry(_state, selection), FakeLocalization.KeysOnly());
        }

        public ChartEditorViewModel Editor { get; }
        public QueueingUiDispatcher Dispatcher { get; } = new();

        public ProjectSessionService Build(Func<string, bool> probe) =>
            new(new FakeAudioPlaybackService(), _config, _logger, new FakeFileStorageService(),
                new NoDocuments(), _state, _transport, Editor, new TempoService(_logger),
                new NoCrashRecovery(), _history, new NoSaveCompanions(),
                uiDispatcher: Dispatcher, localization: FakeLocalization.English())
            {
                StartupProjectExists = probe
            };
    }

    /// <summary>A UI dispatcher whose posts wait until the test runs them — the
    /// shape of the real one, where a Background post runs after first paint.</summary>
    private sealed class QueueingUiDispatcher : IUiDispatcher
    {
        public List<Action> Queued { get; } = [];
        public bool CheckAccess() => true;
        public void Post(Action action) => Queued.Add(action);
        public void PostNormal(Action action) => Queued.Add(action);

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
        public IDisposable SchedulePeriodic(TimeSpan period, Action tick) => new Inert();

        private sealed class Inert : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class NoDocuments : IProjectDocumentService
    {
        public ChartProject LoadChart(string filePath) => throw new FileNotFoundException(filePath);
        public void SaveChart(string filePath, ChartProject project, double? audioLengthMs = null) { }
        public void RefreshArrangementFileManifest(ChartProject project) { }
        public void SaveDlcBuilderXmlBundle(string projectPath, ChartProject project, double? audioLengthMs = null) { }

        public ChartProject AssembleProject(ChartProject? baseProject, double bpm, string audioFileName,
            IEnumerable<LyricPhrase> lyrics) => baseProject ?? new ChartProject();

        public ChartProject CreateNewProject(string title, string artist, string audioFileName) => new();
    }

    private sealed class NoCrashRecovery : ICrashRecoveryService
    {
        public string SnapshotRootDirectory => string.Empty;
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

    private sealed class NoSaveCompanions : ISaveCompanionService
    {
        public string? WriteEof(string projectPath, ChartProject snapshot) => null;
        public string? WriteDlcBuilderProject(string projectPath, ChartProject snapshot, double? audioLengthMs) => null;

        public Task<string?> BuildPsarcAsync(string projectPath, ChartProject snapshot,
            double? audioLengthMs, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
