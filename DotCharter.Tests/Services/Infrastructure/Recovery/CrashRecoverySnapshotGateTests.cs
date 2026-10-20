using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Infrastructure.Recovery;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.Services.Infrastructure.Recovery;

/// <summary>
///     The owner's v0.48.5 report: "sometimes it still asks to restore the
///     project from loaded PSARCs even though there were no changes."
///     <para>
///         The snapshotter's only inputs were CHANGE SIGNALS, and a signal is not
///         a change. Every replace path runs
///         <c>ChartEditorViewModel.ApplyProject(resetHistory: true)</c>, which
///         raises <c>HistoryChanged</c>, and since v0.48
///         <c>ProjectDirtyTracker.MarkDirty</c> arms the scheduler as well — so
///         merely OPENING a library song armed it, and one debounce later the app
///         wrote a full crash snapshot for a chart nobody had edited. A PSARC
///         session has no project path, so that snapshot lands under the
///         <c>untitled</c> key, and
///         <see cref="RecoverySnapshotStore.IsRecoverable" /> has no original file
///         to date it against: untitled is recoverable UNCONDITIONALLY. The next
///         launch therefore offered to restore a song the user had only browsed.
///     </para>
///     <para>
///         Both halves are fixed here and both are pinned below: the
///         did-anything-change gate
///         (<see cref="ICrashRecoveryService.HasUnsavedChanges" />) and the
///         clean-declaration wiring that disarms the scheduler on a library load
///         (<c>ProjectDirtyTracker.CleanMarked</c> →
///         <c>NotifyProjectSwitched</c>, see
///         <c>ProjectSessionRecoveryWiringTests</c>).
///     </para>
/// </summary>
public sealed class CrashRecoverySnapshotGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dc_recgate_{Guid.NewGuid():N}");

    public void Dispose() => BestEffortDelete.Directory(_root);

    /// <summary>
    ///     THE REPRODUCTION. A PSARC-sourced session (no project path ⇒ the
    ///     untitled key) that the user has not edited: the scheduler is armed by
    ///     the load itself, the debounce elapses — and nothing may be written.
    /// </summary>
    [Fact]
    public async Task Tick_OnAnUneditedPsarcSession_WritesNoSnapshot()
    {
        var h = new Harness(_root);
        h.OpenPsarcSong();

        h.Service.NotifyProjectChanged(); // what the load's HistoryChanged/MarkDirty does
        await h.Service.RunTickAsync(DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Empty(h.SnapshotFiles());
        Assert.Empty(h.Store.ListSessions());
    }

    /// <summary>
    ///     The same tick with the ONE input flipped. Without this the test above
    ///     would pass over a snapshotter that never writes anything at all — which
    ///     is exactly the failure mode a "no files were written" assertion invites.
    /// </summary>
    [Fact]
    public async Task Tick_OnAnEDITEDPsarcSession_StillWritesTheSnapshot()
    {
        var h = new Harness(_root);
        h.OpenPsarcSong();
        h.IsDirty = true;

        h.Service.NotifyProjectChanged();
        await h.Service.RunTickAsync(DateTimeOffset.UtcNow.AddMinutes(1));

        var session = Assert.Single(h.Store.ListSessions());
        Assert.True(session.IsUntitled);
        Assert.Equal("Bat Country", session.Metadata.SongName);
        // …and the store would offer it back, which is the whole point: real
        // unsaved work on a PSARC song must survive a crash.
        Assert.True(h.Store.IsRecoverable(session));
    }

    /// <summary>
    ///     A snapshot write that THREW is retried; it does not switch crash
    ///     recovery off for the rest of the session.
    ///     <para>
    ///         The scheduler is told the snapshot was CAPTURED before the write
    ///         is attempted — deliberately, so edits arriving during the async
    ///         write re-dirty it. A write that throws therefore left <c>_dirty</c>
    ///         false with nothing on disk, and every later tick short-circuited on
    ///         <c>if (!_scheduler.IsDirty) return</c>. One transient failure (an
    ///         AV or backup tool holding the recovery folder, a full disk, a
    ///         roaming profile that blinked) silently stopped the snapshotter for
    ///         the whole editing burst — exactly the window it exists for — behind
    ///         a single log line.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task ASnapshotWriteThatThrew_IsRetriedOnTheNextDebounce()
    {
        var h = new Harness(_root, failWrites: 1);
        h.OpenPsarcSong();
        h.IsDirty = true;

        h.Service.NotifyProjectChanged();
        var now = DateTimeOffset.UtcNow;
        await h.Service.RunTickAsync(now.AddMinutes(1));

        Assert.Equal(1, h.Flaky!.WriteAttempts);
        Assert.Empty(h.Store.ListSessions());

        // No new edit — only the debounce elapsing again.
        await h.Service.RunTickAsync(now.AddMinutes(2));

        Assert.Equal(2, h.Flaky.WriteAttempts);
        var session = Assert.Single(h.Store.ListSessions());
        Assert.Equal("Bat Country", session.Metadata.SongName);
    }

    /// <summary>…and a SUCCESSFUL write still stands the scheduler down, so the
    /// re-arm above is scoped to the failure and does not turn the snapshotter
    /// into a per-tick writer.</summary>
    [Fact]
    public async Task ASnapshotThatSucceeded_DoesNotWriteAgainWithoutAnEdit()
    {
        var h = new Harness(_root, failWrites: 0);
        h.OpenPsarcSong();
        h.IsDirty = true;

        h.Service.NotifyProjectChanged();
        var now = DateTimeOffset.UtcNow;
        await h.Service.RunTickAsync(now.AddMinutes(1));
        Assert.Single(h.Store.ListSessions());
        var written = File.GetLastWriteTimeUtc(h.SnapshotFiles()[0]);

        await h.Service.RunTickAsync(now.AddMinutes(2));

        Assert.Equal(written, File.GetLastWriteTimeUtc(h.SnapshotFiles()[0]));
    }

    /// <summary>
    ///     …and the tick may not pre-empt a FRAME. The snapshot's capture phase
    ///     deep-clones the whole project on the UI thread, so the tick belongs
    ///     BELOW Render. The service asks <see cref="IUiDispatcher.SchedulePeriodic" />
    ///     for it — the app's implementation is a Background <c>DispatcherTimer</c>,
    ///     pinned by <c>AvaloniaUiDispatcherTests.PeriodicTimer_RunsBelowRender</c> —
    ///     and this pins that the service goes through it, once, at its interval,
    ///     and stops it on dispose. Structural, not timed.
    /// </summary>
    [Fact]
    public void TheSnapshotTick_IsScheduledThroughTheUiDispatcher_AndStopsOnDispose()
    {
        var h = new Harness(_root);
        Assert.Empty(h.Ui.Scheduled);

        h.Service.Start();
        var tick = Assert.Single(h.Ui.Scheduled);
        Assert.Equal(TimeSpan.FromSeconds(2), tick.Period);
        Assert.False(tick.Disposed);

        h.Service.Dispose();
        Assert.True(tick.Disposed);
    }

    /// <summary>
    ///     A clean project must not keep the tick doing work either: the gate
    ///     RESETS the scheduler rather than skipping, so the cheap idle path
    ///     (no config load, no IO) is restored until something really changes.
    /// </summary>
    [Fact]
    public async Task Tick_OnAnUneditedSession_DisarmsTheScheduler()
    {
        var h = new Harness(_root);
        h.OpenPsarcSong();

        h.Service.NotifyProjectChanged();
        await h.Service.RunTickAsync(DateTimeOffset.UtcNow.AddMinutes(1));

        // The config service is only consulted AFTER the gate, so "was the config
        // read" is an honest probe for "did this tick get past it".
        Assert.Equal(0, h.Config.LoadCount);

        // …and the arming is gone, not merely ignored: the next tick finds nothing
        // pending even though the project is dirty by then, because only a real
        // change re-arms the scheduler.
        h.IsDirty = true;
        await h.Service.RunTickAsync(DateTimeOffset.UtcNow.AddMinutes(2));
        Assert.Equal(0, h.Config.LoadCount);
        Assert.Empty(h.SnapshotFiles());
    }

    /// <summary>
    ///     Edit a PSARC song, close the app normally (discarding the changes at
    ///     the prompt): the untitled key must be retired, or every later launch
    ///     re-offers it — the untitled key cannot be date-gated.
    /// </summary>
    [Fact]
    public async Task CleanShutdown_AfterAnEditedPsarcSession_LeavesTheStoreEmpty()
    {
        var h = new Harness(_root);
        h.OpenPsarcSong();
        h.IsDirty = true;
        h.Service.NotifyProjectChanged();
        await h.Service.RunTickAsync(DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Single(h.Store.ListSessions());

        h.Service.NotifyCleanShutdown();

        Assert.Empty(h.Store.ListSessions());
    }

    /// <summary>
    ///     Edit a PSARC song and CRASH — no clean shutdown, no save. The snapshot
    ///     is the crash evidence and the next launch must offer it. This is the
    ///     case the gate must never take away.
    /// </summary>
    [Fact]
    public async Task Crash_AfterAnEditedPsarcSession_IsStillRecoverable()
    {
        var h = new Harness(_root);
        h.OpenPsarcSong();
        h.IsDirty = true;
        h.Service.NotifyProjectChanged();
        await h.Service.RunTickAsync(DateTimeOffset.UtcNow.AddMinutes(1));

        // No NotifyCleanShutdown / NotifyProjectSaved — the process died.
        var found = new Harness(_root).Service.FindRecoverableSessions();

        var session = Assert.Single(found);
        Assert.True(session.IsUntitled);
        Assert.Equal("Bat Country", session.Metadata.SongName);
    }

    /// <summary>
    ///     A titled project behaves the same way: browsing an unedited .rscp is
    ///     not crash evidence either. Its snapshot WOULD have been date-gated
    ///     against the original — but only until the first real edit moved the
    ///     snapshot past the file's timestamp, which is exactly what a
    ///     spuriously-armed scheduler produced.
    /// </summary>
    [Fact]
    public async Task Tick_OnAnUneditedTitledProject_WritesNoSnapshot()
    {
        var h = new Harness(_root);
        h.OpenPsarcSong();
        h.State.CurrentProjectPath = Path.Combine(_root, "Band - Song", "project.rscp");

        h.Service.NotifyProjectChanged();
        await h.Service.RunTickAsync(DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Empty(h.SnapshotFiles());
    }

    /// <summary>
    ///     A host that never wires the gate (headless, the older tests) keeps the
    ///     previous behaviour rather than silently losing crash cover.
    /// </summary>
    [Fact]
    public async Task Tick_WithNoGateWired_SnapshotsAsBefore()
    {
        var h = new Harness(_root);
        h.OpenPsarcSong();
        h.Service.HasUnsavedChanges = null;

        h.Service.NotifyProjectChanged();
        await h.Service.RunTickAsync(DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Single(h.Store.ListSessions());
    }

    // ---- harness --------------------------------------------------------------

    private sealed class CountingConfigurationService : IConfigurationService
    {
        private readonly FakeConfigurationService _inner = new();

        public int LoadCount { get; set; }

        public AppConfig LoadAppConfig()
        {
            LoadCount++;
            return _inner.LoadAppConfig();
        }

        public void SaveAppConfig(AppConfig config) => _inner.SaveAppConfig(config);
        public void Update(Action<AppConfig> mutate) => _inner.Update(mutate);
        public KeybindConfig LoadKeybinds() => _inner.LoadKeybinds();
        public void SaveKeybinds(KeybindConfig config) => _inner.SaveKeybinds(config);
        public void Flush() => _inner.Flush();
        public void InvalidateCache() => _inner.InvalidateCache();
    }

    /// <summary>Assembling is what the real service does before cloning; the
    /// identity here is enough — the snapshot's CONTENT is covered by
    /// <c>RecoverySnapshotStoreTests</c>.</summary>
    private sealed class PassThroughDocumentService : IProjectDocumentService
    {
        public ChartProject LoadChart(string filePath) => throw new NotSupportedException();
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

    private sealed class Harness
    {
        private readonly string _recoveryDir;

        public Harness(string root, int failWrites = 0)
        {
            _recoveryDir = Path.Combine(root, "recovery");
            Store = new RecoverySnapshotStore(_recoveryDir);
            var store = failWrites > 0 ? Flaky = new FlakyStore(Store, failWrites) : (IRecoverySnapshotStore)Store;

            var logger = new FakeAppLogger();
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), State, new FakeConfigurationService(), FakeLocalization.KeysOnly());
            var selection = new SelectionService();
            var history = new HistoryService(logger);
            var editor = new ChartEditorViewModel(
                State, transport, history, selection,
                new TempoService(logger), new TrackContextRegistry(State, selection), FakeLocalization.KeysOnly());

            Service = new CrashRecoveryService(
                store, history, Config, new PassThroughDocumentService(),
                State, transport, editor, logger, Ui)
            {
                // What ProjectSessionService wires to ProjectDirtyTracker.IsDirty.
                HasUnsavedChanges = () => IsDirty
            };
        }

        public ProjectState State { get; } = new();
        public CountingConfigurationService Config { get; } = new();

        /// <summary>Records the periodic tick instead of running it; the tests
        /// drive <c>RunTickAsync</c> with an injected clock.</summary>
        public SchedulingRecorder Ui { get; } = new();
        public RecoverySnapshotStore Store { get; }

        /// <summary>The throwing decorator, when the harness was built with one.</summary>
        public FlakyStore? Flaky { get; }

        public CrashRecoveryService Service { get; }

        /// <summary>Stands in for <c>ProjectDirtyTracker.IsDirty</c>.</summary>
        public bool IsDirty { get; set; }

        /// <summary>What <c>SongBrowserViewModel.LoadSongItem</c> leaves behind: a
        /// project with NO path (so the untitled key) and a clean session.</summary>
        public void OpenPsarcSong()
        {
            var arrangement = new Arrangement { Name = "Combo" };
            for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
            arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });

            var project = new ChartProject { Chart = new ChartData { SongName = "Bat Country" } };
            project.Arrangements.Add(arrangement);

            State.ActiveProject = project;
            State.CurrentProjectPath = "";
            State.CurrentSongIdentity = "lib:BatCountry";
            IsDirty = false;
        }

        public string[] SnapshotFiles()
            => Directory.Exists(_recoveryDir) ? Directory.GetFiles(_recoveryDir) : [];
    }

    /// <summary>The real store with its first <c>failWrites</c> writes throwing —
    /// an AV or backup tool holding the recovery folder, a full disk, a roaming
    /// profile that went away for a moment.</summary>
    internal sealed class FlakyStore(IRecoverySnapshotStore inner, int failWrites) : IRecoverySnapshotStore
    {
        private int _remainingFailures = failWrites;

        public int WriteAttempts { get; private set; }

        public string RootDirectory => inner.RootDirectory;
        public string GetSnapshotPath(string? originalProjectPath) => inner.GetSnapshotPath(originalProjectPath);
        public IReadOnlyList<RecoverableSession> ListSessions() => inner.ListSessions();
        public bool IsRecoverable(RecoverableSession session) => inner.IsRecoverable(session);
        public void DeleteSnapshot(string? originalProjectPath) => inner.DeleteSnapshot(originalProjectPath);
        public void DeleteSession(RecoverableSession session) => inner.DeleteSession(session);
        public ChartProject LoadSnapshot(string snapshotPath) => inner.LoadSnapshot(snapshotPath);

        public void WriteSnapshot(ChartProject snapshot, RecoverySnapshotMetadata metadata)
        {
            WriteAttempts++;
            if (_remainingFailures-- > 0) throw new IOException("the recovery folder is held by something else");
            inner.WriteSnapshot(snapshot, metadata);
        }
    }

    private sealed class SchedulingRecorder : IUiDispatcher
    {
        public List<ScheduledTick> Scheduled { get; } = [];
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public void PostNormal(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();

        public IDisposable SchedulePeriodic(TimeSpan period, Action tick)
        {
            var scheduled = new ScheduledTick(period);
            Scheduled.Add(scheduled);
            return scheduled;
        }
    }

    private sealed class ScheduledTick(TimeSpan period) : IDisposable
    {
        public TimeSpan Period { get; } = period;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
