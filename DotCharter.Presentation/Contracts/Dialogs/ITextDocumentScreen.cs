using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     Opens <see cref="ITextDocumentScreen" /> on a document the caller has already
///     read or built. <paramref name="FilePath" /> is its copy on disk (empty when there
///     is none); <paramref name="EmptyText" /> is what to say when the text is empty.
/// </summary>
public sealed record TextDocumentRequest(string Title, string Subtitle, string Text, string FilePath,
    string EmptyText = "") : IDialogRequest<TextDocumentRequest, ITextDocumentScreen, bool>;

/// <summary>
///     A plain-text document read inside the app: Help ▸ Usage statistics and the
///     licence. Handing a .txt or .md to the system raised Windows' "choose an app" box
///     on a machine with no viewer for it, and showed nothing.
///     <see cref="IDialogScreen{TResult}.Result" /> is true when closed with Close.
/// </summary>
public interface ITextDocumentScreen : IDialogScreen<bool>
{
    string Title { get; }

    string Subtitle { get; }

    /// <summary>The document as plain text.</summary>
    string Text { get; }

    bool HasText { get; }

    /// <summary>Shown instead of the text when there is none.</summary>
    string EmptyText { get; }

    /// <summary>Puts <see cref="Text" /> on the clipboard.</summary>
    ICommand CopyCommand { get; }

    /// <summary>True when the document has a copy on disk to show.</summary>
    bool CanShowFile { get; }

    /// <summary>Opens the file manager with the document's file selected — never the file itself.</summary>
    ICommand ShowFileCommand { get; }

    ICommand CloseCommand { get; }
}
