using System.ComponentModel;
using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     Edit ▸ Checkpoints: named snapshots of the whole project that survive a
///     restart. Create captures what a save would write; Restore replaces the
///     open project (asking first when that would lose work or undo history);
///     Delete takes two clicks, because a checkpoint is the only copy of its
///     snapshot. <see cref="IDialogScreen{TResult}.Result" /> is true after a
///     restore, and when closed with Close.
/// </summary>
public interface ICheckpointsScreen : IDialogScreen<bool>
{
    /// <summary>The name for the next checkpoint; empty lets the store name it.</summary>
    string NewCheckpointName { get; set; }

    /// <summary>True while a checkpoint is written or read: every verb is off.</summary>
    bool IsBusy { get; }

    ICommand CreateCheckpointCommand { get; }

    bool HasCheckpoints { get; }

    /// <summary>The open project's checkpoints, as the store lists them.</summary>
    IReadOnlyList<ICheckpointRow> Checkpoints { get; }

    /// <summary>Restores the <see cref="ICheckpointRow" /> passed as the parameter.</summary>
    ICommand RestoreCommand { get; }

    /// <summary>Arms the row passed as the parameter; the same row a second time deletes it.</summary>
    ICommand DeleteCommand { get; }

    ICommand CloseCommand { get; }
}

public interface ICheckpointRow : INotifyPropertyChanged
{
    string Name { get; }

    /// <summary>The song and when it was taken, or whichever of the two is known.</summary>
    string Detail { get; }

    /// <summary>"Delete", or the confirm text once the row is armed.</summary>
    string DeleteCaption { get; }
}
