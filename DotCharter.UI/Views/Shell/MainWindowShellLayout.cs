// The shell's grid geometry - the sheet-strip rows, the gameplay-presentation
// collapse/restore (with its native keyboard-focus watchdog) and the editor
// chrome show/hide.
//
// Extracted from the MainWindow.Layout partial: all 11 of its private fields
// were its own and it shared NONE with any sibling, so it was a collaborator in
// hiding rather than a page break. Owned by MainWindow, constructed in its ctor.
// (Distinct from Layout/MainWindowLayoutController, which owns the PERSISTED
// window/splitter geometry; this one owns the derived row/column overrides.)

using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.UI.Views.Shell;

internal sealed class MainWindowShellLayout(MainWindow window)
{
    private readonly MainWindow _window = window;

    /// <summary>
    ///     Drives the sheet-strip rows of SheetSplitGrid: a pixel height
    ///     (restored from AppConfig, adjusted by the GridSplitter) while the
    ///     pane is visible, hard 0 when hidden so no dead space remains. The
    ///     current height is captured into AppConfig before collapsing and
    ///     persisted with the rest of the layout on close.
    /// </summary>
    private bool _sheetGridSizeHooked;

    internal void UpdateSheetRowLayout(MainWindowViewModel vm)
    {
        if (_window.FindControl<Grid>("SheetSplitGrid") is not { } grid || grid.RowDefinitions.Count < 3)
            return;

        ApplyEditorRowFloors(vm);

        if (vm.IsGameplayPresentation && _gameplayLayoutApplied)
        {
            ApplyGameplayRows(vm);
            return;
        }

        if (vm.IsSheetMusicVisible)
        {
            // Gameplay caps it at a compact band — the persisted editor height is
            // sized for reading full scores, not an in-run tab strip. The cap and
            // the write-back rule are ONE type on purpose: see
            // SheetStripHeightPolicy for the preference this would otherwise eat.
            var height = SheetStripHeightPolicy.OpenHeightPx(
                vm.AppConfig.LayoutSheetHeight, vm.IsGameplayPresentation);
            grid.RowDefinitions[0].MinHeight =
                vm.IsGameplayPresentation ? 0 : SheetStripHeightPolicy.MinPx;
            grid.RowDefinitions[0].Height = new GridLength(height);
            grid.RowDefinitions[1].Height = new GridLength(6);
            ClampSheetRow(grid);

            if (!_sheetGridSizeHooked)
            {
                _sheetGridSizeHooked = true;
                grid.SizeChanged += (_, _) =>
                {
                    if (_window.DataContext is MainWindowViewModel v && v.IsSheetMusicVisible)
                        ClampSheetRow(grid);
                };
            }
        }
        else
        {
            // The measured height is only the user's PREFERENCE outside a run.
            // Closing the strip mid-run must not write the gameplay cap back, or
            // one run would permanently shrink a 480 px notation strip to 240 —
            // and that includes the run's EXIT, which restores the pre-run flags
            // (closing a band opened mid-run) before the gameplay rows are undone.
            var persist = SheetStripHeightPolicy.HeightToPersistPx(
                grid.RowDefinitions[0].ActualHeight, vm.IsGameplayPresentation || _gameplayLayoutApplied);
            if (persist is { } px) vm.AppConfig.LayoutSheetHeight = px;
            grid.RowDefinitions[0].MinHeight = 0;
            grid.RowDefinitions[0].Height = new GridLength(0);
            grid.RowDefinitions[1].Height = new GridLength(0);
        }
    }

