using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
/// WHERE a dragged item would land: a lane, and an insertion index INSIDE that
/// lane.
///
/// <para>Both halves are needed and neither is optional. The rig board is not
/// one list — the signal chain forks into parallel lanes, and the gear panel is
/// a separate lane again — so "index 2" means nothing on its own and "something
/// is being dragged" is not a drop target. A drop handler reads
/// <see cref="LaneId"/> to know WHICH chain it is editing and
/// <see cref="Index"/> to know where in it, and those two answers arrive
/// together or not at all.</para>
///
/// <para><see cref="Index"/> is an INSERTION index in <c>[0, count]</c>: 0 is
/// before the first item, <c>count</c> is after the last.</para>
/// </summary>
public readonly record struct FireDropSlot(string LaneId, int Index)
{
    /// <summary>No target — the pointer is over nothing droppable.</summary>
    public static readonly FireDropSlot None = new(string.Empty, -1);

    /// <summary>True when this names a real place to drop.</summary>
    public bool IsValid => Index >= 0 && !string.IsNullOrEmpty(LaneId);

    public override string ToString() => IsValid ? $"{LaneId}[{Index}]" : "none";
}

/// <summary>
/// One droppable lane: its id, the control whose coordinate space its item
/// rects are in, those rects in running order, and which way it runs.
/// </summary>
/// <param name="Id">Stable id — what a drop handler switches on.</param>
/// <param name="Host">The lane's own control; item rects are in ITS space.</param>
/// <param name="ItemBounds">The lane's items, in order.</param>
/// <param name="Orientation">Horizontal for a signal chain, Vertical for a
/// stacked list.</param>
public sealed record FireDragLane(
    string Id,
    Control Host,
    IReadOnlyList<Rect> ItemBounds,
    Orientation Orientation = Orientation.Horizontal);

/// <summary>
/// Pure slot geometry: pointer → insertion index, and index → the rect the
/// 6 px marker occupies. Free of any drag state so it is pinned in the default
/// gate (<c>FireSlotHitTestTests</c>).
/// </summary>
public static class FireSlotHitTest
{
    /// <summary>The insertion marker's thickness — the spec's "6px glowing
    /// violet bar".</summary>
    public const double MarkerThickness = 6.0;

    /// <summary>How far past the lane's items the marker extends, so it reads
    /// as a slot rather than as a short dash.</summary>
    public const double MarkerPadding = 4.0;

    /// <summary>
    /// The insertion index for a point in the lane's own space: the number of
    /// items whose CENTRE the point is past. An empty lane is always index 0.
    /// <para>Centres, not edges: an edge test leaves the gaps between items
    /// ambiguous and makes the marker flicker between two slots as the pointer
    /// crosses one.</para>
    /// </summary>
    public static int InsertionIndex(IReadOnlyList<Rect> items, Point point, Orientation orientation)
    {
        if (items.Count == 0) return 0;

        var along = orientation == Orientation.Horizontal ? point.X : point.Y;
        var index = 0;
        foreach (var item in items)
        {
            var center = orientation == Orientation.Horizontal
                ? item.X + item.Width / 2.0
                : item.Y + item.Height / 2.0;
            if (along < center) break;
            index++;
        }

        return index;
    }

    /// <summary>
    /// The rect the insertion marker occupies, in the lane's own space. It sits
    /// centred in the gap the item would open: before the first item, after the
    /// last, or midway between two neighbours. An empty lane puts it at the
    /// lane's leading edge.
    /// </summary>
    public static Rect MarkerBounds(
        IReadOnlyList<Rect> items, int index, Orientation orientation, Rect laneBounds,
        double thickness = MarkerThickness)
    {
        var horizontal = orientation == Orientation.Horizontal;

        double center;
        double crossStart;
        double crossEnd;

        if (items.Count == 0)
        {
            center = horizontal ? laneBounds.X : laneBounds.Y;
            crossStart = horizontal ? laneBounds.Y : laneBounds.X;
            crossEnd = horizontal ? laneBounds.Bottom : laneBounds.Right;
        }
        else
        {
            var clamped = Math.Clamp(index, 0, items.Count);
            var before = clamped > 0 ? items[clamped - 1] : (Rect?)null;
            var after = clamped < items.Count ? items[clamped] : (Rect?)null;

            center = (before, after) switch
            {
                ({ } b, { } a) => horizontal
                    ? (b.Right + a.X) / 2.0
                    : (b.Bottom + a.Y) / 2.0,
                (null, { } a) => horizontal ? a.X : a.Y,
                ({ } b, null) => horizontal ? b.Right : b.Bottom,
                _ => horizontal ? laneBounds.X : laneBounds.Y
            };

            crossStart = double.MaxValue;
            crossEnd = double.MinValue;
            foreach (var item in items)
            {
                crossStart = Math.Min(crossStart, horizontal ? item.Y : item.X);
                crossEnd = Math.Max(crossEnd, horizontal ? item.Bottom : item.Right);
            }
        }

        crossStart -= MarkerPadding;
        crossEnd += MarkerPadding;
        var crossLength = Math.Max(0.0, crossEnd - crossStart);

        return horizontal
            ? new Rect(center - thickness / 2.0, crossStart, thickness, crossLength)
            : new Rect(crossStart, center - thickness / 2.0, crossLength, thickness);
    }
}
