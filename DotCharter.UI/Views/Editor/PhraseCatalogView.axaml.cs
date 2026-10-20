using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.UI.Views.Editor;

/// <summary>
///     Code-behind for the phrase catalog pane. Two view-only concerns live
///     here: pressing Enter in the find box pastes by typed name (EOF's
///     <c>SHIFT+~</c>), and a keybind can ask for that box to take focus.
///     <para>
///         The <c>FocusSearchRequested</c> subscription is attached and detached
///         with the visual tree — pane views are RE-CREATED on float/redock, so
///         a subscription that only ever attaches leaks one handler per dock
///         cycle (enforced by <c>ViewSubscriptionLeakGuardTests</c>).
///     </para>
/// </summary>
public partial class PhraseCatalogView : UserControl
{
    private PhraseCatalogViewModel? _subscribed;

    public PhraseCatalogView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe(DataContext as PhraseCatalogViewModel);
        DataContextChanged += OnDataContextChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DataContextChanged -= OnDataContextChanged;
        Subscribe(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
        => Subscribe(DataContext as PhraseCatalogViewModel);

    private void Subscribe(PhraseCatalogViewModel? viewModel)
    {
        if (ReferenceEquals(_subscribed, viewModel)) return;
        _subscribed?.FocusSearchRequested -= FocusSearch;
        _subscribed = viewModel;
        if (_subscribed == null) return;
        _subscribed.FocusSearchRequested += FocusSearch;
        // The library load is deferred off the pre-window path (see the VM's
        // ctor). If the user reaches the pane before that Background turn has
        // drained, this is what fills it — the service caches, so on the normal
        // path it is a list rebuild and no folder read at all.
        _subscribed.EnsureLoaded();
    }

    private void FocusSearch()
    {
        var box = this.FindControl<TextBox>("SearchBox");
        if (box == null) return;
        box.Focus();
        box.SelectAll();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is not PhraseCatalogViewModel vm) return;
        vm.PasteByNameCommand.Execute(null);
        e.Handled = true;
    }
}
