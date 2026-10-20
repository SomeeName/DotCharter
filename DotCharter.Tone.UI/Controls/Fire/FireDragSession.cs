using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
/// One in-flight reorder drag, drawn in the <see cref="AdornerLayer"/>: the
/// dragged item rendered FULL SIZE following the cursor (scale .96, rotate
/// −1.6°, one very deep soft shadow) plus a <see cref="FireDropMarker"/> on the
/// exact insertion slot.
///
/// <para><b>It is not <c>DragDrop</c> and there is no OS drag image.</b> The
/// spec is explicit — "never a browser-default drag image" — and the ghost has
/// to be the real card, at its real size, so the board reads as the item having
/// been picked up rather than as a cursor decoration. The caller keeps the
/// pointer captured and drives this; nothing here touches input.</para>
///
/// <para><b>The target is a LANE and a SLOT, never "something is being
/// dragged".</b> The rig board forks into parallel lanes and the gear panel is
/// a lane of its own, so an index with no lane cannot be acted on. See
/// <see cref="FireDropSlot"/>; the geometry is the pure
/// <see cref="FireSlotHitTest"/>.</para>
///
/// <para>Both adorners are positioned inside a <see cref="Canvas"/> adorned to
/// a real element — the ghost's to the dragged item, the marker's to the lane —
/// so every coordinate this class handles is in THAT element's own space and
/// survives the board reflowing underneath the drag. Nothing is expressed in
/// window coordinates.</para>
/// </summary>
public sealed class FireDragSession : IDisposable
{
    /// <summary>The ghost's scale and rotation, straight from the spec's
    /// "scale .96, rotate −1.6deg".</summary>
    public const double GhostScale = 0.96;

    /// <inheritdoc cref="GhostScale"/>
    public const double GhostRotationDegrees = -1.6;

    private readonly AdornerLayer _layer;
    private readonly Canvas _ghostHost;
    private readonly Control _ghost;
    private readonly Canvas _markerHost;
    private readonly FireDropMarker _marker;
    private readonly Point _grab;

    private FireDragLane? _lane;
    private bool _disposed;

    private FireDragSession(
        AdornerLayer layer, Control source, Point grab, Size size, object? payload)
    {
        _layer = layer;
        _grab = grab;
        Source = source;
        Payload = payload;
        Slot = FireDropSlot.None;

        // A source may name the PART to ghost (class FireDragGhost): the rig's node
        // card without the wire and arrowhead its item container also holds —
        // dragging a pedal must not carry the lane's arrow along with it.
        // The grab point moves with it so the card stays
        // under the pointer.
        var part = GhostPartOf(source);
        if (part is not null && part.Bounds.Width > 0 && part.Bounds.Height > 0)
        {
            var offset = part.TranslatePoint(new Point(0, 0), source) ?? default;
            _grab = new Point(grab.X - offset.X, grab.Y - offset.Y);
            _ghost = BuildGhost(part, part.Bounds.Size);
        }
        else
        {
            _ghost = BuildGhost(source, size);
        }
        _ghostHost = Adorner(source);
        _ghostHost.Children.Add(_ghost);

        _marker = new FireDropMarker { IsVisible = false };
        _markerHost = Adorner(source);
        _markerHost.Children.Add(_marker);

        _layer.Children.Add(_markerHost); // under the ghost
        _layer.Children.Add(_ghostHost);

        MoveGhost(grab);
    }

    /// <summary>The control being dragged. Ghost coordinates are in ITS
    /// space.</summary>
    public Control Source { get; }

    /// <summary>Whatever the caller needs handed back on drop — the view model
    /// of the dragged unit, typically.</summary>
    public object? Payload { get; }

    /// <summary>The slot the marker is currently showing, or
    /// <see cref="FireDropSlot.None"/>.</summary>
    public FireDropSlot Slot { get; private set; }

    /// <summary>Marker/ghost updates that actually changed something. Test seam
    /// for the "only invalidate when the rendered state changed" rule — a drag
    /// ticks at pointer rate over a lane whose slots are tens of pixels
    /// wide.</summary>
    internal int SlotUpdates { get; private set; }

    /// <summary>
    /// Starts a drag. <paramref name="grabPointInSource"/> is where inside the
    /// item the pointer went down, so the ghost stays under the finger instead
    /// of snapping its corner to the cursor.
    /// <para>Returns null when the source is not in a visual tree that has an
    /// adorner layer, or has no size yet — both mean there is nothing to
    /// ghost.</para>
    /// </summary>
    public static FireDragSession? Begin(Control source, Point grabPointInSource, object? payload = null)
    {
        var layer = AdornerLayer.GetAdornerLayer(source);
        var size = source.Bounds.Size;
        if (layer is null || size.Width <= 0 || size.Height <= 0) return null;
        return new FireDragSession(layer, source, grabPointInSource, size, payload);
    }

