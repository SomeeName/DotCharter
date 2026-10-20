using Avalonia.Controls;
using Avalonia.Interactivity;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.UI.Views.Shell.Popovers;

/// <summary>
///     The toolbar's overflow panel (P2-16). The four group rows open the
///     rail's flyouts through <see cref="MainWindowViewModel.RequestPopover" />
///     — the same routed door the ALT+M/T/A keybinds use — so the open logic
///     stays in ToolbarView, which owns the buttons the flyouts hang off (and
///     closes this overflow first).
/// </summary>
public partial class OverflowPopoverView : UserControl
{
    public OverflowPopoverView() => InitializeComponent();

    private void OpenMix(object? sender, RoutedEventArgs e) => Request(TransportPopover.Mix);
    private void OpenTempo(object? sender, RoutedEventArgs e) => Request(TransportPopover.Tempo);
    private void OpenTools(object? sender, RoutedEventArgs e) => Request(TransportPopover.Tools);
    private void OpenTracks(object? sender, RoutedEventArgs e) => Request(TransportPopover.Tracks);

    private void Request(TransportPopover which)
    {
        if (DataContext is MainWindowViewModel shell) shell.RequestPopover(which);
    }
}
