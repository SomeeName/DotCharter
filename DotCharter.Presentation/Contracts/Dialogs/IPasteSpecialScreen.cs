using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     Edit ▸ Paste special: pastes the clipboard at the playhead (or the mouse)
///     in one of three overwrite modes. The three mode flags are one radio group —
///     setting one true clears the other two. The mode and the mouse flag carry
///     over to the next open. <see cref="IDialogScreen{TResult}.Result" /> is true
///     once pasted.
/// </summary>
public interface IPasteSpecialScreen : IDialogScreen<bool>
{
    /// <summary>Paste over nothing: only into gaps.</summary>
    bool IsPreserve { get; set; }

    /// <summary>Replace existing items of the same kind in the pasted span.</summary>
    bool IsOverwriteSameType { get; set; }

    /// <summary>Replace everything in the pasted span.</summary>
    bool IsOverwriteAll { get; set; }

    /// <summary>Paste at the last pointer position instead of the playhead.</summary>
    bool UseMousePosition { get; set; }

    ICommand ApplyCommand { get; }

    ICommand CancelCommand { get; }
}
