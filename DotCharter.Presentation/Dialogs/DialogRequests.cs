using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Dialogs;

/// <summary>
///     A dialog's opening arguments, typed to the dialog they open, so
///     <c>dialogs.ShowAsync(new FhpRequest(…))</c> infers the contract and the result.
/// </summary>
public interface IDialogRequest<TRequest, TContract, TResult>
    where TRequest : IDialogRequest<TRequest, TContract, TResult>
    where TContract : class, IDialogScreen<TResult>;

public interface IDialogSetup<in TRequest>
{
    void Configure(TRequest request);
}

/// <summary>Asked on Esc: runs the dialog's cancel, or returns false to stay open; the host then answers the default.</summary>
public interface ICancelableDialog
{
    bool TryCancel();
}

public static class DialogRequests
{
    public static Task<TResult?> ShowAsync<TRequest, TContract, TResult>(this IDialogs dialogs,
        IDialogRequest<TRequest, TContract, TResult> request)
        where TRequest : IDialogRequest<TRequest, TContract, TResult>
        where TContract : class, IDialogScreen<TResult>
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(request);
        return dialogs.ShowAsync<TContract, TResult>(dialog =>
            (dialog as IDialogSetup<TRequest>
             ?? throw new InvalidOperationException($"{dialog.GetType().Name} does not take a {typeof(TRequest).Name}."))
            .Configure((TRequest)request));
    }

    /// <summary>For a dialog nobody awaits: a fault is logged rather than lost as an unobserved task.</summary>
    public static void Observe(this Task dialog, IAppLogger logger, string what)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        dialog.ContinueWith(t => logger.Error(AppLogCategory.Application, $"The {what} dialog failed.", t.Exception),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }
}
