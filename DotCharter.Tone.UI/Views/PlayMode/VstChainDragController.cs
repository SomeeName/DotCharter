using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.Tone.UI.ViewModels.Editor;

namespace DotCharter.Tone.UI.Views.PlayMode;

/// <summary>
///     Drag-to-reorder for the shared plugin rack (<see cref="VstRackView" />) —
///     the mockup's "Signal runs top to bottom. <b>Drag to reorder.</b>", made
///     real. One controller for every surface that shows the chain (DotCharter's
///     Tools popover and Amp/FX panel, RigLab's Plugins tab), because the rack is
///     one view: two reorder gestures over one chain is how two surfaces end up
///     disagreeing about the signal path.
/// </summary>
/// <remarks>
///     <para>
///         It replaces two ▲▼ buttons per slot. Those were not merely uglier:
///         they could only move a slot one place per click, and they cost a row
///         52 px of its 400 — which is why the mockup's slot has room for a
///         grip, an index, a name, a state pill and a GUI button and the old one
///         did not.
///     </para>
///     <para>
///         The geometry is the SHARED Fire drag machinery
///         (<see cref="FireDragSession" /> ghost + <see cref="FireDropMarker" />
///         insertion bar + <see cref="FireSlotHitTest" />), the same one the amp
///         rig's board uses, only <see cref="Orientation.Vertical" />. Three
///         things are specific to living inside a popover and each is a real
///         constraint:
///     </para>
///     <list type="number">
///         <item>
///             <b>Only the GRIP starts a drag.</b> A chain row carries a bypass
///             pill, a GUI toggle and a remove button; a press-anywhere drag
///             would swallow all three. The rig's controller refuses a list of
///             interactive types instead — that inverts here, because a slot is
///             mostly buttons.
///         </item>
///         <item>
///             <b>The press handler is a TUNNEL.</b> The grip sits inside the
///             row Border, and a bubble handler would see the press only after
///             anything above it had a chance to claim it.
///         </item>
///         <item>
///             <b><c>PointerCaptureLost</c> must ignore the capture we
///             are taking ourselves.</b> It bubbles, so a child button shedding
///             its capture as this one takes it would tear the session down one
///             frame after it started — the guard the rig's controller also
///             carries.
///         </item>
///     </list>
///     <para>
///         The commit is <see cref="IVstRack.MoveSlot" /> (DotCharter's
///         <c>TransportViewModel.MoveGuitarSlot</c>, RigLab's
///         <c>VstRackViewModel.MoveSlot</c>), which reorders, persists and
///         re-pushes the whole chain. The pure
///         index arithmetic is <see cref="TargetIndex" /> and is unit-tested —
///         a gap index is <c>[0, count]</c> and a position is <c>[0, count-1]</c>,
///         which is the one place a reorder silently lands one row off.
///     </para>
/// </remarks>
internal sealed class VstChainDragController : IDisposable
{
    /// <summary>The lane id. A drop is only honoured on the lane the drag
    /// started in, so a stray release over some other adorned surface cannot
    /// reorder the chain.</summary>
    private const string LaneId = "vst.chain";

    /// <summary>How far the pointer must travel before a press becomes a drag.
    /// Below this it is a click on the grip, which does nothing.</summary>
    private const double DragThresholdPx = 4.0;

    private readonly ItemsControl _items;
    private readonly Func<IVstRack?> _resolveRack;

    private bool _pressed;
    private bool _capturing;
    private Point _pressInContainer;
    private Point _pressInList;
    private Control? _container;
    private VstChainSlotViewModel? _slot;
    private FireDragSession? _session;

    public VstChainDragController(ItemsControl items, Func<IVstRack?> resolveRack)
    {
        _items = items;
        _resolveRack = resolveRack;

        _items.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        _items.PointerMoved += OnPointerMoved;
        _items.PointerReleased += OnPointerReleased;
        _items.PointerCaptureLost += OnPointerCaptureLost;
    }

