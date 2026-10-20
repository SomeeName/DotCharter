// WindowChromeView: the window chrome's three rows (title bar, back bar, window
// caption) - their move-drag strips, double-click maximize, caption buttons and
// the maximize glyph that swaps to a restore glyph while maximized.

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace DotCharter.UI.Views.Shell;

public partial class WindowChromeView : UserControl
{
    private const string MaximizeGlyphPath = "M 1,1 H 9 V 9 H 1 Z M 2,2 V 8 H 8 V 2 Z";
    private const string RestoreGlyphPath = "M 3,0 H 10 V 7 H 8 V 2 H 3 Z M 0,3 H 7 V 10 H 0 Z M 1,4 V 9 H 6 V 4 Z";

    public WindowChromeView() => InitializeComponent();

    /// <summary>The three rows' move-drag strips: the title bar's, the back bar's and
    /// the window caption's. With <c>WindowDecorations="BorderOnly"</c> they are the
    /// only way to move the window.</summary>
    internal IReadOnlyList<Border> DragAreas => [TitleBarDragArea, BackBarDragArea, WindowCaptionDragArea];

    /// <summary>The three maximize buttons' glyphs, in the same row order.</summary>
    internal IReadOnlyList<PathIcon> MaximizeGlyphs => [MaximizeGlyph, BackBarMaximizeGlyph, CaptionMaximizeGlyph];

    /// <summary>
    ///     Standard desktop convention: the maximize button shows a restore glyph
    ///     while the window is maximized. <c>MainWindow.OnPropertyChanged</c> calls
    ///     this on every <c>WindowState</c> change.
    /// </summary>
    internal void ShowWindowState(WindowState state)
    {
        foreach (var glyph in MaximizeGlyphs)
            glyph.Data = Geometry.Parse(state == WindowState.Maximized ? RestoreGlyphPath : MaximizeGlyphPath);
    }

    private Window? HostWindow => TopLevel.GetTopLevel(this) as Window;

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (HostWindow is { } window && e.GetCurrentPoint(window).Properties.IsLeftButtonPressed)
            window.BeginMoveDrag(e);
    }

    private void TitleBar_DoubleTapped(object? sender, TappedEventArgs e) => ToggleMaximized();

    private void MinimizeWindow(object? sender, RoutedEventArgs e)
    {
        if (HostWindow is { } window) window.WindowState = WindowState.Minimized;
    }

    private void MaximizeWindow(object? sender, RoutedEventArgs e) => ToggleMaximized();

    private void CloseWindow(object? sender, RoutedEventArgs e) => HostWindow?.Close();

    private void ToggleMaximized()
    {
        if (HostWindow is { } window)
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
}
