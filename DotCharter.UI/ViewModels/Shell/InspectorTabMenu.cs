using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.UI.ViewModels.Shell;

/// <summary>
///     What View ▸ Inspector tab binds: the tab it ticks (the open Inspector's selected tab, -1 while the
///     Inspector is closed) and the row command that opens the Inspector on a tab.
/// </summary>
public sealed partial class InspectorTabMenu : ObservableObject
{
    private readonly MainWindowViewModel _shell;

    public InspectorTabMenu(MainWindowViewModel shell)
    {
        _shell = shell;
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.IsInspectorVisible)) Refresh();
        };
        shell.Inspector.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InspectorViewModel.SelectedTabIndex)) Refresh();
        };
    }

    public int ActiveIndex => _shell.IsInspectorVisible ? _shell.Inspector.SelectedTabIndex : -1;

    private void Refresh() => OnPropertyChanged(nameof(ActiveIndex));

    /// <summary>A row's command; the parameter is the tab's index.</summary>
    [RelayCommand]
    private void ShowRow(string? tabIndex)
    {
        if (int.TryParse(tabIndex, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)) _shell.ShowInspectorTab(index);
    }
}
