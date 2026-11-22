using System.Diagnostics;
using System.Runtime.ExceptionServices;
using DotCharter.Tone.Audio;
using DotCharter.Services.Audio.SilentTransport;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Services.Audio.Waveform;
using DotCharter.Services.Diagnostics;
using DotCharter.Services.Export;
using DotCharter.Services.Import.Audio;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Audio.Dsp.Tones;
using DotCharter.Services.Editing;
using DotCharter.Services.Infrastructure.Recovery;
using DotCharter.Services.Editing.Tempo;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Package.DcSong;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Library.Cache;
using DotCharter.Services.State;
using DotCharter.Services.State.Checkpoints;
using DotCharter.Localization;
using DotCharter.Services.Library;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Services.Session;

public sealed class ProjectSessionService : IProjectSessionService, IProjectSaveHost
{
    private readonly IAudioPlaybackService _audioService;
    private readonly IChartEditorSurface _chartEditor;
    private readonly IConfigurationService _configService;
    private readonly ICrashRecoveryService _crashRecovery;
    private readonly IProjectDocumentService _documentService;
    private readonly IAppLogger _logger;
    private readonly ProjectState _projectState;
    private readonly IFileStorageService _storageService;
    private readonly ITempoService _tempoService;
    private readonly IEditorTransport _transport;
    private readonly WaveformDiskCache? _waveformCache;
    private readonly ProjectDirtyTracker _dirtyTracker;

    /// <summary>The app-wide "MessageBox". Confirms REPLACING a project's
    /// existing audio; the saver uses it as the surface a FAILED SAVE cannot be
    /// missed on.
    /// Optional so a host that never registers it (tests) simply skips it
    /// instead of failing composition.</summary>
    private readonly IMessagePrompt? _messageDialog;

    /// <summary>Carries the load's task pill (and, through the saver, the save's).
    /// Optional for the same reason.</summary>
    private readonly IStatusBarService? _statusBar;

    /// <summary>The UI thread: the status messages a worker reports and the
    /// startup reload. The save and the auto-backup tick reach it through their
    /// own collaborators.</summary>
    private readonly IUiDispatcher _uiDispatcher;
    private readonly ILocalizationService _localization;

    /// <summary>Stable id so a second open replaces the pill instead of stacking
    /// one per load, exactly like the save's.</summary>
    private const string LoadTaskId = "project-load";
    private bool _disposed;
    private readonly AudioLoadQueue _audioLoads = new();
    private int _audioLoadGeneration;
    private int _lastFreeAudioGeneration;
    private int _projectOpenGeneration;
    private readonly DetachedCompanionBuilder _detachedCompanions;
    private readonly SilentTransportSession _silentTransport;
    private readonly ProjectAutoBackup _autoBackup;
    private readonly LibraryDeviceRateAudio? _deviceRateAudio;
    private readonly ProjectViewMemory? _viewMemory;
    private readonly DcSongProjectOpener _dcSongOpener;

    public ProjectSessionService(
        IAudioPlaybackService audioService,
        IConfigurationService configService,
        IAppLogger logger,
        IFileStorageService storageService,
        IProjectDocumentService documentService,
        ProjectState projectState,
        IEditorTransport transport,
        IChartEditorSurface chartEditor,
        ITempoService tempoService,
        ICrashRecoveryService crashRecovery,
        IHistoryService historyService,
        ISaveCompanionService saveCompanions,
        IUiDispatcher uiDispatcher,
        ILocalizationService localization,
        IProjectPathRekey pathRekey,
        WaveformDiskCache? waveformCache = null,
        SilentAudioTrack? silentAudio = null,
        IMessagePrompt? messageDialog = null,
        IStatusBarService? statusBar = null,
        SongToneEditStore? songToneEdits = null,
        ICheckpointService? checkpoints = null,
        LibraryDeviceRateAudio? deviceRateAudio = null,
        ProjectViewMemory? viewMemory = null)
    {
        _viewMemory = viewMemory;
        _deviceRateAudio = deviceRateAudio;
        _statusBar = statusBar;
        _uiDispatcher = uiDispatcher;
        _localization = localization;
        _audioService = audioService;
        _configService = configService;
        _logger = logger;
        _storageService = storageService;
        _documentService = documentService;
        _projectState = projectState;
        _transport = transport;
        _chartEditor = chartEditor;
        _tempoService = tempoService;
        _crashRecovery = crashRecovery;
        _waveformCache = waveformCache;
        _silentTransport = new SilentTransportSession(audioService, projectState, transport, logger,
            silentAudio ?? new SilentAudioTrack(logger), historyService, GrowSilentAudioAsync);
        _messageDialog = messageDialog;
        _detachedCompanions = new DetachedCompanionBuilder(saveCompanions, documentService, uiDispatcher, projectState, logger, _localization, statusBar,
            () => _configService.LoadAppConfig().ExportAutoVolumeTargetLufs);

        // Dirty state rides the SAME change signal the crash-recovery
        // scheduler uses (the undo history) — no parallel mutation tracking.
        _dirtyTracker = new ProjectDirtyTracker(historyService);
        _projectState.ProjectChanged += MarkProjectDirty;
        _autoBackup = new ProjectAutoBackup(configService, _dirtyTracker, documentService, projectState, transport,
            chartEditor, uiDispatcher, logger, _localization);
        Saver = new ProjectSaver(configService, documentService, crashRecovery, storageService,
            projectState, transport, chartEditor, _dirtyTracker, _detachedCompanions, uiDispatcher, logger,
            _localization, messageDialog, statusBar, this, songToneEdits ?? SongToneEditStore.Default, pathRekey, checkpoints);
        _dirtyTracker.IsDirtyChanged += () => ProjectDirtyChanged?.Invoke();
        _dcSongOpener = new DcSongProjectOpener(
            new DcSongProjectUnpacker(logger, () => configService.LoadAppConfig().FfmpegPath),
            storageService, configService, projectState, _localization, logger, statusBar,
            MayReplaceOpenProjectAsync, path => LoadFileAsync(path, replaceConfirmed: true));
        // …but the history is not the ONLY change signal, and crash recovery only
        // had that one. Attaching or replacing the song audio, a checkpoint
        // restore, a recovery restore and the new-project wizard all go through
        // MarkDirty, and none of them ever armed a snapshot: the app knew the
        // project was dirty and still had nothing to recover from a crash.
        _dirtyTracker.DirtyMarked += _crashRecovery.NotifyProjectChanged;
        // …and the mirror, which is what actually stops the false prompts. Every
        // replace path ends in MarkClean ("what is in memory is what a fresh load
        // produces"), but only THIS file's open funnel told the recovery service
        // so. The library/PSARC load — the one Play Mode's song select drives —
        // left the scheduler armed by the HistoryChanged that
        // ApplyProject(resetHistory: true) raises, and the outgoing project's
        // snapshot in place. Wiring the declaration itself covers every path at
        // once instead of one call site at a time.
        _dirtyTracker.CleanMarked += _crashRecovery.NotifyProjectSwitched;
        // A change SIGNAL is not a change. The scheduler cannot tell the two
        // apart, so give it the project's own dirty flag as the final gate — see
        // ICrashRecoveryService.HasUnsavedChanges.
        _crashRecovery.HasUnsavedChanges = () => _dirtyTracker.IsDirty;
    }

