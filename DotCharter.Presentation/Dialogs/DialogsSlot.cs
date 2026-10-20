namespace DotCharter.Presentation.Dialogs;

/// <summary>
///     The app-wide <see cref="IDialogs" />: forwards to the dialog host that
///     attached itself. With no host, nothing is open and nothing can be shown.
/// </summary>
public sealed class DialogsSlot : IDialogs
{
    private IDialogs? _host;

    public bool IsAttached => _host is not null;

    public bool HasOpenDialog => _host?.HasOpenDialog ?? false;

    public event EventHandler<DialogChange>? Changed;

    public void Attach(IDialogs host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (ReferenceEquals(_host, host)) return;
        if (_host is not null) throw new InvalidOperationException("A dialog host is already attached.");
        _host = host;
        host.Changed += OnHostChanged;
    }

    public void Detach(IDialogs host)
    {
        if (!ReferenceEquals(_host, host)) return;
        host.Changed -= OnHostChanged;
        _host = null;
    }

    public Task<TResult?> ShowAsync<TContract, TResult>(Action<TContract>? init = null)
        where TContract : class, IDialogScreen<TResult> =>
        Host.ShowAsync<TContract, TResult>(init);

    public Task<bool> MessageAsync(string title, string message, bool isConfirmation = false) =>
        Host.MessageAsync(title, message, isConfirmation);

    public bool CloseTop() => _host?.CloseTop() ?? false;

    private IDialogs Host => _host ?? throw new InvalidOperationException("No dialog host is attached.");

    private void OnHostChanged(object? sender, DialogChange change) => Changed?.Invoke(this, change);
}
