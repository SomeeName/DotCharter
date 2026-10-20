using Avalonia.Controls;
using Avalonia.Input;

namespace DotCharter.UI.Views.Shell;

public partial class ActivityRailView : UserControl
{
    public ActivityRailView() => InitializeComponent();

    /// <summary>The app mark sits in the title bar's row, so it moves the window as the title bar does.</summary>
    private void AppMark_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && TopLevel.GetTopLevel(this) is Window window)
            window.BeginMoveDrag(e);
    }

    private void AppMark_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window window)
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
}