    /// <summary>
    ///     Gap index → final position. A gap index is <c>[0, count]</c> (there is
    ///     one more gap than there are items) and a position is
    ///     <c>[0, count-1]</c>; dropping BELOW your own row means the row above
    ///     the gap has already shifted up.
    /// </summary>
    internal static int TargetIndex(int fromIndex, int insertionIndex, int count)
    {
        if (count <= 0) return 0;
        var target = insertionIndex > fromIndex ? insertionIndex - 1 : insertionIndex;
        return Math.Clamp(target, 0, count - 1);
    }

    public void Dispose()
    {
        _items.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        _items.PointerMoved -= OnPointerMoved;
        _items.PointerReleased -= OnPointerReleased;
        _items.PointerCaptureLost -= OnPointerCaptureLost;
        Cancel();
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Cancel();

        if (!e.GetCurrentPoint(_items).Properties.IsLeftButtonPressed) return;
        if (!IsGrip(e.Source as Visual)) return;

        var container = ContainerOf(e.Source as Visual);
        if (container?.DataContext is not VstChainSlotViewModel slot) return;

        _container = container;
        _slot = slot;
        _pressInContainer = e.GetPosition(container);
        _pressInList = e.GetPosition(_items);
        _pressed = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressed || _container == null) return;

        if (_session == null)
        {
            var now = e.GetPosition(_items);
            if (Math.Abs(now.X - _pressInList.X) < DragThresholdPx &&
                Math.Abs(now.Y - _pressInList.Y) < DragThresholdPx)
            {
                return;
            }

            _session = FireDragSession.Begin(_container, _pressInContainer, _slot);
            if (_session == null)
            {
                // No adorner layer (nothing laid out yet) — stay a click.
                _pressed = false;
                return;
            }

            _capturing = true;
            e.Pointer.Capture(_items);
            _capturing = false;
        }

        _session.MoveGhost(e.GetPosition(_session.Source));

        if (BuildLane() is { } lane) _session.Target(lane, e.GetPosition(lane.Host));
        else _session.ClearTarget();
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_session != null && _slot != null && _resolveRack() is { } rack)
        {
            var slot = _session.Slot;
            if (slot.IsValid && slot.LaneId == LaneId)
            {
                var from = rack.Chain.IndexOf(_slot);
                if (from >= 0)
                    rack.MoveSlot(from, TargetIndex(from, slot.Index, rack.Chain.Count));
            }
        }

        e.Pointer.Capture(null);
        Cancel();
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // Ours: a child shedding its capture as we take it must not end the drag.
        if (_capturing) return;
        Cancel();
    }

    private void Cancel()
    {
        _session?.Dispose();
        _session = null;
        _container = null;
        _slot = null;
        _pressed = false;
    }

    /// <summary>The lane the marker is drawn in: the item panel and one Rect per
    /// realized row. Null while nothing has been laid out — a popover is
    /// PREWARMED by a cancelled open, so this really does run before any
    /// arrange.</summary>
    private FireDragLane? BuildLane()
    {
        if (_items.ItemsPanelRoot is not { } panel) return null;

        var bounds = new List<Rect>();
        foreach (var child in panel.Children)
        {
            if (child.Bounds is { Width: <= 0 } or { Height: <= 0 }) continue;
            bounds.Add(child.Bounds);
        }

        return bounds.Count == 0 ? null : new FireDragLane(LaneId, panel, bounds, Orientation.Vertical);
    }

    /// <summary>True when the press landed on a slot's drag grip. The grip is
    /// the ONLY drag handle, because everything else in the row is a
    /// control.</summary>
    private static bool IsGrip(Visual? source)
    {
        for (var v = source; v != null; v = v.GetVisualParent())
        {
            if (v is StyledElement { Classes: var classes } && classes.Contains("FirePopoverGrip"))
                return true;
            if (v is Border { Classes: var rowClasses } && rowClasses.Contains("FirePopoverSlot"))
                return false;
        }

        return false;
    }

    /// <summary>The realized row that contains this visual: the ancestor whose
    /// visual parent is the items panel.</summary>
    private Control? ContainerOf(Visual? source)
    {
        var panel = _items.ItemsPanelRoot;
        if (panel == null) return null;

        for (var v = source; v != null; v = v.GetVisualParent())
        {
            if (ReferenceEquals(v.GetVisualParent(), panel) && v is Control control)
                return control;
        }

        return null;
    }
}
