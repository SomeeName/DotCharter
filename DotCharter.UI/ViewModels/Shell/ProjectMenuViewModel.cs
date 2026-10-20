// Project and audio-file lifecycle: open / save / save-as / attach audio, the
// one "a file was handed to the app" funnel, the discard prompt shown before
// anything REPLACES the open project, the Open Recent (MRU) list, and
// arrangement + vocal-track creation and deletion.
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Import.Audio;
using DotCharter.Services.Session;
using DotCharter.Services.Plugins;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Shell;
using DotCharter.Tone.UI.ViewModels.Shell;

namespace DotCharter.UI.ViewModels.Shell;

/// <summary>
///     The File menu's project verbs, the Arrangement and Vocals menus' new/delete
///     rows, File ▸ Open Recent and the window's file-drop funnel, exposed as
///     <c>MainWindowViewModel.Project</c>.
///     <para>
///         It also answers the session's "may I replace the open project?"
///         question: <see cref="IProjectSessionService.ConfirmDiscardAsync" /> is
///         assigned here, because the prompt is a hosted dialog and the session
///         cannot name one. It is not on <c>IAppDialogService</c>, which is built
///         from thirty view models that reach back into the session service —
///         injecting it there would close a dependency cycle.
///     </para>
/// </summary>
public sealed partial class ProjectMenuViewModel : ViewModelBase, IDisposable
{
    private readonly IProjectSessionService _projectSession;
    private readonly ProjectState _projectState;
    private readonly ImportMenuViewModel _imports;
    private readonly IChartMaintenanceService _chartMaintenance;
    private readonly IDialogs _hostedDialogs;
    private readonly IUiDispatcher _ui;
    private readonly IAppLogger _logger;
    private readonly ILocalizationService _localization;
    private readonly IPackageFeatures _packageFeatures;

    /// <summary>What this build reads besides its own formats; the palette and the menus prune by it.</summary>
    public IPackageFeatures PackageFeatures => _packageFeatures;
    private readonly IFileStorageService? _fileStorage;
    private readonly IShellLauncher? _shell;
    private readonly IExportTargetWriter _exportFiles;
    private int _recentProjectsGeneration;

    public ProjectMenuViewModel(
        IProjectSessionService projectSession,
        ProjectState projectState,
        ImportMenuViewModel imports,
        IChartMaintenanceService chartMaintenance,
        IDialogs hostedDialogs,
        IUiDispatcher ui,
        IAppLogger logger,
        ILocalizationService localization,
        IPackageFeatures packageFeatures,
        IFileStorageService? fileStorage = null,
        IShellLauncher? shell = null,
        IExportTargetWriter? exportFiles = null)
    {
        _projectSession = projectSession;
        _projectState = projectState;
        _imports = imports;
        _chartMaintenance = chartMaintenance;
        _hostedDialogs = hostedDialogs;
        _ui = ui;
        _logger = logger;
        _localization = localization;
        _packageFeatures = packageFeatures;
        _fileStorage = fileStorage;
        _shell = shell;
        _exportFiles = exportFiles ?? new ExportTargetWriter();

        _projectSession.ConfirmDiscardAsync = ConfirmDiscardOpenProjectAsync;
        _projectSession.RecentProjectsChanged += OnRecentProjectsChanged;
        RefreshRecentProjects();
    }

    /// <summary>Most recent first, already pruned of deleted files — the session
    /// service owns the persisted list, this is its menu-facing projection.</summary>
    public ObservableCollection<RecentProjectItem> RecentProjects { get; } = [];

    /// <summary>False disables the Open Recent submenu header instead of
    /// opening an empty dropdown.</summary>
    public bool HasRecentProjects => RecentProjects.Count > 0;

    public void Dispose() => _projectSession.RecentProjectsChanged -= OnRecentProjectsChanged;

    /// <summary>Open Recent: the session records entries on the open/save funnel (any
    /// thread — the save continuation resumes off a Task.Run), the menu's
    /// collection is UI-thread state, so bounce through the dispatcher.</summary>
    private void OnRecentProjectsChanged() => _ui.Post(RefreshRecentProjects);

    /// <summary>
    ///     Publishes the MRU rows, then prunes them off the UI thread.
    ///     <para>
    ///         This is called from the CONSTRUCTOR, which the composition root runs
    ///         (as a dependency of the shell view model) before
    ///         <c>new MainWindow()</c> — so the pruning <c>File.Exists</c>, one per
    ///         entry, would land on the UI thread before the window existed. A
    ///         cloud-synced or network MRU row blocks that probe for the share's
    ///         timeout; the same shape as the two freezes
    ///         <c>UiThreadIoDeferralTests</c> already pins.
    ///     </para>
    ///     <para>
    ///         Showing an unpruned row for one dispatcher turn is already a
    ///         handled state: <see cref="OpenRecentProjectAsync" /> tests the file
    ///         itself and answers a vanished one with a status message and a
    ///         refresh, because the list can go stale between render and click
    ///         anyway.
    ///     </para>
    /// </summary>
    internal void RefreshRecentProjects()
    {
        _recentProjectsGeneration++;
        PublishRecentProjects(_projectSession.GetRecentProjectsUnverified());
        _ = PruneRecentProjectsAsync();
    }

