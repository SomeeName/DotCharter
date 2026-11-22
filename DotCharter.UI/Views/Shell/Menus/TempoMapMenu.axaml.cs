using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.UI.Views.Shell.Menus;

/// <summary>The editor bar's Tempo map menu (see <see cref="EditorTopMenu" />). One row is disabled by the
///     CHART (its notes), which raises no event the availability listens to, so the menu asks as it opens.</summary>
public partial class TempoMapMenu : EditorTopMenu
{
    public TempoMapMenu()
    {
        InitializeComponent();
        SubmenuOpened += (_, _) => (DataContext as MainWindowViewModel)?.EditingAvailability.Refresh();
    }
}
