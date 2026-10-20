using DotCharter.Localization;
using DotCharter.Services.Export.Validation;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

/// <summary>
/// Display mapping of <see cref="ExportValidationFinding"/> records: the
/// localized message must absorb the Detail payload and the location column
/// must combine arrangement name and timestamp consistently.
/// </summary>
public sealed class ExportFindingFormatterTests
{
    private static readonly ILocalizationService Loc = FakeLocalization.English();

    [Fact]
    public void Message_interpolates_detail_payload()
    {
        var finding = new ExportValidationFinding(
            "Lead", ExportFindingSeverity.Error, ChartExportValidator.KeyFretOutOfRange, 1000, "27");

        var message = ExportFindingFormatter.FormatMessage(finding, Loc);

        Assert.Contains("27", message);
        Assert.DoesNotContain("{0}", message);
    }

    [Fact]
    public void Message_without_placeholder_ignores_detail()
    {
        var finding = new ExportValidationFinding(
            "", ExportFindingSeverity.Error, ChartExportValidator.KeyNoBeatMap);

        var message = ExportFindingFormatter.FormatMessage(finding, Loc);

        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.DoesNotContain("{0}", message);
    }

    [Fact]
    public void Every_validator_key_resolves_to_a_resource()
    {
        string[] keys =
        [
            ChartExportValidator.KeyNoBeatMap,
            ChartExportValidator.KeyNoArrangements,
            ChartExportValidator.KeyNoNotes,
            ChartExportValidator.KeyMissingFhpCoverage,
            ChartExportValidator.KeyOverlappingNotes,
            ChartExportValidator.KeyEmptyPhrase,
            ChartExportValidator.KeyNoPhrases,
            ChartExportValidator.KeyTooManyPhrases,
            ChartExportValidator.KeyToneWithoutSlot,
            ChartExportValidator.KeyMissingBaseTone,
            ChartExportValidator.KeyTooManyTones,
            ChartExportValidator.KeyFretOutOfRange,
            ChartExportValidator.KeyStringOutOfRange,
            ChartExportValidator.KeyExtendedRangeGuitar,
            ChartExportValidator.KeyEightStringMod,
            ChartExportValidator.KeyNoteBelowCapo,
            ChartExportValidator.KeyFirstNoteTooEarly,
            ChartExportValidator.KeyFirstPhraseNotAtStart,
            ChartExportValidator.KeyLyricTooLong
        ];

        foreach (var key in keys)
        {
            var finding = new ExportValidationFinding("A", ExportFindingSeverity.Warning, key, null, "detail");
            var message = ExportFindingFormatter.FormatMessage(finding, Loc);
            // A missing resource falls back to the raw key — that would leak
            // "Export.Validation.X" into the dialog.
            Assert.NotEqual(key, message);
        }
    }

    [Fact]
    public void Location_combines_arrangement_and_time()
    {
        var finding = new ExportValidationFinding(
            "Lead", ExportFindingSeverity.Error, ChartExportValidator.KeyOverlappingNotes, 83_456, "string 2");

        Assert.Equal("Lead · 1:23.456", ExportFindingFormatter.FormatLocation(finding));
    }

    [Fact]
    public void Location_with_arrangement_only_omits_separator()
    {
        var finding = new ExportValidationFinding(
            "Bass", ExportFindingSeverity.Warning, ChartExportValidator.KeyNoPhrases);

        Assert.Equal("Bass", ExportFindingFormatter.FormatLocation(finding));
    }

    [Fact]
    public void Location_with_time_only_shows_timestamp()
    {
        var finding = new ExportValidationFinding(
            "", ExportFindingSeverity.Error, ChartExportValidator.KeyFirstNoteTooEarly, 500);

        Assert.Equal("0:00.500", ExportFindingFormatter.FormatLocation(finding));
    }

    [Fact]
    public void Location_empty_when_no_context()
    {
        var finding = new ExportValidationFinding(
            "", ExportFindingSeverity.Error, ChartExportValidator.KeyNoBeatMap);

        Assert.Equal("", ExportFindingFormatter.FormatLocation(finding));
    }
}
