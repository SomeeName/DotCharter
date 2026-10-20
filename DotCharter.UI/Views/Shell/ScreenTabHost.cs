using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.Presentation.Navigation;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.UI.Views.Shell;

/// <summary>
///     The tab strip's content inside a screen: one realized child per
///     contract, shown by an <c>IsVisible</c> flip, with no back stack.
/// </summary>
/// <remarks>
///     <para>
///         Declared in a screen's XAML and bound to that screen's contract —
///         <c>Screens</c> is the list of tab contracts, <c>Current</c> the one to
///         show. It borrows the SCOPE of the <see cref="ScreenHost" /> above it
///         when it attaches, so a tab view model is the same instance the
///         enclosing screen's view model was given (the settings shell takes its
///         tabs as <c>IEnumerable&lt;ISettingsTab&gt;</c> from the same scope).
///         With no <see cref="ScreenHost" /> above it — the gallery, the goldens —
///         it is inert and draws nothing.
///     </para>
///     <para>
///         Every tab in <c>Screens</c> is realized hidden when the shell's prewarm
///         asks for <see cref="HiddenSurfaces" />, so the first switch into a
///         tab costs a visibility flip. A view model that implements
///         <see cref="IScreenLifecycle" /> hears each flip.
///     </para>
/// </remarks>
public sealed class ScreenTabHost : Panel, IPrewarmableSurfaces
{
    public static readonly StyledProperty<Type?> CurrentProperty =
        AvaloniaProperty.Register<ScreenTabHost, Type?>(nameof(Current));

    public static readonly StyledProperty<IReadOnlyList<Type>?> ScreensProperty =
        AvaloniaProperty.Register<ScreenTabHost, IReadOnlyList<Type>?>(nameof(Screens));

    private readonly Dictionary<Type, Control> _views = [];
    private IServiceProvider? _services;
    private ScreenCatalog? _catalog;

    static ScreenTabHost() => CurrentProperty.Changed.AddClassHandler<ScreenTabHost>((host, _) => host.Apply());

    /// <summary>The contract to show, or null to show none of them.</summary>
    public Type? Current
    {
        get => GetValue(CurrentProperty);
        set => SetValue(CurrentProperty, value);
    }

    /// <summary>The contracts this host realizes for prewarm; <see cref="Current" /> is shown even when not listed.</summary>
    public IReadOnlyList<Type>? Screens
    {
        get => GetValue(ScreensProperty);
        set => SetValue(ScreensProperty, value);
    }

    /// <summary>True once an enclosing <see cref="ScreenHost" /> lent this host its scope.</summary>
    public bool IsAttachedToScope => _services is not null;

    public Control? ViewOf(Type contract) => _views.GetValueOrDefault(contract);

    public IReadOnlyList<Control> HiddenSurfaces()
    {
        if (_services is null) return [];
        foreach (var contract in Screens ?? []) Realize(contract);
        return HiddenSubtreePrewarmer.HiddenChildrenOf(this);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_services is not null) return;
        if (this.FindAncestorOfType<ScreenHost>() is not { } host) return;
        _services = host.Services;
        _catalog = host.Catalog;
        Apply();
    }

    private void Apply()
    {
        if (_services is null) return;
        var current = Current;
        if (current is not null) Realize(current);

        foreach (var (contract, view) in _views)
        {
            if (view.IsVisible && contract != current)
            {
                view.IsVisible = false;
                (view.DataContext as IScreenLifecycle)?.OnHidden();
            }
        }

        if (current is not null && _views.TryGetValue(current, out var shown) && !shown.IsVisible)
        {
            shown.IsVisible = true;
            (shown.DataContext as IScreenLifecycle)?.OnShown();
        }
    }

    private void Realize(Type contract)
    {
        if (_views.ContainsKey(contract) || _services is null || _catalog is null) return;

        var entry = _catalog.Find(contract)
                    ?? throw new InvalidOperationException($"{contract.Name} is not registered in the screen catalog.");
        var view = entry.CreateView();
        view.DataContext = _services.GetRequiredService(contract);
        view.IsVisible = false;
        _views[contract] = view;
        Children.Add(view);
    }
}
