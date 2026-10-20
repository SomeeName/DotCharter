using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     Tools ▸ Chart diff: the arrangement being edited against the same
///     arrangement in another project file — "what did I change since that
///     version". Read-only: neither side is ever written. The FILE is the old
///     side, so "added" means in this chart and not in that file.
///     <see cref="IDialogScreen{TResult}.Result" /> is true when closed with Close.
/// </summary>
public interface IChartDiffScreen : IDialogScreen<bool>
{
    /// <summary>The compared file's name; empty while none is picked.</summary>
    string ComparedFileName { get; }

    /// <summary>Picks a project file and compares against it; ignored while one loads.</summary>
    ICommand BrowseFileCommand { get; }

    bool HasComparableArrangements { get; }

    /// <summary>"Lead (Lead)" for each arrangement of the compared file.</summary>
    IReadOnlyList<string> ComparableArrangements { get; }

    /// <summary>Index into <see cref="ComparableArrangements" />; changing it compares again. −1 for none.</summary>
    int SelectedArrangementIndex { get; set; }

    bool HasResults { get; }

    IReadOnlyList<IChartDiffRow> Rows { get; }

    /// <summary>"3 added · 1 removed · 2 changed", or that the two are identical.</summary>
    string Summary { get; }

    /// <summary>What to do next, or why the file could not be compared.</summary>
    string Status { get; }

    /// <summary>Closes and lets go of the compared project; refused while a file loads.</summary>
    ICommand CloseCommand { get; }
}

public interface IChartDiffRow
{
    /// <summary>Added, removed or changed, in the user's language.</summary>
    string Change { get; }

    /// <summary>Difficulty and time, or "—" for what has no place on the timeline.</summary>
    string Position { get; }

    /// <summary>The note, chord or structure element.</summary>
    string Sound { get; }

    /// <summary>The changed fields, or what identifies an added or removed element.</summary>
    string Detail { get; }

    bool IsAdded { get; }

    bool IsRemoved { get; }

    bool IsChanged { get; }
}
