using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>The result a dialog owes its host, completed once by the dialog's own verbs.</summary>
public abstract class DialogViewModel<TResult> : ObservableObject, IDialogScreen<TResult>
{
    private readonly TaskCompletionSource<TResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<TResult> Result => _result.Task;

    protected bool IsClosed => _result.Task.IsCompleted;

    protected void Close(TResult result) => _result.TrySetResult(result);
}
