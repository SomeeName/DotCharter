using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.UI.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.UI.Views.Shell;

/// <summary>
///     Hosts the modal dialogs of <see cref="IDialogs" /> (target-architecture.md § 3.2).
/// </summary>
/// <remarks>
///     <para>
///         <b>A scope per open.</b> Each <see cref="ShowAsync{TContract,TResult}" />
///         resolves the dialog's view model from a fresh <see cref="IServiceScope" />,
///         awaits its <c>Result</c> (or <see cref="CloseTop" />, which asks an
///         <see cref="ICancelableDialog" /> and then completes it with the default),
///         and disposes the scope.
///     </para>
///     <para>
///         <b>One shared scrim</b> — <c>Border.FireOverlayScrim</c>, the
///         translucent one: a dialog opens over the work it is about. It sits
///         directly under the TOP dialog, so a dialog opened over another dims it.
///         Clicking it does nothing; Esc closes the top dialog.
///     </para>
///     <para>
///         <b>Attached while it is in a window.</b> The app-wide
///         <see cref="DialogsSlot" /> forwards to this host from the moment it
///         enters a visual tree until it leaves it, so a window that was built but
///         never shown (or has closed) never answers for the app.
///     </para>
///     <para>
///         <b>Prewarm with a null <c>DataContext</c>.</b> <see cref="HiddenSurfaces" />
///         creates one hidden, context-free view per catalog dialog, so
///         <c>MainWindowPrewarm</c> pays template and style application after first
///         paint and the first real open pays only binding. A closed dialog's view
///         goes back to that pool.
///     </para>
/// </remarks>
public sealed class DialogHost : Panel, IPrewarmableSurfaces, IDialogs, IDisposable
{
    /// <summary>The shared dialog grammar the migrated dialog cards are drawn in.</summary>
    public static readonly Uri DialogStylesUri = new("avares://DotCharter/Views/Dialogs/DialogStyles.axaml");

    private readonly IServiceScopeFactory _scopes;
    private readonly ScreenCatalog _catalog;
    private readonly DialogsSlot _slot;
    private readonly DialogStack _stack = new();
    private readonly Border _scrim = new() { IsVisible = false };
    private readonly List<Control> _openViews = [];
    private readonly List<Control> _idleViews = [];
    private bool _disposed;

    public DialogHost(IServiceScopeFactory scopes, ScreenCatalog catalog, DialogsSlot slot)
    {
        _scopes = scopes;
        _catalog = catalog;
        _slot = slot;
        // A Border family is styled only inside its ControlTheme: the class
        // without the theme is a silent no-op.
        WearTheme(_scrim, "FireOverlayScrimBorder");
        _scrim.Classes.Add("FireOverlayScrim");
        Children.Add(_scrim);
    }

    public bool HasOpenDialog => _stack.HasOpenDialog;

    public event EventHandler<DialogChange>? Changed;

    /// <summary>The scrim every open dialog shares.</summary>
    public Border Scrim => _scrim;

    /// <summary>The open dialogs' views, bottom first.</summary>
    public IReadOnlyList<Control> OpenViews => _openViews;

    public async Task<TResult?> ShowAsync<TContract, TResult>(Action<TContract>? init = null)
        where TContract : class, IDialogScreen<TResult>
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // timed from here, so the view model's construction counts too.
        var showStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var entry = _catalog.Find(typeof(TContract))
                    ?? throw new InvalidOperationException($"{typeof(TContract).Name} is not registered in the screen catalog.");

        using var scope = _scopes.CreateScope();
        var dialog = scope.ServiceProvider.GetRequiredService<TContract>();
        init?.Invoke(dialog);

        var view = TakeIdle(entry.ViewType) ?? entry.CreateView();
        view.DataContext = dialog;
        BringToTop(view);
        view.IsVisible = true;
        _openViews.Add(view);
        var session = _stack.Open(dialog.Result, dialog as ICancelableDialog);
        Restack();
        Dispatcher.UIThread.Post(() => FocusOnOpen.FocusTargetIn(view), DispatcherPriority.Loaded);
        Changed?.Invoke(this, new DialogChange(entry.Contract, entry.UsageId, true));
        SurfaceShowTiming.BeginAt($"Dialog {entry.Contract.Name}", showStarted);

