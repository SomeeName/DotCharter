// MainWindow: what the window state drives in the chrome (the maximize glyph,
// the full-bleed caption), borderless fullscreen and the global UI scale
// transform. The chrome's rows, drag strips and caption buttons are
// Views/Shell/WindowChromeView.

using Avalonia.Controls;

namespace DotCharter.UI.Views;

public partial class MainWindow
{
    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WindowStateProperty) return;
        // Fullscreen drops the full-bleed caption (ShellPresentation.ShowsWindowCaption).
        if (DataContext is ViewModels.Shell.MainWindowViewModel vm)
            vm.IsWindowFullScreen = WindowState == WindowState.FullScreen;
        // Null only before InitializeComponent has built the chrome.
        WindowChrome?.ShowWindowState(WindowState);
    }

    // Global UI scale: a LayoutTransformControl around the shell reflows the
    // whole window (chrome + docking + editor) at the chosen factor. Identity
    // (1.0) leaves the transform null so nothing is composed differently.
    // Custom-drawn controls and the docking layout reflow through it fine; the
    // native GL/Vulkan panels mirror the scale via their layout slot.
    private void ApplyUiScale(double scale)
    {
        if (this.FindControl<LayoutTransformControl>("RootScaler") is not { } scaler) return;
        scaler.LayoutTransform = scale is > 0.999 and < 1.001
            ? null
            : new Avalonia.Media.ScaleTransform(scale, scale);
    }

    /// <summary>
    ///     The AUTO arm's one input. <c>UiScaleController.ApplyAutoForHeight</c>
    ///     no-ops unless the mode is on, and <c>AutoUiScale.Next</c> quantises
    ///     and applies hysteresis, so this is safe to call from a size delivery:
    ///     a drag-resize across a 54-DIP step costs ONE re-layout, and a wobble
    ///     inside the band costs none.
    ///     <para>
    ///         <b>It cannot feed back.</b> The derived factor changes the
    ///         shell's layout transform, i.e. the size of the content INSIDE
    ///         the window; the window's own client size is what we read, and it
    ///         is untouched.
    ///     </para>
    /// </summary>
    internal void ReapplyAutoUiScale() => Services.UiScaleController.ApplyAutoForHeight(ClientSize.Height);

    /// <summary>Turning Auto ON cannot derive a factor by itself — the client
    /// height lives here. Turning it OFF leaves the current factor in place;
    /// whoever switched the mode is about to push their own number.</summary>
    private void OnAutoUiScaleModeChanged(bool auto)
    {
        if (auto) ReapplyAutoUiScale();
    }

    /// <summary>Borderless fullscreen; F11/Alt+Enter to come back.
    /// <para>The title bar STAYS where it is shown: that row IS the menu bar, and
    /// fullscreen would leave the chart editor with no File menu. Only the full-bleed
    /// window caption goes, through the view
    /// model (<see cref="OnPropertyChanged" />). The row's visibility and height
    /// are BOUND, so this must not write them: a local value replaces the
    /// binding, and one F11 then pinned the title bar over Home and Play Mode
    /// for the rest of the session.</para></summary>
    private void ToggleFullScreen() => WindowState = WindowState != WindowState.FullScreen ? WindowState.FullScreen : WindowState.Normal;
}
