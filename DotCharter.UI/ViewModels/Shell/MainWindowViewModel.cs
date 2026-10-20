// The shell root: the injected services, the constructor that wires the whole
// shell together, the child view models it exposes, top-level tab switching and
// the Esc chain. Everything else lives in MainWindowViewModel.<Topic>.cs.
using DotCharter.Presentation.Contracts.Shell;
using DotCharter.Presentation.Shared;
using DotCharter.Services.Diagnostics;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.UI.ViewModels.Shell;
using DotCharter.UI.ViewModels.CommandPalette;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.UI.Input;
using DotCharter.UI.ViewModels.Application;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Session;

namespace DotCharter.UI.ViewModels.Shell;

public partial class MainWindowViewModel : ViewModelBase, IDisposable, IShortcutCatalog
{
    private const int ChartEditorTabIndex = 0;
    private const int SongBrowserTabIndex = 1;
    private const int SettingsTabIndex = 2;
    private const int PlayModeTabIndex = 3;
    private const int HomeTabIndex = 4;
    private readonly IUiDispatcher _ui;
    private readonly IChartMaintenanceService _chartMaintenance;
    private readonly DotCharter.Services.Editing.Structure.IStructureGenerationService _structureGeneration;
    private readonly IEventEditingService _eventEditing;
    private readonly IGlobalEditingCoordinator _globalEditing;
    private readonly IHighwayEditingService _highwayEditing;
    private readonly IChartPenService _pen;
    private readonly IAppLogger _logger;
    private readonly ILocalizationService _localization;

    private readonly DotCharter.Presentation.Shared.SettingsSession _settings;

    /// <summary>The four file importers' doors; null only where a test builds the shell by hand.</summary>

    private readonly DotCharter.Presentation.Screens.Dialogs.RecoveryLauncher? _recovery;

    /// <summary>Supplies the KNOWN keybinds, i.e. the denominator for "never pressed".</summary>

