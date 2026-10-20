namespace DotCharter.Presentation.Navigation;

/// <summary>
///     <see cref="INavigator" /> over a <see cref="BackStack" /> and one
///     <see cref="IScreenPresenter" />. Calls <see cref="IScreenLifecycle.OnHidden" />
///     on the screen it leaves and <see cref="IScreenLifecycle.OnShown" /> on the
///     one it arrives at, after that screen's initializer.
/// </summary>
public sealed class Navigator : INavigator
{
    private readonly IScreenPresenter _presenter;
    private readonly BackStack _stack;
    private object? _screen;

    public Navigator(IScreenPresenter presenter, int capacity = BackStack.DefaultCapacity)
    {
        _presenter = presenter;
        _stack = new BackStack(capacity);
    }

    public Type? Current => _stack.Top;

    public bool CanGoBack => _stack.Count > 1;

    public IReadOnlyList<Type> Stack => _stack.Items;

    public void Show<TContract>(Action<TContract>? init = null) where TContract : class =>
        Show(ScreenRequest.For(init));

    public void Show(ScreenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _stack.Push(request.Contract);
        var screen = Activate(request.Contract);
        request.Init?.Invoke(screen);
        (screen as IScreenLifecycle)?.OnShown();
    }

    public bool Back()
    {
        if (!CanGoBack) return false;
        _stack.Pop();
        var screen = Activate(_stack.Top!);
        (screen as IScreenLifecycle)?.OnShown();
        return true;
    }

    private object Activate(Type contract)
    {
        (_screen as IScreenLifecycle)?.OnHidden();
        _screen = _presenter.Present(contract);
        return _screen;
    }
}
