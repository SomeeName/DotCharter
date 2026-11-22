using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Editing.Maintenance;

namespace DotCharter.UI.ViewModels.Shell;

/// <summary>
///     Generating fret hand positions: the whole arrangement, the selected notes and chords, and
///     the one Tools ▸ Generate row that picks between them. The command palette keeps a row per
///     scope; the menu row runs whichever its text names (<see cref="EditingAvailability.GenerateFhpLabel" />).
/// </summary>
public sealed partial class FhpGenerationViewModel(IChartMaintenanceService maintenance, EditingAvailability availability)
{
    [RelayCommand]
    private void GenerateFhp() => maintenance.GenerateFhp();

    [RelayCommand]
    private void GenerateFhpForSelection() => maintenance.GenerateFhpForSelection();

    /// <summary>The selection when it holds a note or chord, else the arrangement.</summary>
    [RelayCommand]
    private void GenerateFhpForScope() => availability.GenerateFhp(GenerateFhpForSelection, GenerateFhp);
}
