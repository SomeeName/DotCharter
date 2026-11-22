using Avalonia.Interactivity;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.UI.Views.Shell.Menus;

/// <summary>The editor bar's Tools menu (see <see cref="EditorTopMenu" />).</summary>
public partial class ToolsMenu : EditorTopMenu
{
    public ToolsMenu() => InitializeComponent();

    /// <summary>The Generate group's first row names its scope in the current language, so it is read as the group opens.</summary>
    private void OnGenerateGroupOpened(object? sender, RoutedEventArgs e)
    {
        if (sender == e.Source && DataContext is MainWindowViewModel vm) vm.EditingAvailability.RefreshGenerateFhpLabel();
    }
}
