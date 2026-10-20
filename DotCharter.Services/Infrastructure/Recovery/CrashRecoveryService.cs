using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Editing;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.State;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Services.Infrastructure.Recovery;

/// <summary>
///     0.12.0 crash recovery. The roadmap wording is "append-only journaled
///     autosave"; against this codebase's in-place mutation model a mutation
///     journal is not practical, so the equivalent guarantee is provided by
///     frequent CRASH SNAPSHOTS: a deep-cloned project (torn-snapshot safe,
///     same <see cref="ChartProjectCloner"/> path as manual save) serialized
///     off-thread to <c>ConfigRoot/recovery/&lt;key&gt;.rscp</c> via
///     tmp-file + atomic rename. Snapshots are deleted on successful manual
///     save, on project switch and on clean shutdown — a leftover file at
///     startup is the crash evidence the recovery dialog acts on.
/// </summary>
public interface ICrashRecoveryService : IDisposable
{
    /// <summary>
    ///     The folder this service writes its snapshots into. A file under it is
    ///     an INTERNAL artifact, not a project the user opened — <c>LoadFile</c>
    ///     asks so it can keep the recovery copy out of <c>LastProjectPath</c>
    ///     and out of the Open Recent list.
    /// </summary>
    string SnapshotRootDirectory { get; }

    /// <summary>Arm the autosave timer + history hook. Called once at startup.</summary>
    void Start();

    /// <summary>Snapshots on disk that pass the newer-than-original gating; stale (superseded) ones are cleaned up.</summary>
    IReadOnlyList<RecoverableSession> FindRecoverableSessions();

    /// <summary>Delete a session's files (user chose Discard).</summary>
    void DiscardSession(RecoverableSession session);

    /// <summary>The project was saved to <paramref name="projectPath"/> — its snapshot is now redundant.</summary>
    void NotifyProjectSaved(string projectPath);

    /// <summary>A different project was loaded — the previous session's snapshot does not represent open work.</summary>
    void NotifyProjectSwitched();

    /// <summary>Clean app shutdown — no crash, so leave no crash evidence.</summary>
    void NotifyCleanShutdown();

    /// <summary>
    ///     Unsaved work changed in a way the UNDO HISTORY cannot represent, so arm
    ///     the snapshot the way a history edit would.
    ///     <para>
    ///         The scheduler had exactly one input, <c>HistoryChanged</c>, and
    ///         project-level state lives beside the chart graph: attaching or
    ///         replacing the song audio, a checkpoint restore, a recovery restore,
    ///         the new-project wizard. All of those call
    ///         <c>ProjectDirtyTracker.MarkDirty</c>, and none of them was ever
    ///         snapshotted — the app knew the project was dirty and still had
    ///         nothing to recover.
    ///     </para>
    /// </summary>
    void NotifyProjectChanged();

    /// <summary>
    ///     "The open project holds work that is not on disk." Wired to
    ///     <c>ProjectDirtyTracker.IsDirty</c> by <c>ProjectSessionService</c>
    ///     (the tracker is created there, so it cannot be injected here).
    ///     <para>
    ///         This is the <b>did-anything-change gate</b> the snapshotter never
    ///         had. Its only inputs were CHANGE SIGNALS, and a signal is not a
    ///         change: <c>ApplyProject(resetHistory: true)</c> raises
    ///         <c>HistoryChanged</c> on every replace path, so merely opening a
    ///         song armed the scheduler, and ten seconds later a full snapshot was
    ///         written for a chart nobody had edited. On a PSARC/library song that
    ///         snapshot lands under the <c>untitled</c> key —
    ///         <see cref="IRecoverySnapshotStore.IsRecoverable" /> has no original
    ///         file to compare it against and returns <c>true</c> unconditionally —
    ///         so the next launch offered to restore it. Left null (tests,
    ///         headless) the gate is open and behaviour is exactly as before.
    ///     </para>
    /// </summary>
    Func<bool>? HasUnsavedChanges { get; set; }
}

public sealed class CrashRecoveryService : ICrashRecoveryService
{
    /// <summary>Burst debounce: snapshot 10 s after the last edit of a burst.</summary>
    private static readonly TimeSpan Debounce = RecoverySnapshotScheduler.DebounceDefault;

    /// <summary>Timer granularity. Each tick is only scheduler bookkeeping when idle.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);

