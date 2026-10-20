namespace DotCharter.Presentation.Dialogs;

/// <summary>Opens modal dialogs over the shell; one scope per open, closed top first.</summary>
public interface IDialogs
{
    bool HasOpenDialog { get; }

    /// <summary>Raised on the UI thread as a dialog opens or closes; <c>Id</c> is its usage-statistics name.</summary>
    event EventHandler<DialogChange>? Changed;

    /// <summary>Completes with the dialog's result, or with the default when it is closed from outside.</summary>
    Task<TResult?> ShowAsync<TContract, TResult>(Action<TContract>? init = null)
        where TContract : class, IDialogScreen<TResult>;

    Task<bool> MessageAsync(string title, string message, bool isConfirmation = false);

    bool CloseTop();
}

public interface IDialogScreen<TResult> : System.ComponentModel.INotifyPropertyChanged
{
    Task<TResult> Result { get; }
}

public sealed record DialogChange(Type Contract, string Id, bool IsOpen);