    /// <summary>Shrinks (never grows) the sheet row so the timeline below
    /// always keeps its row's minimum — 360 px on a tall window (the full
    /// event ribbon, six string lanes and the time ruler), less on a short one
    /// where it gives way to the bottom dock (<see cref="EditorRowFloors" />).</summary>
    private static void ClampSheetRow(Grid grid)
    {
        var timelineMin = grid.RowDefinitions[2].MinHeight;
        var available = grid.Bounds.Height;
        // Below sheet minimum + splitter + timeline minimum: let the MinHeights fight it out.
        if (available < SheetStripHeightPolicy.MinPx + TimelineAreaMinHeight.SheetSplitterPx + timelineMin) return;

        var max = available - TimelineAreaMinHeight.SheetSplitterPx - timelineMin;
        var current = grid.RowDefinitions[0].Height;
        if (current.IsAbsolute && current.Value > max)
            grid.RowDefinitions[0].Height = new GridLength(Math.Max(SheetStripHeightPolicy.MinPx, max));
    }

    private bool _splitGridSizeHooked;

    /// <summary>
    ///     The minimum heights of the editor's top area (<c>MainSplitGrid</c> row 0), its
    ///     timeline row (<c>SheetSplitGrid</c> row 2) and the bottom dock (<c>MainSplitGrid</c>
    ///     row 2) for the height the split has NOW — re-derived on every size change of the
    ///     split, because on a short window the timeline gives way to the dock and on a tall one
    /// it does not (<see cref="EditorRowFloors" />). The ONE writer of those three
    ///     <c>MinHeight</c>s outside the gameplay presentation.
    ///     <para>
    ///         The top area's floor stays what it refuses to be smaller than — otherwise the
    ///         splitter (or a large persisted bottom dock) squeezes it below the timeline's own
    ///         minimum, and the timeline paints over the song map and under the inspector
    ///         (<see cref="TimelineAreaMinHeight" />). During the gameplay presentation the
    ///         timeline area is collapsed entirely, so NO minimum applies — re-imposing one here
    ///         (this runs on every notation toggle) blew a 570 px hole between the tab band and
    ///         the highway — and the timeline row belongs to <see cref="ApplyGameplayLayout" />.
    ///     </para>
    /// </summary>
    internal void ApplyEditorRowFloors(MainWindowViewModel vm)
    {
        if (_window.FindControl<Grid>("MainSplitGrid") is not { } main || main.RowDefinitions.Count < 3)
            return;

        if (!_splitGridSizeHooked)
        {
            _splitGridSizeHooked = true;
            main.SizeChanged += (_, e) =>
            {
                if (e.HeightChanged && _window.DataContext is MainWindowViewModel v)
                    ApplyEditorRowFloors(v);
            };
        }

        var topArea = TimelineAreaMinHeight.For(
            vm.IsSheetMusicVisible, vm.IsMinimapShown, vm.IsGameplayPresentation);
        if (vm.IsGameplayPresentation)
        {
            SetMinHeight(main.RowDefinitions[0], topArea);
            SetMinHeight(main.RowDefinitions[2],
                vm.IsBottomDockVisible ? EditorRowFloors.DockLegacyMinPx : 0);
            return;
        }

        var floors = EditorRowFloors.For(main.Bounds.Height, topArea, vm.IsBottomDockVisible);
        SetMinHeight(main.RowDefinitions[0], floors.TopAreaMinPx);
        SetMinHeight(main.RowDefinitions[2], floors.DockMinPx);
        if (_window.FindControl<Grid>("SheetSplitGrid") is { } sheet && sheet.RowDefinitions.Count >= 3)
            SetMinHeight(sheet.RowDefinitions[2], floors.TimelineRowMinPx);
    }

    /// <summary>Writes only a CHANGED value: every write invalidates the grid's measure.</summary>
    private static void SetMinHeight(RowDefinition row, double px)
    {
        if (!row.MinHeight.Equals(px)) row.MinHeight = px;
    }

