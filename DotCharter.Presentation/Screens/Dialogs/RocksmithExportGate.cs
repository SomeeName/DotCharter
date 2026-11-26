using DotCharter.Localization;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Validation;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     The export dialog's gate: which clauses are false right now, whether Export and Test export are live,
///     and the sentence the dialog prints for each failing clause, in gate order.
/// </summary>
internal readonly record struct RocksmithExportGate(bool Building, bool MacOnlyBlocked, bool BlockingErrors, bool InvalidDlcKey,
    bool InvalidPreviewStart, bool MissingPaths, bool NothingSelected)
{
    /// <summary>The dialog properties a tick, a path, the key or the platform can change, raised together.</summary>
    public static readonly string[] Properties =
    [
        nameof(RocksmithExportViewModel.IsCheckingFindings), nameof(RocksmithExportViewModel.HasBlockingErrors), nameof(RocksmithExportViewModel.NothingSelectedToExport),
        nameof(RocksmithExportViewModel.MissingPaths), nameof(RocksmithExportViewModel.HasInvalidDlcKey),
        nameof(RocksmithExportViewModel.MacBlock), nameof(RocksmithExportViewModel.PlatformNote),
        nameof(RocksmithExportViewModel.HasPlatformNote), nameof(RocksmithExportViewModel.OffersStandardPackage),
        .. RocksmithExportLowBass.PropertyNames
    ];

    private bool Common => !Building && !MacOnlyBlocked && !BlockingErrors && !InvalidDlcKey && !InvalidPreviewStart && !NothingSelected;

    public bool CanExport => Common && !MissingPaths;

    /// <summary>A test export needs everything Export does except the output file: it writes to the test folder.</summary>
    public bool CanTestExport(string audioPath) => Common && !string.IsNullOrWhiteSpace(audioPath);

    public IReadOnlyList<string> Reasons(ILocalizationService loc, RocksmithMacBlock? macBlock)
    {
        var reasons = new List<string>(5);
        if (MacOnlyBlocked) reasons.Add(loc.Format("Dialog_Export_MacUnavailable", RocksmithExportSupport.MacBlockText(loc, macBlock!.Value)));
        if (BlockingErrors) reasons.Add(loc["Dialog_Export_ErrorsBlockExport"]);
        if (InvalidDlcKey) reasons.Add(loc["Dialog_Export_DlcKeyInvalid"]);
        if (InvalidPreviewStart) reasons.Add(loc["Dialog_Export_PreviewStartInvalid"]);
        if (MissingPaths) reasons.Add(loc["Dialog_Export_MissingPathsHint"]);
        if (NothingSelected) reasons.Add(loc["Dialog_Export_NothingSelectedHint"]);
        return reasons;
    }

    /// <summary>An error blocks the export unless it is about an arrangement (or the vocals) the dialog leaves out.</summary>
    public static bool Blocks(ExportValidationFinding finding, IEnumerable<ExportArrangementRow> rows, bool includeVocals)
    {
        if (finding.Severity != ExportFindingSeverity.Error) return false;
        if (finding.ArrangementName.Length == 0) return true;
        return rows.FirstOrDefault(a => a.Name == finding.ArrangementName) is { } row ? row.IsSelected : includeVocals;
    }
}
