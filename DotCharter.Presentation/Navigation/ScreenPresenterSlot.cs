namespace DotCharter.Presentation.Navigation;

/// <summary>The scoped seam between a host scope's <see cref="Navigator" /> and the host that created that scope.</summary>
public sealed class ScreenPresenterSlot : IScreenPresenter
{
    private IScreenPresenter? _host;

    public bool IsAttached => _host is not null;

    public void Attach(IScreenPresenter host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (_host is not null && !ReferenceEquals(_host, host))
            throw new InvalidOperationException("A screen scope serves exactly one host.");
        _host = host;
    }

    public object Present(Type contract) =>
        (_host ?? throw new InvalidOperationException("No screen host is attached to this scope.")).Present(contract);
}
