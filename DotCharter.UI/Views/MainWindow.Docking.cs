// MainWindow: the three dock hosts (pane registry, layout restore/persist/
// reset) and the click forwarders of the pop-out windows (Shell/MainWindowPopOuts).

using Avalonia.Controls;
using Avalonia.Interactivity;
using DotCharter.UI.Docking;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views.Shell;

namespace DotCharter.UI.Views;

public partial class MainWindow
{
    private DockLayoutController? _dockController;
    private DockLayoutController? _topDockController;
    private DockLayoutController? _sideDockController;

    /// <summary>
    ///     Whether the AI chat pane is in the side dock's layout. It stays there while the
    ///     pane is hidden — hiding is the column's <c>IsVisible</c>, so the view is built,
    ///     prewarmed and shown again without a rebuild — and leaves it only when the pane's
    ///     own close or float-window close removes it.
    /// </summary>
    private bool _aiChatInLayout = true;

    /// <summary>The 3D-preview and timeline pop-out windows (<see cref="MainWindowPopOuts" />), built on
    /// first use: it holds no state until something pops out.</summary>
    private MainWindowPopOuts? _popOutWindows;

    private MainWindowPopOuts PopOuts => _popOutWindows ??= new MainWindowPopOuts(this);

    /// <summary>The end of a run put back the Inspector width it started with: fit it to the window now.</summary>
    internal void RefitSideColumns() => _layout.RefitSideColumns();

    /// <summary>A run must cancel a splitter drag before snapshotting or collapsing its column.</summary>
    internal void CancelSideColumnDrags() => _layout.CancelSideColumnDrags();

    /// <summary>Brings both pop-outs home: View → Reset Layout and entering the gameplay presentation.</summary>
    internal void ClosePopOutWindows() => PopOuts.ClosePopOutWindows();

    /// <summary>The timeline's pop-out window, or null while it is docked, plus the pop-out verb
    /// itself (the env-gated UI autopilots in <c>Views/Shell/MainWindowDiagnostics</c> float the
    /// timeline mid-run and close it again).</summary>
    internal Window? TimelinePopOutWindow => PopOuts.TimelineWindow;

    internal void PopOutTimelineWindow() => PopOuts.PopOutTimeline(LocalizationService.Instance["Menu_FloatTimeline"]);

    private void Preview3DPopOut_Click(object? sender, RoutedEventArgs e)
        => PopOuts.PopOutPreview3D(LocalizationService.Instance["Menu_Panel3DPreview"]);

    private void Preview3DRestore_Click(object? sender, RoutedEventArgs e) => PopOuts.ClosePreview3DWindow();

    private void TimelineRestore_Click(object? sender, RoutedEventArgs e) => PopOuts.CloseTimelineWindow();

    // Contexts are VIEW MODELS, not view instances. Each dock host (main
    // DockControl, floated HostWindow) materializes its own view through the
    // app-level ViewLocator; a shared control instance crashes on
    // float/redock ("already has a visual parent").
    private void InitializeBottomDock(MainWindowViewModel vm)
    {
        var bottomPanes = new List<DockPaneDefinition>
        {
            new()
            {
                Id = "Inspector",
                Title = LocalizationService.Instance["Menu_PanelInspector"],
                Context = vm.Inspector,
                IsVisible = () => vm.IsInspectorVisible,
                SetVisible = v => vm.IsInspectorVisible = v,
                SetFloating = f => vm.IsInspectorFloating = f
            },
            // Stem Mixer and Riff Library are not panes here. They are
            // INSPECTOR TABS (a second tab strip is pointless) - the Stem Mixer pane would duplicate the Mixer tab
            // outright, and the riff library is the same kind of surface as the
            // other tabs. The View-menu items, the palette entries and the
            // keybind all SELECT the tab instead of showing
            // a pane. See MainWindowViewModel.IsStemMixerVisible /
            // IsPhraseCatalogVisible and InspectorViewModel.RiffLibraryTabIndex.
        };

        _dockController = new DockLayoutController(BottomDock, bottomPanes) { FloatingWindowCreated = PrepareDockWindow };
        _dockController.Rebuild();

        var topPanes = new List<DockPaneDefinition>
        {
            new()
            {
                Id = "SheetMusic",
                Title = LocalizationService.Instance["Menu_PanelSheetMusic"],
                Context = vm.SheetMusicPane,
                IsVisible = () => vm.IsSheetMusicVisible,
                SetVisible = v => vm.IsSheetMusicVisible = v,
                SetFloating = f => vm.IsSheetMusicFloating = f
            }
        };

        _topDockController = new DockLayoutController(TopDock, topPanes) { FloatingWindowCreated = PrepareDockWindow };
        _topDockController.Rebuild();

        InitializeSideDock(vm);

        // "Dock it back" on the card a floated pane leaves in its spot.
        DockInspectorHomeButton.Click += (_, _) => _dockController?.DockPaneHome("Inspector");
        DockSheetMusicHomeButton.Click += (_, _) => _topDockController?.DockPaneHome("SheetMusic");
    }

