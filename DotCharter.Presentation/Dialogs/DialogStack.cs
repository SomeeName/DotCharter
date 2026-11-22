namespace DotCharter.Presentation.Dialogs;

/// <summary>
///     The open dialogs of one host, top last. Closing from outside asks a
///     <see cref="ICancelableDialog" /> first, then completes the dialog with the default result.
/// </summary>
public sealed class DialogStack
{
    private readonly List<IDialogSession> _open = [];

    public int Count => _open.Count;

    public bool HasOpenDialog => _open.Count > 0;

    public DialogSession<TResult> Open<TResult>(Task<TResult> result, ICancelableDialog? cancel = null)
    {
        var session = new DialogSession<TResult>(result, cancel);
        _open.Add(session);
        return session;
    }

    /// <summary>True when there was a top dialog to ask; it stays open if it refused.</summary>
    public bool CloseTop()
    {
        if (_open.Count == 0) return false;
        var top = _open[^1];
        if (!top.TryCancel()) return true;
        _open.Remove(top);
        top.Dismiss();
        return true;
    }

    /// <summary>Dismisses every dialog without asking — the host is going away.</summary>
    public void CloseAll()
    {
        while (_open.Count > 0)
        {
            var top = _open[^1];
            _open.RemoveAt(_open.Count - 1);
            top.Dismiss();
        }
    }

    public void Remove(IDialogSession session) => _open.Remove(session);
}

public interface IDialogSession
{
    /// <summary>Asks the dialog whether it may close now; true when it has no say.</summary>
    bool TryCancel();

    void Dismiss();
}

/// <summary>Races a dialog's own result against a dismissal from outside.</summary>
public sealed class DialogSession<TResult> : IDialogSession
{
    private readonly TaskCompletionSource<TResult?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ICancelableDialog? _cancel;

    public DialogSession(Task<TResult> result, ICancelableDialog? cancel = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        _cancel = cancel;
        // On the context the dialog opened on (the UI thread), never the pool: a pool continuation
        // completed the session off the UI queue, so a caller could still be waiting after that
        // queue had drained. Dismiss can still win between the result and the queued completion;
        // both paths use TrySet, so the first one decides.
        var scheduler = SynchronizationContext.Current is null
            ? TaskScheduler.Default
            : TaskScheduler.FromCurrentSynchronizationContext();
        result.ContinueWith(Complete, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, scheduler);
    }

    public Task<TResult?> Completion => _done.Task;

    public bool TryCancel() => _cancel?.TryCancel() ?? true;

    public void Dismiss() => _done.TrySetResult(default);

    private void Complete(Task<TResult> result)
    {
        if (result.IsCompletedSuccessfully) _done.TrySetResult(result.Result);
        else if (result.Exception is { } error) _done.TrySetException(error.InnerExceptions);
        else _done.TrySetResult(default);
    }
}
