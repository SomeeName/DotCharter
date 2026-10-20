using DotCharter.Presentation.Dialogs;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     An <see cref="IDialogs" /> that opens the REAL view model a test builds
///     for the contract, configures it exactly as the host would, records it,
///     and hands it to <c>act</c> — the "user" — before awaiting its answer. A
///     launcher under test therefore runs its whole flow: read, open, answer,
///     and whatever it does with the answer.
/// </summary>
internal sealed class ScriptedDialogs(Func<Type, object> create, Action<object>? act = null) : IDialogs
{
    public List<object> Opened { get; } = [];

    public bool HasOpenDialog => false;

    public event EventHandler<DialogChange>? Changed
    {
        add { }
        remove { }
    }

    public async Task<TResult?> ShowAsync<TContract, TResult>(Action<TContract>? init = null)
        where TContract : class, IDialogScreen<TResult>
    {
        var screen = (TContract)create(typeof(TContract));
        init?.Invoke(screen);
        Opened.Add(screen);
        act?.Invoke(screen);
        return await screen.Result;
    }

    public Task<bool> MessageAsync(string title, string message, bool isConfirmation = false) => Task.FromResult(false);

    public bool CloseTop() => false;
}