    private readonly IChartEditorSurface _chartEditor;
    private readonly IConfigurationService _configService;
    private readonly IProjectDocumentService _documentService;
    private readonly IHistoryService _historyService;
    private readonly IAppLogger _logger;
    private readonly ProjectState _projectState;
    private readonly RecoverySnapshotScheduler _scheduler = new(DateTimeOffset.UtcNow);
    private readonly IRecoverySnapshotStore _store;
    private readonly IUiDispatcher _ui;
    private readonly IEditorTransport _transport;

    /// <summary>The running snapshot tick; null until <see cref="Start" />.</summary>
    private IDisposable? _timer;

    private bool _disposed;
    /// <summary>
    ///     Invalidates an in-flight snapshot write when the world changed
    ///     (save/switch/shutdown) between capture and write completion, so a
    ///     slow write can't resurrect a snapshot that was just deleted.
    /// </summary>
    private int _generation;
    /// <summary>Original-path key of the snapshot last written THIS session (null = none yet).</summary>
    private string? _lastSnapshotOriginalPath;
    private bool _snapshotInProgress;
    private bool _started;

    public CrashRecoveryService(
        IRecoverySnapshotStore store,
        IHistoryService historyService,
        IConfigurationService configService,
        IProjectDocumentService documentService,
        ProjectState projectState,
        IEditorTransport transport,
        IChartEditorSurface chartEditor,
        IAppLogger logger,
        IUiDispatcher ui)
    {
        _ui = ui;
        _store = store;
        _historyService = historyService;
        _configService = configService;
        _documentService = documentService;
        _projectState = projectState;
        _transport = transport;
        _chartEditor = chartEditor;
        _logger = logger;

        // The snapshot tick runs through IUiDispatcher.SchedulePeriodic, which the
        // app implements as a BACKGROUND DispatcherTimer — below Render, and that
        // priority is load-bearing: the tick deep-clones the whole project, and the
        // clone has to stay on the UI thread (a worker would tear it — see
        // Timer_Tick's capture/write split). Nothing about it is deadline-sensitive
        // (the interval is seconds and the scheduler re-reads the clock), so it
        // must never go ahead of a frame while the charter is scrubbing.
        // AvaloniaUiDispatcherTests.PeriodicTimer_RunsBelowRender pins the priority.
    }

    /// <inheritdoc />
    public string SnapshotRootDirectory => _store.RootDirectory;

    /// <inheritdoc />
    public Func<bool>? HasUnsavedChanges { get; set; }

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        _historyService.HistoryChanged += OnHistoryChanged;
        _timer = _ui.SchedulePeriodic(TickInterval, Timer_Tick);
        _logger.Info(AppLogCategory.Project,
            $"Crash recovery armed. Dir='{_store.RootDirectory}', intervalSeconds={_configService.LoadAppConfig().RecoveryAutosaveSeconds}, debounceSeconds={Debounce.TotalSeconds:0}, enabled={_configService.LoadAppConfig().RecoveryEnabled}.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        _timer = null;
        if (_started) _historyService.HistoryChanged -= OnHistoryChanged;
    }

