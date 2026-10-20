using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     The one message dialog behind <see cref="IDialogs.MessageAsync" />: a
///     title, an optional body and OK — or OK and Cancel when it asks a question.
///     <see cref="IDialogScreen{TResult}.Result" /> is true for OK, false for
///     Cancel and for a dialog closed from outside.
/// </summary>
public interface IMessagePromptScreen : IDialogScreen<bool>
{
    string Title { get; }

    /// <summary>The body, already formatted. Empty when the title says it all.</summary>
    string Message { get; }

    bool HasMessage { get; }

    /// <summary>True when the prompt asks a question and offers Cancel.</summary>
    bool IsConfirmation { get; }

    ICommand OkCommand { get; }

    ICommand CancelCommand { get; }
}

/// <summary>How <see cref="IDialogs.MessageAsync" /> fills a message prompt before it opens.</summary>
public interface IMessagePromptSetup
{
    void Configure(string title, string message, bool isConfirmation);
}
