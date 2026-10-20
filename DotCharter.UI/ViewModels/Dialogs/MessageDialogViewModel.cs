using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Diagnostics;

namespace DotCharter.UI.ViewModels.Dialogs;

/// <summary>
///     Generic message/confirmation dialog — the app-wide "MessageBox".
///     Failures that would otherwise only be status-bar entries can
///     surface as a real dialog. <see cref="ShowAsync"/> is awaitable and
///     safe to call from any thread; a second show while one is open queues
///     behind it (one shared overlay instance, matching every other dialog).
/// </summary>
public partial class MessageDialogViewModel : ObservableObject, IDialogViewModel, IMessagePrompt
{
    /// <summary>One dialog at a time, with a bounded wait: a dialog nobody can
    /// dismiss (a view model that opens while nothing renders) does not wedge
    /// every later message in the process — see <see cref="DialogTurnstile" />.</summary>
    private readonly DialogTurnstile _queue;

    private TaskCompletionSource<bool>? _pending;

    public MessageDialogViewModel() : this(DialogTurnstile.DefaultStuckTimeout)
    {
    }

    /// <summary>Test seam: how long a second message waits behind a first one
    /// that is never dismissed before it displaces it.</summary>
    internal MessageDialogViewModel(TimeSpan stuckDialogTimeout)
        => _queue = new DialogTurnstile(stuckDialogTimeout);

    /// <summary>How many stuck dialogs have been displaced so a later message
    /// could show. Diagnostic; non-zero means a dialog opened with no view to
    /// dismiss it.</summary>
    public int StuckDialogsDisplaced => _queue.Displacements;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private string? _details;
    [ObservableProperty] private MessageDialogKind _kind = MessageDialogKind.Info;
    [ObservableProperty] private bool _isConfirmation;

    public bool HasDetails => !string.IsNullOrWhiteSpace(Details);
    public bool IsError => Kind == MessageDialogKind.Error;
    public bool IsWarning => Kind == MessageDialogKind.Warning;
    public bool IsInfo => Kind == MessageDialogKind.Info;
    public bool IsQuestion => Kind == MessageDialogKind.Question;

    partial void OnDetailsChanged(string? value) => OnPropertyChanged(nameof(HasDetails));

    partial void OnKindChanged(MessageDialogKind value)
    {
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(IsWarning));
        OnPropertyChanged(nameof(IsInfo));
        OnPropertyChanged(nameof(IsQuestion));
    }

    /// <summary>Shows the dialog and completes with the user's choice:
    /// true for OK/Yes, false for Cancel/dismiss (always true for plain
    /// non-confirmation messages once dismissed).
    /// <para>
    /// <c>virtual</c> so a caller's use of it can be asserted without a
    /// dispatcher loop — the body hops <c>Dispatcher.UIThread</c>, which never
    /// completes on a plain test thread.
    /// </para></summary>
    public virtual async Task<bool> ShowAsync(
        string title,
        string message,
        MessageDialogKind kind = MessageDialogKind.Info,
        string? details = null,
        bool confirmation = false)
    {
        // A stuck holder's task is completed with FALSE — the cancel answer,
        // safe for every confirmation in the app — so its ShowAsync returns
        // and the caller behind it gets the overlay.
        var (turn, _) = await _queue.EnterAsync(
            () => Interlocked.Exchange(ref _pending, null)?.TrySetResult(false));
        try
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Title = title;
                Message = message;
                Details = details;
                Kind = kind;
                IsConfirmation = confirmation;
                _pending = tcs;
                IsOpen = true;
            });
            return await tcs.Task;
        }
        finally
        {
            _queue.Exit(turn);
        }
    }

    public void RequestClose() => Complete(false);

    [RelayCommand]
    private void Ok() => Complete(true);

    [RelayCommand]
    private void Cancel() => Complete(false);

    private void Complete(bool result)
    {
        IsOpen = false;
        var pending = _pending;
        _pending = null;
        pending?.TrySetResult(!IsConfirmation || result);
    }
}