    public AppConfig AppConfig { get; private set; } = new();

    public bool IsProjectDirty => _dirtyTracker.IsDirty;

    /// <summary>The save pipeline; internal so tests can drive its phases directly.</summary>
    internal ProjectSaver Saver { get; }

    /// <inheritdoc />
    public string LoadedChartName => _projectState.ActiveProject is { } project
        ? ProjectDisplayName.For(_projectState.CurrentProjectPath, project.Chart)
        : "";

    /// <inheritdoc />
    public Func<Task<bool>>? ConfirmDiscardAsync { get; set; }

    /// <summary>
    ///     True when the caller may replace the open project. Clean sessions and
    ///     hosts with no prompt wired never ask. A pending draft is applied first, so it counts; a staged draft that
    ///     only its own Apply writes counts too.
    /// </summary>
    public Task<bool> MayReplaceOpenProjectAsync() => ShouldProceedAsync(HasUnsavedWork(), ConfirmDiscardAsync);

    /// <inheritdoc />
    public bool HasUnsavedWork()
    {
        _chartEditor.CommitPendingEdits();
        return IsProjectDirty || _chartEditor.HasPendingDrafts;
    }

    /// <inheritdoc />
    public void MarkProjectClean() => _dirtyTracker.MarkClean();

    /// <inheritdoc />
    public void MarkProjectDirty() => _dirtyTracker.MarkDirty();

    /// <summary>
    ///     The rule itself, pure so it can be tested without constructing a whole
    ///     session. A clean project never interrupts the user; a host with no
    ///     prompt wired (tests, headless) behaves exactly as it did before rather
    ///     than silently refusing every open.
    /// </summary>
    internal static async Task<bool> ShouldProceedAsync(bool isDirty, Func<Task<bool>>? confirm)
    {
        if (!isDirty || confirm == null) return true;
        return await confirm();
    }

    public event Action? ProjectDirtyChanged;

    /// <inheritdoc />
    public event Action? RecentProjectsChanged;

    public event Action? SongPackageOpened;

    /// <inheritdoc />
    public IReadOnlyList<RecentProjectEntry> GetRecentProjects()
        => RecentProjectsList.Prune(AppConfig.RecentProjects);

    /// <inheritdoc />
    public IReadOnlyList<RecentProjectEntry> GetRecentProjectsUnverified()
        => AppConfig.RecentProjects.ToList();

    /// <inheritdoc />
    public Task<IReadOnlyList<RecentProjectEntry>> GetRecentProjectsAsync()
    {
        // Snapshot on the CALLER's thread — AppConfig.RecentProjects is mutated
        // by RecordRecentProject through the config service, and handing the
        // live list to a worker would race it.
        var snapshot = AppConfig.RecentProjects.ToList();
        return Task.Run<IReadOnlyList<RecentProjectEntry>>(() => RecentProjectsList.Prune(snapshot));
    }

    /// <summary>
    ///     The ONE writer of the MRU list — called from the open funnel
    ///     (<see cref="LoadFile" />) and the save funnel
    ///     (<see cref="ProjectSaver.SaveProjectCoreAsync" />) after the project is safely
    ///     applied/on disk. The path is user data, so it is deliberately not
    ///     logged here.
    /// </summary>
    private void RecordRecentProject(string projectPath, string? displayName)
    {
        try
        {
            // TouchLive, not Touch: a dead entry must not keep a Cap slot. Pruning
            // on the READ alone left them in the config, so the menu shrank as
            // projects were deleted and the trim then dropped a LIVE entry to make
            // room for the new one.
            UpdateAppConfig(c => RecentProjectsList.TouchLive(
                c.RecentProjects, projectPath, displayName, DateTime.UtcNow));
            RecentProjectsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            // Cosmetic bookkeeping — never fail an open/save over it.
            _logger.Warning(AppLogCategory.Settings,
                "Could not record the recent-project entry", ex);
        }
    }

    public void Initialize()
    {
        LoadAppConfig();
        _autoBackup.Start();
        // Crash snapshots complement the zip auto-backups: a single frequently
        // refreshed "work since the last save" copy whose presence at the next
        // startup means this session died mid-edit.
        _crashRecovery.Start();
        var audioReady = InitializeAudioEngineAsync();
        // NO file-system probe here. This runs inside MainWindowViewModel's
        // constructor — the vmGraph= startup phase, on the UI thread, before the
        // window exists — and the last project can live on a spun-down HDD: one
        // R2R boot read vmGraph=336 ms against 45 ms for every other, with the
        // whole gap between "Initializing audio engine" and the view model's
        // "initialized" line, i.e. this File.Exists. The existence check moves
        // into the queued reload, onto a worker, after first paint. An empty path
        // still gets the default project synchronously (no I/O to wait for); a
        // MISSING file gets it from the reload, which leaves the first frame in the
        // same not-yet-loaded state a present file always has.
        if (string.IsNullOrEmpty(AppConfig.LastProjectPath))
        {
            InitializeDefaultProject();
            return;
        }

        QueueStartupProjectReload(AppConfig.LastProjectPath, audioReady);
    }

    /// <summary>
    ///     Whether the startup project still exists. A seam so a test can count
    ///     the probe and see WHICH thread it runs on; production is
    ///     <see cref="File.Exists(string)" />, called from a worker.
    /// </summary>
    internal Func<string, bool> StartupProjectExists { get; init; } = File.Exists;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _projectState.ProjectChanged -= MarkProjectDirty;