    /// <summary>
    /// The editor's right-hand dock: the AI chat pane. The toggle, the keybind, the
    ///     palette and the View menu write <see cref="MainWindowViewModel.IsAiChatVisible" />,
    ///     which shows the column (<see cref="SyncAiChatPane" />); the pane's own ✕ closes it.
    /// </summary>
    private void InitializeSideDock(MainWindowViewModel vm)
    {
        if (vm.AiChatPane is null) return;
        var sidePanes = new List<DockPaneDefinition>
        {
            new()
            {
                Id = AiChatPaneId,
                Title = vm.AiChatPane.PaneTitle,
                Context = vm.AiChatPane,
                IsVisible = () => _aiChatInLayout,
                SetVisible = v =>
                {
                    _aiChatInLayout = v;
                    if (!v) vm.IsAiChatVisible = false;
                },
                SetFloating = f => vm.IsAiChatFloating = f
            }
        };

        _sideDockController = new DockLayoutController(SideDock, sidePanes) { FloatingWindowCreated = PrepareDockWindow };
        _sideDockController.Rebuild();
        DockAiChatHomeButton.Click += (_, _) => _sideDockController?.DockPaneHome(AiChatPaneId);
    }

    internal const string AiChatPaneId = "AiChat";

    /// <summary>A Dock float window is a top-level of the shell like the pop-outs: its key routes, scale and direction.</summary>
    private void PrepareDockWindow(Window window)
    {
        WireGlobalKeyRoutes(window);
        FloatingWindowShell.HostTemplated(window, WindowRootGrid);
    }

    /// <summary>The side dock (tests read its layout).</summary>
    internal DockLayoutController? SideDockController => _sideDockController;

    /// <summary>
    ///     Follows <see cref="MainWindowViewModel.IsAiChatVisible" />: showing a pane its ✕
    ///     closed puts it back in the layout; hiding a FLOATED pane closes its window too, so
    ///     the toggle never leaves a chat window behind it.
    /// </summary>
    private void SyncAiChatPane(MainWindowViewModel vm)
    {
        if (_sideDockController is not { } side) return;
        if (vm.IsAiChatVisible && !_aiChatInLayout)
        {
            _aiChatInLayout = true;
            side.SetPaneVisible(AiChatPaneId, true);
        }
        else if (!vm.IsAiChatVisible && side.IsPaneFloating(AiChatPaneId))
        {
            side.SetPaneVisible(AiChatPaneId, false);
        }
    }

