using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.UI.Views.Dialogs;

public partial class PackageSongPickerDialogView : UserControl
{
    public PackageSongPickerDialogView() => InitializeComponent();

    /// <summary>Opens the row that was double-clicked, not whichever was selected before.</summary>
    private void OnRowsDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Control source || DataContext is not IPackageSongPickerScreen screen) return;
        var container = source as ListBoxItem ?? source.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (container?.DataContext is not IPackageSongRow row) return;
        screen.Selected = row;
        if (screen.OpenCommand.CanExecute(null)) screen.OpenCommand.Execute(null);
        e.Handled = true;
    }
}