    private async Task PruneRecentProjectsAsync()
    {
        var generation = _recentProjectsGeneration;
        try
        {
            var pruned = await _projectSession.GetRecentProjectsAsync().ConfigureAwait(false);
            _ui.Post(() =>
            {
                if (generation == _recentProjectsGeneration)
                {
                    PublishRecentProjects(pruned);
                }
            });
        }
        catch (Exception ex)
        {
            // Cosmetic bookkeeping on a discarded task: a failure here must not
            // fault an unobserved task, and the unpruned rows still work (the
            // open funnel re-checks the file).
            _logger.Warning(AppLogCategory.Settings, "Pruning the recent-projects list failed.", ex);
        }
    }

    private void PublishRecentProjects(IReadOnlyList<RecentProjectEntry> entries)
    {
        // Nothing to publish when the rows already ARE these — a rebuild drops
        // and re-realizes every menu container, and the pruned answer is
        // identical whenever every recent project is still on disk, which is the
        // ordinary case.
        if (RecentProjects.Count == entries.Count &&
            !RecentProjects.Where((row, i) => !string.Equals(row.Path, entries[i].Path, StringComparison.Ordinal) ||
                                             row.DisplayName != entries[i].DisplayName)
                .Any())
        {
            return;
        }

        RecentProjects.Clear();
        foreach (var entry in entries)
        {
            RecentProjects.Add(new RecentProjectItem(entry.Path, entry.DisplayName, OpenRecentProjectAsync));
        }

        OnPropertyChanged(nameof(HasRecentProjects));
    }

    /// <summary>
    ///     Opens a recent project through the SAME funnel as File ▸ Open. A file
    ///     that vanished since the list was rendered gets a status message and a
    ///     list refresh instead of the project loader's "error loading project".
    /// </summary>
    public async Task OpenRecentProjectAsync(string path)
    {
        if (!_exportFiles.FileExists(path))
        {
            _projectState.MainDisplayMessage = _localization.Format("Status_RecentProjectMissing", Path.GetFileName(path));
            RefreshRecentProjects();
            return;
        }

        await LoadFile(path);
    }

    /// <summary>
    ///     The ONE funnel for "the app was handed a file" — the window's file DROP
    ///     and File ▸ Open Recent both land here. Which subsystem gets it is decided
    ///     by <see cref="DroppedFileRouter.Route" /> and NOWHERE else: this method
    ///     may not test an extension itself (pinned by
    ///     <c>DotCharter.Tests/UI/DroppedFileRoutingTests</c>).
    ///     <para>
    ///     Only <c>.eof</c> was routed before; every other importable file
    ///     (<c>.gp*</c>, <c>.mid</c>, RS <c>.xml</c>, <c>.psarc</c>) fell into the
    ///     project loader and came back as "Error loading project" — a corrupt-file
    ///     message for a file the app has a whole importer for.
    ///     </para>
    /// </summary>
    public async Task LoadFile(string filePath)
    {
        switch (DroppedFileRouter.Route(filePath, _packageFeatures.ReadsGamePackages))
        {
            case DroppedFileRoute.EofImport:
                _logger.Info(AppLogCategory.Import,
                    $"Editor on Fire file dropped/opened. Routing to EOF import dialog. Path='{filePath}'.");
                _imports.OpenEofImport(filePath);
                return;

            case DroppedFileRoute.MidiImport:
                // The launcher refuses, and says so, when no project is open — a
                // MIDI draft has nothing to be added to.
                _logger.Info(AppLogCategory.Import,
                    $"MIDI file dropped/opened. Routing to MIDI import dialog. Path='{filePath}'.");
                _imports.OpenMidiImport(filePath);
                return;

            case DroppedFileRoute.RocksmithXmlImport:
                // With a project open this MERGES (song / vocals / showlights all
                // land correctly). With none, there is nothing to merge into, so
                // fall through to the project loader — its "xml" serializer reads
                // an RS-XML song as a fresh project, which is today's behaviour.
                if (_projectState.ActiveProject != null)
                {
                    _logger.Info(AppLogCategory.Import,
                        $"Rocksmith XML dropped/opened. Merging into the open project. Path='{filePath}'.");
                    _imports.OpenRsXmlImport(filePath);
                    return;
                }

                break;

            case DroppedFileRoute.GuitarProImport:
                _logger.Info(AppLogCategory.Import,
                    $"Guitar Pro file dropped/opened. Routing to GP import dialog. Path='{filePath}'.");
                _imports.OpenGpImport(filePath);
                return;

            case DroppedFileRoute.RocksmithPackage:
                _logger.Info(AppLogCategory.Import,
                    $"Rocksmith package dropped/opened; there is no single-file load seam. Path='{filePath}'.");
                _projectState.MainDisplayMessage =
                    _localization.Format("Status_DroppedPsarcNotSupported", Path.GetFileName(filePath));
                return;

            case DroppedFileRoute.Unsupported:
                var extension = DroppedFileRouter.ExtensionOf(filePath);
                _logger.Info(AppLogCategory.Project,
                    $"Dropped/opened file has no route (extension='{extension}'). Path='{filePath}'.");
                _projectState.MainDisplayMessage = extension.Length > 0
                    ? _localization.Format("Status_DroppedUnsupportedFileType", extension, Path.GetFileName(filePath))
                    : _localization.Format("Status_DroppedUnknownFileType", Path.GetFileName(filePath));
                return;
        }

        await _projectSession.LoadFile(filePath);
    }

