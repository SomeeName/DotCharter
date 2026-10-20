using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace DotCharter.UI.Views.Shell;

/// <summary>
/// Keeps a hidden surface warm when its LISTS change while it is hidden.
/// </summary>
/// <remarks>
///     <para>
///         The startup prewarm (<see cref="HiddenSubtreePrewarmer" />) realizes each
///         hidden surface ONCE, after first paint — usually before a project is open.
///         A surface whose rows come from the project is then empty when it is
///         realized, and opening the project fills its lists behind its
///         <c>IsVisible=false</c>: the new rows are not laid out until the user's
///         click shows them. Measured on the Inspector's Song tab over a real
///         chart: 125-290 ms on the click (Debug, headless), the same as never
///         having prewarmed it at all.
///     </para>
///     <para>
///         This watches every <see cref="ItemsControl" /> declared in the surface
///         (a <c>ComboBox</c>, <c>ListBox</c> or bare <c>ItemsControl</c>; their
///         <see cref="ItemsControl.Items" /> mirror the bound source), and when one
///         changes while the surface is hidden it schedules ONE
///         <see cref="HiddenSubtreePrewarmer.RealizeStale" /> through
///         <see cref="PrewarmPacer" /> — <see cref="Avalonia.Threading.DispatcherPriority.Background" />,
///         once input is quiet, coalesced however many lists moved. A visible
///         surface lays its own changes out, so nothing is scheduled for it.
///     </para>
///     <para>
///         A surface with SECTIONS of its own (an Inspector tab with a section rail)
///         hands them in: the prewarmer stops at the first hidden root, so a
///         section hidden inside the surface is realized on its own — after the
///         surface, and also while the surface itself is on screen and only the
///         section is hidden.
///     </para>
///     <para>
///         The subscriptions are on the surface's OWN controls, so they share its
///         lifetime: nothing here reaches a singleton.
///     </para>
/// </remarks>
public sealed class HiddenSurfaceRewarmer
{
    private readonly Control _surface;
    private readonly Func<IReadOnlyList<Control>> _sections;
    private bool _pending;

    public HiddenSurfaceRewarmer(Control surface, Func<IReadOnlyList<Control>>? sections = null)
    {
        _surface = surface;
        _sections = sections ?? (() => []);
        foreach (var list in surface.GetLogicalDescendants().OfType<ItemsControl>())
            ((INotifyCollectionChanged)list.Items).CollectionChanged += OnItemsChanged;
    }

    /// <summary>How many re-realizations actually found stale rows — the test hook.</summary>
    public int Rewarms { get; private set; }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Request();

    /// <summary>
    /// Asks for one background re-realization of the hidden surface — for
    ///     changes no list announces: a project or arrangement switch flips
    ///     <c>IsVisible</c> bindings, text and enabled states all over a tab, and the
    ///     click would lay all of it out. Coalesced with the list-change path; a
    ///     visible surface lays its own changes out, so nothing is scheduled for it.
    /// </summary>
    public void Request()
    {
        if (_pending || (_surface.IsVisible && _sections().All(s => s.IsVisible))) return;
        _pending = true;
        PrewarmPacer.Post(Rewarm);
    }

    private void Rewarm()
    {
        _pending = false;
        if (TopLevel.GetTopLevel(_surface) is not { } top) return;
        var realized = _surface.IsVisible ? 0 : HiddenSubtreePrewarmer.RealizeStale(_surface, top.Bounds.Size);
        foreach (var section in _sections().Where(s => !s.IsVisible))
            realized += HiddenSubtreePrewarmer.RealizeStale(section, top.Bounds.Size);
        if (realized > 0) Rewarms++;
    }
}
