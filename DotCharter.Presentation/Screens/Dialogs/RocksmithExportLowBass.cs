using DotCharter.Core.Domain.Project;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     What the export dialog's "Fix low bass tuning" box says for the ticked rows: which stock basses
///     reach below C1 (<see cref="LowBassTuningFix" />), the reason line naming them, and the warning
///     while the box is cleared.
/// </summary>
internal sealed class RocksmithExportLowBass(ChartProject? project, IEnumerable<ExportArrangementRow> rows, bool fixOn,
    ILocalizationService localization)
{
    public static readonly string[] PropertyNames =
    [
        nameof(IRocksmithExportScreen.OffersLowBassTuningFix), nameof(IRocksmithExportScreen.LowBassTuningReason),
        nameof(IRocksmithExportScreen.LowBassTuningWarning), nameof(IRocksmithExportScreen.HasLowBassTuningWarning)
    ];

    public IReadOnlyList<string> Arrangements { get; } = project is null ? []
        : LowBassTuningFix.NeedingFix(project, a => rows.Any(r => r.IsSelected && r.Name == a.Name));

    public bool Offers => Arrangements.Count > 0;

    public string Reason => Offers ? localization.Format("Dialog_Export_LowBassTuningReason", string.Join(", ", Arrangements)) : "";

    public string Warning => Offers && !fixOn
        ? localization.Format("Dialog_Export_LowBassTuningWarning", string.Join(", ", Arrangements)) : "";
}