    [RelayCommand]
    private void RevealProjectInExplorer()
    {
        var projectPath = _projectState.CurrentProjectPath;
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return;
        }

        try
        {
            _shell?.RevealInFileManager(projectPath);
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Project, $"Revealing the project in the file manager failed. Path='{projectPath}'.", ex);
        }
    }

    [RelayCommand]
    private async Task OpenProject() => await _projectSession.OpenProject();

    /// <summary>
    ///     "File → Attach audio…": picks a song file and attaches it to the open
    ///     project. This is the menu route that simply did not exist — a project
    ///     created without audio had NO way to gain any (the drop path only
    ///     auditioned, and only .wav/.ogg at that).
    /// </summary>
    [RelayCommand]
    private async Task AttachAudio()
    {
        if (_fileStorage == null)
        {
            _logger.Warning(AppLogCategory.Audio, "Attach audio unavailable: no file storage service.");
            return;
        }

        var path = await _fileStorage.OpenFilePickerAsync(
            _localization["Picker_SelectAudioFile"],
            new[]
            {
                new FilePickerFilter(_localization["Picker_AudioFiles"], AudioFileTypes.PickerPatterns),
                FilePickerFilters.AllFiles
            },
            _projectSession.AppConfig.LastAudioDir);

        if (string.IsNullOrEmpty(path))
        {
            _logger.Info(AppLogCategory.Audio, "Attach audio canceled at the file picker.");
            return;
        }

        await _projectSession.AttachAudioAsync(path);
    }

    [RelayCommand]
    private async Task SaveProject() => await _projectSession.SaveProjectAsync();

    [RelayCommand]
    private async Task SaveProjectAs() => await _projectSession.SaveProjectAsAsync();

    /// <summary>Save for the close prompt: true when the project ended up
    /// safely on disk, false when the user canceled the Save As picker or the
    /// save failed — the caller must then abort the close.</summary>
    public Task<bool> SaveProjectForCloseAsync() => _projectSession.SaveProjectAsync();

    /// <summary>Save / Don't save / Cancel over unsaved work; Esc answers Cancel.
    /// Asked before the window closes and before anything replaces the open project.</summary>
    internal Task<UnsavedChangesChoice> AskAboutUnsavedChangesAsync() => _hostedDialogs.ShowAsync<IUnsavedChangesScreen, UnsavedChangesChoice>();

    /// <summary>
    ///     The prompt shown before an operation REPLACES the open project.
    ///     <para>
    ///         Reuses the close-time dialog deliberately: "you have unsaved work,
    ///         what do you want to do with it" is the same question whether the
    ///         session is ending or being replaced, and a second wording would
    ///         only make the two look like different guarantees.
    ///     </para>
    ///     <para>
    ///         Returns true to proceed. Save that fails or is cancelled at the
    ///         picker returns FALSE — the caller must not go on to discard work
    ///         the user just asked to keep.
    ///     </para>
    /// </summary>
    private async Task<bool> ConfirmDiscardOpenProjectAsync()
    {
        switch (await AskAboutUnsavedChangesAsync())
        {
            case UnsavedChangesChoice.Save:
                return await _projectSession.SaveProjectAsync();
            case UnsavedChangesChoice.Discard:
                return true;
            default:
                return false;
        }
    }

    [RelayCommand]
    private void NewArrangement()
    {
        _logger.Info(AppLogCategory.Project, "Opening new arrangement dialog.");
        Launch(() => _hostedDialogs.ShowAsync(new ArrangementSettingsRequest(CreateNew: true)), "new arrangement");
    }

    /// <summary>
    ///     Deleting an arrangement is the most destructive thing in the app, so it
    ///     goes through the maintenance service's project snapshot — one undo node.
    ///     It must not mutate the collection here directly, which would mean Ctrl+Z could
    ///     not bring it back.
    /// </summary>
    [RelayCommand]
    private void DeleteArrangement() => _chartMaintenance.DeleteActiveArrangement();

    [RelayCommand]
    private void NewVocalTrack()
    {
        _logger.Info(AppLogCategory.Project, "Opening new vocal track dialog.");
        Launch(() => DialogRequests.ShowAsync(_hostedDialogs, new VocalTrackSettingsRequest(CreateNew: true)), "new vocal track");
    }

    /// <summary>Undoable, for the same reason as <see cref="DeleteArrangement" />.</summary>
    [RelayCommand]
    private void DeleteVocalTrack() => _chartMaintenance.DeleteActiveVocalTrack();

    /// <summary>Opens a dialog nobody awaits; a fault is logged rather than lost.</summary>
    private void Launch(Func<Task> show, string what) => DialogLaunch.Run(_logger, show, what);
}