    public IReadOnlyList<RecoverableSession> FindRecoverableSessions()
    {
        try
        {
            var recoverable = new List<RecoverableSession>();
            foreach (var session in _store.ListSessions())
            {
                if (_store.IsRecoverable(session))
                {
                    recoverable.Add(session);
                }
                else
                {
                    // Superseded: the original was saved after this snapshot
                    // was written, so it holds nothing the project file lacks.
                    _store.DeleteSession(session);
                    _logger.Info(AppLogCategory.Project,
                        $"Crash recovery: deleted superseded snapshot '{session.SnapshotPath}' (original is newer).");
                }
            }

            if (recoverable.Count > 0)
            {
                _logger.Info(AppLogCategory.Project,
                    $"Crash recovery: found {recoverable.Count} recoverable session(s) — previous session did not shut down cleanly.");
            }

            return recoverable;
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Project, "Crash recovery: listing recoverable sessions failed.", ex);
            return Array.Empty<RecoverableSession>();
        }
    }

    public void DiscardSession(RecoverableSession session)
    {
        _store.DeleteSession(session);
        _logger.Info(AppLogCategory.Project,
            $"Crash recovery: user discarded snapshot '{session.SnapshotPath}' (original='{session.Metadata.OriginalProjectPath}').");
    }

    public void NotifyProjectSaved(string projectPath)
    {
        _generation++;
        _scheduler.Reset(DateTimeOffset.UtcNow);
        try
        {
            _store.DeleteSnapshot(projectPath);
            // A save can also retire an untitled-session snapshot (Save on an
            // untitled project) — the key differs, so clean up the
            // last snapshot this session actually wrote as well.
            if (_lastSnapshotOriginalPath != null &&
                !string.Equals(_lastSnapshotOriginalPath, projectPath, StringComparison.OrdinalIgnoreCase))
            {
                _store.DeleteSnapshot(_lastSnapshotOriginalPath);
            }

            _lastSnapshotOriginalPath = null;
            _logger.Info(AppLogCategory.Project,
                $"Crash recovery: snapshot cleared after successful save of '{projectPath}'.");
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Project,
                $"Crash recovery: could not clear snapshot after save. Message='{ex.Message}'.");
        }
    }

    public void NotifyProjectSwitched()
    {
        _generation++;
        _scheduler.Reset(DateTimeOffset.UtcNow);
        if (_lastSnapshotOriginalPath == null) return;
        try
        {
            _store.DeleteSnapshot(_lastSnapshotOriginalPath);
            _logger.Info(AppLogCategory.Project,
                $"Crash recovery: snapshot for '{_lastSnapshotOriginalPath}' cleared on project switch.");
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Project,
                $"Crash recovery: could not clear snapshot on project switch. Message='{ex.Message}'.");
        }
        finally
        {
            _lastSnapshotOriginalPath = null;
        }
    }

    public void NotifyCleanShutdown()
    {
        _generation++;
        // Retire the snapshot for whatever is open NOW as well as the one this
        // session wrote. They are usually the same key, but not always: a
        // session that wrote no snapshot of its own (nothing edited) leaves
        // _lastSnapshotOriginalPath null while an EARLIER session's snapshot for
        // the same key is still on disk — and for the untitled key (a psarc
        // load, which has no project path) that snapshot is unconditionally
        // "recoverable", so it re-offered itself on every launch forever.
        var openPath = _projectState.CurrentProjectPath ?? string.Empty;
        var keys = new List<string> { openPath };
        if (_lastSnapshotOriginalPath != null &&
            !string.Equals(_lastSnapshotOriginalPath, openPath, StringComparison.OrdinalIgnoreCase))
        {
            keys.Add(_lastSnapshotOriginalPath);
        }

        try
        {
            foreach (var key in keys) _store.DeleteSnapshot(key);
            _logger.Info(AppLogCategory.Project,
                $"Crash recovery: snapshot(s) for [{string.Join(", ", keys)}] cleared on clean shutdown.");
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Project,
                $"Crash recovery: could not clear snapshot on clean shutdown. Message='{ex.Message}'.");
        }
        finally
        {
            _lastSnapshotOriginalPath = null;
        }
    }

    private void OnHistoryChanged() => _scheduler.NotifyChanged(DateTimeOffset.UtcNow);

    /// <inheritdoc />
    public void NotifyProjectChanged()
    {
        if (_disposed) return;
        _scheduler.NotifyChanged(DateTimeOffset.UtcNow);
    }

    private void Timer_Tick() => _ = RunTickAsync(DateTimeOffset.UtcNow);

    /// <summary>
    ///     One timer tick, with the clock injected and the snapshot write
    ///     awaitable, so the whole decision is exercisable from the default gate
    ///     (<c>CrashRecoverySnapshotGateTests</c>) instead of only by a running
    ///     app. <see cref="Timer_Tick" /> is the single production caller.
    /// </summary>
    internal async Task RunTickAsync(DateTimeOffset nowUtc)
    {
        if (_snapshotInProgress) return;
        if (!_scheduler.IsDirty) return; // cheap idle path — no config load, no IO

        // The did-anything-change gate. A change SIGNAL armed the scheduler, but
        // the project may hold nothing a fresh load would not produce — every
        // replace path raises HistoryChanged through ApplyProject(resetHistory:
        // true) and then declares itself clean. Writing a snapshot there fabricates
        // crash evidence for an untouched chart; on a PSARC song it lands under the
        // "untitled" key, which IsRecoverable can never gate, so the next launch
        // offers to restore a song the user only browsed. Reset (not just skip) so
        // the idle path above stays free until something really changes.
        if (HasUnsavedChanges is { } isDirty && !isDirty())
        {
            _scheduler.Reset(nowUtc);
            return;
        }

        var config = _configService.LoadAppConfig();
        if (!config.RecoveryEnabled)
        {
            _scheduler.Reset(nowUtc);
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(5, config.RecoveryAutosaveSeconds));
        if (!_scheduler.ShouldSnapshot(nowUtc, interval, Debounce)) return;

        await WriteSnapshotAsync();
    }

    private async Task WriteSnapshotAsync()
    {
        if (_snapshotInProgress) return;
        _snapshotInProgress = true;
        try
        {
            var project = _projectState.ActiveProject;
            if (project == null)
            {
                _scheduler.Reset(DateTimeOffset.UtcNow);
                return;
            }

            // Capture phase (UI thread): assemble exactly what a manual save
            // would persist, then deep-clone it. The clone is what the worker
            // serializes, so edits landing during the write cannot tear the
            // snapshot — same protection as SaveProjectAsync.
            var originalPath = _projectState.CurrentProjectPath ?? string.Empty;
            var assembled = _documentService.AssembleProject(project, _transport.Bpm,
                _projectState.CurrentAudioFileName, _chartEditor.LyricPhrases);
            var snapshot = ChartProjectCloner.Clone(assembled);

            var metadata = new RecoverySnapshotMetadata
            {
                OriginalProjectPath = originalPath,
                OriginalAudioFileName = assembled.AudioFileName ?? string.Empty,
                SongName = snapshot.Chart?.SongName ?? string.Empty,
                TimestampUtc = DateTimeOffset.UtcNow,
                AppVersion = DotCharter.Services.Infrastructure.AppVersionInfo.Current
            };

            // The recovery file lives far away from the project folder, so a
            // relative audio reference would not resolve when the snapshot is
            // opened. Store it absolute; the sidecar keeps the original
            // (usually relative) reference for restoring after recovery.
            snapshot.AudioFileName = ResolveAudioAbsolute(originalPath, snapshot.AudioFileName);

            // Mark captured NOW (not after the write) so edits arriving while
            // the worker serializes re-dirty the scheduler.
            _scheduler.NotifySnapshotCaptured(DateTimeOffset.UtcNow);
            var generationAtCapture = _generation;

            await Task.Run(() => _store.WriteSnapshot(snapshot, metadata));

            if (generationAtCapture != _generation)
            {
                // Saved/switched/shut down while writing — this snapshot is
                // already obsolete and must not linger as false crash evidence.
                _store.DeleteSnapshot(originalPath);
                return;
            }

            _lastSnapshotOriginalPath = originalPath;
            _logger.Info(AppLogCategory.Project,
                $"Crash recovery snapshot written. File='{_store.GetSnapshotPath(originalPath)}', original='{(string.IsNullOrEmpty(originalPath) ? "<untitled>" : originalPath)}', song='{metadata.SongName}'.");
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Project, "Crash recovery snapshot failed.", ex);
            // RE-ARM, because the scheduler was told the snapshot was CAPTURED
            // before the write was attempted (deliberately — see above: edits
            // arriving during the async write must re-dirty it). A write that
            // THREW leaves `_dirty` false with nothing on disk, and the next
            // tick's `if (!_scheduler.IsDirty) return` short-circuits — so one
            // transient failure (an AV or backup tool holding the recovery
            // folder, a full disk, a momentarily unreachable roaming profile)
            // silently switched crash recovery off for the rest of the editing
            // burst, which is exactly the window it exists for, and nothing said
            // so beyond one log line. NotifyChanged restarts the debounce, so
            // this retries every ~10 s while the work stays unsaved rather than
            // spinning.
            _scheduler.NotifyChanged(DateTimeOffset.UtcNow);
        }
        finally
        {
            _snapshotInProgress = false;
        }
    }

    /// <summary>
    ///     Same walk-up resolution the project loader uses: try the project
    ///     directory, then its parents. Falls back to the name unchanged.
    /// </summary>
    private static string ResolveAudioAbsolute(string projectPath, string? audioFileName)
    {
        if (string.IsNullOrWhiteSpace(audioFileName)) return string.Empty;
        if (Path.IsPathRooted(audioFileName)) return audioFileName;

        try
        {
            var startDir = string.IsNullOrWhiteSpace(projectPath)
                ? Directory.GetCurrentDirectory()
                : Path.GetDirectoryName(Path.GetFullPath(projectPath));

            var directory = string.IsNullOrWhiteSpace(startDir) ? null : new DirectoryInfo(startDir);
            while (directory != null)
            {
                var candidate = Path.Combine(directory.FullName, audioFileName);
                if (File.Exists(candidate)) return candidate;
                directory = directory.Parent;
            }
        }
        catch
        {
            // resolution is best-effort; an unresolved name just means the
            // recovered session opens without audio, like a moved project.
        }

        return audioFileName;
    }
}
