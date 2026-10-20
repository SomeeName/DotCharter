namespace DotCharter.Services.Diagnostics;

public enum MessageDialogKind
{
    Info,
    Warning,
    Error,
    Question
}

/// <summary>
///     The app-wide message box, as the Services-side coordinators see it: show a
///     message or a yes/no question and await the answer. Implemented by the UI's
///     <c>MessageDialogViewModel</c>. Safe to call from any thread; completes with
///     true for OK/Yes and false for Cancel/dismiss (always true for a plain,
///     non-confirmation message once dismissed).
/// </summary>
public interface IMessagePrompt
{
    Task<bool> ShowAsync(
        string title,
        string message,
        MessageDialogKind kind = MessageDialogKind.Info,
        string? details = null,
        bool confirmation = false);
}
