using DotCharter.Localization;

namespace DotCharter.Services.Export.Validation;

/// <summary>
///     Turns an <see cref="ExportValidationFinding" /> (a localization key and a raw detail
///     payload) into display strings, for the export dialog and the Inspector's issues tab alike.
/// </summary>
public static class ExportFindingFormatter
{
    /// <summary>Every Export.Validation.* resource has one {0} fed by the detail, or no placeholder at all;
    /// a detail written "a|b|c" fills {0}, {1}, {2} (the six-string drop's counts).</summary>
    public static string FormatMessage(ExportValidationFinding finding, ILocalizationService localization) =>
        finding.MessageKey == ChartExportValidator.KeySixStringDrop
            ? localization.Format(finding.MessageKey, [.. finding.Detail.Split('|')])
            : localization.Format(finding.MessageKey, finding.Detail);

    /// <summary>"Rhythm · 1:23.456", the arrangement or the time alone, or empty.</summary>
    public static string FormatLocation(ExportValidationFinding finding)
    {
        var time = finding.TimeMs.HasValue
            ? ElapsedTimeLabel.Format(finding.TimeMs.Value, 3)
            : null;

        if (finding.ArrangementName.Length > 0 && time != null) return $"{finding.ArrangementName} · {time}";
        if (finding.ArrangementName.Length > 0) return finding.ArrangementName;
        return time ?? "";
    }
}
