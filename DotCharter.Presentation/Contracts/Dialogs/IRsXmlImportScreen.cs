using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>Merges the Rocksmith XML document at <paramref name="FilePath" /> into the open project.</summary>
public sealed record RsXmlImportRequest(string FilePath)
    : IDialogRequest<RsXmlImportRequest, IRsXmlImportScreen, bool>;

/// <summary>
///     File ▸ Import ▸ Rocksmith XML: a progress card for merging a song, vocal
///     or showlight document into the open project. The file is chosen before it
///     appears, so the card only reports progress and, on failure, why — it
///     stays up with the reason, and closes by itself on success.
///     <see cref="IDialogScreen{TResult}.Result" /> is true once merged.
/// </summary>
public interface IRsXmlImportScreen : IDialogScreen<bool>
{
    /// <summary>The file this card is about.</summary>
    string FileName { get; }

    /// <summary>True while the document is read. Close then only asks it to stop; nothing is merged after.</summary>
    bool IsBusy { get; }

    /// <summary>Why the merge failed; empty otherwise.</summary>
    string ErrorMessage { get; }

    ICommand CloseCommand { get; }
}
