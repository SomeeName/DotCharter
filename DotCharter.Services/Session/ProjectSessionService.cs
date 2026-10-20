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
    private readonly DetachedCompanionBuilder _detachedCompanions;
    private readonly SilentTransportSession _silentTransport;
    private readonly ProjectAutoBackup _autoBackup;
    private readonly LibraryDeviceRateAudio? _deviceRateAudio;
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
        WaveformDiskCache? waveformCache = null,
        SilentAudioTrack? silentAudio = null,
        IMessagePrompt? messageDialog = null,
        IStatusBarService? statusBar = null,
        SongToneEditStore? songToneEdits = null,
        ICheckpointService? checkpoints = null,
        LibraryDeviceRateAudio? deviceRateAudio = null)
    {
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
            silentAudio ?? new SilentAudioTrack(logger));
        _messageDialog = messageDialog;
        _detachedCompanions = new DetachedCompanionBuilder(saveCompanions, uiDispatcher, projectState, logger, _localization);

        // Dirty state rides the SAME change signal the crash-recovery
        // scheduler uses (the undo history) — no parallel mutation tracking.
        _dirtyTracker = new ProjectDirtyTracker(historyService);
        _autoBackup = new ProjectAutoBackup(configService, _dirtyTracker, documentService, projectState, transport,
            chartEditor, uiDispatcher, logger, _localization);
        Saver = new ProjectSaver(configService, documentService, saveCompanions, crashRecovery, storageService,
            projectState, transport, chartEditor, _dirtyTracker, _detachedCompanions, uiDispatcher, logger,
            _localization, messageDialog, statusBar, this, songToneEdits ?? SongToneEditStore.Default, checkpoints);
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
    ///     hosts with no prompt wired never ask.
    /// </summary>
    public Task<bool> MayReplaceOpenProjectAsync() => ShouldProceedAsync(IsProjectDirty, ConfirmDiscardAsync);

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
                $"Could not record the recent-project entry: {ex.Message}");
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
        _logger.Info(AppLogCategory.Audio, "FreeAudio requested.");
        _audioService.FreeAudio();
    }

    public async Task OpenProject()
    {
        _logger.Info(AppLogCategory.Project, "Open project/audio command invoked.");
        var startDir = !string.IsNullOrEmpty(AppConfig.LastRscpDir) ? AppConfig.LastRscpDir : AppConfig.LastProjectPath;
        // File ▸ Open was the ONE picker in the app that passed no filters at all
        // — every other one names its types — so the single dialog the user opens
        // most often was an unfiltered folder listing to hunt a `.rscp` in. The
        // first entry spans BOTH kinds because this router accepts both (audio
        // attaches to the open project, or auditions without one), so the default
        // view shows everything the command can actually take.
        var filters = new List<FilePickerFilter>
        {
            new(L("Picker_ProjectOrAudioFiles"),
                new[] { "*.rscp", "*" + DcSongFormat.Extension }.Concat(AudioFileTypes.PickerPatterns).ToArray()),
            new(L("Picker_ProjectFiles"), new[] { "*.rscp" }),
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

    public Task<bool> SaveProjectAsync() => Saver.SaveWithGuardAsync(false);

    public Task<bool> SaveProjectAsAsync() => Saver.SaveWithGuardAsync(true);

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

    public Task LoadFile(string filePath) => LoadFileAsync(filePath, replaceConfirmed: false);

    /// <param name="filePath">A project, audio or <c>.dcsong</c> file.</param>
    /// <param name="replaceConfirmed">True when the caller already asked before
    /// replacing the open project (the <c>.dcsong</c> opener asks before its folder
    /// picker), so the user is not asked twice.</param>
    private async Task LoadFileAsync(string filePath, bool replaceConfirmed)
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
                return;
            }

            _logger.Info(AppLogCategory.Audio, $"Loading dropped/opened audio file '{filePath}'.");
            await LoadAudioFile(filePath);
            return;
        }

        // A song package opens as a NEW project folder it unpacks first.
        if (string.Equals(Path.GetExtension(filePath), DcSongFormat.Extension, StringComparison.OrdinalIgnoreCase))
        {
            await _dcSongOpener.OpenAsync(filePath);
            return;
        }

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

        // Whether the session was actually REPLACED, which is what decides
        // whether the finally below may speak for it. A failure before
        // ApplyProject leaves the previous project open with its own edits, and
        // declaring THAT clean would discard them at the close prompt.
        var sessionReplaced = false;
        var isInternalSnapshot = IsRecoverySnapshotPath(filePath);

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
            var project = await Task.Run(() =>
            {
                var loaded = _documentService.LoadChart(filePath);
                ChartSpacingEnforcer.EnforceProject(loaded, _configService.LoadAppConfig());
                return loaded;
            });
            _logger.Info(AppLogCategory.Project,
                $"Project spacing enforced after load. Arrangements={project.Arrangements.Count}, beats={project.Beats.Count}, bpm={project.Bpm:0.###}.");
            _projectState.CurrentProjectPath = filePath;
            // The library song identity belongs to the PREVIOUS session.
            // SongBrowserViewModel.LoadSongItem is its only writer, and nothing
            // cleared it — so after loading one PSARC song every own-chart project
            // opened afterwards still carried `psarc:<SongKey>`, and
            // BuildWaveformCacheIdentity (which reads the identity BEFORE the
            // project path) keyed each one's waveform pyramid under the library
            // song: cache pollution plus a permanent miss for the real project.
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
            sessionReplaced = true;
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
                await _silentTransport.LoadForProjectAsync(project);
            }

            _logger.Info(AppLogCategory.Project,
                $"Loaded project '{filePath}'. Song='{project.Chart.SongName}', arrangements={project.Arrangements.Count}, beats={project.Beats.Count}, audio='{project.AudioFileName}'.");
            // A project whose declared audio is gone loaded to a SILENT transport
            // and said "Loaded Chart" — so pressing play produced silence with no
            // explanation anywhere the user looks (the only record was a log
            // line). Say which file is missing and name the menu route that fixes
            // it; File ▸ Attach audio… is the command that exists for exactly
            // this, and it re-points the project's reference.
            _projectState.SetMessage(
                missingAudioName.Length > 0
                    ? L("Status_ProjectAudioMissingOnOpen", missingAudioName)
                    : L("Status_LoadedChart", ProjectDisplayName.For(filePath, project.Chart)),
                missingAudioName.Length > 0 ? StatusSeverity.Warning : StatusSeverity.Info);
            // After the swap succeeded — a file that failed to parse or a
            // declined discard prompt must not enter the MRU. Nor may a crash
            // snapshot: see the note beside LastProjectPath above.
            if (!isInternalSnapshot) RecordRecentProject(filePath, project.Chart?.SongName);
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Project, $"Error loading project '{filePath}'.", ex);
            _projectState.SetMessage(L("Status_LoadProjectError", ex.Message), StatusSeverity.Error);
        }
        finally
        {
            // The pill must go on EVERY exit path — a load that threw would
            // otherwise leave "Opening project…" on the rail forever.
            _statusBar?.CompleteTask(LoadTaskId);
            // EVERY exit path, not just the happy one. Neither of these may sit
            // at the end of the try, after fallible audio I/O (the decode throws,
            // the file is unreadable) — so a project whose audio would not load
            // leaves the chart on screen, correct and untouched, while:
            //   * IsDirty is `CurrentNodeId != _baselineNodeId` and ApplyProject
            //     had just minted a NEW root id, so the baseline could never match
            //     again — the title showed `*` forever and closing raised "unsaved
            //     changes" over a chart nobody had edited;
            //   * CrashRecoveryService watches the same flag, so it started
            //     snapshotting that chart and the restore prompt came back on
            //     every launch;
            //   * the PREVIOUS project's snapshot was never dropped, so what the
            //     prompt offered to restore was the wrong session entirely.
            // Guarded on sessionReplaced: before ApplyProject there is nothing new
            // to describe and the old project's dirty state is still the truth.
            if (sessionReplaced)
            {
                // Clean project switch: the previous session's crash snapshot no
                // longer describes open work. This also swallows the dirty flag
                // raised by history churn during ApplyProject, so merely opening
                // a project never writes a snapshot.
                _crashRecovery.NotifyProjectSwitched();
                // The freshly loaded CHART is the on-disk chart — audio that
                // failed to load is not an unsaved edit.
                _dirtyTracker.MarkClean();
            }
        }
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
            _logger.Warning(AppLogCategory.Audio,
                $"Attach audio ignored: '{sourcePath}' does not exist.");
            _projectState.SetMessage(L("Status_AttachAudioFailed", sourcePath), StatusSeverity.Error);
            return false;
        }

        var project = _projectState.ActiveProject;
        if (project == null)
        {
            // Nothing to attach TO — audition it so the drop still does something.
            _logger.Info(AppLogCategory.Audio,
                $"No active project; auditioning '{sourcePath}' instead of attaching it.");
            await LoadAudioFile(sourcePath);
            return false;
        }

        var newName = Path.GetFileName(sourcePath);
        var existing = _projectState.CurrentAudioFileName;
        if (!string.IsNullOrWhiteSpace(existing) && _messageDialog != null)
        {
            // Replacing the song audio re-bases everything already charted against
            // it, so never do it silently on a stray drop.
            var confirmed = await _messageDialog.ShowAsync(
                L("Dialog_ReplaceAudio_Title"),
                L("Dialog_ReplaceAudio_Message", Path.GetFileName(existing), newName),
                MessageDialogKind.Question,
                confirmation: true);
            if (!confirmed)
            {
                _logger.Info(AppLogCategory.Audio,
                    $"Attach audio canceled by user (would have replaced '{existing}' with '{newName}').");
                return false;
            }
        }

        try
        {
            // Release the previous source (or the silent transport an audio-less
            // chart is running) BEFORE loading the new one, so the old engine
            // source is never left dangling. The silent WAV was engine-only and
            // was never the audio identity, so there is no stale reference to
            // clean up.
            FreeAudio();

            var projectDirectory = GetProjectDirectory();
            string audioReference;
            string audioPathToLoad;

            if (!string.IsNullOrEmpty(projectDirectory))
            {
                Directory.CreateDirectory(projectDirectory);
                if (IsInsideDirectory(sourcePath, projectDirectory))
                {
                    // Already living in the project folder — reference it in place
                    // rather than copying a file onto itself.
                    audioReference = newName;
                    audioPathToLoad = Path.GetFullPath(sourcePath);
                }
                else
                {
                    // Deliberately 0/0 silence: unlike NEW-project creation, the
                    // chart here already has beats and notes placed against a time
                    // base. Baking leading silence would shift the audio out from
                    // under all of them, so an attach is always a verbatim copy and
                    // Chart.MainAudioOffsetAdded is left alone.
                    var prepared = ImportAudioPreparer.Prepare(
                        sourcePath, projectDirectory, leadingSeconds: 0, trailingSeconds: 0,
                        logWarning: w => _logger.Warning(AppLogCategory.Audio, w));
                    audioReference = prepared.WorkingAudioFileName;
                    audioPathToLoad = Path.Combine(projectDirectory, audioReference);
                }
            }
            else
            {
                // Unsaved session with no folder to copy into. Store the ABSOLUTE
                // source path instead of forcing a Save As mid-attach: both audio
                // resolvers (ResolveProjectAudioPath here, ResolveCurrentAudioPath
                // in ChartMaintenanceService) already accept a rooted
                // AudioFileName, so this round-trips through save/reopen — while a
                // forced picker would make an audio drop cancelable by a dialog
                // the user never asked for.
                audioReference = Path.GetFullPath(sourcePath);
                audioPathToLoad = audioReference;
                _logger.Info(AppLogCategory.Audio,
                    "Project has no folder yet; attaching audio by absolute path. " +
                    "Saving the project does not relocate it.");
            }

            // The pairing that was missing entirely: without BOTH of these the
            // audio plays but the save writes the "song.wav" fallback and the
            // reopened project finds nothing.
            project.AudioFileName = audioReference;
            _projectState.CurrentAudioFileName = audioReference;

            // An audio-less project already carries a beat grid (the new-project
            // flow lays one down), and regenerating would throw away tempo work.
            // Only a genuinely empty grid gets the default one.
            var generateBeats = _chartEditor.Beats.Count == 0;
            await LoadAudioFile(audioPathToLoad, generateBeats);

            RememberAudioDirectory(sourcePath);
            _dirtyTracker.MarkDirty();
            _logger.Info(AppLogCategory.Audio,
                $"Attached audio to project. source='{sourcePath}', reference='{audioReference}', " +
                $"loaded='{audioPathToLoad}', generatedBeats={generateBeats}.");
            _projectState.MainDisplayMessage = L("Status_AttachedAudio", Path.GetFileName(audioPathToLoad));
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Audio, $"Failed to attach audio '{sourcePath}'.", ex);
            _projectState.SetMessage(L("Status_AttachAudioFailed", ex.Message), StatusSeverity.Error);
            return false;
        }
    }

    /// <summary>The folder the project lives in, or null for a session that has
    /// never been saved (nothing to copy audio into).</summary>
    private string? GetProjectDirectory()
    {
        var projectPath = _projectState.CurrentProjectPath;
        if (string.IsNullOrWhiteSpace(projectPath)) return null;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
            return string.IsNullOrWhiteSpace(directory) ? null : directory;
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Project,
                $"Could not derive a project directory from '{projectPath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>True when <paramref name="filePath" /> sits directly in
    /// <paramref name="directory" /> (so copying it there would copy it onto
    /// itself).</summary>
    private static bool IsInsideDirectory(string filePath, string directory)
    {
        try
        {
            var fileDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (string.IsNullOrEmpty(fileDirectory)) return false;
            return string.Equals(
                Path.TrimEndingDirectorySeparator(fileDirectory),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

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
                $"Could not remember the last audio directory: {ex.Message}");
        }
    }

    public async Task LoadAudioFile(string filePath, bool generateBeats = true, bool preferMemoryPlayback = false)
    {
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
                await Task.Run(() => deviceRateAudio.PrepareForOpen(filePath, deviceRate)).ConfigureAwait(false);
            fileBytes = await NativeFileBuffer.ReadAsync(filePath).ConfigureAwait(false);
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

        // 0.14 P4: generate-once waveform. Cache-first (keyed like the stem
        // cache: song identity + audio content signature); a miss generates
        // from the FILE (the native pass streams it; on the library path the
        // read above has just put it in the OS cache) and saves best-effort.
        // The task stays on the open's critical path (awaited below), so no
        // load-generation token is needed: a stale waveform can't land on a
        // newer song.
        var identity = BuildWaveformCacheIdentity(filePath);
        var waveTask = Task.Run(async () =>
        {
            var cached = _waveformCache?.TryLoad(identity, filePath);
            if (cached != null)
            {
                _logger.Info(AppLogCategory.Audio, $"Waveform served from disk cache for '{filePath}'.");
                return cached;
            }

            var generated = await _audioService.GenerateWaveformAsync(filePath).ConfigureAwait(false);
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
            if (!await Task.Run(() => deviceRateAudio!.RecoverAfterFailedOpen(filePath)).ConfigureAwait(false))
                ExceptionDispatchInfo.Capture(openFailure).Throw();
            _logger.Warning(AppLogCategory.Audio,
                $"Re-opening '{filePath}' from its re-derived original after: {openFailure.Message}");
            await OpenLibraryBytesAsync(filePath).ConfigureAwait(false);
        }
        var convertForRate = fileBytes != null
            ? await SettleDeviceRateAsync(filePath, deviceRate).ConfigureAwait(false)
            : 0;
        _projectState.TotalAudioLengthMs = _audioService.GetTotalAudioLengthMs();
        _logger.Info(AppLogCategory.Audio,
            $"Audio decoded. Path='{filePath}', totalLengthMs={_projectState.TotalAudioLengthMs:0.###}.");
        _projectState.WaveformData = await waveTask;
        // the background conversion REPLACES the file, so only once the
        // waveform pass has stopped reading it.
        if (convertForRate > 0) _ = deviceRateAudio!.ScheduleConversion(filePath, convertForRate);
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
    public Task LoadSilentTransportAsync(double projectLengthMs) => _silentTransport.LoadAsync(projectLengthMs);

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
