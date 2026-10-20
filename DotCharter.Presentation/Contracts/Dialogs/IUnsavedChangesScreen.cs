using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     What the user wants done with unsaved work. <see cref="Cancel" /> is the
///     zero value on purpose: a dialog closed from outside completes with the
///     default, and that must never read as Save or Discard.
/// </summary>
public enum UnsavedChangesChoice
{
    Cancel,
    Save,
    Discard
}

/// <summary>
///     Save / Don't save / Cancel, asked before the window closes or the open
///     project is replaced. The close never proceeds without an explicit choice.
/// </summary>
public interface IUnsavedChangesScreen : IDialogScreen<UnsavedChangesChoice>
{
    ICommand SaveCommand { get; }

    /// <summary>The one action here that loses work.</summary>
    ICommand DiscardCommand { get; }

    ICommand CancelCommand { get; }
}
