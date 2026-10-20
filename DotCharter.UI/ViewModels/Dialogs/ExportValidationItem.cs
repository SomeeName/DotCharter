using DotCharter.Services.Export.Validation;

namespace DotCharter.UI.ViewModels.Dialogs;

/// <summary>A pre-export validation finding prepared for the Inspector's issues tab.</summary>
public sealed class ExportValidationItem
{
    public required ExportFindingSeverity Severity { get; init; }
    public required string Location { get; init; }
    public required string Message { get; init; }

    /// <summary>Chart position of the finding, when it has one — enables click-to-jump.</summary>
    public double? TimeMs { get; init; }

    /// <summary>
    ///     The arrangement the finding belongs to, verbatim from
    ///     <see cref="ExportValidationFinding.ArrangementName" />: the validator runs over the whole
    ///     project, so a jump switches arrangement before it seeks. <see cref="Location" /> is display
    ///     text and is never parsed back for it.
    /// </summary>
    public string ArrangementName { get; init; } = "";

    /// <summary>The validator's finding, which the Fix runs on.</summary>
    public ExportValidationFinding? Finding { get; init; }

    /// <summary>An existing verb repairs the finding and its arrangement is unambiguous.</summary>
    public bool HasFix { get; init; }

    /// <summary>What Fix will do, for its tooltip.</summary>
    public string FixDescription { get; init; } = "";

    public bool IsError => Severity == ExportFindingSeverity.Error;
    public bool HasLocation => Location.Length > 0;
    public bool HasTime => TimeMs.HasValue;
}
