using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.UI.Layout;

/// <summary>
/// Owns the persisted-layout (window size, splitter positions) and the
/// derived row-sizing that depends on view-model toggles
/// (bottom dock visibility, stacked timelines visibility/count).
///
/// Layout (MainSplitGrid, 3 rows — default favors the lane area):
///   0: timeline   ( 2* , MinHeight 120 )  — charting lanes + minimap
///   1: splitter   ( 6 px, collapses if dock hidden )
///   2: dock       ( *  , MinHeight 80…300 — EditorRowFloors )  — Inspector + 3D Preview
///
/// There is exactly one resizable boundary: the splitter between the
/// minimap and the dock. Dragging it grows the timeline and shrinks the
/// dock (or vice versa). The minimap height is fixed, so the splitter
/// is never adjacent to two rows pinned at their limits at the same time
/// — the previous off-screen / freeze problems with two splitters and a
/// resizable minimap row are gone.
/// </summary>
public sealed class MainWindowLayoutController
{
    private readonly Window _window;

    // Collapses the bottom-dock row and its splitter when both panels are
    // toggled off so the minimap sits flush against the bottom of the window.
    // When the dock comes back on, the rows are restored to their previous
    // heights so user resizes stick.
    private GridLength _savedDockRowHeight = Views.Shell.EditorSplitDefaults.DockRow;
    private GridLength _savedDockSplitterHeight = new(6);

    // The side columns' widths as the user chose them: a narrow window narrows the columns,
    // never these, and these are what is saved (a hidden chat column is 0 px wide).
    private readonly SideColumnWidth _chatWidth = new();
    private readonly SideColumnWidth _inspectorWidth = new();

    private bool _sideColumnClampHooked;

    public MainWindowLayoutController(Window window)
    {
        _window = window;
        _chatWidth.Prefer(AiChatColumnPolicy.DefaultWidthPx);
    }

    private double ChatPreferredPx => _chatWidth.PreferredPx ?? AiChatColumnPolicy.DefaultWidthPx;