    /// <summary>
    /// Restores the persisted dock layout (per-pane docked/floating/pinned/
    /// closed state, float bounds, active tabs). Called from Loaded so float
    /// windows open after the main window is up (earlier races activation).
    ///
    /// <para><b>A layout snapshot must never be able to brick startup.</b> It
    /// is JSON inside <c>AppConfig</c>, so it can be absent, from an older or
    /// newer build, or corrupt in ways <c>CaptureState</c> never writes. This
    /// runs inside the <c>Loaded</c> handler, and everything after it there —
    /// <c>PrewarmTopLevelViews</c>, the autopilot arm, the focus anchor, the
    /// gameplay-layout wiring — is skipped if it throws, so an unreadable
    /// layout would silently cost the user working keyboard focus and the
    /// whole responsiveness budget. Every rejection falls back to the DEFAULT
    /// layout and says why; <see cref="DockLayoutController.RestoreState" />
    /// does the same per-pane one level down.</para>
    /// </summary>
    private void RestoreDockLayout(MainWindowViewModel vm)
    {
        var state = vm.AppConfig.DockLayout;
        if (state == null) return;
        if (state.Version != DockLayoutState.CurrentVersion)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[Dock] Saved layout is version {state.Version}, this build writes " +
                $"{DockLayoutState.CurrentVersion} — falling back to the default layout.");
            return;
        }

        try
        {
            _dockController?.RestoreState(state);
            _topDockController?.RestoreState(state);
            // The chat pane's column visibility is its own setting (AppConfig.LayoutAiChatVisible);
            // the snapshot says only where the pane is, so a "Closed" entry must not hide it.
            if (state.Panes.ContainsKey(AiChatPaneId)) _sideDockController?.RestoreState(state);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[Dock] Restoring the saved layout failed ({ex.GetType().Name}: {ex.Message}) — " +
                "falling back to the default layout.");
        }
    }

    /// <summary>
    /// Snapshots the full dock layout into the app config. Called from
    /// OnClosing before the layout controller persists.
    ///
    /// <para><b>Never during a run.</b> The gameplay presentation CLOSES the
    /// Inspector and the Sheet Music pane itself
    /// (<c>MainWindowViewModel.OnIsGameplayPresentationChanged</c>, which
    /// snapshots the pre-run flags and restores them when the run ends) — and
    /// <c>OnClosing</c> never passes through that restore. Capturing here
    /// recorded both panes as "Closed" as if the user had closed them, so
    /// quitting from the pause menu came back next launch to an editor with no
    /// Inspector and no notation strip. The snapshot already in the config is
    /// the last layout the user actually arranged; leaving it alone is the
    /// fix. See <see cref="Layout.ShellGeometryPersistPolicy" />, which states
    /// the same rule for the splitter geometry.</para>
    /// </summary>
    private void PersistDockLayout()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (!Layout.ShellGeometryPersistPolicy.MayPersistDockPanes(vm.IsGameplayPresentation)) return;
        var state = new DockLayoutState();
        _dockController?.CaptureState(state);
        _topDockController?.CaptureState(state);
        _sideDockController?.CaptureState(state);
        vm.AppConfig.DockLayout = state;
    }

    /// <summary>
    /// View → Reset Layout: default splitter positions, all panes docked,
    /// cleared float-state persistence.
    /// </summary>
    private void ResetLayoutToDefaults(MainWindowViewModel vm)
    {
        if (this.FindControl<Grid>("MainSplitGrid") is { } mainGrid && mainGrid.RowDefinitions.Count >= 3)
        {
            // The split a first launch shows: one source, so reset means
            // "back to how it started".
            mainGrid.RowDefinitions[0].Height = Shell.EditorSplitDefaults.TimelineRow;
            mainGrid.RowDefinitions[1].Height = new GridLength(6);
            mainGrid.RowDefinitions[2].Height = Shell.EditorSplitDefaults.DockRow;
        }

        // The rows a posted dock-visibility change restores are the defaults now too.
        _layout.ResetDockRowMemory();

        if (this.FindControl<Grid>("InspectorSplitGrid") is { } inspGrid && inspGrid.ColumnDefinitions.Count >= 3)
        {
            inspGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            inspGrid.ColumnDefinitions[2].Width = new GridLength(2, GridUnitType.Star);
        }

        var cfg = vm.AppConfig;
        cfg.DockLayout = null;
        cfg.LayoutDockHeight = null;
        cfg.LayoutInspectorWidth = null;
        cfg.LayoutSheetHeight = null;
        _shellLayout.UpdateSheetRowLayout(vm);

        cfg.LayoutAiChatWidth = null;
        _layout.ResetAiChatColumn(vm.IsAiChatVisible);

        // Rebuild so floated/closed panes come home docked.
        _dockController?.Rebuild();
        _topDockController?.Rebuild();
        _aiChatInLayout = true;
        _sideDockController?.Rebuild();

        // ...and so do the two pop-outs. They are NOT Dock.Avalonia panes, so
        // Rebuild() cannot see them: before this, "float the timeline, then
        // View → Reset Layout" left the timeline window open over a main window
        // showing the "popped out" placeholder — the exact orphan shape the
        // manual dock checklist tests for on the dock side. Close() runs the
        // Closed handler, which puts the single instance back in its slot.
        ClosePopOutWindows();
        _shellLayout.ResetGameplayLayoutMemory(vm, _gameplayDock);
    }
}