    /// <summary>
    /// Moves the ghost. <paramref name="pointerInSource"/> is the pointer in
    /// the SOURCE's coordinate space — <c>e.GetPosition(session.Source)</c> —
    /// which keeps working while the pointer is captured and while the board
    /// reflows underneath.
    /// </summary>
    public void MoveGhost(Point pointerInSource)
    {
        if (_disposed) return;
        Canvas.SetLeft(_ghost, pointerInSource.X - _grab.X);
        Canvas.SetTop(_ghost, pointerInSource.Y - _grab.Y);
    }

    /// <summary>
    /// Declares the lane under the pointer and resolves the slot inside it,
    /// moving the marker there. <paramref name="pointInLane"/> is in
    /// <see cref="FireDragLane.Host"/>'s own space.
    /// <para>Returns the resolved slot. Re-targeting the SAME slot does no
    /// work.</para>
    /// </summary>
    public FireDropSlot Target(FireDragLane lane, Point pointInLane)
    {
        if (_disposed) return FireDropSlot.None;

        var index = FireSlotHitTest.InsertionIndex(lane.ItemBounds, pointInLane, lane.Orientation);
        var slot = new FireDropSlot(lane.Id, index);
        if (slot == Slot && ReferenceEquals(lane.Host, _lane?.Host)) return slot;

        Slot = slot;
        _lane = lane;
        SlotUpdates++;

        AdornerLayer.SetAdornedElement(_markerHost, lane.Host);

        var bounds = FireSlotHitTest.MarkerBounds(
            lane.ItemBounds, index, lane.Orientation, new Rect(lane.Host.Bounds.Size));

        _marker.Orientation = lane.Orientation;
        _marker.Width = bounds.Width;
        _marker.Height = bounds.Height;
        Canvas.SetLeft(_marker, bounds.X);
        Canvas.SetTop(_marker, bounds.Y);
        _marker.IsVisible = bounds is { Width: > 0, Height: > 0 };

        return slot;
    }

    /// <summary>The pointer left every lane: no slot, no marker. The ghost
    /// stays — the item is still being dragged, it just has nowhere to
    /// land.</summary>
    public void ClearTarget()
    {
        if (_disposed || !Slot.IsValid) return;
        Slot = FireDropSlot.None;
        _lane = null;
        SlotUpdates++;
        _marker.IsVisible = false;
    }

    /// <summary>Ends the drag and takes both adorners down. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _layer.Children.Remove(_ghostHost);
        _layer.Children.Remove(_markerHost);
        _ghostHost.Children.Clear();
        _markerHost.Children.Clear();
    }

    /// <summary>Test seam: the ghost's top-left inside the source's space.</summary>
    internal Point GhostOrigin => new(Canvas.GetLeft(_ghost), Canvas.GetTop(_ghost));

    /// <summary>Test seam: the marker, so a smoke test can measure the real
    /// bar rather than trust a number this class computed.</summary>
    internal FireDropMarker Marker => _marker;

    /// <summary>Test seam: the ghost, whose size must equal the source's.</summary>
    internal Control Ghost => _ghost;

    /// <summary>A canvas covering exactly <paramref name="adorned"/>, with
    /// clipping OFF so the ghost's shadow and its rotated corners are not
    /// sliced at the item's edge.</summary>
    private static Canvas Adorner(Control adorned)
    {
        var canvas = new Canvas { IsHitTestVisible = false };
        AdornerLayer.SetAdornedElement(canvas, adorned);
        AdornerLayer.SetIsClipEnabled(canvas, false);
        return canvas;
    }

    /// <summary>The descendant wearing <c>FireDragGhost</c>, or null when the
    /// whole source is the ghost. Internal for the drag tests.</summary>
    internal static Control? GhostPartOf(Control source) =>
        source.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Classes.Contains("FireDragGhost"));

    private static Control BuildGhost(Control source, Size size)
    {
        var ghost = new Border
        {
            Width = size.Width,
            Height = size.Height,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            // FULL SIZE, and the real card: a VisualBrush of the source itself,
            // not a screenshot and not a stand-in rectangle.
            Background = new VisualBrush { Visual = source },
            RenderTransformOrigin = RelativePoint.Center,
            RenderTransform = new TransformGroup
            {
                Children =
                {
                    new ScaleTransform(GhostScale, GhostScale),
                    new RotateTransform(GhostRotationDegrees)
                }
            }
        };

        // The "large shadow" is the overlay-class token — a dragged card is
        // floating over the whole board, not sitting on it.
        ghost.Bind(Border.BoxShadowProperty, new DynamicResourceExtension("FireShadowScreen"));
        return ghost;
    }
}