        try
        {
            // Clean (disposed, not hard-killed) shutdown: leave no crash
            // evidence behind. A crash never reaches this line, which is
            // exactly what makes a leftover snapshot meaningful at startup.
            // NOTE: while the shutdown path hard-terminates (TerminateNow),
            // this may not run — save + project switch are the reliable
            // delete points. A snapshot surviving a close-without-saving is
            // offered for recovery next launch, which errs on the side of
            // never losing work.
            _crashRecovery.NotifyCleanShutdown();
            _crashRecovery.Dispose();
            _dirtyTracker.Dispose();
            _silentTransport.Stop();
            _autoBackup.Dispose();

            _detachedCompanions.Stop();

            _audioService.Dispose();
            _logger.Info(AppLogCategory.Application, "Audio service shut down during project session disposal.");
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Application, "Project session disposal failed while shutting down audio.", ex);
            Debug.WriteLine($"Error during ProjectSessionService disposal: {ex.Message}");
        }
    }

    public void LoadAppConfig()
    {
        AppConfig = _configService.LoadAppConfig();
        _logger.ReloadSettings();
        _logger.Info(AppLogCategory.Settings,
            $"Application configuration loaded. Logging console={AppConfig.EnableConsoleLogging}, file={AppConfig.EnableFileLogging}, coalesce={AppConfig.CoalesceRepeatedLogs}, rotate={AppConfig.RotateLogFiles}, path='{AppConfig.LogFilePath}', categories={AppConfig.EnabledLogCategories}.");
    }

    public void SaveAppConfig()
    {
        _configService.SaveAppConfig(AppConfig);
        _logger.ReloadSettings();
        _logger.Info(AppLogCategory.Settings,
            $"Application configuration saved. LastProjectPath='{AppConfig.LastProjectPath}'.");
    }

    /// <summary>
    ///     Mutate + persist <see cref="AppConfig" /> atomically under the
    ///     configuration service's lock (<see cref="IConfigurationService.Update" />).
    ///     Used by the save/load paths, whose continuation resumes off a
    ///     <c>Task.Run</c>, so the write can't race a background collection
    ///     mutation serializing the same shared config elsewhere.
    /// </summary>
    private void UpdateAppConfig(Action<AppConfig> mutate)
    {
        _configService.Update(mutate);
        AppConfig = _configService.LoadAppConfig();
        _logger.ReloadSettings();
        _logger.Info(AppLogCategory.Settings,
            $"Application configuration saved. LastProjectPath='{AppConfig.LastProjectPath}'.");
    }

    public void FreeAudio()
    {
        _lastFreeAudioGeneration = Interlocked.Increment(ref _audioLoadGeneration);
        Interlocked.Increment(ref _projectOpenGeneration);
        _silentTransport.Stop();
        _projectState.WaveformData = WaveformSamples.Empty;
        _projectState.TotalAudioLengthMs = 0;
        _logger.Info(AppLogCategory.Audio, "FreeAudio requested.");
        _ = ClearAudioAsync(_lastFreeAudioGeneration);
    }

    public void DrainSaveCompanions() => _detachedCompanions.Stop();

    public async Task ReloadProjectAudioAsync()
    {
        if (_projectState.ActiveProject is not { } project) return;
        Interlocked.Increment(ref _projectOpenGeneration);
        Interlocked.Increment(ref _audioLoadGeneration);
        _silentTransport.Stop();
        _projectState.WaveformData = WaveformSamples.Empty;
        _projectState.TotalAudioLengthMs = 0;
        _projectState.CurrentAudioFileName = project.AudioFileName ?? "";
        await LoadProjectAudioAsync(project, _projectState.CurrentProjectPath);
    }

    public async Task OpenProject()
    {
        _logger.Info(AppLogCategory.Project, "Open project/audio command invoked.");
        var startDir = !string.IsNullOrEmpty(AppConfig.LastRscpDir) ? AppConfig.LastRscpDir : AppConfig.LastProjectPath;
        // File ▸ Open was the ONE picker in the app that passed no filters at all
        // — every other one names its types — so the single dialog the user opens
        // most often was an unfiltered folder listing to hunt a project file in. The
        // first entry spans BOTH kinds because this router accepts both (audio
        // attaches to the open project, or auditions without one), so the default
        // view shows everything the command can actually take.
        var filters = new List<FilePickerFilter>
        {
            new(L("Picker_ProjectOrAudioFiles"),
                ProjectFiles.OpenablePatterns.Append("*" + DcSongFormat.Extension).Concat(AudioFileTypes.PickerPatterns).ToArray()),
            new(L("Picker_ProjectFiles"), ProjectFiles.OpenablePatterns.ToArray()),
            new(L("Picker_DcSongFiles"), new[] { "*" + DcSongFormat.Extension }),
            new(L("Picker_AudioFiles"), AudioFileTypes.PickerPatterns.ToArray()),
            FilePickerFilters.AllFiles
        };
        var filePath = await _storageService.OpenFilePickerAsync(
            L("Picker_OpenProjectOrAudio"), filters, startDir);
        if (!string.IsNullOrEmpty(filePath))
        {
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    if (AudioFileTypes.IsAudioFile(filePath)) AppConfig.LastAudioDir = dir;
                    else AppConfig.LastRscpDir = dir;
                    SaveAppConfig();
                }
            }
            catch { /* ignore */ }
            await LoadFile(filePath);
        }
        else
        {
            _logger.Info(AppLogCategory.Project, "Open project/audio command canceled by user.");
        }
    }

    public async Task<bool> SaveProjectAsync(SaveInitiator initiator) => await Saver.SaveWithGuardAsync(false, initiator) && !IsProjectDirty;

    public Task<bool> SaveProjectAsAsync() => Saver.SaveWithGuardAsync(true, SaveInitiator.User);

    string? IProjectSaveHost.LastRscpDirectory => AppConfig.LastRscpDir;

    void IProjectSaveHost.RecordSavedPath(string projectPath, string? songName)
    {
        UpdateAppConfig(c => c.LastProjectPath = projectPath);
        RecordRecentProject(projectPath, songName);
        _autoBackup.ResetSchedule();
    }

    void IProjectSaveHost.RememberSaveAsDirectory(string directory) => UpdateAppConfig(c => c.LastRscpDir = directory);

    /// <summary>
    ///     Is this path a file the crash-recovery store itself wrote? Asked of the
    ///     store (through <see cref="ICrashRecoveryService.SnapshotRootDirectory" />)
    ///     rather than re-spelled here, so the two cannot drift. Null service
    ///     (tests, headless) means "no store", i.e. nothing is internal.
    /// </summary>
    internal bool IsRecoverySnapshotPath(string filePath)
    {
        var root = _crashRecovery?.SnapshotRootDirectory;
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(filePath)) return false;
        try
        {
            var full = Path.GetFullPath(filePath);
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            return full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // An unrooted or malformed path is not in our store by definition.
            return false;
        }
    }

    public async Task LoadFile(string filePath)
    {
        var previous = _projectState.ActiveProject;
        var previousPath = _projectState.CurrentProjectPath;
        var preferred = await PreferProjectFileAsync(filePath);
        if (!IsCurrentProject(previous, previousPath)) return;
        await LoadFileAsync(preferred, replaceConfirmed: false);
    }

    /// <summary>
    ///     A <c>.rscp</c> with its <c>.dcproj</c> beside it is that project's Charter companion:
    ///     the <c>.dcproj</c> is the project, whichever of the two was picked, dropped or
    ///     remembered (File ▸ Open, a drop, Open Recent and the startup reload all come here).
    /// </summary>
    private async Task<string> PreferProjectFileAsync(string filePath)
    {
        var preferred = await Task.Run(() => ProjectFiles.PreferProject(filePath));
        if (string.Equals(preferred, filePath, StringComparison.Ordinal)) return filePath;

        _logger.Info(AppLogCategory.Project, $"'{filePath}' is the Charter companion of '{preferred}'; opening the .dcproj.");
        return preferred;
    }

    /// <param name="filePath">A project, audio or <c>.dcsong</c> file.</param>
    /// <param name="replaceConfirmed">True when the caller already asked before
    /// replacing the open project (the <c>.dcsong</c> opener asks before its folder
    /// picker), so the user is not asked twice.</param>
    private async Task LoadFileAsync(string filePath, bool replaceConfirmed)
    {
        if (await TryOpenAudioOrSongPackageAsync(filePath)) return;

        var previous = _projectState.ActiveProject;
        // Opening REPLACES the session: ApplyProject(resetHistory: true) clears
        // the undo history, so unsaved edits become unreachable the instant it
        // runs. This path is worse than the others — it also calls
        // NotifyProjectSwitched, which DELETES the previous project's crash
        // snapshot, so there is no recovery copy left either.
        if (!replaceConfirmed && !await MayReplaceOpenProjectAsync())
        {
            _logger.Info(AppLogCategory.Project,
                $"Open '{filePath}' cancelled — the user kept the unsaved project.");
            return;
        }

        if (!ReferenceEquals(previous, _projectState.ActiveProject)) return;
        // Save may migrate .rscp or give this same session its first path.
        var previousPath = _projectState.CurrentProjectPath;
        var previousAudio = _projectState.CurrentAudioFileName;
        var request = Interlocked.Increment(ref _projectOpenGeneration);
        Interlocked.Increment(ref _audioLoadGeneration);
        var loadingProject = previous;
        var isInternalSnapshot = IsRecoverySnapshotPath(filePath);
        // The arrangements ApplyProject and the view memory pick are the open's, not user switches.
        using var opening = _projectState.BeginProjectOpen();

        try
        {
            _logger.Info(AppLogCategory.Project, $"Loading project file '{filePath}'.");
            // A LOAD is the one heavy project operation that had no pill. The
            // deserialize, the spacing enforce and the audio decode below take
            // whole seconds on a dense chart, and for all of them the status rail
            // kept showing the PREVIOUS project's message — a silent wait that
            // reads as a dead click. Begun here, completed in the finally, so it
            // covers every exit path exactly as the save's does.
            _statusBar?.BeginTask(LoadTaskId, L("Status_LoadingProject"));
            // Parsing + spacing enforcement are CPU/IO-bound and touch only
            // the freshly loaded (not yet applied) project, so run them on a
            // worker thread — otherwise opening a dense multi-arrangement
            // chart freezes the window for the whole deserialize.
            var layoutChanged = false;
            var project = await Task.Run(() =>
            {
                var loaded = _documentService.LoadChart(filePath);
                if (!isInternalSnapshot && (ProjectFiles.IsProject(filePath) ||
                                           CharterRscp.IsCharterProject(filePath) && CharterRscp.IsWrittenByDotCharter(filePath)))
                {
                    layoutChanged = ProjectFolderTidy.Run(filePath, loaded, _logger);
                }
                ChartSpacingEnforcer.EnforceProject(loaded, _configService.LoadAppConfig());
                return loaded;
            });
            if (request != Volatile.Read(ref _projectOpenGeneration) ||
                !IsCurrentOpenSource(previous, previousPath, previousAudio))
            {
                return;
            }
            loadingProject = project;
            _logger.Info(AppLogCategory.Project,
                $"Project spacing enforced after load. Arrangements={project.Arrangements.Count}, beats={project.Beats.Count}, bpm={project.Bpm:0.###}.");
            _projectState.CurrentProjectPath = filePath;
            // Project files carry no library identity. Clear the outgoing song's
            // cache identity before resolving this project's media and waveform.
            _projectState.CurrentSongIdentity = "";
            // A crash-recovery snapshot is an INTERNAL artifact this app wrote,
            // not a project the user has on disk — so it may not become the
            // "last project" or an Open Recent row. This avoids two failures:
            // RecoveryViewModel repointed LastProjectPath afterwards but
            // ONLY on the success path, so a recovery that failed to load left
            // the next launch's startup reload opening
            // ConfigRoot/recovery/<hash>.rscp as the project — and nothing at all
            // repointed the MRU, so the recovery copy sat in File ▸ Open Recent
            // wearing the song's real name, one click from a Ctrl+S into the
            // recovery folder.
            if (!isInternalSnapshot) UpdateAppConfig(c => c.LastProjectPath = filePath);
            _autoBackup.ResetSchedule();

            _chartEditor.ApplyProject(project, resetHistory: true);
            _transport.Bpm = project.Bpm;

            // UNCONDITIONAL, exactly like every other replace path (the song
            // browser, New Project, the EOF importer, crash recovery).
            // This assignment must not sit INSIDE the `has audio` branch: an
            // audio-less project (an RS1 chart-only import) would inherit the
            // PREVIOUS song's reference: AssembleProject writes
            // ProjectState.CurrentAudioFileName into project.AudioFileName
            // unconditionally, and it is the entry point for the manual save, the
            // auto-backup, the crash snapshot, checkpoint create, the Rocksmith
            // export and the diagnostics bundle — so the first save would stamp the
            // previous song's <musicFileName> into this project. AttachAudioAsync
            // read the same stale value and asked "replace the song audio?" for a
            // project that had none.
            _projectState.CurrentAudioFileName = project.AudioFileName ?? "";

            // The applied chart is the disk baseline even if audio fails. Declare it
            // before yielding: edits during audio loading must remain unsaved, and
            // CleanMarked retires only the outgoing session's recovery snapshot here.
            _dirtyTracker.MarkClean();

            var missingAudioName = await LoadAudioAndRestoreViewAsync(project, filePath, isInternalSnapshot);
            if (!IsCurrentProject(project, filePath)) return;

            FinishOpenedProject(project, filePath, isInternalSnapshot, missingAudioName);
            if (layoutChanged) _detachedCompanions.Schedule(filePath, ChartProjectCloner.Clone(project, retainIdentities: true), _projectState.TotalAudioLengthMs);
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Project, $"Error loading project '{filePath}'.", ex);
            if (request == Volatile.Read(ref _projectOpenGeneration) && ReferenceEquals(loadingProject, _projectState.ActiveProject))
                _projectState.SetMessage(L("Status_LoadProjectError", ex.Message), StatusSeverity.Error);
        }
        finally
        {
            // The pill must go on EVERY exit path — a load that threw would
            // otherwise leave "Opening project…" on the rail forever.
            if (request == Volatile.Read(ref _projectOpenGeneration)) _statusBar?.CompleteTask(LoadTaskId);

        }
    }

    /// <summary>Audio and <c>.dcsong</c> files take their own paths; true when handled.</summary>
    private async Task<bool> TryOpenAudioOrSongPackageAsync(string filePath)
    {
        // Audio goes to the audio path, ALWAYS. Testing only .wav/.ogg
        // would let a dropped .mp3/.flac fall through to the project loader below, where
        // ResolveFormat throws NotSupportedException and the blanket catch reports
        // it as "error loading project" — a corrupt-project message for a perfectly
        // good song. The engine decodes every format in AudioFileTypes.
        if (AudioFileTypes.IsAudioFile(filePath))
        {
            // With a project open, dropping audio ATTACHES it (copied into the
            // project folder, reference written, so it survives save/reopen).
            // Without one there is nothing to attach to, so keep the historical
            // audition behaviour.
            if (_projectState.ActiveProject != null)
            {
                _logger.Info(AppLogCategory.Audio,
                    $"Attaching dropped/opened audio file '{filePath}' to the open project.");
                await AttachAudioAsync(filePath);
                return true;
            }

            _logger.Info(AppLogCategory.Audio, $"Loading dropped/opened audio file '{filePath}'.");
            await LoadAudioFile(filePath);
            return true;
        }

        // A song package opens as a NEW project folder it unpacks first.
        if (string.Equals(Path.GetExtension(filePath), DcSongFormat.Extension, StringComparison.OrdinalIgnoreCase))
        {
            var before = _projectState.ActiveProject;
            await _dcSongOpener.OpenAsync(filePath);
            if (!ReferenceEquals(before, _projectState.ActiveProject)) SongPackageOpened?.Invoke();
            return true;
        }

        return false;
    }

    private bool IsCurrentProject(ChartProject? project, string path) =>
        ReferenceEquals(project, _projectState.ActiveProject) && path == _projectState.CurrentProjectPath;

    private bool IsCurrentOpenSource(ChartProject? project, string path, string audio) =>
        IsCurrentProject(project, path) && audio == _projectState.CurrentAudioFileName;

    private void FinishOpenedProject(ChartProject project, string filePath, bool isInternalSnapshot, string missingAudioName)
    {
        _logger.Info(AppLogCategory.Project,
            $"Loaded project '{filePath}'. Song='{project.Chart.SongName}', arrangements={project.Arrangements.Count}, beats={project.Beats.Count}, audio='{project.AudioFileName}'.");
        // A project whose declared audio is gone loaded to a SILENT transport
        // and said "Loaded Chart" — so pressing play produced silence with no
        // explanation anywhere the user looks (the only record was a log
        // line). Say which file is missing and name the menu route that fixes
        // it; File ▸ Attach audio… is the command that exists for exactly
        // this, and it re-points the project's reference.
        if (!string.IsNullOrEmpty(project.NewerWriterVersion))
        {
            _projectState.SetMessage(L("Status_ProjectFromNewerBuild", project.NewerWriterVersion), StatusSeverity.Warning);
        }
        else
        {
            _projectState.SetMessage(
                missingAudioName.Length > 0
                    ? L("Status_ProjectAudioMissingOnOpen", missingAudioName)
                    : L("Status_LoadedChart", ProjectDisplayName.For(filePath, project.Chart)),
                missingAudioName.Length > 0 ? StatusSeverity.Warning : StatusSeverity.Info);
        }
        // After the swap succeeded — a file that failed to parse or a
        // declined discard prompt must not enter the MRU. Nor may a crash
        // snapshot: see the note beside LastProjectPath above.
        if (!isInternalSnapshot) RecordRecentProject(filePath, project.Chart?.SongName);
    }

    /// <summary>
    ///     Loads the audio, then puts back what the owner had on screen in this project:
    ///     the active arrangement, the ghost and stacked selections and the playhead.
    ///     A crash snapshot is no project of its own and restores nothing.
    /// </summary>
    private async Task<string> LoadAudioAndRestoreViewAsync(ChartProject project, string filePath, bool isInternalSnapshot)
    {
        var missingAudioName = await LoadProjectAudioAsync(project, filePath);
        if (!IsCurrentProject(project, filePath)) return missingAudioName;
        if (!isInternalSnapshot && _viewMemory?.Restore(project, filePath) is { } savedPlayheadMs)
        {
            var playheadMs = Math.Clamp(savedPlayheadMs, 0, Math.Max(0, _projectState.TotalAudioLengthMs));
            _projectState.CurrentTimeMs = playheadMs;
            _audioService.SeekTo(playheadMs);
        }

        return missingAudioName;
    }

    /// <summary>
    ///     Loads the audio the project names (or a silent transport of its length when there is none),
    ///     returning the declared name when that file could not be found, else an empty string.
    /// </summary>
    private async Task<string> LoadProjectAudioAsync(ChartProject project, string filePath)
    {
        if (!ReferenceEquals(project, _projectState.ActiveProject)) return "";
        var audioLoaded = false;
        // Set only when the project NAMES audio we could not find — an
        // audio-less chart (no reference at all) is a legitimate state and
        // must not be nagged about.
        var missingAudioName = "";
        if (!string.IsNullOrEmpty(project.AudioFileName))
        {
            var audioPath = ProjectAudioLocator.ResolveOrDefault(filePath, project.AudioFileName);
            if (File.Exists(audioPath))
            {
                _logger.Info(AppLogCategory.Project,
                    $"Project references audio '{project.AudioFileName}'. Loading from '{audioPath}'.");
                await LoadAudioFile(audioPath, false);
                audioLoaded = true;
            }
            else
            {
                _logger.Warning(AppLogCategory.Project,
                    $"Project references missing audio '{project.AudioFileName}' at '{audioPath}'.");
                missingAudioName = project.AudioFileName;
            }
        }

        if (!audioLoaded)
        {
            // Audio-less chart (RS1 chart-only import, missing/unresolvable
            // audio, or no reference at all): give it a silent transport of
            // the project's length so the playhead, scrubbing, count-in and
            // Play Mode work. CurrentAudioFileName is left as-is (the declared
            // name is preserved for save; empty stays empty), never the silent
            // file — so stem separation / detection never run against silence.
            var generation = Interlocked.Increment(ref _audioLoadGeneration);
            await RunOwnedAudioLoadAsync(project, generation,
                () => _silentTransport.LoadForProjectAsync(project, () => OwnsAudioLoad(project, generation)));
        }

        return missingAudioName;
    }

    /// <summary>Same identity rule as the stem cache
    /// (MainWindowViewModel.BuildStemCacheIdentity): PSARC library songs key
    /// on their stable song identity, everything else on the audio path.</summary>
    private StemCacheIdentity BuildWaveformCacheIdentity(string audioPath)
    {
        var songId = _projectState.CurrentSongIdentity;
        return LibrarySongIdentity.IsLibrary(songId)
            ? StemCacheIdentity.ForLibrary(songId, audioPath)
            : StemCacheIdentity.ForAudioFile(audioPath);
    }

    /// <inheritdoc />
    public async Task<bool> AttachAudioAsync(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            _projectState.SetMessage(L("Status_AttachAudioFailed", sourcePath), StatusSeverity.Error);
            return false;
        }

        var project = _projectState.ActiveProject;
        if (project == null)
        {
            await LoadAudioFile(sourcePath);
            return false;
        }

        var projectPath = _projectState.CurrentProjectPath;
        var existing = _projectState.CurrentAudioFileName;
        var newName = Path.GetFileName(sourcePath);
        if (!string.IsNullOrWhiteSpace(existing) && _messageDialog != null &&
            !await _messageDialog.ShowAsync(L("Dialog_ReplaceAudio_Title"),
                L("Dialog_ReplaceAudio_Message", Path.GetFileName(existing), newName),
                MessageDialogKind.Question, confirmation: true))
        {
            return false;
        }

        // A declined or obsolete prompt never invalidates an accepted engine load.
        if (!ReferenceEquals(project, _projectState.ActiveProject) || projectPath != _projectState.CurrentProjectPath) return false;
        var generation = Interlocked.Increment(ref _audioLoadGeneration);
        bool MayCommit() => OwnsAudioLoad(project, generation) && projectPath == _projectState.CurrentProjectPath;
        var committed = false;
        try
        {
            var (reference, destination) = ProjectAudioPaths.ForAttachment(sourcePath, projectPath, _logger);
            var generateBeats = _chartEditor.Beats.Count == 0;
            var attachment = new AudioAttachment(_audioService, _audioLoads, _logger, ClearPlaybackAudio, L("Status_AttachAudioDecodeRefused"));
            committed = await attachment.LoadAsync(sourcePath, destination, MayCommit, RestoreCurrentPlaybackAsync, waveform =>
            {
                _projectState.CommitSongAudio(project, reference);
                if (!MayCommit()) return;
                _dirtyTracker.MarkDirty();
                PublishLoadedAudio(destination, waveform, generateBeats);
            });
            if (!committed) return false;
            var targetLufs = _configService.LoadAppConfig().ExportAutoVolumeTargetLufs;
            await AttachmentVolumeMeasurement.MeasureAsync(project, destination,
                () => OwnsAudioLoad(project, generation) && _configService.LoadAppConfig().ExportAutoVolumeTargetLufs == targetLufs,
                _logger, targetLufs);
            if (MayCommit())
            {
                RememberAudioDirectory(sourcePath);
                _projectState.MainDisplayMessage = L("Status_AttachedAudio", Path.GetFileName(destination));
            }
            // A caller may delete an unaccepted download on false. Once committed,
            // even an outgoing/saved project can still own that file.
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Audio, $"Failed to attach audio '{sourcePath}'.", ex);
            if (!committed && MayCommit()) _projectState.SetMessage(L("Status_AttachAudioFailed", ex.Message), StatusSeverity.Error);
            return committed;
        }
    }

    private bool OwnsAudioLoad(ChartProject? project, int generation) =>
        !_disposed && ReferenceEquals(project, _projectState.ActiveProject) && generation == Volatile.Read(ref _audioLoadGeneration);

    /// <summary>Remembers the folder an attached audio file came from, so the next
    /// picker starts there. Cosmetic — never fails the attach.</summary>
    private void RememberAudioDirectory(string sourcePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
            if (string.IsNullOrEmpty(directory)) return;
            AppConfig.LastAudioDir = directory;
            SaveAppConfig();
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Settings,
                "Could not remember the last audio directory", ex);
        }
    }

    public Task LoadAudioFile(string filePath, bool generateBeats = true, bool preferMemoryPlayback = false)
    {
        var project = _projectState.ActiveProject;
        var generation = Interlocked.Increment(ref _audioLoadGeneration);
        return RunOwnedAudioLoadAsync(project, generation,
            () => LoadAudioFileCoreAsync(filePath, generateBeats, preferMemoryPlayback, project, generation));
    }

    private Task RunOwnedAudioLoadAsync(ChartProject? project, int generation, Func<Task> load) => _audioLoads.RunAsync(async () =>
    {
        if (!OwnsAudioLoad(project, generation)) return;
        try
        {
            await load();
        }
        finally
        {
            if (!OwnsAudioLoad(project, generation)) await RestoreCurrentPlaybackAsync();
        }
    });

    private void ClearPlaybackAudio() => AudioSessionNotifications.Clear(_audioService, _projectState, _uiDispatcher, () => _disposed);

    private async Task ClearAudioAsync(int generation)
    {
        try
        {
            await _audioLoads.RunAsync(() =>
            {
                if (!_disposed && generation == Volatile.Read(ref _audioLoadGeneration)) ClearPlaybackAudio();
                return Task.CompletedTask;
            });
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Audio, "Could not clear playback", ex);
        }
    }

    private async Task RestoreCurrentPlaybackAsync()
    {
        if (_disposed) return;
        _audioService.FreeMasterAudio();
        _silentTransport.Stop();
        _projectState.WaveformData = WaveformSamples.Empty;
        _projectState.TotalAudioLengthMs = 0;
        if (_projectState.ActiveProject is not { } project ||
            Volatile.Read(ref _lastFreeAudioGeneration) == Volatile.Read(ref _audioLoadGeneration))
        {
            return;
        }
        var reference = project.AudioFileName;
        try
        {
            var path = ProjectAudioLocator.ResolveOrDefault(_projectState.CurrentProjectPath, reference);
            bool IsCurrent() => !_disposed && ReferenceEquals(project, _projectState.ActiveProject) &&
                Volatile.Read(ref _lastFreeAudioGeneration) != Volatile.Read(ref _audioLoadGeneration) &&
                ((string.IsNullOrEmpty(reference) && string.IsNullOrEmpty(project.AudioFileName)) || OwnsDeclaredAudio(project, path));
            if (!string.IsNullOrEmpty(reference) && File.Exists(path))
            {
                await LoadAudioFileCoreAsync(path, false, LibrarySongIdentity.IsLibrary(_projectState.CurrentSongIdentity),
                    project, Volatile.Read(ref _audioLoadGeneration), restoring: true);
            }
            else
            {
                await _silentTransport.LoadForProjectAsync(project, IsCurrent);
            }
            if (!IsCurrent()) _audioService.FreeMasterAudio();
        }
        catch (Exception ex)
        {
            _audioService.FreeMasterAudio();
            _logger.Warning(AppLogCategory.Audio, "Could not reload the previous audio after an abandoned attachment.", ex);
        }
    }

    private bool OwnsDeclaredAudio(ChartProject? project, string path) =>
        !_disposed && project != null && ReferenceEquals(project, _projectState.ActiveProject) &&
        Volatile.Read(ref _lastFreeAudioGeneration) != Volatile.Read(ref _audioLoadGeneration) &&
        string.Equals(Path.GetFullPath(path),
            Path.GetFullPath(ProjectAudioLocator.ResolveOrDefault(_projectState.CurrentProjectPath, project.AudioFileName)),
            StringComparison.OrdinalIgnoreCase);

    private Task GrowSilentAudioAsync(double length)
    {
        var project = _projectState.ActiveProject;
        var generation = Volatile.Read(ref _audioLoadGeneration);
        return RunOwnedAudioLoadAsync(project, generation, async () =>
        {
            if (_silentTransport.LengthMs > 0) await _silentTransport.LoadAsync(length, () => OwnsAudioLoad(project, generation));
        });
    }

    private async Task LoadAudioFileCoreAsync(string filePath, bool generateBeats, bool preferMemoryPlayback,
        ChartProject? project, int generation, bool restoring = false)
    {
        bool MayPublish() => restoring ? OwnsDeclaredAudio(project, filePath) : OwnsAudioLoad(project, generation);
        if (!MayPublish()) return;
        _silentTransport.Stop();
        _audioService.FreeMasterAudio();
        _projectState.TotalAudioLengthMs = 0;
        _projectState.WaveformData = WaveformSamples.Empty;
        _projectState.MainDisplayMessage = L("Status_DecodingAudio", Path.GetFileName(filePath));
        _logger.Info(AppLogCategory.Audio, $"Decoding audio '{filePath}'. GenerateBeats={generateBeats}, MemoryPlayback={preferMemoryPlayback}.");
        // Run audio decode + waveform generation concurrently: the waveform
        // pass reads the file independently and doesn't need the engine
        // source, so on a 3-minute song this roughly halves the cold-load
        // time perceived by the user.
        Task loadTask;
        // Library songs hand the engine the file's BYTES rather than its path,
        // so the engine never holds the cached WAV open (and never needs to:
        // it decodes the song into RAM and frees the blob). The bytes live in
        // NATIVE memory, freed the moment the engine's open returns:
        // as a managed byte[] they were ~50 MB of large-object heap per song
        // that the GC handed back to the OS only much later, if at all.
        //
        // a cached song may already be stored at the device's rate (it skips
        // the per-open upsample). One made for ANOTHER rate, or a damaged one, is
        // re-derived from its .wem first (a whole-song decode: off this thread), so
        // the engine converts at load as before.
        NativeFileBuffer? fileBytes = null;
        var deviceRate = _audioService.ActiveSampleRate;
        var deviceRateAudio = _deviceRateAudio;
        if (preferMemoryPlayback && File.Exists(filePath))
        {
            if (deviceRateAudio != null)
                await Task.Run(() => deviceRateAudio.PrepareForOpen(filePath, deviceRate));
            fileBytes = await NativeFileBuffer.ReadAsync(filePath);
        }
        if (!MayPublish())
        {
            fileBytes?.Release();
            return;
        }
        if (fileBytes != null)
        {
            var hint = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
            loadTask = _audioService.LoadAudioFromMemoryAsync(fileBytes.Memory, hint, filePath);
        }
        else
        {
            loadTask = _audioService.LoadAudioAsync(filePath);
        }

        // Decode/cache independently of the engine; the generation check below
        // prevents an older waveform from settling over a newer load.
        var identity = BuildWaveformCacheIdentity(filePath);
        var waveTask = Task.Run(async () =>
        {
            var cached = _waveformCache?.TryLoad(identity, filePath);
            if (cached != null)
            {
                _logger.Info(AppLogCategory.Audio, $"Waveform served from disk cache for '{filePath}'.");
                return cached;
            }

            var generated = await _audioService.GenerateWaveformAsync(filePath);
            if (generated.HasData) _waveformCache?.Save(identity, filePath, generated);
            return generated;
        });
        Exception? openFailure = null;
        try
        {
            await loadTask;
        }
        catch (Exception ex) when (fileBytes != null && deviceRateAudio != null)
        {
            openFailure = ex;
        }
        finally
        {
            // The engine's open has returned (or failed): it has its own copy.
            fileBytes?.Release();
        }
        if (openFailure != null)
        {
            // the engine refuses a damaged device-rate file rather than play
            // it wrong. Re-derive the original and open that; anything else fails
            // exactly as it did.
            if (!await Task.Run(() => deviceRateAudio!.RecoverAfterFailedOpen(filePath)))
                ExceptionDispatchInfo.Capture(openFailure).Throw();
            _logger.Warning(AppLogCategory.Audio,
                $"Re-opening '{filePath}' from its re-derived original failed", openFailure);
            await OpenLibraryBytesAsync(filePath);
        }
        var convertForRate = fileBytes != null
            ? await SettleDeviceRateAsync(filePath, deviceRate)
            : 0;
        AwaitingWaveformForTesting?.Invoke();
        var waveform = await waveTask;
        if (!MayPublish())
        {
            _audioService.FreeMasterAudio();
            return;
        }
        PublishLoadedAudio(filePath, waveform, generateBeats);
        // The background conversion replaces the file after the waveform reader has closed it.
        if (convertForRate > 0) _ = deviceRateAudio!.ScheduleConversion(filePath, convertForRate);
    }

    /// <summary>Test seam: runs as a load starts waiting for its waveform.</summary>
    internal Action? AwaitingWaveformForTesting { get; set; }

    private void PublishLoadedAudio(string filePath, WaveformSamples waveform, bool generateBeats)
    {
        _projectState.TotalAudioLengthMs = _audioService.GetTotalAudioLengthMs();
        _logger.Info(AppLogCategory.Audio,
            $"Audio decoded. Path='{filePath}', totalLengthMs={_projectState.TotalAudioLengthMs:0.###}.");
        _projectState.WaveformData = waveform;
        _logger.Info(AppLogCategory.Audio,
            $"Waveform generated for '{filePath}'. BaseBlocks={(_projectState.WaveformData?.Mipmaps.Length > 0 ? _projectState.WaveformData.Mipmaps[0].Length : 0)}.");
        _transport.SeekTo(0);

        if (generateBeats)
        {
            _tempoService.GenerateInitialBeats(_chartEditor.Beats, _projectState.TotalAudioLengthMs, _transport.Bpm);
            _logger.Info(AppLogCategory.Audio,
                $"Generated initial beats after audio load. Beat count={_chartEditor.Beats.Count}, bpm={_transport.Bpm:0.###}.");
        }

        _silentTransport.Stop();
        _projectState.MainDisplayMessage = L("Status_LoadedAudio", Path.GetFileName(filePath));
        var project = _projectState.ActiveProject;
        var generation = Volatile.Read(ref _audioLoadGeneration);
        AudioSessionNotifications.Loaded(_projectState, _uiDispatcher, () => OwnsAudioLoad(project, generation));
    }

    /// <summary>
    /// After a library song's open: the device can change rate between the
    /// pick and the open (a device change, the NAM 48 kHz request). A song stored
    /// at the OLD rate would then play through the transport's own linear
    /// resampler, so it is re-derived and re-opened, and the engine converts at
    /// load as it always did. Returns the rate the engine runs at NOW, which the
    /// song is then converted to in the background (0: no conversion service).
    /// </summary>
    private async Task<int> SettleDeviceRateAsync(string filePath, int pickedRate)
    {
        var deviceRateAudio = _deviceRateAudio;
        if (deviceRateAudio == null) return 0;
        var rate = _audioService.ActiveSampleRate;
        if (rate != pickedRate &&
            await Task.Run(() => deviceRateAudio.PrepareForOpen(filePath, rate)).ConfigureAwait(false))
        {
            _logger.Info(AppLogCategory.Audio,
                $"Device rate moved {pickedRate} -> {rate} Hz during the open; re-opening '{filePath}'.");
            await OpenLibraryBytesAsync(filePath).ConfigureAwait(false);
        }

        return rate;
    }

    /// <summary>Reads a library song's bytes into native memory and opens them, as
    /// <see cref="LoadAudioFile"/> does, releasing the bytes once the engine has its
    /// own copy.</summary>
    private async Task OpenLibraryBytesAsync(string filePath)
    {
        var bytes = await NativeFileBuffer.ReadAsync(filePath).ConfigureAwait(false);
        if (bytes == null)
        {
            await _audioService.LoadAudioAsync(filePath).ConfigureAwait(false);
            return;
        }

        try
        {
            await _audioService.LoadAudioFromMemoryAsync(bytes.Memory,
                Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant(), filePath).ConfigureAwait(false);
        }
        finally
        {
            bytes.Release();
        }
    }

    /// <inheritdoc />
    public Task LoadSilentTransportAsync(double projectLengthMs)
    {
        var project = _projectState.ActiveProject;
        var generation = Interlocked.Increment(ref _audioLoadGeneration);
        return RunOwnedAudioLoadAsync(project, generation,
            () => _silentTransport.LoadAsync(projectLengthMs, () => OwnsAudioLoad(project, generation)));
    }

    private void InitializeDefaultProject()
    {
        var defaultArr = new Arrangement { Name = "Lead Guitar" };
        for (var i = 0; i < 6; i++) defaultArr.TuningPitches.Add(new TuningOffset(0));
        defaultArr.Levels.Add(new ChartLevel { DifficultyIndex = 0 });

        var defaultProject = new ChartProject();
        defaultProject.Arrangements.Add(defaultArr);
        defaultProject.Chart = new ChartData { SongName = "New Song" };

        _chartEditor.ApplyProject(defaultProject, resetHistory: true);
        // Same declaration the other replace paths make: this project is not a
        // library song, so it must not inherit the identity of one. Reached from
        // startup AND from a failed startup-project reload, which can follow a
        // session that had a PSARC song open.
        _projectState.CurrentProjectPath = "";
        _projectState.CurrentSongIdentity = "";
        _projectState.CurrentAudioFileName = "";
        // An untouched default project is CLEAN, and saying so is not optional
        // here either. The tracker is baselined when this service is CONSTRUCTED,
        // but this runs later from Initialize() — so the Clear above mints a root
        // id the baseline can never match and the app is dirty from the moment it
        // launches. The title bar showed "*" on a freshly opened window and
        // closing without touching anything raised the unsaved-changes prompt.
        // (Found by smoke-running the v0.42.2 build after fixing the other five
        // replace paths — this one was exempted from the lint on exactly the
        // wrong reasoning, that construction-time baselining covered it.)
        _dirtyTracker.MarkClean();
        _logger.Info(AppLogCategory.Project,
            "Initialized default project with one Lead Guitar arrangement and one difficulty level.");
    }

    private void QueueStartupProjectReload(string startupProject, Task audioReady)
    {
        // Background priority: the reload must not compete with first paint.
        _uiDispatcher.Post(() => _ = ReloadStartupProjectAsync());

        async Task ReloadStartupProjectAsync()
        {
            try
            {
                // The existence probe Initialize() does not make on the UI thread
                // (see there): on a worker, so a parked HDD costs the reload its
                // wake-up and never the first frame.
                if (!await Task.Run(() => StartupProjectExists(startupProject)))
                {
                    InitializeDefaultProject();
                    return;
                }

                // The project load itself touches the audio service (decodes
                // the song, opens stems). Wait for the audio engine init
                // task before kicking off the load so we don't race the
                // engine_create / device-open path. The await is on a
                // worker-thread task, so the UI thread remains responsive
                // and continues pumping render/input events meanwhile.
                _logger.Info(AppLogCategory.Project,
                    $"Attempting startup project reload from '{AppConfig.LastProjectPath}'.");
                try { await audioReady; }
                catch { /* audio init errors are already logged */ }
                await LoadFile(AppConfig.LastProjectPath);
            }
            catch (Exception ex)
            {
                _logger.Error(AppLogCategory.Project,
                    $"Startup project reload failed for '{AppConfig.LastProjectPath}'.", ex);
                _projectState.SetMessage(L("Status_StartupLoadError", ex.Message), StatusSeverity.Error);
                InitializeDefaultProject();
            }
        }
    }

    private async Task InitializeAudioEngineAsync()
    {
        try
        {
            _projectState.MainDisplayMessage = L("Status_InitializingAudio");
            _logger.Info(AppLogCategory.Audio, "Initializing audio engine.");
            // Run device probe + engine bring-up as ONE background unit.
            // Awaiting InitializeAsync directly here resumed the continuation
            // on the UI thread, which at startup is busy inflating MainWindow
            // XAML for well over a second — so ApplyAudioConfiguration (ASIO/
            // WASAPI bring-up) didn't even START until the window was done.
            // Chaining both steps on the worker shaved ~1.5 s off
            // engine-ready time, which also pulls the startup project reload
            // and the queued VST3 restores forward by the same amount.
            await Task.Run(async () =>
            {
                await _audioService.InitializeAsync().ConfigureAwait(false);
                _audioService.ApplyAudioConfiguration(AppConfig);
            }).ConfigureAwait(false);
            var bufferFrames = Math.Clamp(AppConfig.AudioBufferFrames, 16, 8192);
            var sampleRate = _audioService.ActiveSampleRate > 0 ? _audioService.ActiveSampleRate : 44100;
            var bufferMs = bufferFrames * 1000.0 / sampleRate;
            if (_audioService.ActiveSampleRate <= 0 || _audioService.ActualBufferFrames <= 0)
            {
                _logger.Warning(AppLogCategory.Audio,
                    $"Audio engine did not start. RequestedDriver={AppConfig.AudioDriver}, requestedBuffer={bufferFrames} frames (~{bufferMs:0.##}ms), actualBuffer={_audioService.ActualBufferFrames} frames.");
            }
            else
            {
                _logger.Info(AppLogCategory.Audio,
                    $"Audio engine ready. RequestedDriver={AppConfig.AudioDriver}, sampleRate={sampleRate}, requestedBuffer={bufferFrames} frames (~{bufferMs:0.##}ms), actualBuffer={_audioService.ActualBufferFrames} frames, delay={AppConfig.AudioDelayMs:0.###}ms, midiDelay={AppConfig.MidiDelayMs:0.###}ms.");
                // ConfigureAwait(false) above means we may be on a worker
                // here; status-bar bindings must be poked from the UI thread.
                _uiDispatcher.PostNormal(() =>
                    _projectState.MainDisplayMessage = L("Status_AudioEngineReady"));
            }
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Audio, "Audio engine initialization failed.", ex);
            _uiDispatcher.PostNormal(() =>
                _projectState.SetMessage(L("Status_AudioInitError", ex.Message), StatusSeverity.Error));
        }
    }

    private string L(string key) => _localization[key];

    private string L(string key, params object[] args) => _localization.Format(key, args);
}
