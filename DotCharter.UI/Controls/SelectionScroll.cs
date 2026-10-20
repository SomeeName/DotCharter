using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace DotCharter.UI.Controls;

/// <summary>
///     Keeps a song list's selection in view with ONE scroll per dispatcher turn, in place of
/// Avalonia's <see cref="SelectingItemsControl.AutoScrollToSelectedItem" />.
///     <para>
///         The built-in auto-scroll follows the selected INDEX, and a filter pass moves that
///         index without changing the selection: every row inserted or removed above the
///         selected song shifts it by one, and each shift scrolls the list into view through a
///         synchronous layout. Measured headless on the Library over 1,800 songs (Debug):
///         relaxing a query from 12 to 71 rows was 59 inserts, 56 layouts and 456 row
///         containers prepared — 5.6–9 s for one keystroke — and every Reset pass prepared
///         each visible row twice. With it off the same keystroke is 2 layouts and 5
///         containers.
///     </para>
///     <para>
///         What the auto-scroll was FOR stays: when the selected ITEM changes (a click, the
///         keyboard, a filter that picks the first row) the list scrolls to it once, at
///         <see cref="DispatcherPriority.Background" />, after the pass and its layout. A list
///         that is not on screen is left alone.
///     </para>
///     <para>
///         Since the selection survives a filter pass (<c>LibraryScreen</c>), a pass that keeps
///         the selected song keeps the list where the user left it only when the song is on
///         screen; otherwise the list scrolls to it, as the built-in behaviour did.
///     </para>
/// </summary>
public static class SelectionScroll
{
    /// <summary>True: auto-scroll off, one coalesced scroll per selection change instead.</summary>
    public static readonly AttachedProperty<bool> CoalescedProperty =
        AvaloniaProperty.RegisterAttached<SelectingItemsControl, bool>("Coalesced", typeof(SelectionScroll));

    private static readonly AttachedProperty<bool> PendingProperty =
        AvaloniaProperty.RegisterAttached<SelectingItemsControl, bool>("Pending", typeof(SelectionScroll));

    static SelectionScroll() => CoalescedProperty.Changed.AddClassHandler<SelectingItemsControl>((list, e) => Arm(list, e.GetNewValue<bool>()));

    public static bool GetCoalesced(SelectingItemsControl list) => list.GetValue(CoalescedProperty);

    public static void SetCoalesced(SelectingItemsControl list, bool value) => list.SetValue(CoalescedProperty, value);

    private static void Arm(SelectingItemsControl list, bool on)
    {
        list.RemoveHandler(SelectingItemsControl.SelectionChangedEvent, OnSelectionChanged);
        list.AutoScrollToSelectedItem = !on;
        if (on) list.AddHandler(SelectingItemsControl.SelectionChangedEvent, OnSelectionChanged, RoutingStrategies.Bubble);
    }

    private static void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // A nested list's selection bubbles through this one; only our own counts.
        if (sender is not SelectingItemsControl list || !ReferenceEquals(e.Source, list) || list.GetValue(PendingProperty)) return;
        list.SetValue(PendingProperty, true);
        Dispatcher.UIThread.Post(() =>
        {
            list.SetValue(PendingProperty, false);
            var index = list.SelectedIndex;
            if (index < 0 || !list.IsEffectivelyVisible) return;

            // A pass that reset the list has not been laid out yet, so nothing is realized and a
            // ScrollIntoView would realize the viewport twice (once for the scroll, once for the
            // layout). Run that layout first — it was due anyway — and then the selected row is
            // usually on screen already and only needs bringing fully into view.
            if (list.ContainerFromIndex(index) is null) list.UpdateLayout();
            if (list.ContainerFromIndex(index) is { } container) container.BringIntoView();
            else list.ScrollIntoView(index);
        }, DispatcherPriority.Background);
    }
}
