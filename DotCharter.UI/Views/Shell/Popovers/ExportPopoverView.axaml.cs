using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DotCharter.UI.Views.Shell.Popovers;

/// <summary>
///     The toolbar's Export popover: rows are plain command buttons; a row also
///     closes the flyout it sits in, so a dialog or file picker a command opens is never
///     stacked over it.
/// </summary>
public partial class ExportPopoverView : UserControl
{
    public ExportPopoverView() => InitializeComponent();

    private void OnRowClick(object? sender, RoutedEventArgs e) => PopoverRowClick.RunThenClose(this, sender, e);
}
