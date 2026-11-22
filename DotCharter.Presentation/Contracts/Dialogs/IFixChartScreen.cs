using System.Windows.Input;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing.Maintenance;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     Opens <see cref="IFixChartScreen" /> with <paramref name="Preselect" /> ticked: the palette's
///     "Polish chart" opens it with Polish, "Fix chart errors" with Errors, Tools ▸ Fix chart… with both.
/// </summary>
public sealed record FixChartRequest(FixChartGroups Preselect = FixChartGroups.Both)
    : IDialogRequest<FixChartRequest, IFixChartScreen, bool>;

/// <summary>One line of what a group will change: "Duplicate notes and chords removed: 12".</summary>
public interface IFixChartChange
{
    string Text { get; }
}

/// <summary>
///     Tools ▸ Fix chart…: LISTS what Fix chart errors ("Errors") and Polish chart ("Polish") would change,
///     counted per kind from a run on a copy, before anything changes; Apply runs the ticked groups as ONE undo
///     step. <see cref="IDialogScreen{TResult}.Result" /> is true when Apply ran.
/// </summary>
public interface IFixChartScreen : IDialogScreen<bool>
{
    /// <summary>Tick: run the Errors group (what Fix chart errors does).</summary>
    bool FixErrors { get; set; }

    /// <summary>Tick: run the Polish group (what Polish chart does).</summary>
    bool FixPolish { get; set; }

    /// <summary>"12 changes", or that there is nothing to change.</summary>
    string ErrorsSummary { get; }

    IReadOnlyList<IFixChartChange> ErrorsChanges { get; }

    bool HasErrorsChanges { get; }

    /// <summary>"4 changes", or that there is nothing to change (counted after the Errors group when that is ticked, as Apply runs them).</summary>
    string PolishSummary { get; }

    IReadOnlyList<IFixChartChange> PolishChanges { get; }

    bool HasPolishChanges { get; }

    /// <summary>The check on the copy is running; the lists are not final.</summary>
    bool IsChecking { get; }

    /// <summary>"Apply makes 16 changes as one undo step", or why it is off.</summary>
    string TotalSummary { get; }

    /// <summary>Something ticked would change something, and the check has finished.</summary>
    bool CanApply { get; }

    ICommand ApplyCommand { get; }

    ICommand CancelCommand { get; }
}
