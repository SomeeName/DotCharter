namespace DotCharter.Tone.UI.Controls.Dsp;

/// <summary>
/// Pure drop-target math for reordering pedals on the board. No Avalonia types,
/// so it is unit-tested directly (<c>PedalboardVisualsTests</c>,
/// <c>PedalDragReflowTests</c>).
///
/// <para>The LIVE-REFLOW drag resolves its drop
/// slot with <see cref="ResolveRowSlot"/> against the <b>rest</b> centres — the
/// layout with the dragged pedal REMOVED, captured once when the drag starts.
/// That is react-beautiful-dnd's displacement model: the neighbours you see
/// flowing aside have already moved, so comparing against their (static) rest
/// positions is what makes the reflow stable instead of oscillating on a
/// boundary. A small hysteresis band on top kills the last of the jitter.</para>
/// </summary>
public static class PedalDragMath
{
    /// <summary>How far past a neighbour's rest centre the pointer must travel
    /// before the gap jumps to the next slot. Wide enough to kill boundary
    /// flutter, small enough that the reflow still feels immediate.</summary>
    public const double SlotHysteresisPx = 14.0;

    /// <summary>Lift scale for a normal stompbox chassis.</summary>
    public const double StompboxLiftScale = 1.04;

    /// <summary>Lift scale for a WIDE amp/cab chassis — a big box scaled like a
    /// stompbox looks like it is ballooning, so cap it and lean on the deeper
    /// floating shadow instead (the long-standing board rule).</summary>
    public const double WideLiftScale = 1.015;

    /// <summary>Chassis lift scale at full lift (<paramref name="wideChassis"/> =
    /// an amp head or a cabinet).</summary>
    public static double LiftScale(bool wideChassis)
        => wideChassis ? WideLiftScale : StompboxLiftScale;

    /// <summary>Interpolates the lift scale by a 0..1 <paramref name="lift"/>
    /// factor, so the drop can EASE the chassis back down instead of popping.</summary>
    public static double LiftScale(bool wideChassis, double lift)
        => 1.0 + (LiftScale(wideChassis) - 1.0) * Math.Clamp(lift, 0, 1);

    /// <summary>
    /// The slot the dragged pedal should occupy inside ONE row, given that row's
    /// rest centres (ascending) and the pointer X. Result is an INSERTION index
    /// in 0..<c>rowCenters.Count</c>.
    ///
    /// <para><paramref name="currentSlot"/> is last frame's answer (or -1 when
    /// the drag just started / just changed row): the pointer must cross a
    /// neighbour's centre by <paramref name="hysteresisPx"/> before the slot
    /// moves, so sitting exactly on a boundary can never thrash the reflow.</para>
    /// </summary>
    public static int ResolveRowSlot(
        double pointerX, IReadOnlyList<double> rowCenters, int currentSlot, double hysteresisPx)
    {
        var count = rowCenters.Count;
        if (count == 0) return 0;

        var k = currentSlot < 0
            ? ComputeInsertIndex(pointerX, rowCenters)
            : Math.Clamp(currentSlot, 0, count);

        while (k < count && pointerX > rowCenters[k] + hysteresisPx) k++;
        while (k > 0 && pointerX < rowCenters[k - 1] - hysteresisPx) k--;
        return k;
    }

    /// <summary>
    /// Index of the slot whose centre is closest to <paramref name="pointerX"/>.
    /// Returns -1 when there are no slots. Ties resolve to the lower index
    /// (leftmost), giving stable behaviour when the pointer sits exactly on a
    /// boundary.
    /// </summary>
    public static int ComputeDropIndex(double pointerX, IReadOnlyList<double> slotCenters)
    {
        if (slotCenters.Count == 0) return -1;

        var best = 0;
        var bestDist = double.MaxValue;
        for (var i = 0; i < slotCenters.Count; i++)
        {
            var d = pointerX - slotCenters[i];
            if (d < 0) d = -d;
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// Insertion slot for dropping NEW gear between existing pedals: the
    /// number of slot centres strictly left of <paramref name="pointerX"/>,
    /// i.e. 0 = before the first pedal … Count = after the last. An empty
    /// board yields 0 (the only slot). Unlike <see cref="ComputeDropIndex"/>
    /// (which reorders an EXISTING pedal onto the nearest occupied slot),
    /// this yields one of N+1 gaps.
    /// </summary>
    public static int ComputeInsertIndex(double pointerX, IReadOnlyList<double> slotCenters)
    {
        var index = 0;
        for (var i = 0; i < slotCenters.Count; i++)
        {
            if (pointerX > slotCenters[i]) index = i + 1;
        }

        return index;
    }
}
