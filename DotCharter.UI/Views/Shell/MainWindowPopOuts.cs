// The 3D-preview and chart-timeline pop-out windows: each moves the ONE
// instance of its view into an owned window and back, and both stand down on
// the shutdown path.
//
// Extracted from the MainWindow.Docking partial: its three fields (the two
// windows and the shutdown latch) were touched by nothing else, so it was a
// collaborator in hiding rather than a page break. Owned by MainWindow,
// built on first use; the XAML click handlers forward here with the
// localized window title (the window reads the static localization, this does not).

using Avalonia.Controls;

namespace DotCharter.UI.Views.Shell;

internal sealed class MainWindowPopOuts(MainWindow window)
{
    private readonly MainWindow _window = window;
    private Window? _preview3DWindow;
    private Window? _timelineWindow;

    /// <summary>
    /// Set once <c>MainWindow.OnClosing</c> has passed the unsaved-changes gate.
    ///
    /// <para>Both pop-out windows are OWNED (<c>Show(owner)</c>), so closing the
    /// main window closes them — which runs their <c>Closed</c> handler, which
    /// re-parents the single <c>Preview3DView</c> / <c>TimelineGrid</c> instance
    /// back into a window that is going away. For the 3D panel that is a
    /// composition surface rebuilt (and its render loop restarted from
    /// <c>OnAttachedToVisualTree</c>) against a top-level mid-teardown — GPU
    /// work for no benefit, since nothing will ever paint it. The
    /// handlers consult this flag and do nothing on the shutdown path.</para>
    /// </summary>
    private bool _shuttingDown;

    /// <summary>The timeline's pop-out window, or null while it is docked.</summary>
    internal Window? TimelineWindow => _timelineWindow;

    /// <summary>Closes any pop-out window, without restoring its content — the
    /// window is closing because the app is.</summary>
    internal void MarkShuttingDownAndClosePopOuts()
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

    internal void ClosePreview3DWindow() => _preview3DWindow?.Close();

    internal void CloseTimelineWindow() => _timelineWindow?.Close();

    /// <summary>
    /// Moves the single <c>Preview3DView</c> instance into its own window.
    /// The move is a detach + attach of <c>Preview3DComposedCanvas</c>: the
    /// detach stops the loop and retires the old window's surface and pump, the
    /// attach builds new ones against the new window's compositor and restarts
    /// the loop (guarded by its attach generation).
    /// </summary>
    internal void PopOutPreview3D(string title)
    {
        if (_preview3DWindow != null)
        {
            _preview3DWindow.Activate();
            return;
        }

        var view = _window.Preview3DPane;
        var width = Math.Max(640, view.Bounds.Width);
        var height = Math.Max(360, view.Bounds.Height);
        _window.Preview3DSlot.Content = null;

        var window = NewPopOutWindow(title, width, height);
        var scaler = FloatingWindowShell.Host(window, view, _window.WindowRootGrid);
        _window.WireGlobalKeyRoutes(window);
        window.Closed += (_, _) =>
        {
            _preview3DWindow = null;
            scaler.Child = null;
            window.Content = null;
            // Shutdown path: never re-attach the native Vulkan child HWND to a
            // main window that is itself tearing down (see _shuttingDown).
            if (_shuttingDown) return;
            _window.Preview3DSlot.Content ??= view;
            UpdatePreview3DPlaceholder();
        };

        _preview3DWindow = window;
        UpdatePreview3DPlaceholder();
        window.Show(_window);
    }

    private void UpdatePreview3DPlaceholder()
    {
        _window.Preview3DPlaceholder.IsVisible = _preview3DWindow != null;
        _window.Preview3DSlot.IsVisible = _preview3DWindow == null;
    }

    /// <summary>
    /// Moves the chart timeline composite (track header, renderer, stacked
    /// tracks) into its own window. The renderer's backend is torn down and
    /// re-created for the new top-level (composed-Vulkan rebuilds surface +
    /// pump on re-attach; the GL fallback handles context loss in its inner
    /// control); all pointer/key handlers are attached to the subtree
    /// itself, so editing keeps working in the float window.
    /// </summary>
    internal void PopOutTimeline(string title)
    {
        if (_timelineWindow != null)
        {
            _timelineWindow.Activate();
            return;
        }

        var timeline = _window.TimelineGrid;
        var width = Math.Max(800, timeline.Bounds.Width);
        var height = Math.Max(320, timeline.Bounds.Height);
        _window.TimelineSlot.Content = null;
        // The inline hand-position and section editors hang over the timeline's rows, so they float with it.
        var overlay = _window.InlineEditOverlay;
        var overlayHome = overlay.Parent as Panel;
        var overlayIndex = overlayHome?.Children.IndexOf(overlay) ?? -1;
        overlayHome?.Children.Remove(overlay);
        var floating = new Panel();
        floating.Children.Add(timeline);
        floating.Children.Add(overlay);

        var window = NewPopOutWindow(title, width, height);
        var scaler = FloatingWindowShell.Host(window, floating, _window.WindowRootGrid);
        _window.WireGlobalKeyRoutes(window);
        window.Closed += (_, _) =>
        {
            _timelineWindow = null;
            floating.Children.Clear();
            scaler.Child = null;
            window.Content = null;
            // Shutdown path: re-attaching rebuilds the composed-Vulkan surface
            // + pump against a dying top-level, and Focus() on a closing window
            // is what leaves the app keyboard-deaf on its way out.
            if (_shuttingDown) return;
            _window.TimelineSlot.Content ??= timeline;
            overlayHome?.Children.Insert(Math.Clamp(overlayIndex, 0, overlayHome.Children.Count), overlay);
            UpdateTimelinePlaceholder();
            timeline.Focus();
        };

        _timelineWindow = window;
        UpdateTimelinePlaceholder();
        window.Show(_window);
        timeline.Focus();
    }

    private void UpdateTimelinePlaceholder()
    {
        _window.TimelinePlaceholder.IsVisible = _timelineWindow != null;
        _window.TimelineSlot.IsVisible = _timelineWindow == null;
    }

    private Window NewPopOutWindow(string title, double width, double height) => new()
    {
        Title = title,
        Width = width,
        Height = height,
        Background = _window.Background,
        Icon = _window.Icon,
        DataContext = _window.DataContext,
        WindowStartupLocation = WindowStartupLocation.CenterOwner
    };
}
