using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using DotCharter.Presentation.Dialogs;
using DotCharter.UI.ViewModels.Dialogs;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.UI.Views.Shell;

/// <summary>
/// Hosts the modal overlay dialogs. Only the generic message dialog is
/// declared in XAML (it must exist from the very first frame so early
/// startup errors can surface); every other dialog view is constructed on
/// the first <c>IsOpen = true</c> of its singleton view model and then
/// kept alive, so re-opens are as fast as before while app startup no
/// longer pays for ~18 overlay visual trees nobody can see.
/// </summary>
public partial class DialogContainerView : UserControl, IPrewarmableSurfaces, IPrewarmConstruction
{
    private bool _lazyDialogsWired;

    /// <summary>Factories for the dialogs that have not been materialized yet,
    /// keyed by view model so <see cref="Materialize" /> can drop the entry.</summary>
    private readonly Dictionary<IDialogViewModel, Func<Control>> _pending = [];

    public DialogContainerView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, handledEventsToo: true);
        DataContextChanged += (_, _) => WireLazyDialogs();
        if (App.Services is { } services) Dialogs = AddDialogHost(services);
    }

    /// <summary>
    ///     The rewrite's dialog host (<see cref="DialogHost" />): the dialogs
    ///     opened through <c>IDialogs</c>. Null when there is no composition root —
    ///     a view built for a test. It sits above every legacy dialog and below
    ///     the eager generic message dialog, and it attaches itself to the
    ///     app-wide <c>DialogsSlot</c> while this view is in a window.
    /// </summary>
    public DialogHost? Dialogs { get; }

    private DialogHost AddDialogHost(IServiceProvider services)
    {
        var host = new DialogHost(services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<ScreenCatalog>(), services.GetRequiredService<DialogsSlot>());
        DialogPanel.Children.Insert(Math.Max(0, DialogPanel.Children.Count - 1), host);
        return host;
    }

    /// <summary>
    ///     Every dialog, built and handed over for the shell's post-first-frame
    ///     prewarm. Each returned control is the dialog's own
    ///     <c>IsVisible="{Binding IsOpen}"</c> scrim — the thing that costs 5-110
    ///     ms of first layout the first time that dialog is opened.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This MATERIALIZES the still-deferred views as a side effect, which
    ///         is the point: lazy construction moved ~18 visual trees off startup,
    ///         but it moved them onto the first open instead, where the user is
    ///         waiting. Building them at <c>Background</c> priority after the
    ///         first frame keeps both wins. The view models are unaffected — all
    ///         32 already exist from startup (<see cref="WireLazyDialogs" />
    ///         subscribes to every one of them), so nothing new is constructed
    ///         below the view layer.
    ///     </para>
    ///     <para>
    ///         The prewarm never flips <c>IsVisible</c>, which is what keeps this
    ///         safe: five of these views focus themselves when their scrim becomes
    ///         visible (see <see cref="HiddenSubtreePrewarmer" />).
    ///     </para>
    /// </remarks>
    /// <summary>The still-deferred dialog views, one drain turn each —
    /// <see cref="HiddenSurfaces" /> must not build all 34 in one.</summary>
    public IEnumerable<Action> ConstructionSteps()
    {
        foreach (var (viewModel, factory) in _pending.ToList())
            yield return () => Materialize(viewModel, factory);
    }

    public IReadOnlyList<Control> HiddenSurfaces()
    {
        foreach (var (viewModel, factory) in _pending.ToList())
            Materialize(viewModel, factory);

        return
        [
            .. DialogPanel.Children.OfType<ContentControl>()
                .SelectMany(HiddenSubtreePrewarmer.HiddenContentOf),
            // …then the selection-gated bodies INSIDE two of those scrims. The
            // prewarmer stops at the first hidden root, so the scrim being
            // realized buys these nothing; after their scrim, by name.
            .. HiddenSubtreePrewarmer.HiddenNamedDescendants(DialogPanel, NestedDialogBodyNames)
        ];
    }

    /// <summary>
    ///     The dialog bodies gated on state the dialog has none of when it opens —
    ///     the Tone Designer's editor column, the Pattern Builder's editor (both
    ///     selection-gated) and the first-run wizard's Audio output and Guitar input
    ///     pages (step-gated) — markup that would otherwise pay its whole first layout
    ///     on the first row click or the first Next.
    /// </summary>
    internal static readonly string[] NestedDialogBodyNames =
        ["ToneEditor", "PatternEditorBody", "OnboardingAudioStep", "OnboardingInputStep"];

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // MainWindow's tunnel handler owns Esc now (it fires regardless of
        // focus); this routed fallback must not double-handle the same press.
        if (e.Handled) return;
        if (e.Key == Key.Escape && DataContext is MainWindowViewModel vm)
        {
            vm.HandleEscapeKey();
            e.Handled = true;
        }
    }

    private void WireLazyDialogs()
    {
        if (_lazyDialogsWired || DataContext is not MainWindowViewModel vm) return;
        _lazyDialogsWired = true;

        var d = vm.Dialogs;
        RegisterLazy(d.Onboarding, static () => new OnboardingWizardView());
    }

    /// <summary>
    /// Builds the dialog's view the first time its view model opens. The
    /// handler runs synchronously inside the IsOpen setter, so the view is
    /// in the tree (with IsVisible bound to the already-true IsOpen) before
    /// the frame that shows the dialog is rendered — the first open pays a
    /// few ms of inflation, never a missed dialog.
    /// </summary>
    private void RegisterLazy<TViewModel>(TViewModel dialogViewModel, Func<Control> factory)
        where TViewModel : IDialogViewModel, INotifyPropertyChanged
    {
        if (dialogViewModel.IsOpen)
        {
            Materialize(dialogViewModel, factory);
            return;
        }

        _pending[dialogViewModel] = factory;

        PropertyChangedEventHandler? handler = null;
        handler = (_, e) =>
        {
            if (e.PropertyName != nameof(IDialogViewModel.IsOpen) || !dialogViewModel.IsOpen) return;
            dialogViewModel.PropertyChanged -= handler;
            Materialize(dialogViewModel, factory);
        };
        dialogViewModel.PropertyChanged += handler;
    }

    /// <summary>Builds a dialog's view exactly once, whether the trigger was the
    /// first open or the prewarm. Re-entrant by design — both paths can race the
    /// same dialog and the second one must be a no-op, not a second view.</summary>
    private void Materialize(IDialogViewModel dialogViewModel, Func<Control> factory)
    {
        if (!_pending.Remove(dialogViewModel) && DialogPanel.Children
                .Any(c => ReferenceEquals(c.DataContext, dialogViewModel)))
        {
            return;
        }

        var view = factory();
        view.DataContext = dialogViewModel;
        // Insert below the dialog host (or, without one, below the eager
        // MessageDialogView, always the last child): a legacy dialog is never
        // drawn over a hosted one, and the generic message dialog stays topmost.
        var below = Dialogs is { } host ? DialogPanel.Children.IndexOf(host) : DialogPanel.Children.Count - 1;
        DialogPanel.Children.Insert(Math.Max(0, below), view);
    }
}
