using System.ComponentModel;
using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     Edit ▸ Bookmarks…: the ten bookmark slots with the time each holds. Go to moves the
///     playhead, Set puts the slot at the playhead, Clear empties it. The 0-9 keybinds still
///     set, jump to and clear a slot in one press; this is the list that shows what they hold.
///     <see cref="IDialogScreen{TResult}.Result" /> is true once the dialog is closed.
/// </summary>
public interface IBookmarksScreen : IDialogScreen<bool>
{
    /// <summary>Slots 0 to 9, in order, set or not.</summary>
    IReadOnlyList<IBookmarkRow> Slots { get; }

    /// <summary>What the digit keys do,.</summary>
    string ShortcutHint { get; }

    ICommand CloseCommand { get; }
}

public interface IBookmarkRow : INotifyPropertyChanged
{
    /// <summary>The slot's digit.</summary>
    string Slot { get; }

    /// <summary>The key chord that sets, jumps to or clears the slot, read from the live keybinds when the dialog opens.</summary>
    string Chord { get; }

    /// <summary>The slot's time as m:ss.fff, or the words for an empty slot.</summary>
    string TimeText { get; }

    /// <summary>Moves the playhead to the slot; off while the slot is empty.</summary>
    ICommand GoToCommand { get; }

    /// <summary>Puts the slot at the playhead, replacing its time.</summary>
    ICommand SetCommand { get; }

    /// <summary>Empties the slot; off while it is empty.</summary>
    ICommand ClearCommand { get; }
}
