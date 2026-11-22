using Avalonia.Interactivity;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.UI.Views.Shell.Menus;

/// <summary>The editor bar's Edit menu (see <see cref="EditorTopMenu" />).</summary>
public partial class EditMenu : EditorTopMenu
{
    public EditMenu() => InitializeComponent();

    /// <summary>The Select group lists the user's saved patterns, which change in the pattern builder.</summary>
    private void OnSelectGroupOpened(object? sender, RoutedEventArgs e)
    {
        if (sender == e.Source && DataContext is MainWindowViewModel vm) vm.SelectionEdits.RefreshSavedPatterns();
    }
}
