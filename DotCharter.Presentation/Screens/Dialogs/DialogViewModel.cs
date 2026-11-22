using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>The result a dialog owes its host, completed once by the dialog's own verbs.</summary>
public abstract class DialogViewModel<TResult> : ObservableObject, IDialogScreen<TResult>
{
    private readonly TaskCompletionSource<TResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _open = new();

    public Task<TResult> Result => _result.Task;

    protected bool IsClosed => _result.Task.IsCompleted;

    /// <summary>Cancelled synchronously on close, so open-time work stops holding what it captured.</summary>
    protected CancellationToken OpenToken => _open.Token;

    protected void Close(TResult result)
    {
        EndOpenWork();
        _result.TrySetResult(result);
    }

    protected void EndOpenWork() => _open.Cancel();
}