        try
        {
            return await session.Completion;
        }
        finally
        {
            _stack.Remove(session);
            _openViews.Remove(view);
            view.IsVisible = false;
            view.DataContext = null;
            if (!_disposed) _idleViews.Add(view);
            Restack();
            Changed?.Invoke(this, new DialogChange(entry.Contract, entry.UsageId, false));
        }
    }

    public Task<bool> MessageAsync(string title, string message, bool isConfirmation = false) =>
        ShowAsync<IMessagePromptScreen, bool>(prompt =>
            (prompt as IMessagePromptSetup
             ?? throw new InvalidOperationException($"{prompt.GetType().Name} cannot be configured as a message prompt."))
            .Configure(title, message, isConfirmation));

    public bool CloseTop() => _stack.CloseTop();

    public IReadOnlyList<Control> HiddenSurfaces()
    {
        foreach (var entry in _catalog.Entries.Where(e => e.Kind == ScreenKind.Dialog))
        {
            if (_idleViews.Any(v => v.GetType() == entry.ViewType)) continue;
            if (_openViews.Any(v => v.GetType() == entry.ViewType)) continue;
            var view = entry.CreateView();
            view.DataContext = null;
            view.IsVisible = false;
            _idleViews.Add(view);
            Children.Insert(0, view);
        }

        return [.. HiddenSubtreePrewarmer.HiddenChildrenOf(this).Where(c => !ReferenceEquals(c, _scrim))];
    }

    /// <summary>
    ///     A dialog view as the host shows it — over the ground and the shared
    ///     scrim, inside the dialog grammar — for the gallery and the goldens.
    /// </summary>
    public static Control Preview(Control dialogView)
    {
        var ground = WearTheme(new Border(), "FireGroundBorder");
        ground.Classes.Add("FireGround");
        ground.Classes.Add("Screen");
        var scrim = WearTheme(new Border(), "FireOverlayScrimBorder");
        scrim.Classes.Add("FireOverlayScrim");
        var frame = new Panel { Children = { ground, scrim, dialogView } };
        frame.Styles.Add(new StyleInclude(DialogStylesUri) { Source = DialogStylesUri });
        return frame;
    }

    private static Border WearTheme(Border border, string theme)
    {
        border[!StyledElement.ThemeProperty] = new DynamicResourceExtension(theme);
        return border;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!_disposed && !_slot.IsAttached) _slot.Attach(this);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _slot.Detach(this);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape) e.Handled = CloseTop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stack.CloseAll();
        _slot.Detach(this);
    }

    private Control? TakeIdle(Type viewType)
    {
        var idle = _idleViews.FirstOrDefault(v => v.GetType() == viewType);
        if (idle is not null) _idleViews.Remove(idle);
        return idle;
    }

    /// <summary>
    ///     Puts <paramref name="view" /> last (on top) WITHOUT taking it out of the
    ///     tree. A pooled view is already a child; removing and re-adding it would
    ///     detach it from the logical tree, which drops every style it had and
    ///     re-applies them all on the re-attach — measured at 55-130 ms (Debug) per
    ///     open of the Tone designer, every open, with its prewarmed realization
    /// thrown away. <c>Children.Move</c> reorders in place.
    /// </summary>
    private void BringToTop(Control view)
    {
        var index = Children.IndexOf(view);
        if (index < 0) Children.Add(view);
        else if (index != Children.Count - 1) Children.Move(index, Children.Count - 1);
    }

    private void Restack()
    {
        if (_openViews.Count == 0)
        {
            MoveScrimTo(0);
            _scrim.IsVisible = false;
            return;
        }

        // Directly under the top dialog. Moving the scrim out first shifts every
        // later index down by one, which is why the target is read afterwards.
        var scrimAt = Children.IndexOf(_scrim);
        var topAt = Children.IndexOf(_openViews[^1]);
        MoveScrimTo(scrimAt < topAt ? topAt - 1 : topAt);
        _scrim.IsVisible = true;
    }

    private void MoveScrimTo(int index)
    {
        var at = Children.IndexOf(_scrim);
        if (at != index) Children.Move(at, index);
    }
}