    public void Bind(MainWindowViewModel vm)
    {
        ApplyBottomDockRowSizing(vm);
        UpdateStackedRowSizing(vm);
        ApplyAiChatColumn(vm.IsAiChatVisible);

        vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainWindowViewModel.IsBottomDockVisible):
                    Dispatcher.UIThread.Post(() => ApplyBottomDockRowSizing(vm));
                    break;
                case nameof(MainWindowViewModel.IsStackedTracksVisible):
                    Dispatcher.UIThread.Post(() => UpdateStackedRowSizing(vm));
                    break;
            }
        };

        vm.MultiTrackOverlay.StackedEntries.CollectionChanged += (_, _) =>
            Dispatcher.UIThread.Post(() => UpdateStackedRowSizing(vm));
    }

    public void RestoreOnLoad()
    {
        if (_window.DataContext is not MainWindowViewModel vm) return;
        var cfg = vm.AppConfig;

        // Force the 3D preview airspace flag back on at startup. The flag is
        // briefly toggled off on shutdown (and at certain dialog/lifecycle
        // points) for NativeControlHost cleanup; we don't want a stale `false`
        // to persist across sessions and leave the panel blank with no visible
        // way for the user to re-enable it (the toggle button was removed).
        vm.ProjectState.Is3DPreviewVisible = true;

        // The normal size first, maximized or not: PersistOnClose saves it only from the
        // Normal state, and a maximized start that skipped it restored DOWN to the window's
        // declared default instead.
        if (cfg.WindowWidth is { } w && w > 200) _window.Width = w;
        if (cfg.WindowHeight is { } h && h > 200) _window.Height = h;
        if (cfg.WindowMaximized) _window.WindowState = WindowState.Maximized;

        // Restore dock row to its persisted size. We use **star** units
        // (not pixels) so the row remains proportional and shrinks together with
        // the window when the user resizes it. Pixel units would pin the dock at
        // a fixed height and push the 3D preview off the bottom of the window.
        if (vm.IsBottomDockVisible &&
            _window.FindControl<Grid>("MainSplitGrid") is { } mainGrid &&
            mainGrid.RowDefinitions.Count >= 3 &&
            cfg.LayoutDockHeight is { } dockPx && dockPx >= MinDockRowPx)
        {
            // Derive timeline star value from total window content height so the
            // saved dock/timeline ratio is preserved on first show. After this,
            // GridSplitter keeps both rows as star and Avalonia handles
            // resize-with-window automatically.
            var (timelinePart, dockPart) = SavedSplitStars(cfg.LayoutTimelineAreaHeight, dockPx)
                ?? DockRowStars(dockPx, cfg.WindowHeight ?? _window.Height, Services.UiScaleController.Scale);
            mainGrid.RowDefinitions[0].Height = new GridLength(timelinePart, GridUnitType.Star);
            mainGrid.RowDefinitions[2].Height = new GridLength(dockPart, GridUnitType.Star);
            _savedDockRowHeight = mainGrid.RowDefinitions[2].Height;
        }

        // The AI chat column: its width, then whether it shows (the flag's change applies both).
        _chatWidth.Prefer(AiChatColumnPolicy.Restored(cfg.LayoutAiChatWidth));
        vm.IsAiChatVisible = cfg.LayoutAiChatVisible;
        ApplyAiChatColumn(vm.IsAiChatVisible);

        // Restore dock column widths (Inspector in col 0, 3D Preview in col 2).
        if (_window.FindControl<Grid>("InspectorSplitGrid") is { } inspGrid &&
            inspGrid.ColumnDefinitions.Count >= 3 &&
            cfg.LayoutInspectorWidth is { } inspPx && inspPx >= 200)
        {
            inspGrid.ColumnDefinitions[0].Width = new GridLength(inspPx, GridUnitType.Pixel);
            inspGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            _inspectorWidth.Prefer(inspPx);
        }

        // Both side widths are absolute pixels from whatever monitor and scale they were
        // saved on. They are clamped once the restored window has been laid out, and again
        // whenever a split changes size; the clamp narrows the columns, not the preferred widths.
        HookSideColumnClamp();
        Dispatcher.UIThread.Post(ClampSideColumns, DispatcherPriority.Background);
    }

    /// <summary>
    ///     Re-clamps the side columns whenever either split changes size, and takes a splitter
    ///     drag's width as the preferred one when the drag ends.
    /// </summary>
    private void HookSideColumnClamp()
    {
        if (_sideColumnClampHooked) return;
        _sideColumnClampHooked = true;
        if (_window.FindControl<Grid>("MainSplitGrid") is { } split) split.SizeChanged += (_, _) => ClampSideColumns();
        if (_window.FindControl<Grid>("InspectorSplitGrid") is { } insp) insp.SizeChanged += (_, _) => ClampSideColumns();
        if (_window.FindControl<GridSplitter>("AiChatSplitter") is { } chatSplitter)
            AdoptDrags(chatSplitter, ShownChatColumn, _chatWidth);
        if (_window.FindControl<GridSplitter>("InspectorPreviewSplitter") is { } inspSplitter)
            AdoptDrags(inspSplitter, PixelInspectorColumn, _inspectorWidth);
        _window.Deactivated += (_, _) => CancelSideColumnDrags();
    }

    /// <summary>
    ///     A drag that moved the splitter is the user's choice, even one that ends exactly on the
    ///     narrowed width. A press and release without a move, or an Escape that puts the width
    ///     back, also raises <c>DragCompleted</c> (Avalonia's <c>VectorEventArgs</c> carries no
    ///     cancel flag), so only a width that differs from the one at <c>DragStarted</c> counts.
    ///     The clamp leaves the column alone during the drag and re-fits it once the drag ends.
    /// </summary>
    private void AdoptDrags(GridSplitter splitter, Func<ColumnDefinition?> column, SideColumnWidth width)
    {
        splitter.DragStarted += (_, _) => width.BeginDrag(column()?.Width.Value);
        splitter.DragCompleted += (_, _) =>
        {
            width.EndDrag(column()?.Width.Value);
            ClampSideColumns();
        };
        WatchDragVisibility(splitter, CancelDrag);

        void CancelDrag()
        {
            width.CancelDrag(column()?.Width.Value);
            ClampSideColumns();
        }
    }

    /// <summary>A splitter or any ancestor hiding cancels its drag; reparenting refreshes the visibility subscriptions.</summary>
    private static void WatchDragVisibility(GridSplitter splitter, Action cancelDrag)
    {
        var sources = new List<Visual>();
        splitter.AttachedToVisualTree += (_, _) => Hook();
        splitter.DetachedFromVisualTree += (_, _) =>
        {
            cancelDrag();
            Unhook();
        };
        Hook();

        void VisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Visual.IsVisibleProperty && e.NewValue is false) cancelDrag();
        }

        void Hook()
        {
            Unhook();
            sources.Add(splitter);
            sources.AddRange(splitter.GetVisualAncestors());
            foreach (var source in sources) source.PropertyChanged += VisibilityChanged;
        }

        void Unhook()
        {
            foreach (var source in sources) source.PropertyChanged -= VisibilityChanged;
            sources.Clear();
        }
    }

    /// <summary>Cancels unfinished side-column drags and re-fits their original preferences.</summary>
    public void CancelSideColumnDrags()
    {
        _chatWidth.CancelDrag(ShownChatColumn()?.Width.Value);
        _inspectorWidth.CancelDrag(PixelInspectorColumn()?.Width.Value);
        ClampSideColumns();
    }

    /// <summary>A width the splitter wrote into a side column becomes that column's preferred width.</summary>
    private void ObserveSideColumns()
    {
        if (ShownChatColumn() is { } chat) _chatWidth.Observe(chat.Width.Value);
        if (PixelInspectorColumn() is { } inspector) _inspectorWidth.Observe(inspector.Width.Value);
    }

    private ColumnDefinition? ShownChatColumn() =>
        _window.FindControl<Grid>("MainSplitGrid") is { ColumnDefinitions.Count: >= 3 } split &&
        split.ColumnDefinitions[2] is { Width.IsAbsolute: true, Width.Value: > 0 } chat
            ? chat
            : null;

    /// <summary>
    ///     The Inspector column while it shows a pixel width. A run collapses it to 0 px, and a
    ///     collapsed column is never clamped back open (the Inspector would cover the highway).
    /// </summary>
    private ColumnDefinition? PixelInspectorColumn() =>
        _window.FindControl<Grid>("InspectorSplitGrid") is { ColumnDefinitions.Count: >= 3 } insp &&
        insp.ColumnDefinitions[0] is { Width.IsAbsolute: true, Width.Value: > 0 } inspector
            ? inspector
            : null;

    /// <summary>
    ///     Keeps the AI chat column and the Inspector column inside the split they live in, read
    ///     from the grids' own bounds (inside the UI-scale transform, so already in the units the
    ///     widths are in). A width saved on a wider monitor otherwise put its splitter off-screen
    ///     and left the editor or the 3D preview no room. Each column shows its preferred width as
    ///     far as the split allows, so a temporary shrink gives the width back when it grows.
    /// </summary>
    private void ClampSideColumns()
    {
        ObserveSideColumns();
        if (!_chatWidth.IsDragging && _window.FindControl<Grid>("MainSplitGrid") is { } split && ShownChatColumn() is { } chat &&
            _chatWidth.Shown(px => AiChatColumnPolicy.ClampToSplit(px, split.Bounds.Width)) is { } chatPx)
        {
            SetPixelWidth(chat, chatPx);
        }

        if (!_inspectorWidth.IsDragging && _window.FindControl<Grid>("InspectorSplitGrid") is { } insp &&
            PixelInspectorColumn() is { } inspector && _inspectorWidth.Shown(px => ClampedInspectorWidth(px, insp.Bounds.Width)) is { } inspectorPx)
        {
            SetPixelWidth(inspector, inspectorPx);
        }
    }

    /// <summary>
    ///     Fits both side columns to their splits now: the end of a run writes back the Inspector
    ///     width the run started with (<c>MainWindowShellLayout</c>), sized for the window as it was.
    /// </summary>
    public void RefitSideColumns() => ClampSideColumns();

    private static void SetPixelWidth(ColumnDefinition column, double px)
    {
        if (Math.Abs(column.Width.Value - px) >= 0.01) column.Width = new GridLength(px, GridUnitType.Pixel);
    }

    /// <summary>The Inspector column's floor, the markup's <c>MinWidth</c> on that column.</summary>
    internal const double MinInspectorWidthPx = 320;

    /// <summary>What the 3D preview keeps beside a restored Inspector.</summary>
    internal const double MinPreviewColumnPx = 200;

    /// <summary>
    ///     A restored Inspector width inside an <c>InspectorSplitGrid</c> that is
    ///     <paramref name="gridWidthPx" /> wide: it leaves the splitter and
    ///     <see cref="MinPreviewColumnPx" /> for the preview, never goes under
    ///     <see cref="MinInspectorWidthPx" />, and is unchanged while the grid has no size yet.
    /// </summary>
    internal static double ClampedInspectorWidth(double widthPx, double gridWidthPx)
    {
        if (!double.IsFinite(gridWidthPx) || gridWidthPx <= 0) return widthPx;
        var max = Math.Max(MinInspectorWidthPx, gridWidthPx - SplitterPx - MinPreviewColumnPx);
        return Math.Min(widthPx, max);
    }

    /// <summary>
    ///     View ▸ Reset Layout: the dock row's remembered heights go back to the defaults too.
    ///     The reset turns the Inspector and the preview on, which POSTS
    ///     <see cref="ApplyBottomDockRowSizing" />; that callback runs after the reset wrote the
    ///     default rows and wrote the user's old dock height back over them.
    /// </summary>
    public void ResetDockRowMemory()
    {
        CancelSideColumnDrags();
        _savedDockRowHeight = Views.Shell.EditorSplitDefaults.DockRow;
        _savedDockSplitterHeight = new GridLength(SplitterPx);
    }

    /// <summary>Below this the bottom dock is not a usable pane area, so a saved
    /// value under it is ignored and a restored one is floored here. It is also
    /// what keeps the restored star value POSITIVE — see
    /// <see cref="DockRowStars" />.</summary>
    internal const double MinDockRowPx = 80;

    /// <summary>The timeline row's own floor: ribbon + lanes + ruler.</summary>
    internal const double MinTimelineRowPx = 120;

    private const double SplitterPx = 6;

    /// <summary>Menu + toolbar + status rail, subtracted so the ratio is over the
    /// space the two rows actually share.</summary>
    private const double ChromeEstimatePx = 60;

    /// <summary>Used when the persisted window height is absent or unusable —
    /// the shell's own declared height.</summary>
    private const double FallbackWindowHeightPx = 900;

    /// <summary>
    ///     The star values for the timeline row and the dock row when restoring a
    ///     saved split.
    ///
    ///     <para><b>Both inputs must be in the SAME coordinate space, and they
    ///     were not.</b> <c>MainSplitGrid</c> lives inside <c>MainWindow</c>'s
    ///     <c>RootScaler</c> (<see cref="Views.Shell.WindowUiScale.ScalerName" />),
    ///     so <c>LayoutDockHeight</c> is measured in the grid's INNER,
    ///     scale-divided pixels, while <c>AppConfig.WindowHeight</c> is the
    ///     window's OUTER DIPs. Comparing them cost the user their split every
    ///     time they used the UI-scale accessibility setting: at 150 % or 200 %
    ///     the saved dock height is a fraction of the number it is capped and
    ///     subtracted against, so a 70/30 charting split came back at roughly
    ///     85/15 — and it happens on the very first launch after the scale
    ///     change, with nothing on screen to connect it to.</para>
    ///
    ///     <para><b>And the result must be POSITIVE.</b> <c>GridLength</c> throws
    ///     on a negative value, this runs from <c>MainWindow</c>'s <c>Loaded</c>
    ///     handler, and <c>RestoreOnLoad</c> is the FIRST thing in it — so a
    ///     hand-edited or corrupt negative <c>WindowHeight</c> (used unguarded,
    ///     unlike the <c>&gt; 200</c> the window size itself is checked with) made
    ///     the cap negative and took the dock-layout restore, the gameplay-layout
    ///     wiring, the focus anchor, the prewarm pass and the autopilot arm down
    ///     with it. That is the same "a saved layout must never brick startup"
    ///     rule the dock snapshot was hardened for, on the geometry beside it.</para>
    /// </summary>
    internal static (double Timeline, double Dock) DockRowStars(
        double savedDockPx, double windowOuterHeightPx, double uiScale)
    {
        var scale = double.IsFinite(uiScale) && uiScale > 0.01 ? uiScale : 1.0;
        var outer = double.IsFinite(windowOuterHeightPx) && windowOuterHeightPx > 0
            ? windowOuterHeightPx
            : FallbackWindowHeightPx;

        // The grid's own space — what LayoutDockHeight was measured in.
        var inner = outer / scale;

        // A stale over-sized dock height (saved on a different monitor or before
        // a layout change) must never starve the timeline: cap at half the
        // shared space, then floor so the star value is always usable AND
        // always positive.
        var dock = double.IsFinite(savedDockPx) ? Math.Min(savedDockPx, inner * 0.5) : MinDockRowPx;
        dock = Math.Max(dock, MinDockRowPx);
        var timeline = Math.Max(MinTimelineRowPx, inner - SplitterPx - dock - ChromeEstimatePx);
        return (timeline, dock);
    }

    /// <summary>
    ///     The user's splitter, restored EXACTLY: the two row heights saved
    ///     together become the two star values, so the split comes back where it was
    ///     on the same window and keeps its ratio on a taller or shorter one — the
    ///     timeline grows with the window instead of a fixed dock eating the growth.
    ///     Null when the pair is incomplete or unusable (an older config has the dock height
    ///     only): <see cref="DockRowStars" /> then estimates.
    /// </summary>
    internal static (double Timeline, double Dock)? SavedSplitStars(double? timelineAreaPx, double? dockPx) =>
        timelineAreaPx is { } top && dockPx is { } dock &&
        double.IsFinite(top) && double.IsFinite(dock) && top >= MinTimelineRowPx && dock >= MinDockRowPx
            ? (top, dock)
            : null;

    public void PersistOnClose()
    {
        if (_window.DataContext is not MainWindowViewModel vm) return;
        var cfg = vm.AppConfig;

        cfg.WindowMaximized = _window.WindowState == WindowState.Maximized;
        if (_window.WindowState == WindowState.Normal)
        {
            cfg.WindowWidth = _window.Width;
            cfg.WindowHeight = _window.Height;
        }

        // Quitting DURING a run must not persist the presentation as the editor
        // layout: ApplyGameplayLayout collapses the timeline row and the tool
        // dock, and OnClosing never passes through the restore. See
        // ShellGeometryPersistPolicy — this is the close-the-APP half of the bug
        // SheetStripHeightPolicy ended on the close-the-strip path.
        var inRun = vm.IsGameplayPresentation;

        if (_window.FindControl<Grid>("MainSplitGrid") is { } mainGrid &&
            mainGrid.RowDefinitions.Count >= 3)
        {
            // Only persist while the dock is visible — when hidden, ActualHeight
            // is 0 and saving that would lose the user's previous size.
            if (ShellGeometryPersistPolicy.DockHeightToPersistPx(
                    mainGrid.RowDefinitions[2].ActualHeight, vm.IsBottomDockVisible, inRun) is { } px)
            {
                cfg.LayoutDockHeight = px;
                cfg.LayoutTimelineAreaHeight = mainGrid.RowDefinitions[0].ActualHeight;
            }
        }

        // A width the user dragged and never resized after is taken first; a narrowed column
        // saves the user's width, not the narrowed one.
        ObserveSideColumns();
        if (_window.FindControl<Grid>("InspectorSplitGrid") is { } inspGrid &&
            inspGrid.ColumnDefinitions.Count > 0)
        {
            var measured = inspGrid.ColumnDefinitions[0].ActualWidth;
            var inspectorPx = measured > 0 && PixelInspectorColumn() is not null &&
                              _inspectorWidth.PreferredPx is { } preferred
                ? preferred
                : measured;
            if (ShellGeometryPersistPolicy.InspectorWidthToPersistPx(inspectorPx, inRun) is { } px)
            {
                cfg.LayoutInspectorWidth = px;
            }
        }

        if (!inRun) cfg.LayoutAiChatVisible = vm.IsAiChatVisible;
        if (AiChatColumnPolicy.ToPersist(ChatPreferredPx, vm.IsAiChatVisible, inRun, ChatPreferredPx) is { } chatPx)
        {
            cfg.LayoutAiChatWidth = chatPx;
        }

        if (_window.FindControl<Grid>("SheetSplitGrid") is { } sheetGrid &&
            sheetGrid.RowDefinitions.Count > 0)
        {
            // Only while visible — the collapsed row reports 0 and saving that
            // would lose the user's previous size — and never during a run,
            // where the row is capped at SheetStripHeightPolicy.GameplayMaxPx.
            if (vm.IsSheetMusicVisible &&
                Views.Shell.SheetStripHeightPolicy.HeightToPersistPx(
                    sheetGrid.RowDefinitions[0].ActualHeight, inRun) is { } px)
            {
                cfg.LayoutSheetHeight = px;
            }
        }

        try
        {
            vm.SaveAppConfig();
        }
        catch
        {
            // Saving layout must not block window close.
        }
    }

    /// <summary>
    /// Shows or hides the AI chat column: its splitter and its width come back at the
    ///     size the user left them, and a hidden column takes no pixels from the editor.
    /// </summary>
    public void ApplyAiChatColumn(bool visible)
    {
        if (_window.FindControl<Grid>("MainSplitGrid") is not { ColumnDefinitions.Count: >= 3 } grid) return;
        var editor = grid.ColumnDefinitions[0];
        var splitter = grid.ColumnDefinitions[1];
        var chat = grid.ColumnDefinitions[2];
        if (visible)
        {
            editor.MinWidth = AiChatColumnPolicy.MinEditorWidthPx;
            splitter.Width = new GridLength(AiChatColumnPolicy.SplitterPx);
            chat.MinWidth = AiChatColumnPolicy.MinWidthPx;
            // A width remembered on a wider window comes back only as wide as the split allows.
            var shown = _chatWidth.Shown(px => AiChatColumnPolicy.ClampToSplit(px, grid.Bounds.Width));
            chat.Width = new GridLength(shown ?? AiChatColumnPolicy.DefaultWidthPx);
            return;
        }

        if (chat is { Width.IsAbsolute: true, Width.Value: > 0 }) _chatWidth.Observe(chat.Width.Value);
        editor.MinWidth = 0;
        splitter.Width = new GridLength(0);
        chat.MinWidth = 0;
        chat.Width = new GridLength(0);
    }

    /// <summary>View ▸ Reset Layout: the chat column goes back to its first-show width.</summary>
    public void ResetAiChatColumn(bool visible)
    {
        _chatWidth.Prefer(AiChatColumnPolicy.DefaultWidthPx);
        if (visible) ApplyAiChatColumn(true);
    }

    private void ApplyBottomDockRowSizing(MainWindowViewModel vm)
    {
        if (_window.FindControl<Grid>("MainSplitGrid") is not { } grid || grid.RowDefinitions.Count < 3)
            return;

        var visible = vm.IsBottomDockVisible;
        var splitterRow = grid.RowDefinitions[1];
        var dockRow = grid.RowDefinitions[2];

        if (visible)
        {
            // During a run the splitter row belongs to MainWindowShellLayout's gameplay rows: 0 with
            // nothing above the highway, 6 under the notation band. This runs POSTED after a run
            // starts (the run hides the Inspector), and writing 6 here put the splitter back over
            // an empty band (EditorRowSplitterPolicy).
            if (!vm.IsGameplayPresentation)
            {
                splitterRow.Height = _savedDockSplitterHeight.IsAuto || _savedDockSplitterHeight.Value <= 0
                    ? new GridLength(6)
                    : _savedDockSplitterHeight;
            }

            dockRow.Height = _savedDockRowHeight.Value <= 0
                ? Views.Shell.EditorSplitDefaults.DockRow
                : _savedDockRowHeight;
        }
        else
        {
            // Remember current sizes so a subsequent toggle restores user resize state.
            if (splitterRow.Height.Value > 0) _savedDockSplitterHeight = splitterRow.Height;
            if (dockRow.Height.Value > 0) _savedDockRowHeight = dockRow.Height;
            splitterRow.Height = new GridLength(0);
            dockRow.Height = new GridLength(0);
        }

        // The dock row's MinHeight is NOT written here: it depends on the split's
        // height as well as on this flag, so its one writer is
        // MainWindowShellLayout.ApplyEditorRowFloors (0 while hidden), which
        // MainWindow runs synchronously on the same IsBottomDockVisible change.
    }

    /// <summary>
    /// Keeps the timeline grid's row sizing proportional to the number of
    /// stacked rows so a splitter resize distributes space evenly across
    /// every visible timeline (main + each stacked row). Without this the
    /// stacked panel uses an Auto row and only the main timeline grows.
    ///
    /// **The row is found through the HOST, never by a literal index**, and
    /// that is not a style preference — a literal `RowDefinitions[1]` blanks the entire chart surface
    /// the moment `TimelineGrid` grows a `ROWS` strip above the renderer: the strip
    /// takes index 0, the renderer moves to index 1, and the very first call
    /// (`Bind`, before any frame) collapses the RENDERER's row to 0 px with no
    /// exception, no warning and a log line still reporting the Vulkan
    /// timeline as active. `StackedRowSizingTests` pins the lookup.
    /// </summary>
    private void UpdateStackedRowSizing(MainWindowViewModel vm)
    {
        if (_window.FindControl<Grid>("TimelineGrid") is not { } grid) return;
        if (_window.FindControl<ContentControl>("StackedTracksHost") is not { } host) return;
        var row = StackedRowIndex(grid, host);
        if (row < 0) return;

        var visible = vm.IsStackedTracksVisible;
        var count = visible ? vm.MultiTrackOverlay.StackedEntries.Count : 0;
        // The stacked row gets N stars so each of its N children is 1*,
        // identical to the renderer's row. When hidden or empty it collapses
        // to 0 so the main timeline fills.
        grid.RowDefinitions[row].Height = count > 0
            ? new GridLength(count, GridUnitType.Star)
            : new GridLength(0, GridUnitType.Pixel);
    }

    /// <summary>
    /// The row <paramref name="host" /> occupies inside <paramref name="grid" />,
    /// or -1 when the grid has no such row. Pure so the guard can call it
    /// without a window (<c>StackedRowSizingTests</c>).
    /// </summary>
    internal static int StackedRowIndex(Grid grid, Control host)
    {
        var row = Grid.GetRow(host);
        return row >= 0 && row < grid.RowDefinitions.Count ? row : -1;
    }
}
