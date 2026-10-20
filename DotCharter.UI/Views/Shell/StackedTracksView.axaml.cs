using Avalonia;
using Avalonia.Controls;
using DotCharter.UI.Controls;
using DotCharter.UI.Input;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.UI.Views.Shell;

public partial class StackedTracksView : UserControl
{
    private readonly Dictionary<ChartRenderer, TimelineInteractionManager> _interactionManagers = [];

    public StackedTracksView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => SyncItemsSource();
    }

    /// <summary>
    ///     (Re)binds the stacked rows to the overlay's entries. Assigning the
    ///     same collection instance again is a styled-property no-op, so this
    ///     is safe to call from both DataContextChanged and attach.
    /// </summary>
    private void SyncItemsSource()
    {
        if (DataContext is MainWindowViewModel vm)
            StackedItemsHost.ItemsSource = vm.MultiTrackOverlay.StackedEntries;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // The timeline composite is MOVED between windows on View → Float
        // Chart Timeline / redock — this single view instance is never
        // recreated and its DataContext never changes, so DataContextChanged
        // does not refire after the detach below nulls the ItemsSource.
        // Restoring here is what keeps the stacked rows alive across
        // float/redock (they would blank permanently for the session).
        SyncItemsSource();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Keep the detach-time null: it releases the row containers (and via
        // StackedRenderer_DetachedFromVisualTree their interaction managers)
        // while the view is parked outside any visual tree.
        StackedItemsHost.ItemsSource = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void StackedRenderer_AttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not ChartRenderer renderer) return;
        if (_interactionManagers.ContainsKey(renderer)) return;

        // Walk up to the hosting StackedTracksView so we get the same
        // MainWindowViewModel the renderer is bound against. Each row needs
        // its own interaction manager so dragging on a stacked row produces
        // hover / selection feedback identical to the main timeline.
        // Each manager is bound to the row's specific arrangement, so edits
        // target THAT arrangement's data without touching the active one.
        if (DataContext is not MainWindowViewModel vm) return;

        var entry = renderer.DataContext as MultiTrackEntry;
        var boundArr = entry?.Arrangement;
        if (boundArr == null) return;

        _interactionManagers[renderer] = vm.InteractionFactory.CreateManager(vm, renderer, boundArr);
    }

    private void StackedRenderer_DetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not ChartRenderer renderer) return;
        if (_interactionManagers.TryGetValue(renderer, out var manager))
        {
            manager.Dispose();
            _interactionManagers.Remove(renderer);
        }
    }
}

