using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     Tools ▸ Batch lint: validates every project under a folder and, when
///     asked, runs the auto-fixes and writes the fixed projects back. Writing
///     is opt-in, needs the auto-fixes, and is confirmed with the file count
///     first — it rewrites files nobody opened, with no undo. The run is off
///     the UI thread and cancellable: the file in flight finishes, then it stops.
///     <see cref="IDialogScreen{TResult}.Result" /> is true when closed after a run.
/// </summary>
public interface IBatchLintScreen : IDialogScreen<bool>
{
    /// <summary>The folder to lint, as picked.</summary>
    string FolderPath { get; }

    ICommand BrowseFolderCommand { get; }

    bool ApplyAutoFixes { get; set; }

    /// <summary>Write the fixed projects back; cleared whenever <see cref="ApplyAutoFixes" /> is.</summary>
    bool SaveFixedProjects { get; set; }

    /// <summary>Saving only makes sense when fixes run.</summary>
    bool CanSaveFixedProjects { get; }

    bool HasResults { get; }

    IReadOnlyList<ILintResultRow> Results { get; }

    bool IsBusy { get; }

    /// <summary>0..1 across the batch.</summary>
    double Progress { get; }

    /// <summary>The file the batch is on; empty when idle.</summary>
    string CurrentFile { get; }

    /// <summary>The run's summary, what stopped it, or what is missing.</summary>
    string Status { get; }

    ICommand RunCommand { get; }

    /// <summary>Cancels a run in flight; otherwise closes.</summary>
    ICommand CancelCommand { get; }
}

public interface ILintResultRow
{
    string FileName { get; }

    string Folder { get; }

    /// <summary>The issue/fix tally, or why the file could not be read or written.</summary>
    string Detail { get; }

    bool Failed { get; }

    bool Saved { get; }
}
