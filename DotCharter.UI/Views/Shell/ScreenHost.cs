using Avalonia.Controls;
using Avalonia.Input;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Navigation;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.UI.Views.Shell;

/// <summary>
///     Hosts the screens of one navigation stack (target-architecture.md § 3.1).
/// </summary>
/// <remarks>
///     <para>
///         <b>One realized child per contract, shown by an <c>IsVisible</c> flip.</b>
///         A screen is created from <see cref="ScreenCatalog" /> the first time it
///         is shown or prewarmed, its view model is resolved from THIS host's
///         <see cref="IServiceScope" /> (so a scoped view model lives as long as the
///         host), and the view is kept: a later show costs the same ~0.1 ms re-show
///         <see cref="HiddenSubtreePrewarmer" /> relies on.
///     </para>
///     <para>
///         <b>Prewarm never writes <c>IsVisible</c>.</b> The hidden children are
///         offered through <see cref="IPrewarmableSurfaces" /> (so
///         <c>MainWindowPrewarm</c> realizes them after first paint), and each show
///         pre-creates the screen's neighbours in <see cref="Flow" />, one per
///         <c>Background</c> dispatcher turn through <see cref="PrewarmPacer" />. A
///         neighbour is created hidden and realized by measuring its visible
///         children; no command runs.
///     </para>
///     <para>
///         Esc closes the top dialog first (<see cref="IDialogs.CloseTop" />), then
///         goes back one screen. Dispose the host with its owner: that disposes the
///         scope and every scoped view model in it.
///     </para>
/// </remarks>
public sealed class ScreenHost : Panel, IPrewarmableSurfaces, IScreenPresenter, IDisposable
{
    private readonly ScreenCatalog _catalog;
    private readonly IServiceScope _scope;
    private readonly IDialogs _dialogs;
    private readonly Dictionary<Type, Control> _views = [];
    private bool _disposed;

    public ScreenHost(IServiceScopeFactory scopes, ScreenCatalog catalog)
    {
        _catalog = catalog;
        _scope = scopes.CreateScope();
        _scope.ServiceProvider.GetRequiredService<ScreenPresenterSlot>().Attach(this);
        Navigator = _scope.ServiceProvider.GetRequiredService<INavigator>();
        _dialogs = _scope.ServiceProvider.GetRequiredService<IDialogs>();
    }

    /// <summary>This host's navigator — the same instance its screens' view models are given.</summary>
    public INavigator Navigator { get; }

    /// <summary>The host's scope, lent to a <see cref="ScreenTabHost" /> inside one of its screens.</summary>
    internal IServiceProvider Services => _scope.ServiceProvider;

    internal ScreenCatalog Catalog => _catalog;

    /// <summary>The flow order neighbour prewarm follows, e.g. <c>ScreenCatalog.FlowOf(PlayMode)</c>. Empty: no neighbour prewarm.</summary>
    public IReadOnlyList<Type> Flow { get; set; } = [];

    /// <summary>The realized view for <paramref name="contract" />, or null when it has not been created.</summary>
    public Control? ViewOf(Type contract) => _views.GetValueOrDefault(contract);

    object IScreenPresenter.Present(Type contract)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var view = Realize(contract);
        foreach (var (type, child) in _views)
        {
            var show = type == contract;
            if (child.IsVisible && !show) Lifecycle(child)?.OnHidden();
            child.IsVisible = show;
        }

        Lifecycle(view)?.OnShown();
        ScheduleNeighbours(contract);
        return view.DataContext!;
    }

    /// <summary>
    ///     Re-announces the SHOWN screen to its view model, for the axis
    ///     navigation cannot see: this host's own container became visible
    ///     again because the shell switched tabs. A host with one screen never
    ///     navigates after its first show, so without this its figures are
    ///     whatever they were when the app started.
    /// </summary>
    public void NotifyReshown()
    {
        if (_disposed) return;
        foreach (var (_, child) in _views)
        {
            if (child.IsVisible)
                Lifecycle(child)?.OnShown();
        }
    }

    private static IScreenLifecycle? Lifecycle(Control view) => view.DataContext as IScreenLifecycle;

    public IReadOnlyList<Control> HiddenSurfaces() => HiddenSubtreePrewarmer.HiddenChildrenOf(this);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != Key.Escape) return;
        e.Handled = _dialogs.CloseTop() || Navigator.Back();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Children.Clear();
        _views.Clear();
        _scope.Dispose();
    }

    private Control Realize(Type contract)
    {
        if (_views.TryGetValue(contract, out var cached)) return cached;

        var entry = _catalog.Find(contract)
                    ?? throw new InvalidOperationException($"{contract.Name} is not registered in the screen catalog.");
        var view = entry.CreateView();
        view.DataContext = _scope.ServiceProvider.GetRequiredService(contract);
        // Created hidden: the show that asked for it flips it, a prewarm never does.
        view.IsVisible = false;
        _views[contract] = view;
        Children.Add(view);
        return view;
    }

    private void ScheduleNeighbours(Type contract)
    {
        var at = -1;
        for (var i = 0; i < Flow.Count; i++)
            if (Flow[i] == contract) at = i;
        if (at < 0) return;

        foreach (var neighbour in new[] { at + 1, at - 1 })
        {
            if (neighbour < 0 || neighbour >= Flow.Count || _views.ContainsKey(Flow[neighbour])) continue;
            var next = Flow[neighbour];
            PrewarmPacer.Post(() => PrewarmOne(next));
        }
    }

    private void PrewarmOne(Type contract)
    {
        if (_disposed || _views.ContainsKey(contract)) return;
        HiddenSubtreePrewarmer.Realize(Realize(contract), Bounds.Size);
    }
}
