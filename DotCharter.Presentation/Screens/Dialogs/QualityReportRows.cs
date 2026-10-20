using System.Windows.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>The rows <see cref="ChartQualityReportViewModel" /> projects a report onto.</summary>
internal sealed record QualityArrangementRow(string Name, string Difficulty, string Counts, bool IsClean, bool HasErrors)
    : IQualityArrangementSummary;

internal sealed record QualityFindingGroupRow(string Title, bool IsBlocking, bool IsImportant, IReadOnlyList<IQualityFinding> Items)
    : IQualityFindingGroup;

internal sealed record QualityFixPart(bool HasFix, string Description, ICommand Command);

internal sealed record QualityFindingRow(string Location, string Message, string Source, ICommand JumpCommand, bool HasTime,
    QualityFixPart Fix) : IQualityFinding
{
    public bool HasFix => Fix.HasFix;
    public string FixDescription => Fix.Description;
    public ICommand FixCommand => Fix.Command;
}