    private readonly IProjectSessionService _projectSession;


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChartEditorVisible))]
    [NotifyPropertyChangedFor(nameof(IsSongBrowserVisible))]
    [NotifyPropertyChangedFor(nameof(IsSettingsVisible))]
    [NotifyPropertyChangedFor(nameof(IsPlayModeVisible))]
    [NotifyPropertyChangedFor(nameof(IsHomeVisible))]
    [NotifyPropertyChangedFor(nameof(IsShellChromeVisible))]
    [NotifyPropertyChangedFor(nameof(IsActivityRailVisible))]
    [NotifyPropertyChangedFor(nameof(TitleBarRowHeight))]
    [NotifyPropertyChangedFor(nameof(IsBackBarVisible))]
    [NotifyPropertyChangedFor(nameof(IsWindowCaptionVisible))]
    [NotifyPropertyChangedFor(nameof(Is3DPreviewActive))]
    [NotifyPropertyChangedFor(nameof(IsToolbarVisible))]
    private int _selectedMainTabIndex;

    public MainWindowViewModel(
        IAppLogger logger,
        IProjectSessionService projectSession,
        IChartMaintenanceService chartMaintenance,
        DotCharter.Services.Editing.Structure.IStructureGenerationService structureGeneration,
        ProjectState projectState,
        TransportViewModel transport,
        InspectorViewModel inspector,
        ChartEditorViewModel chartEditor,
        SongBrowserViewModel songBrowser,
        AudioPlaybackCoordinator playbackCoordinator,
        ChartKeybindHandler keybindHandler,
        MultiTrackOverlayViewModel multiTrackOverlay,
        IGlobalEditingCoordinator globalEditing,
        IHighwayEditingService highwayEditing,
        IEventEditingService eventEditing,
        IChartPenService pen,
        IAppDialogService dialogs,
        StemMixerViewModel stemMixer,
        StatusBarViewModel statusBar,
        ITimelineInteractionFactory interactionFactory,
        IUiDispatcher ui,
        LivePlayViewModel livePlay,
        SheetMusicPaneViewModel sheetMusicPane,
        PhraseCatalogViewModel phraseCatalog,
        PlayModeViewModel playMode,
        StemSeparationViewModel stemSeparation,
        HelpMenuViewModel help,
        ImportMenuViewModel imports,
        ToneRigViewModel toneRig,
        ExportMenuViewModel export,
        ProjectMenuViewModel project,
        ShellDialogsViewModel shellDialogs,
        DotCharter.Presentation.Shared.SettingsSession? settingsSession = null,
        DotCharter.Presentation.Screens.Dialogs.RecoveryLauncher? recovery = null,
        DotCharter.Services.Gameplay.Profiles.IPlayerProfileService? playerProfiles = null)
    {
        _recovery = recovery;
        _settings = settingsSession ?? new DotCharter.Presentation.Shared.SettingsSession();
        _logger = logger;
        // The shell's own words come from its status rail's localization rather
        // than a parameter of their own: the shell is over its dependency budget
        // (MainWindowViewModelDependencyBudgetTests), and it is the same instance.
        _localization = statusBar.Localization;
        InteractionFactory = interactionFactory;
        _ui = ui;
        _projectSession = projectSession;
        _chartMaintenance = chartMaintenance;
        _structureGeneration = structureGeneration;
        _globalEditing = globalEditing;
        _highwayEditing = highwayEditing;
        _eventEditing = eventEditing;
        _pen = pen;
        StemMixer = stemMixer;
        StatusBar = statusBar;

        ProjectState = projectState;
        Transport = transport;
        LivePlay = livePlay;
        Inspector = inspector;
        ChartEditor = chartEditor;
        SongBrowser = songBrowser;
        SheetMusicPane = sheetMusicPane;
        PhraseCatalog = phraseCatalog;
        ProjectState.PropertyChanged += (_, e) =>
        {
            // Forward the ORIGINAL args object: ProjectState raises its 60 Hz
            // CurrentTimeMs with a cached PropertyChangedEventArgs, and the
            // string overload would allocate a fresh instance per raise,
            // undoing that optimization at playback rate.
            OnPropertyChanged(e);
            if (e.PropertyName is nameof(ProjectState.ActiveArrangement) or nameof(ProjectState.ActiveProject))
            {
                OnPropertyChanged(nameof(ActiveArrangementName));
            }
            if (e.PropertyName is nameof(ProjectState.TotalAudioLengthMs)
                or nameof(ProjectState.ActiveProject) or nameof(ProjectState.ActiveArrangement)
                or nameof(ProjectState.Beats) or nameof(ProjectState.CurrentProjectPath))
            {
                OnPropertyChanged(nameof(IsEditorLaunchCardVisible));
                OnPropertyChanged(nameof(SongMapFacts));
            }
        };
        // Title-bar dirty marker: the session derives dirty from the undo
        // history, so this fires on the UI thread (edits are UI-driven).
        // StatusBar carries the visible dot (title-bar context + status rail);
        // WindowTitle carries the taskbar/alt-tab asterisk.
        StatusBar.DirtyStateSource = () => _projectSession.IsProjectDirty;
        StatusBar.RefreshDirtyState();
        StatusBar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StatusBar.ProjectName)) OnPropertyChanged(nameof(WindowTitle));
        };
        _projectSession.ProjectDirtyChanged += () =>
        {
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(IsProjectDirty));
            StatusBar.RefreshDirtyState();
        };
        // The status rail's ⚠/⛔ chips show the CURRENT chart's lint issues
        // (counting log lines since launch would climb
        // during pure browsing). IssueSummary is re-announced on every debounced
        // InspectorIssuesViewModel.RecomputeIssues, so this rides the inspector's own 150 ms cadence —
        // no extra timer, and a fresh empty project pushes 0/0. The chips open
        // the Issues tab; the delegate keeps StatusBarViewModel shell-agnostic.
        StatusBar.OpenChartIssues = () =>
        {
            SelectedMainTabIndex = ChartEditorTabIndex;
            ShowInspectorTab(InspectorViewModel.IssuesTabIndex);
        };

        Inspector.Issues.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(Editor.Inspector.InspectorIssuesViewModel.IssueSummary)) return;
            var important = 0;
            var warnings = 0;
            foreach (var issue in Inspector.Issues.Items)
            {
                if (issue.Severity == ArrangementIssueSeverity.Important) important++;
                else warnings++;
            }

            StatusBar.SetChartIssueCounts(important, warnings);
        };

        SongBrowser.SongLoaded += () => SelectedMainTabIndex = ChartEditorTabIndex;
        PlayMode = playMode;
        StemSeparation = stemSeparation;
        StemSeparation.StemsApplied += OnStemsApplied;
        Help = help;
        Imports = imports;
        ToneRig = toneRig;
        Export = export;
        Project = project;
        ShellDialogs = shellDialogs;
        // Tools ▸ Fix & clean up ▸ AI settings… swaps to the Settings tab like every other door.
        if (ShellDialogs.AiChart is { } aiChart) aiChart.OpenSettingsRequested += OpenSettings;
        if (ShellDialogs.AiChat is { } aiChat)
        {
            aiChat.Chat.OpenSettingsRequested += OpenSettings;
            // the Issues tab's Fix / Explain with AI answer in the chat, so it shows.
            aiChat.Chat.ShowRequested += () => IsAiChatVisible = true;
        }
        ToneRig.ShellChromeVisibleSource = () => IsShellChromeVisible;
        ToneRig.OpenSettingsRequested += OpenSettings;
        Help.CommandUsageIds = () => CommandCatalog.Build(this, _localization).Select(c => c.UsageId);
        PlayMode.ExitToEditorRequested += () =>
        {
            IsGameplayPresentation = false;
            SelectedMainTabIndex = ChartEditorTabIndex;
        };
        // Start's button, Esc on Song Select and the pause menu's Home all
        // land on Home, which took the retired Play Mode menu's place.
        PlayMode.HomeRequested += () =>
        {
            IsGameplayPresentation = false;
            SelectedMainTabIndex = HomeTabIndex;
        };
        // Gameplay runs in the editor surface but presented Rocksmith-style:
        // highway + HUD only, all charting chrome collapsed until results.
        PlayMode.ShowEditorSurfaceRequested += () =>
        {
            SelectedMainTabIndex = ChartEditorTabIndex;
            IsGameplayPresentation = true;
        };
        PlayMode.ShowPlayModeSurfaceRequested += () =>
        {
            IsGameplayPresentation = false;
            SelectedMainTabIndex = PlayModeTabIndex;
        };
        PlayMode.RiffRepeaterRequested += () => ShowPracticeToolsInGameplay = true;
        // Pause: swap to the Play Mode surface WITHOUT restoring the chrome —
        // the pause menu floats over the frozen-highway backdrop.
        PlayMode.ShowPauseSurfaceRequested += () => SelectedMainTabIndex = PlayModeTabIndex;
        PlayMode.OpenSettingsRequested += () => SelectedMainTabIndex = SettingsTabIndex;
        // Get Ready's "T TUNER" hint. The dialog is the shell's, so the screen
        // asks rather than reaching across — the same shape as the stem-
        // separation request below.
        PlayMode.TunerRequested += () => ShellDialogs.OpenTunerCommand.Execute(null);
        // Observed (not fire-and-forget): a faulted separation surfaced only
        // via UnobservedTaskException made the Play Mode button appear dead.
        PlayMode.StemSeparationRequested += async () =>
        {
            try
            {
                await RunStemSeparationForPlayMode();
            }
            catch (Exception ex)
            {
                _logger.Error(AppLogCategory.Audio, $"Play Mode stem separation failed: {ex.Message}");
            }
        };
        LivePlay.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(LivePlayViewModel.IsActive)) return;
            // Run over: the riff-repeater strip doesn't leak into the next one.
            if (!LivePlay.IsActive) ShowPracticeToolsInGameplay = false;
            OnPropertyChanged(nameof(IsPracticeStripVisible));
        };
        // Speed ladder steps go through the transport so its speed slider,
        // the engine and the practice session stay one value.
        LivePlay.PlaybackSpeedChangeRequested += speed => Transport.PlaybackSpeed = speed;
        PlaybackCoordinator = playbackCoordinator;
        KeybindHandler = keybindHandler;
        MultiTrackOverlay = multiTrackOverlay;
        Dialogs = dialogs;

        _settings.PropertyChanged += OnSettingsSessionChanged;

        // View ▸ Reset Layout (also in the command palette) discards a
        // hand-tuned layout — splitters, every floated pane, both pop-out
        // windows and the persisted DockLayout — on one click, with no undo.
        // A menu verb takes the app-wide modal prompt; see ConfirmLayoutResetAsync.
        ConfirmLayoutResetAsync = () => Dialogs.Message.ShowAsync(
            _localization["ResetLayout_ConfirmTitle"],
            _localization["ResetLayout_ConfirmMessage"],
            MessageDialogKind.Warning,
            confirmation: true);

        _projectSession.Initialize();
        // Restore the persisted minimap preference. AFTER Initialize, which is
        // what loads the config; assigning it here also means the setter's own
        // "value unchanged ⇒ no write" check makes this a no-op on disk.
        IsMinimapVisible = AppConfig.ShowMinimap;
        // Crash recovery: leftover autosave snapshots mean the previous
        // session died mid-edit. Checked at background priority so it runs
        // after the startup project reload (queued above by Initialize) has
        // been dispatched — the dialog overlays the shell while/after the
        // last project loads underneath.
        _ui.Post(() => _recovery?.OpenIfPending());
        // First run: the onboarding wizard, on the same deferred hook, so the
        // shell's first frame paints before its view is materialized. It opens
        // exactly until AppConfig.OnboardingCompleted is set (Finish or dismiss).
        _ui.Post(() => Dialogs.Onboarding.OpenAtStartupIfNeeded());
        _logger.Info(AppLogCategory.Application,
            $"Main window view model initialized. Last project path='{AppConfig.LastProjectPath}'.");
        KeybindHandler.Register(() => ProjectState.Keybinds.ClearSelection, HandleEscapeKey);
        KeybindHandler.RefreshBinds();

        // Command palette (Ctrl+Shift+P): its catalogue is a lazy provider so it
        // captures the fully-constructed shell + current keybinds at open time.
        CommandPalette = new CommandPaletteViewModel(
            () => ViewModels.CommandPalette.CommandCatalog.Build(this, _localization),
            _localization,
            action => Avalonia.Threading.Dispatcher.UIThread.Post(
                action, Avalonia.Threading.DispatcherPriority.Background));

        // A Player boots on Play Mode's Start screen (its CurrentScreen starts
        // there), and its Start leads on to Home. The last project still loads underneath, so
        // switching to the editor is instant. AppConfig.StartupScreen (Charter →
        // editor, Player → Play Mode), read through the one router.
        switch (StartupRouting.SurfaceFor(AppConfig, playerProfiles?.Profiles.Count ?? 1))
        {
            case StartupSurface.Start:
                SelectedMainTabIndex = PlayModeTabIndex;
                break;
            case StartupSurface.Home:
                ShellContext = ShellContext.Playing;
                SelectedMainTabIndex = HomeTabIndex;
                break;
        }
    }

    public StemMixerViewModel StemMixer { get; }
    public StatusBarViewModel StatusBar { get; }

    public SheetMusicPaneViewModel SheetMusicPane { get; }

    /// <summary>The AI chat pane's dock Context; null only in a host built without it.</summary>
    public AiChatDockViewModel? AiChatPane => ShellDialogs.AiChat;

    /// <summary>The named phrase catalog pane (EOF's fret catalog).</summary>
    public PhraseCatalogViewModel PhraseCatalog { get; }

    // Seams for view code-behind that cannot take constructor injection
    // (views are created from XAML): timeline input glue and input logging
    // reach their services through here instead of the static container.
    public ITimelineInteractionFactory InteractionFactory { get; }
    public IAppLogger Logger => _logger;

    public ProjectState ProjectState { get; }
    public TransportViewModel Transport { get; }
    public LivePlayViewModel LivePlay { get; }
    public InspectorViewModel Inspector { get; }
    public ChartEditorViewModel ChartEditor { get; }

    /// <summary>
    ///     The two editing services, exposed for <c>CommandCatalog</c> only.
    ///     <para>
    ///         The palette must not wrap ONLY what hangs off this view model, or
    ///         ~45 editing verbs — every technique toggle, the fret/string
    ///         nudges, the fingering keys, double/half time — would have no palette
    ///         entry at all and several would be reachable by keybind alone. They are
    ///         <c>internal</c> rather than public because the palette is the one
    ///         legitimate consumer; a view that wants to edit goes through a
    ///         command on this view model like everything else.
    ///     </para>
    /// </summary>
    internal IHighwayEditingService HighwayEditing => _highwayEditing;

    /// <inheritdoc cref="HighwayEditing" />
    internal IGlobalEditingCoordinator GlobalEditing => _globalEditing;
    /// <summary>The taskbar and alt-tab caption: the song, then the app, as the status
    /// rail names it (not the arrangement, which read "DotCharter - Combo").</summary>
    public string WindowTitle => ComposeWindowTitle(StatusBar.ProjectName, _projectSession.IsProjectDirty);

    /// <summary>Trailing asterisk = unsaved changes (the standard desktop marker).</summary>
    internal static string ComposeWindowTitle(string projectName, bool dirty)
    {
        var mark = dirty ? "*" : string.Empty;
        return projectName.Length > 0 ? $"{projectName}{mark} — DotCharter" : $"DotCharter{mark}";
    }
    public string ActiveArrangementName => ChartEditor.ActiveArrangement?.Name ?? "—";
    public SongBrowserViewModel SongBrowser { get; }
    public AudioPlaybackCoordinator PlaybackCoordinator { get; }
    public ChartKeybindHandler KeybindHandler { get; }
    public MultiTrackOverlayViewModel MultiTrackOverlay { get; }
    public IAppDialogService Dialogs { get; }
    public bool IsChartEditorVisible => SelectedMainTabIndex == ChartEditorTabIndex;
    public bool IsSongBrowserVisible => SelectedMainTabIndex == SongBrowserTabIndex;
    public bool IsSettingsVisible => SelectedMainTabIndex == SettingsTabIndex;
    public bool IsPlayModeVisible => SelectedMainTabIndex == PlayModeTabIndex;
    public bool IsHomeVisible => SelectedMainTabIndex == HomeTabIndex;

    public PlayModeViewModel PlayMode { get; }

    /// <summary>Tools ▸ Separate Stems and Play Mode's backing-track setup.</summary>
    public StemSeparationViewModel StemSeparation { get; }

    /// <summary>The Help menu: About, Session Health, the licence, updates, usage.</summary>
    public HelpMenuViewModel Help { get; }

    /// <summary>File ▸ New project and the imports.</summary>
    public ImportMenuViewModel Imports { get; }

    /// <summary>The editor's guitar-tone surfaces: the Amp &amp; FX rig overlay,
    /// Preview tones, the Tools popover's tuner readout and Generate Rocksmith tones.</summary>
    public ToneRigViewModel ToneRig { get; }

    /// <summary>File ▸ Export (Guitar Pro, Editor on Fire, sheet PDF) and the highway screenshot.</summary>
    public ExportMenuViewModel Export { get; }

    /// <summary>Open / save / attach audio, Open Recent, the file-drop funnel and the
    /// arrangement and vocal-track new/delete rows.</summary>
    public ProjectMenuViewModel Project { get; }

    /// <summary>The menu, palette and editor-tool doors onto the hosted dialogs.</summary>
    public ShellDialogsViewModel ShellDialogs { get; }

    /// <summary>True while the session holds edits not yet on disk. The
    /// window-close path gates on this (see MainWindow.OnClosing).</summary>
    public bool IsProjectDirty => _projectSession.IsProjectDirty;

    /// <summary>Raised by <see cref="ExitApplication" />; MainWindow answers it
    /// with <c>Close()</c> so File ▸ Exit runs the SAME path as the caption
    /// button — the unsaved-changes gate in <c>OnClosing</c> included. Never
    /// call <c>App.TerminateNow</c> from here: that skips the gate.</summary>
    public event Action? ExitRequested;

    /// <summary>Pre-play "backing track" setup: run the normal stem
    /// separation, then tell Play Mode stems are available for muting.</summary>
    private async Task RunStemSeparationForPlayMode()
    {
        await StemSeparation.SeparateStemsAsync();
        PlayMode.RefreshStemAvailability();
    }

    private void OnStemsApplied() => IsStemMixerVisible = true;

    /// <summary>The Ctrl+Shift+P command palette. Its catalogue is rebuilt from
    /// this shell each time it opens (see <see cref="ViewModels.CommandPalette.CommandCatalog" />), so the
    /// listed shortcuts and enabled state always reflect the current keybinds.</summary>
    public CommandPaletteViewModel CommandPalette { get; }

    /// <summary>Help ▸ Keyboard shortcuts' list: every bound command, read from the palette catalog
    /// at the moment the dialog opens.</summary>
    public IReadOnlyList<ShortcutEntry> CurrentShortcuts() => ShortcutEntries.From(CommandCatalog.Build(this, _localization), _localization);

    public AppConfig AppConfig => _projectSession.AppConfig;

    public void Dispose()
    {
        _settings.PropertyChanged -= OnSettingsSessionChanged;
        StemSeparation.StemsApplied -= OnStemsApplied;
        ToneRig.OpenSettingsRequested -= OpenSettings;
        if (ShellDialogs.AiChart is { } aiChart) aiChart.OpenSettingsRequested -= OpenSettings;
        ToneRig.Dispose();
        Project.Dispose();
        _projectSession.Dispose();
        GC.SuppressFinalize(this);
    }

    public void HandleEscapeKey()
    {
        // Settings is hosted as an embedded "tab" rather than a true modal
        // dialog, so we deliberately skip it here — Esc inside Settings would
        // otherwise close the whole panel and surprise the user (it's also
        // very easy to fat-finger Esc while editing keybinds). Users can
        // leave Settings via the side-rail or the Close button.
        if (Dialogs.CloseTopDialog() is { } closed)
        {
            _logger.Info(AppLogCategory.Input, $"Escape pressed; closing dialog {closed}.");
        }
        else if (_settings.IsOpen)
        {
            _logger.Trace(AppLogCategory.Input, "Escape pressed inside Settings; ignored.");
        }
        else if (ToneRig.IsPedalboardOpen && ToneRig.Pedalboard?.Browser.IsDeleteConfirmOpen == true)
        {
            // The tone-delete confirmation is the other modal on the rig: Esc
            // keeps the tone and closes it, like its Cancel.
            _logger.Info(AppLogCategory.Input, "Escape pressed; cancelling the tone delete confirmation.");
            ToneRig.Pedalboard.Browser.CancelDelete();
        }
        else if (ToneRig.IsPedalboardOpen && ToneRig.Pedalboard?.Browser.IsSaveDialogOpen == true)
        {
            // The "save as new" modal sits ON TOP of the pedalboard — Esc closes
            // that first; only a second Esc dismisses the whole overlay.
            _logger.Info(AppLogCategory.Input, "Escape pressed; cancelling the pedalboard save dialog.");
            ToneRig.Pedalboard.Browser.IsSaveDialogOpen = false;
        }
        else if (ToneRig.IsPedalboardOpen && ToneRig.Pedalboard?.IsBrowserOpen == true)
        {
            // …and the TONE BROWSER drawer is the layer between them: it opens
            // over the board, and the save modal opens over IT. This rung was
            // missing HERE and present in `PlayModeViewModel.TryCloseOverlay`,
            // over the SAME `PedalboardViewModel` singleton — so browsing tones
            // in the chart editor and pressing Esc threw away the whole rig
            // overlay, while the identical gesture in Play Mode closed only the
            // drawer. Esc walks the three down in the order they were stacked,
            // in both hosts.
            _logger.Info(AppLogCategory.Input, "Escape pressed; closing the editor tone browser drawer.");
            ToneRig.Pedalboard.IsBrowserOpen = false;
        }
        else if (ToneRig.IsPedalboardOpen)
        {
            // The editor's tone-rig pedalboard overlay is the topmost editor
            // surface — Esc dismisses it before falling through to clearing the
            // chart selection.
            _logger.Info(AppLogCategory.Input, "Escape pressed; closing the editor pedalboard overlay.");
            ToneRig.IsPedalboardOpen = false;
        }
        else if (PlayMode.TryCloseOverlay())
        {
            // A Play Mode overlay (Amp/FX, gameplay settings, planner)
            // was open — Esc dismisses it before falling through to pause or
            // back-navigation (matters most for Amp/FX opened over the pause
            // menu, where Esc would otherwise resume the run underneath it).
            _logger.Info(AppLogCategory.Input, "Escape pressed; closing Play Mode overlay.");
        }
        else if (IsGameplayPresentation || PlayMode.IsPausedScreen || PlayMode.IsFailedScreen)
        {
            // Esc during a game run = Rocksmith-style pause menu; Esc on the
            // pause menu = resume; Esc on the fail-out overlay = quit (the run
            // is already over — NavigateBack owns that route).
            _logger.Info(AppLogCategory.Input, "Escape pressed during gameplay; toggling pause.");
            if (PlayMode.IsFailedScreen) PlayMode.NavigateBack();
            else if (PlayMode.IsPausedScreen) PlayMode.ResumeGameplayCommand.Execute(null);
            else PlayMode.PauseGameplay();
        }
        // Esc on the LAUNCH screen does not quit the application: Esc
        // is the key you press to back out of things, and having the one screen
        // where nothing is open answer it by closing the program turns a reflex
        // into a way to lose your place. Quitting stays on the window's own
        // close button and File → Exit, both of which run the
        // unsaved-changes prompt. The footer prints no hint for it — a printed
        // shortcut that does nothing is worse than no shortcut.
        else if (IsHomeVisible)
        {
            // Home sits one step past Play Mode's Start screen; Esc goes back to it.
            _logger.Info(AppLogCategory.Input, "Escape pressed on Home; back to the Start screen.");
            PlayMode.CurrentScreen = PlayModeScreen.Start;
            SelectedMainTabIndex = PlayModeTabIndex;
        }
        else if (IsPlayModeVisible)
        {
            // Inside Play Mode, Esc walks one screen back.
            _logger.Info(AppLogCategory.Input, "Escape pressed in Play Mode; navigating back.");
            PlayMode.NavigateBack();
        }
        else if (_pen.RevertPendingEntry())
        {
            // The keyboard pen has an in-progress fret entry — Esc reverts it
            // (audit 03 §6.3 step 3: the commit is implicit, Esc is the undo of
            // the digits you just typed). Sits directly ahead of "clear the
            // selection" because reverting must not also drop the target you
            // were typing onto. RevertPendingEntry claims the key ONLY when it
            // actually reverted, so with no entry pending this branch is
            // invisible and Esc behaves exactly as it always has.
            _logger.Info(AppLogCategory.Input, "Escape pressed; reverted the pen's in-progress fret entry.");
        }
        else
        {
            _logger.Info(AppLogCategory.Input, "Escape pressed; clearing chart selections.");
            ChartEditor.Selection.ClearSelections();
        }
    }

    public void LoadAppConfig() => _projectSession.LoadAppConfig();

    public void SaveAppConfig() => _projectSession.SaveAppConfig();

    public void SeekTo(double timeMs)
    {
        _logger.Trace(AppLogCategory.Input, $"Seek requested to {timeMs:0.###} ms.");
        Transport.SeekTo(timeMs);
    }

    public void FreeAudio() => _projectSession.FreeAudio();

    public string[] GetTuningNames(int stringCount, int[] offsets) => TuningUtilities.GetTuningNames(stringCount, offsets);

    partial void OnSelectedMainTabIndexChanged(int value)
    {
        TrackShellContext(value);
        StatusBar.IsEditorSurface = value == ChartEditorTabIndex;

        // The tone preview belongs to the chart editor; a chart synth that is still
        // on takes the chart's tones back when the editor is.
        if (value != ChartEditorTabIndex) ToneRig.StopTonePreviewOutsideTheEditor();
        else ToneRig.ResumeTonePreviewInTheEditor();

        // Leaving the Settings tab reverts the page's unsaved edits; the
        // settings shell posts that work so the destination view paints first.
        if (value != SettingsTabIndex) _settings.IsOpen = false;

        if (value == SongBrowserTabIndex)
        {
            // Defer scan/config-load so the visibility flip paints first.
            _ui.Post(() => _ = SongBrowser.Activate());
        }
        else if (value == SettingsTabIndex)
        {
            // The settings shell loads every tab on the next dispatcher frame
            // (it probes audio devices and re-reads the JSON config), so the
            // page appears without the freeze.
            _settings.IsOpen = true;
        }

        // Leaving the chart editor - for the game shell (menu / song select), the
        // Library, Home or Settings - must not leave the editor's song playing in
        // the background: the chart synth would go on sounding through a rig
        // the editor has just handed back (StopTonePreviewOutsideTheEditor above).
        // Active gameplay drives the transport itself (its pause surface also lands
        // on the Play Mode tab), so only pause when no run is in progress.
        //
        // The `IsPlaying` guard is load-bearing at STARTUP, not a micro-opt:
        // AppConfig.StartInPlayMode makes this the boot tab, so the shell's own
        // constructor lands here — and Pause() reaches into the native engine
        // (stem transports, scheduled clicks, MIDI all-notes-off) while
        // ProjectSessionService.Initialize is still bringing that engine up on a
        // worker thread. Measured 48 ms on a warm device and ~265 ms while the
        // engine was mid-open, paid on the UI thread before the window exists.
        // Pausing a transport that was never started is a no-op by definition,
        // so the guard costs nothing and skips the whole native round-trip.
        if (value != ChartEditorTabIndex && !LivePlay.IsActive && Transport.IsPlaying)
            Transport.PauseCommand.Execute(null);
    }

    /// <summary>The settings shell closed the page (Save or Cancel): leave the tab.</summary>
    private void OnSettingsSessionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (null or "" or nameof(DotCharter.Presentation.Shared.SettingsSession.IsOpen))) return;
        if (!_settings.IsOpen && SelectedMainTabIndex == SettingsTabIndex)
            SelectedMainTabIndex = ChartEditorTabIndex;
    }

    [RelayCommand]
    private void OpenSettings() => SelectedMainTabIndex = SettingsTabIndex;

    // `ShowChartEditorCommand` is DELETED (0.58). Its two siblings above and
    // below ARE bound (File menu entries); this one never was, because the editor
    // is reached by the `MainNavSwitcher` segmented control, whose first
    // RadioButton writes `SelectedMainTabIndex` two-way through
    // `IntEqualsConverter`. Only tests invoked it, which is exactly the shape the
    // reachability lint exists to flag — they set the property directly now.

    /// <summary>File ▸ Exit. The menu row must carry a command, not only an "Alt+F4" gesture LABEL:
    /// a label alone does nothing when clicked.</summary>
    [RelayCommand]
    private void ExitApplication() => ExitRequested?.Invoke();

    [RelayCommand]
    private void OpenPlayMode() => SelectedMainTabIndex = PlayModeTabIndex;

    /// <summary>HUD pause button: same as Esc during gameplay.</summary>
    [RelayCommand]
    private void PauseGameplayRun()
    {
        if (!IsGameplayPresentation) return;
        PlayMode.PauseGameplay();
    }

    /// <summary>HUD "end run" button: straight to the results.</summary>
    [RelayCommand]
    private void EndGameplayRun()
    {
        if (!IsGameplayPresentation) return;
        LivePlay.IsActive = false;
        Transport.StopCommand.Execute(null);
    }

    [RelayCommand]
    private void OpenSongBrowser()
    {
        if (SelectedMainTabIndex == SongBrowserTabIndex)
        {
            _ = SongBrowser.Activate();
            return;
        }

        SelectedMainTabIndex = SongBrowserTabIndex;
    }

    // There is no `RequestApplicationExit`: a seam like that would be DEAD but
    // read as a live feature (the window assigned it `Close`, its own doc
    // claimed HandleEscapeKey invoked it, and nothing ever did). It would be the
    // leftover of an Esc-quits-the-app behaviour, which is deliberately absent
    // along with the launch footer's "ESC QUIT" hint — `LaunchScreenTests`
    // FAILS if the Esc chain quits or if the hint comes back. The app has one
    // exit seam and it is `ExitRequested` (File ▸ Exit and the palette's
    // `file.exit`, both landing on `MainWindow.Close()` so `OnClosing`'s
    // unsaved-changes gate runs). Do not reintroduce a second one; wire a new
    // quit entry point to `ExitApplicationCommand` instead.
    // Pinned by `UI/ExitCommandWiringTests.TheShellHasExactlyOneApplicationExitSeam`.
}
