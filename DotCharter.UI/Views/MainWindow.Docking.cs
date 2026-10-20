// MainWindow: the three dock hosts (pane registry, layout restore/persist/
// reset) and the 3D-preview / chart-timeline pop-out windows.

using Avalonia.Controls;
using Avalonia.Interactivity;
using DotCharter.UI.Docking;
using DotCharter.UI.ViewModels.Shell;

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
    private Window? _preview3DWindow;


    /// <summary>
    /// Set once <see cref="OnClosing" /> has passed the unsaved-changes gate.
    ///
    /// <para>Both pop-out windows are OWNED (<c>Show(this)</c>), so closing the
    /// main window closes them — which runs their <c>Closed</c> handler, which
    /// re-parents the single <c>Preview3DView</c> / <c>TimelineGrid</c> instance
    /// back into a window that is going away. For the 3D panel that is a
    /// composition surface rebuilt (and its render loop restarted from
    /// <c>OnAttachedToVisualTree</c>) against a top-level mid-teardown — GPU
    /// work for no benefit, since nothing will ever paint it. The
    /// handlers consult this flag and do nothing on the shutdown path.</para>
    /// </summary>
    private bool _shuttingDown;

    /// <summary>Closes any pop-out window, without restoring its content — the
    /// window is closing because the app is. Called from
    /// <see cref="OnClosing" /> after the data-loss gate.</summary>
    private void MarkShuttingDownAndClosePopOuts()
    {
        _shuttingDown = true;
        _preview3DWindow?.Close();
        _timelineWindow?.Close();
    }

    /// <summary>
    ///     Brings both pop-outs home (their <c>Closed</c> handlers put the single
    ///     instance back in its slot). The pop-outs are NOT Dock.Avalonia panes,
    ///     so <c>Rebuild()</c> cannot see them and every "bring it home" path has
    ///     to name them: View → Reset Layout, and entering the gameplay
    ///     presentation.
    /// </summary>
    internal void ClosePopOutWindows()
    {
        _preview3DWindow?.Close();
        _timelineWindow?.Close();
    }

    /// <summary>
    /// Moves the single <c>Preview3DView</c> instance into its own window.
    /// The move is a detach + attach of <c>Preview3DComposedCanvas</c>: the
    /// detach stops the loop and retires the old window's surface and pump, the
    /// attach builds new ones against the new window's compositor and restarts
    /// the loop (guarded by its attach generation).
    /// </summary>
    private void Preview3DPopOut_Click(object? sender, RoutedEventArgs e)
    {
        if (_preview3DWindow != null)
        {
            _preview3DWindow.Activate();
            return;
        }

        var view = Preview3DPane;
        var width = Math.Max(640, view.Bounds.Width);
        var height = Math.Max(360, view.Bounds.Height);
        Preview3DSlot.Content = null;

        var window = new Window
        {
            Title = LocalizationService.Instance["Menu_Panel3DPreview"],
            Width = width,
            Height = height,
            Background = Background,
            Icon = Icon,
            DataContext = DataContext,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = view
        };
        WireGlobalKeyRoutes(window);
        window.Closed += (_, _) =>
        {
            _preview3DWindow = null;
            window.Content = null;
            // Shutdown path: never re-attach the native Vulkan child HWND to a
            // main window that is itself tearing down (see _shuttingDown).
            if (_shuttingDown) return;
            Preview3DSlot.Content ??= view;
            UpdatePreview3DPlaceholder();
        };

        _preview3DWindow = window;
        UpdatePreview3DPlaceholder();
        window.Show(this);
    }

    private void Preview3DRestore_Click(object? sender, RoutedEventArgs e)
        => _preview3DWindow?.Close();

    private void UpdatePreview3DPlaceholder()
    {
        Preview3DPlaceholder.IsVisible = _preview3DWindow != null;
        Preview3DSlot.IsVisible = _preview3DWindow == null;
    }

    private Window? _timelineWindow;

    /// <summary>The timeline's pop-out window, or null while it is docked, plus the
    /// pop-out verb itself. This is the ONE tie
    /// <c>Views/Shell/MainWindowDiagnostics</c> had to this partial when it was
    /// extracted — the env-gated UI autopilots float the timeline mid-run and close
    /// it again — exposed as members instead of left a shared field.</summary>
    internal Window? TimelinePopOutWindow => _timelineWindow;

    internal void PopOutTimelineWindow() => PopOutTimeline();


    /// <summary>
    /// Moves the chart timeline composite (track header, renderer, stacked
    /// tracks) into its own window. The renderer's backend is torn down and
    /// re-created for the new top-level (composed-Vulkan rebuilds surface +
    /// pump on re-attach; the GL fallback handles context loss in its inner
    /// control); all pointer/key handlers are attached to the subtree
    /// itself, so editing keeps working in the float window.
    /// </summary>
    private void PopOutTimeline()
    {
        if (_timelineWindow != null)
        {
            _timelineWindow.Activate();
            return;
        }

        var timeline = TimelineGrid;
        var width = Math.Max(800, timeline.Bounds.Width);
        var height = Math.Max(320, timeline.Bounds.Height);
        TimelineSlot.Content = null;

        var window = new Window
        {
            Title = LocalizationService.Instance["Menu_FloatTimeline"],
            Width = width,
            Height = height,
            Background = Background,
            Icon = Icon,
            DataContext = DataContext,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = timeline
        };
        WireGlobalKeyRoutes(window);
        window.Closed += (_, _) =>
        {
            _timelineWindow = null;
            window.Content = null;
            // Shutdown path: re-attaching rebuilds the composed-Vulkan surface
            // + pump against a dying top-level, and Focus() on a closing window
            // is what leaves the app keyboard-deaf on its way out.
            if (_shuttingDown) return;
            TimelineSlot.Content ??= timeline;
            UpdateTimelinePlaceholder();
            timeline.Focus();
        };

        _timelineWindow = window;
        UpdateTimelinePlaceholder();
        window.Show(this);
        timeline.Focus();
    }

    private void TimelineRestore_Click(object? sender, RoutedEventArgs e)
        => _timelineWindow?.Close();

    private void UpdateTimelinePlaceholder()
    {
        TimelinePlaceholder.IsVisible = _timelineWindow != null;
        TimelineSlot.IsVisible = _timelineWindow == null;
    }

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

        _dockController = new DockLayoutController(BottomDock, bottomPanes);
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

        _topDockController = new DockLayoutController(TopDock, topPanes);
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

        _sideDockController = new DockLayoutController(SideDock, sidePanes);
        _sideDockController.Rebuild();
        DockAiChatHomeButton.Click += (_, _) => _sideDockController?.DockPaneHome(AiChatPaneId);
    }

    internal const string AiChatPaneId = "AiChat";

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
    }
}