    // ---- Gameplay presentation (Rocksmith-style run) --------------------
    // The VM collapses panel flags; the grid ROWS (timeline area, splitter)
    // are XAML-static, so they are collapsed/restored here. The sheet strip
    // stays available: with the timeline row pinned to 0, toggling
    // IsSheetMusicVisible during a run shows just the notation band above
    // the highway (UpdateSheetRowLayout keeps managing its pixel height).
    private bool _gameplayLayoutApplied;
    private GridLength _savedMainRow0Height;
    private double _savedMainRow0MinHeight;
    private GridLength _savedMainSplitterHeight;
    private GridLength _savedTimelineRowHeight;
    private double _savedTimelineRowMinHeight;
    private GridLength _savedDockCol0Width;
    private GridLength _savedDockCol1Width;
    private double _savedBottomDockMinWidth;

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint parent, EnumChildProc proc, nint lParam);

    private delegate bool EnumChildProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtrW(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GuiThreadInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int cbSize;
        public uint flags;
        public nint hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public int l, t, r, b;
    }

    private const int GwlExStyle = -20;
    private const nint WsExNoActivate = 0x08000000;

    // The native swapchain child HWND (different thread!) grabs Win32
    // keyboard focus whenever the user clicks the highway — after that the
    // Avalonia window receives NO key messages, and because the child lives
    // on another thread, a same-thread GetFocus() can't even SEE the theft.
    // Two defenses:
    //  1. WS_EX_NOACTIVATE on every child HWND — clicks keep working but no
    //     longer move activation/focus (the actual fix).
    //  2. A watchdog that reads the FOREGROUND thread's real focus window via
    //     GetGUIThreadInfo and yanks focus back cross-thread if needed
    //     (belt-and-braces for child windows created later).
    private DispatcherTimer? _gameplayFocusWatchdog;

    private void MarkNativeChildrenNoActivate()
    {
        var hwnd = _window.TryGetPlatformHandle()?.Handle ?? 0;
        if (hwnd == 0) return;
        EnumChildWindows(hwnd, (child, _) =>
        {
            var style = GetWindowLongPtrW(child, GwlExStyle);
            if ((style & WsExNoActivate) == 0)
                SetWindowLongPtrW(child, GwlExStyle, style | WsExNoActivate);
            return true;
        }, 0);
    }

    private void ReclaimKeyboardFocus()
    {
        var hwnd = _window.TryGetPlatformHandle()?.Handle ?? 0;
        if (hwnd == 0) return;

        var info = new GuiThreadInfo { cbSize = Marshal.SizeOf<GuiThreadInfo>() };
        // idThread 0 = the foreground thread, whichever process/thread it is.
        if (!GetGUIThreadInfo(0, ref info)) return;
        if (info.hwndFocus == hwnd || info.hwndFocus == 0) return;

        // Only steal focus back from OUR OWN child windows — never from
        // another application the user switched to.
        GetWindowThreadProcessId(info.hwndFocus, out var focusPid);
        if (focusPid != (uint)Environment.ProcessId) return;

        var focusThread = GetWindowThreadProcessId(info.hwndFocus, out _);
        var myThread = GetCurrentThreadId();
        if (focusThread == myThread)
        {
            SetFocus(hwnd);
            return;
        }

        if (!AttachThreadInput(myThread, focusThread, true)) return;
        try { SetFocus(hwnd); }
        finally { AttachThreadInput(myThread, focusThread, false); }
    }

    private void UpdateGameplayFocusWatchdog(MainWindowViewModel vm)
    {
        if (vm.IsGameplayPresentation)
        {
            MarkNativeChildrenNoActivate();
            if (_gameplayFocusWatchdog != null) return;
            _gameplayFocusWatchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _gameplayFocusWatchdog.Tick += (_, _) =>
            {
                MarkNativeChildrenNoActivate();
                ReclaimKeyboardFocus();
            };
            _gameplayFocusWatchdog.Start();
        }
        else
        {
            _gameplayFocusWatchdog?.Stop();
            _gameplayFocusWatchdog = null;
        }
    }

    /// <summary>
    ///     Home reads live figures — the library size, the last run, the device
    ///     — so arriving on its tab has to re-read them. Its host holds one
    ///     screen and therefore never navigates again after the first show,
    ///     which is the only thing that would otherwise announce it.
    /// </summary>
    private void ReshowHome() => (_window.FindControl<ContentControl>("HomeHost")?.Content as ScreenHost)?.NotifyReshown();

    internal void WireGameplayLayout(MainWindowViewModel vm)
    {
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.SelectedMainTabIndex))
            {
                if (vm.IsHomeVisible) ReshowHome();
                Dispatcher.UIThread.Post(_window.AnchorFocus);
                return;
            }
            if (e.PropertyName != nameof(MainWindowViewModel.IsGameplayPresentation)) return;
            ApplyGameplayLayout(vm);
            UpdateGameplayFocusWatchdog(vm);
            Dispatcher.UIThread.Post(_window.AnchorFocus);
        };

        // Every Play Mode screen swap rips the focused control out
        // of the tree; without a re-anchor the app goes keyboard-deaf.
        vm.PlayMode.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModels.PlayMode.PlayModeViewModel.CurrentScreen))
                Dispatcher.UIThread.Post(_window.AnchorFocus);
        };

        // Dialog opened/closed: same problem (overlay swaps the tree around
        // the focused element). Anchor so Esc can close the dialog.
        vm.Dialogs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IAppDialogService.HasOpenDialog))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _window.AnchorFocusIfLost();
                });
            }
        };

        ApplyGameplayLayout(vm);
        UpdateGameplayFocusWatchdog(vm);
    }

    private void ApplyGameplayLayout(MainWindowViewModel vm)
    {
        var mainGrid = _window.FindControl<Grid>("MainSplitGrid");
        var sheetGrid = _window.FindControl<Grid>("SheetSplitGrid");
        var timelineSlot = _window.FindControl<ContentControl>("TimelineSlot");
        var dockGrid = _window.FindControl<Grid>("InspectorSplitGrid");
        var bottomDock = _window.FindControl<Dock.Avalonia.Controls.DockControl>("BottomDock");
        if (mainGrid == null || sheetGrid == null || dockGrid == null) return;

        if (vm.IsGameplayPresentation && !_gameplayLayoutApplied)
        {
            _gameplayLayoutApplied = true;

            // The run is PRESENTED in the 3D preview panel — this branch
            // collapses the timeline row and the tool dock so that panel owns
            // the window. With the panel popped out that slot is EMPTY: the
            // count-in ran against a main window showing the "popped out"
            // placeholder while the highway drew in a small window off to one
            // side. The timeline pop-out is the mirror case, because the
            // restore branch below turns TimelineSlot visible again
            // unconditionally and would uncollapse an empty slot beside its own
            // placeholder. Both come home first, exactly as View → Reset Layout
            // brings them home — Rebuild() cannot see either of them.
            _window.ClosePopOutWindows();

            _savedMainRow0Height = mainGrid.RowDefinitions[0].Height;
            _savedMainRow0MinHeight = mainGrid.RowDefinitions[0].MinHeight;
            _savedMainSplitterHeight = mainGrid.RowDefinitions[1].Height;
            _savedTimelineRowHeight = sheetGrid.RowDefinitions[2].Height;
            _savedTimelineRowMinHeight = sheetGrid.RowDefinitions[2].MinHeight;
            _savedDockCol0Width = dockGrid.ColumnDefinitions[0].Width;
            _savedDockCol1Width = dockGrid.ColumnDefinitions[1].Width;
            _savedBottomDockMinWidth = bottomDock?.MinWidth ?? 0;

            mainGrid.RowDefinitions[0].MinHeight = 0;
            sheetGrid.RowDefinitions[2].MinHeight = 0;
            sheetGrid.RowDefinitions[2].Height = new GridLength(0);
            timelineSlot?.IsVisible = false;
            ApplyGameplayRows(vm);

            // The empty tool dock keeps its splitter proportion (and a 320 px
            // MinWidth) even with every pane closed — reclaim the full width
            // for the highway.
            dockGrid.ColumnDefinitions[0].Width = new GridLength(0);
            dockGrid.ColumnDefinitions[1].Width = new GridLength(0);
            if (bottomDock != null)
            {
                bottomDock.MinWidth = 0;
                bottomDock.IsVisible = false;
            }
        }
        else if (!vm.IsGameplayPresentation && _gameplayLayoutApplied)
        {
            _gameplayLayoutApplied = false;
            mainGrid.RowDefinitions[0].Height = _savedMainRow0Height;
            mainGrid.RowDefinitions[0].MinHeight = _savedMainRow0MinHeight;
            mainGrid.RowDefinitions[1].Height = _savedMainSplitterHeight;
            sheetGrid.RowDefinitions[2].Height = _savedTimelineRowHeight;
            sheetGrid.RowDefinitions[2].MinHeight = _savedTimelineRowMinHeight;
            timelineSlot?.IsVisible = true;

            dockGrid.ColumnDefinitions[0].Width = _savedDockCol0Width;
            dockGrid.ColumnDefinitions[1].Width = _savedDockCol1Width;
            if (bottomDock != null)
            {
                bottomDock.MinWidth = _savedBottomDockMinWidth;
                bottomDock.IsVisible = true;
            }

            // Re-derive the sheet/timeline row minimums for editor use —
            // UpdateSheetRowLayout may have run (notation toggle) while the
            // presentation overrides were in force.
            UpdateSheetRowLayout(vm);
        }
    }

    /// <summary>
    ///     The rows above the run while it is on. Nothing above the highway — the timeline area
    ///     is collapsed — means no row and no splitter: the run fills the editor's height. The
    ///     one view that can sit above it is the notation band the HUD toggles; while it is on,
    ///     the band takes the top row at its gameplay height (filling it: the sheet grid's own
    ///     splitter would trade against the 0 px timeline row) and the DOCK splitter is the seam
    ///     between band and run, so dragging it resizes the two
    ///     (<see cref="Layout.EditorRowSplitterPolicy" />).
    /// </summary>
    private static void ApplyGameplayRows(MainWindowViewModel vm, Grid mainGrid, Grid sheetGrid)
    {
        if (vm.IsSheetMusicVisible)
        {
            mainGrid.RowDefinitions[0].Height = new GridLength(
                SheetStripHeightPolicy.OpenHeightPx(vm.AppConfig.LayoutSheetHeight, gameplayPresentation: true));
            mainGrid.RowDefinitions[1].Height = new GridLength(6);
            sheetGrid.RowDefinitions[0].MinHeight = 0;
            sheetGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            mainGrid.RowDefinitions[0].Height = new GridLength(0);
            mainGrid.RowDefinitions[1].Height = new GridLength(0);
            sheetGrid.RowDefinitions[0].MinHeight = 0;
            sheetGrid.RowDefinitions[0].Height = new GridLength(0);
        }

        sheetGrid.RowDefinitions[1].Height = new GridLength(0);
    }

    private void ApplyGameplayRows(MainWindowViewModel vm)
    {
        if (_window.FindControl<Grid>("MainSplitGrid") is { RowDefinitions.Count: >= 3 } mainGrid &&
            _window.FindControl<Grid>("SheetSplitGrid") is { RowDefinitions.Count: >= 3 } sheetGrid)
        {
            ApplyGameplayRows(vm, mainGrid, sheetGrid);
        }
    }

    // There is no `ApplyShellChrome`. Writing `IsVisible` on the menu bar, the title-bar context cluster and the
    // status rail from code, against an `IsPlayModeVisible ||
    // IsGameplayPresentation` condition, would be a SECOND writer for a property the
    // markup binds to `IsShellChromeVisible`, and one that does not know
    // about Home. Two writers on one property is the bug shape this repo keeps
    // paying for: the binding and the assignment take turns, and which one you
    // see depends on the order the notifications arrive in. The menu bar and
    // the context cluster live INSIDE `TitleBarRow` and hide with it; the
    // status rail binds the flag through the window.
}
