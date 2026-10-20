// Panel and dock visibility: the inspector / preview / stem-mixer / riff-library
// flags, the gameplay-presentation snapshot, the minimap preference, and the
// derived grid geometry MainWindow binds its rows and columns to.
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Shell;
using DotCharter.Presentation.Shared;
using DotCharter.UI.Layout;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.UI.ViewModels.Shell;

public partial class MainWindowViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBottomToolAreaVisible))]
    [NotifyPropertyChangedFor(nameof(IsBottomDockVisible))]
    [NotifyPropertyChangedFor(nameof(AreBothBottomPanelsVisible))]
    [NotifyPropertyChangedFor(nameof(IsInspectorOnlyVisible))]
    [NotifyPropertyChangedFor(nameof(IsPreviewOnlyVisible))]
    [NotifyPropertyChangedFor(nameof(InspectorColumnSpan))]
    [NotifyPropertyChangedFor(nameof(PreviewColumnStart))]
    [NotifyPropertyChangedFor(nameof(PreviewColumnSpan))]
    [NotifyPropertyChangedFor(nameof(BottomDockRowHeight))]
    [NotifyPropertyChangedFor(nameof(BottomDockSplitterHeight))]
    [NotifyPropertyChangedFor(nameof(IsDockRowSplitterVisible))]
    private bool _isInspectorVisible = true;

    /// <summary>The Inspector is in its own window; its dock spot shows the way back.</summary>
    [ObservableProperty] private bool _isInspectorFloating;

    /// <summary>The sheet music is in its own window; its dock spot shows the way back.</summary>
    [ObservableProperty] private bool _isSheetMusicFloating;

    /// <summary>
    /// The AI chat pane shows in the editor's right-hand column. Off at first: the
    ///     toolbar toggle, its keybind, the palette and the View menu turn it on, and the
    ///     shell persists it with the column's width.
    /// </summary>
    [ObservableProperty] private bool _isAiChatVisible;

    /// <summary>The AI chat is in its own window; its column shows the way back.</summary>
    [ObservableProperty] private bool _isAiChatFloating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBottomDockVisible))]
    [NotifyPropertyChangedFor(nameof(AreBothBottomPanelsVisible))]
    [NotifyPropertyChangedFor(nameof(IsInspectorOnlyVisible))]
    [NotifyPropertyChangedFor(nameof(IsPreviewOnlyVisible))]
    [NotifyPropertyChangedFor(nameof(InspectorColumnSpan))]
    [NotifyPropertyChangedFor(nameof(PreviewColumnStart))]
    [NotifyPropertyChangedFor(nameof(PreviewColumnSpan))]
    [NotifyPropertyChangedFor(nameof(Is3DPreviewActive))]
    [NotifyPropertyChangedFor(nameof(BottomDockRowHeight))]
    [NotifyPropertyChangedFor(nameof(BottomDockSplitterHeight))]
    [NotifyPropertyChangedFor(nameof(IsDockRowSplitterVisible))]
    private bool _isPreviewVisible = true;


    /// <summary>
    ///     Rocksmith-style gameplay presentation: while a Play Mode run is on,
    ///     every piece of charting chrome (toolbar, 2D timeline, minimap,
    ///     inspector, practice strip) collapses so the 3D highway + HUD own
    ///     the window. The layout rows are collapsed by MainWindow code-behind
    ///     listening to this flag; panel visibility is saved/restored here.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsToolbarVisible))]
    [NotifyPropertyChangedFor(nameof(IsPracticeStripVisible))]
    [NotifyPropertyChangedFor(nameof(IsMinimapShown))]
    [NotifyPropertyChangedFor(nameof(IsActivityRailVisible))]
    [NotifyPropertyChangedFor(nameof(IsShellChromeVisible))]
    [NotifyPropertyChangedFor(nameof(TitleBarRowHeight))]
    [NotifyPropertyChangedFor(nameof(IsBackBarVisible))]
    [NotifyPropertyChangedFor(nameof(IsWindowCaptionVisible))]
    [NotifyPropertyChangedFor(nameof(IsDockRowSplitterVisible))]
    [NotifyPropertyChangedFor(nameof(IsSheetSplitterVisible))]
    private bool _isGameplayPresentation;

    /// <summary>
    ///     Playing or charting: set by Play Mode, a run and the chart editor, and
    ///     kept by Home, the Library, Settings and the rig, which take the context
    ///     they were reached from (<see cref="ShellPresentation" />).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShellChromeVisible))]
    [NotifyPropertyChangedFor(nameof(IsActivityRailVisible))]
    [NotifyPropertyChangedFor(nameof(TitleBarRowHeight))]
    [NotifyPropertyChangedFor(nameof(IsBackBarVisible))]
    [NotifyPropertyChangedFor(nameof(IsWindowCaptionVisible))]
    private ShellContext _shellContext;

    /// <summary>The surface the back bar returns to: the last one shown that is not the Library or Settings.</summary>
    private int _backBarTabIndex = HomeTabIndex;

    private ShellDestination CurrentSurface => SurfaceOf(SelectedMainTabIndex);

    private static ShellDestination SurfaceOf(int tabIndex) => tabIndex switch
    {
        ChartEditorTabIndex => ShellDestination.Chart,
        SongBrowserTabIndex => ShellDestination.Library,
        SettingsTabIndex => ShellDestination.Settings,
        PlayModeTabIndex => ShellDestination.Play,
        _ => ShellDestination.Home
    };

    private void TrackShellContext(int tabIndex)
    {
        var surface = SurfaceOf(tabIndex);
        if (!ShellPresentation.IsShared(surface)) _backBarTabIndex = tabIndex;
        ShellContext = ShellPresentation.ContextAfter(surface, ShellContext, IsGameplayPresentation);
        ToneRig.SyncRigCloseButton();
    }

    partial void OnShellContextChanged(ShellContext value)
    {
        ToneRig.SyncRigCloseButton();
    }

    /// <summary>
    ///     <b>The shell has CHROME or it has none — one flag, so a surface cannot
    ///     end up with a rail and no title bar.</b> Home and Play Mode are
    ///     full-bleed, the chart editor carries all three pieces, and the Library
    ///     and Settings wear whichever the context calls for:
    ///     a player reaching them from Home or Play Mode gets them
    ///     full-bleed under <see cref="IsBackBarVisible" />, a charter on the
    ///     rail gets the workbench. The Amp &amp; FX rig is an overlay and keeps
    ///     whatever the surface under it has.
    /// </summary>
    public bool IsShellChromeVisible =>
        ShellPresentation.HasChrome(CurrentSurface, ShellContext, IsGameplayPresentation);

    /// <inheritdoc cref="IsShellChromeVisible" />
    public bool IsActivityRailVisible => IsShellChromeVisible;

    /// <summary>A full-bleed Library or Settings: a slim bar with Back and the window buttons, in the title bar's row.</summary>
    public bool IsBackBarVisible =>
        ShellPresentation.NeedsBackBar(CurrentSurface, ShellContext, IsGameplayPresentation);

    /// <summary>
    ///     The slim caption a full-bleed surface keeps in a window: the app's mark, a strip that moves
    ///     the window, and the window buttons (<see cref="ShellPresentation.ShowsWindowCaption" />).
    /// </summary>
    public bool IsWindowCaptionVisible =>
        ShellPresentation.ShowsWindowCaption(IsShellChromeVisible, IsBackBarVisible, IsWindowFullScreen);

    /// <summary>Kept by the window as its state changes: fullscreen has no window to manage, so no caption.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWindowCaptionVisible))]
    [NotifyPropertyChangedFor(nameof(TitleBarRowHeight))]
    private bool _isWindowFullScreen;

    [RelayCommand]
    private void GoBack() => SelectedMainTabIndex = _backBarTabIndex;

    /// <summary>
    ///     The top ROW, which collapses with what it holds. `IsVisible=false` on a control inside a
    ///     fixed row hides the control and keeps the band, so a full-bleed surface would start under
    ///     a band of nothing. The title bar, the back bar and the window caption each take it at their
    ///     own height (<see cref="ShellPresentation.TopRowHeight" />).
    /// </summary>
    public GridLength TitleBarRowHeight =>
        new(ShellPresentation.TopRowHeight(IsShellChromeVisible, IsBackBarVisible, IsWindowFullScreen));

    /// <summary>
    ///     The user's minimap preference, persisted across launches. The strip is
    ///     40 px of permanent vertical budget under a timeline that is already
    ///     short on it, and a charter working bar-by-bar does not navigate from
    ///     it — so it has to be closable, and stay closed.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMinimapShown))]
    private bool _isMinimapVisible = true;

    /// <summary>
    ///     Whether the minimap strip is actually on screen. Two independent
    ///     reasons to hide it, and they must not overwrite each other: the user
    ///     closed it, or the gameplay presentation has taken the window. Folding
    ///     the second into the first would make starting a run silently forget
    ///     the preference — the mistake the pre-game panel snapshot above exists
    ///     to avoid for the docked panes.
    /// </summary>
    public bool IsMinimapShown => IsMinimapVisible && !IsGameplayPresentation;

    partial void OnIsMinimapVisibleChanged(bool value)
    {
        // Layout preference, so it survives the session that set it.
        var config = _projectSession.AppConfig;
        if (config.ShowMinimap == value) return;
        config.ShowMinimap = value;
        _projectSession.SaveAppConfig();
    }

    /// <summary>
    ///     Brings an inspector TAB up: shows the inspector if it is closed, then
    ///     selects the tab. The Stem Mixer and Riff Library are tabs
    ///     (rather than dock panes in a second strip), and this is what keeps their
    ///     View-menu items, palette entries and keybind meaningful — "show it"
    ///     still shows it, it just is not a pane.
    /// </summary>
    public void ShowInspectorTab(int tabIndex)
    {
        IsInspectorVisible = true;
        Inspector.SelectedTabIndex = tabIndex;
    }

    [RelayCommand]
    private void ToggleMinimap() => IsMinimapVisible = !IsMinimapVisible;

    public bool IsToolbarVisible => IsChartEditorVisible && !IsGameplayPresentation;

    /// <summary>Riff Repeater engaged from the pause menu: the practice/loop
    /// strip joins the gameplay HUD for the rest of the run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPracticeStripVisible))]
    private bool _showPracticeToolsInGameplay;

    /// <summary>Loop/ladder/hit-window strip: practice-only, unless the
    /// pause menu's Riff Repeater pulled it into the game run.</summary>
    public bool IsPracticeStripVisible =>
        LivePlay.IsActive && (!IsGameplayPresentation || ShowPracticeToolsInGameplay);

    private bool _preGameInspectorVisible;
    private bool _preGamePreviewVisible;
    private bool _preGameStemMixerVisible;
    private bool _preGameSheetMusicVisible;
    private bool _preGamePhraseCatalogVisible;
    private bool _preGameAiChatVisible;

    partial void OnIsGameplayPresentationChanged(bool value)
    {
        if (value) ShellContext = ShellContext.Playing;
        ToneRig.SyncRigCloseButton();

        if (value)
        {
            _preGameInspectorVisible = IsInspectorVisible;
            _preGamePreviewVisible = IsPreviewVisible;
            _preGameStemMixerVisible = IsStemMixerVisible;
            _preGameSheetMusicVisible = IsSheetMusicVisible;
            _preGamePhraseCatalogVisible = IsPhraseCatalogVisible;
            _preGameAiChatVisible = IsAiChatVisible;
            IsInspectorVisible = false;
            IsAiChatVisible = false;
            IsStemMixerVisible = false;
            IsSheetMusicVisible = false;
            IsPhraseCatalogVisible = false;
            IsPreviewVisible = true;
        }
        else
        {
            IsInspectorVisible = _preGameInspectorVisible;
            IsPreviewVisible = _preGamePreviewVisible;
            IsStemMixerVisible = _preGameStemMixerVisible;
            IsSheetMusicVisible = _preGameSheetMusicVisible;
            IsPhraseCatalogVisible = _preGamePhraseCatalogVisible;
            IsAiChatVisible = _preGameAiChatVisible;
        }
    }

    [ObservableProperty] private bool _isStackedTracksVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDockRowSplitterVisible))]
    [NotifyPropertyChangedFor(nameof(IsSheetSplitterVisible))]
    private bool _isSheetMusicVisible;

    /// <summary>"The Mixer tab is up." A TAB SELECTOR, not a pane flag — see
    /// <see cref="IsBottomToolAreaVisible" />.</summary>
    [ObservableProperty] private bool _isStemMixerVisible;

    /// <summary>"The Riff Library tab is up." A TAB SELECTOR, not a pane flag —
    /// see <see cref="IsBottomToolAreaVisible" />.</summary>
    [ObservableProperty] private bool _isPhraseCatalogVisible;

    /// <summary>
    ///     Whether the bottom tool area (the <c>BottomDock</c> DockControl) has
    ///     anything to show. It is exactly "is a registered DOCK PANE visible",
    ///     and the registry in <c>MainWindow.InitializeBottomDock</c> is the
    ///     authoritative list — the Inspector, and only the Inspector, since
    ///     0.42.4.
    ///     <para>
    ///         It read <c>IsInspectorVisible || IsStemMixerVisible ||
    ///         IsPhraseCatalogVisible</c> for six releases after the Stem Mixer
    ///         and the Riff Library stopped being panes and became inspector
    ///         TABS. A tab flag cannot hold the dock open, because with the
    ///         Inspector pane closed the dock has nothing to draw: open the Riff
    ///         Library (which turns BOTH flags on), then close the Inspector, and
    ///         the empty DockControl kept its third of the bottom row —
    ///         <see cref="IsPreviewOnlyVisible" /> stayed false, so the 3D preview
    ///         could not widen into it either, and with the preview off as well
    ///         the whole row stayed 1* tall with nothing in it. That is the
    ///         dead-black-quadrant shape <c>docs/agents/ui/docking.md</c> warns
    ///         about, arrived at from the other end.
    ///     </para>
    ///     <para>Pinned by <c>DockPaneContractTests</c>, which derives the flag
    ///     set from the pane registry itself.</para>
    /// </summary>
    public bool IsBottomToolAreaVisible => IsInspectorVisible;

    public bool IsBottomDockVisible => IsBottomToolAreaVisible || IsPreviewVisible;
    public bool AreBothBottomPanelsVisible => IsBottomToolAreaVisible && IsPreviewVisible;
    public bool IsInspectorOnlyVisible => IsBottomToolAreaVisible && !IsPreviewVisible;
    public bool IsPreviewOnlyVisible => !IsBottomToolAreaVisible && IsPreviewVisible;

    /// <summary>The timeline-area / dock splitter: never above an empty band in a run (<see cref="EditorRowSplitterPolicy" />).</summary>
    public bool IsDockRowSplitterVisible =>
        EditorRowSplitterPolicy.DockSplitterVisible(IsBottomDockVisible, IsGameplayPresentation, IsSheetMusicVisible);

    /// <summary>The sheet strip / timeline splitter (<see cref="EditorRowSplitterPolicy" />).</summary>
    public bool IsSheetSplitterVisible => EditorRowSplitterPolicy.SheetSplitterVisible(IsSheetMusicVisible, IsGameplayPresentation);

    /// <summary>
    ///     The Mixer and the Riff Library are inspector TABS, so closing the
    ///     Inspector pane takes them off screen with it — their flags must stop
    ///     claiming otherwise. They did not, and the lie cost the user a
    ///     keystroke as well as a checkbox: with the Inspector closed, View ▸
    ///     Riff Library still read as CHECKED, and the first press of its
    ///     keybind only flipped the stale <c>true</c> back to <c>false</c> —
    ///     which the shell's property switch ignores, because it acts on "show
    ///     it" — so the pane came back on the SECOND press.
    /// </summary>
    partial void OnIsInspectorVisibleChanged(bool value)
    {
        if (value) return;
        IsStemMixerVisible = false;
        IsPhraseCatalogVisible = false;
    }

    [RelayCommand]
    private void ToggleStemMixer() => IsStemMixerVisible = !IsStemMixerVisible;

    [RelayCommand]
    private void TogglePhraseCatalog() => IsPhraseCatalogVisible = !IsPhraseCatalogVisible;

    /// <summary>The palette's "Chord shapes": opens the inspector on its Chord shapes tab.</summary>
    [RelayCommand]
    private void ShowChordShapes() => ShowInspectorTab(InspectorViewModel.ChordShapesTabIndex);

    /// <summary>Shows the catalog and puts the caret in its find box, so the
    /// paste-by-typed-name keybind is one gesture from anywhere.</summary>
    [RelayCommand]
    private void FocusPhraseCatalogSearch()
    {
        IsPhraseCatalogVisible = true;
        PhraseCatalog.RequestFocusSearch();
    }

    /// <summary>
    /// Raised by <see cref="ResetLayoutCommand"/>; MainWindow owns splitters
    /// and the dock, so the actual reset happens in the view layer.
    /// </summary>
    public event Action? LayoutResetRequested;

    /// <summary>
    /// Raised by <see cref="PopOutTimelineCommand"/>; MainWindow owns the
    /// timeline subtree, so the actual re-parenting happens in the view layer.
    /// </summary>
    public event Action? TimelinePopOutRequested;

    /// <summary>
    ///     Raised when a keybind asks for one of the transport bar's grouped
    ///     popovers. <c>ToolbarView</c> owns the buttons those flyouts hang off,
    ///     so the actual open happens in the view layer — same split as
    ///     <see cref="LayoutResetRequested" /> and
    ///     <see cref="TimelinePopOutRequested" />.
    /// </summary>
    public event Action<TransportPopover>? PopoverRequested;

    /// <summary>
    ///     Opens one of the transport bar's grouped popovers from a keybind.
    ///     <para>
    ///         It is a METHOD on the shell rather than three commands because
    ///         there is nothing for a command to do here: the shell holds no
    ///         popover state, and the only receiver is the view that owns the
    ///         buttons. A no-op when the toolbar is not showing (Play Mode hides
    ///         the whole chrome) — the event simply has no subscriber.
    ///     </para>
    /// </summary>
    public void RequestPopover(TransportPopover which) => PopoverRequested?.Invoke(which);

    /// <summary>
    ///     Confirms View ▸ Reset Layout before it runs. Wired by the constructor
    ///     to the app-wide <c>MessageDialogViewModel</c> prompt — a MENU verb, so
    ///     the modal prompt rather than the in-place two-click latch the row
    ///     deletes use (see <c>docs/agents/ui/dialogs.md</c>).
    ///     <para>
    ///         It is a settable delegate rather than a direct
    ///         <c>Dialogs.Message</c> call for the same reason
    ///         <c>BatchLintViewModel.ConfirmOverwriteAsync</c> is: the rule
    ///         is then testable without building the whole shell graph. UNWIRED it
    ///         REFUSES — a reset throws away a hand-tuned multi-monitor layout with
    ///         no undo and nothing on disk to reopen, so "no way to ask" must mean
    ///         "do not do it", never "do it silently".
    ///     </para>
    /// </summary>
    public Func<Task<bool>>? ConfirmLayoutResetAsync { get; set; }

    /// <summary>
    ///     The rule itself, pure so it can be driven without a shell: no prompt
    ///     wired ⇒ refuse; prompt refused ⇒ <paramref name="apply" /> never runs.
    /// </summary>
    internal static async Task ResetLayoutAsync(Func<Task<bool>>? confirm, Action apply)
    {
        if (confirm == null) return;
        if (!await confirm()) return;
        apply();
    }

    [RelayCommand]
    private Task ResetLayout() => ResetLayoutAsync(ConfirmLayoutResetAsync, ApplyLayoutReset);

    private void ApplyLayoutReset()
    {
        IsInspectorVisible = true;
        IsPreviewVisible = true;
        LayoutResetRequested?.Invoke();
    }

    [RelayCommand]
    private void PopOutTimeline() => TimelinePopOutRequested?.Invoke();

    // The 3D preview panel shows whenever the preview is on and the chart
    // editor is the visible surface. Dialogs, popups and the rig layer above it
    // like any other Avalonia content (the preview is composed, never a child
    // HWND), so nothing else collapses it — collapsing it would only cost a
    // full editor column re-flow on every open and close.
    public bool Is3DPreviewActive => IsPreviewVisible && IsChartEditorVisible;

    /// <summary>
    ///     The LAUNCH CARD over the empty timeline (New / Open / Attach Audio +
    ///     the recent list). Its predecessor (`IsEditorEmptyHintVisible`)
    ///     tracked AUDIO only — after a GP import (a 1000-note chart, no audio)
    ///     the "open a project or drop an audio file" hint sat over a full
    ///     chart. Content-aware now: no chart content AND no audio ⇒ the card;
    ///     a chart without audio shows the chart, covered by nothing.
    /// </summary>
    public bool IsEditorLaunchCardVisible =>
        ShowsLaunchCard(ProjectState.TotalAudioLengthMs, ProjectState.ActiveProject, ProjectState.CurrentProjectPath);

    /// <summary>
    ///     The whole rule, static so it tests without the shell: no audio, no
    ///     chart content, and NO PROJECT PATH. The third term is what dismisses
    ///     the card after File > New Project - a fresh project has neither audio
    ///     nor notes, so the content rule alone left "open a project or drop an
    ///     audio file here" sitting over the project the user had just created.
    ///     The path is set by every create/open/import
    ///     (<c>NewProjectCreator</c>, <c>ProjectSessionService</c>, the EOF
    ///     import) and cleared to "" only when the untitled
    ///     default project is installed, which is exactly the state the card is
    ///     for.
    /// </summary>
    internal static bool ShowsLaunchCard(double totalAudioMs, ChartProject? project, string? projectPath)
        => totalAudioMs <= 0 && string.IsNullOrEmpty(projectPath) && !ProjectHasChartContent(project);

    /// <summary>
    ///     "Is there anything a launch card would cover?" — any note or chord on
    ///     any level, or any vocal phrase. Static + internal so the rule tests
    ///     without constructing the shell. Deliberately NOT per-edit reactive:
    ///     the card can only be dismissed by an open/new/attach (all of which
    ///     raise project-level changes), because an empty default project has no
    ///     beat grid to place a note against.
    /// </summary>
    internal static bool ProjectHasChartContent(ChartProject? project)
    {
        if (project == null) return false;
        foreach (var arrangement in project.Arrangements)
        {
            foreach (var level in arrangement.Levels)
            {
                if (level.Notes.Count > 0 || level.Chords.Count > 0)
                    return true;
            }
        }

        foreach (var vocalTrack in project.VocalTracks)
        {
            if (vocalTrack.Phrases.Count > 0)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     The line under the minimap's <c>SONG MAP</c> eyebrow —
    ///     <c>4:12 · 84 BARS</c>. Both halves are real data: the duration is the
    ///     loaded audio's, the bar count is the chart's own beat map.
    ///     <para>
    ///         Recomputed on demand rather than cached, and re-announced when the
    ///         project, the arrangement, the beat map or the audio length changes
    ///         — deliberately NOT per beat. `ProjectState.Beats` is an
    ///         `ObservableCollection` and a project load appends to it one marker
    ///         at a time, so subscribing to its `CollectionChanged` would make an
    ///         O(n) count run n times during every open. The consequence is
    ///         narrow and known: Insert/Remove Beat leaves the bar count stale
    ///         until the next project or arrangement change.
    ///     </para>
    /// </summary>
    public string SongMapFacts
    {
        get
        {
            var bars = 0;
            foreach (var beat in ProjectState.Beats)
            {
                if (beat.IsMeasure)
                    bars++;
            }

            return SongMapCaption.Describe(
                ProjectState.TotalAudioLengthMs, bars,
                _localization.Plural(
                    "Timeline_SongMap_BarOne", "Timeline_SongMap_BarsFmt", bars),
                separator: "\n");
        }
    }

    // Column layout for the Inspector/Preview grid. A single instance of each view
    // is always mounted; we adjust Grid.Column/ColumnSpan to show/hide/stretch them.
    // ColumnDefinitions are fixed as "*, 6, 2*".
    public int InspectorColumnSpan => IsInspectorOnlyVisible ? 3 : 1;
    public int PreviewColumnStart => IsPreviewOnlyVisible ? 0 : 2;
    public int PreviewColumnSpan => IsPreviewOnlyVisible ? 3 : 1;

    // Row sizing for the Inspector/Preview row inside MainSplitGrid. When both
    // bottom panels are hidden the row collapses entirely so the Minimap stays
    // glued under the Timeline (issue: Minimap disappeared when both panels off).
    public GridLength BottomDockRowHeight =>
        IsBottomDockVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

    public GridLength BottomDockSplitterHeight =>
        IsBottomDockVisible ? new GridLength(6) : new GridLength(0);

    [RelayCommand]
    private void ToggleStackedTracks() => IsStackedTracksVisible = !IsStackedTracksVisible;

    [RelayCommand]
    private void ToggleInspector() => IsInspectorVisible = !IsInspectorVisible;

    [RelayCommand]
    private void TogglePreview() => IsPreviewVisible = !IsPreviewVisible;

    [RelayCommand]
    private void ToggleSheetMusic() => IsSheetMusicVisible = !IsSheetMusicVisible;

    [RelayCommand]
    private void ToggleAiChat() => IsAiChatVisible = !IsAiChatVisible;
}
