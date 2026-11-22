using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DotCharter.UI.Views.Shell.Popovers;

/// <summary>
///     The caret half of the tone-preview split button: two target rows, plain
///     command buttons; a row also closes the flyout it sits in once the target is picked.
/// </summary>
public partial class TonePreviewTargetPopoverView : UserControl
{
    public TonePreviewTargetPopoverView() => InitializeComponent();

    private void OnRowClick(object? sender, RoutedEventArgs e) => PopoverRowClick.RunThenClose(this, sender, e);
}
