namespace DotCharter.UI.Input.Tools;

/// <summary>
///     Pure clamp math for a timeline drag that would carry part of its payload
///     before the start of the song — the highway/FHP/hand-shape twin of
///     <see cref="EventInteractionGestures.LyricDragBounds" />.
///     <para>
///         <b>The clamp is on the DELTA, never on the individual item.</b> A drag
///         moves a whole selection rigidly, so the only correct answer when the
///         EARLIEST member would land before zero is to shorten the delta for
///         everyone. Clamping each item on its own
///         (<c>Max(Zero, original + delta)</c>) piles every member that went
///         negative onto beat 0 while the rest keep moving — the selection's
///         internal spacing, which is the only thing a move is supposed to
///         preserve, is destroyed, and the preview is what the release commits.
///     </para>
///     <para>
///         It replaces a <c>new FractionalPosition(999999)</c> sentinel that two
///         tools each carried a copy of, tested with
///         <c>minTime.BeatId != 999999</c> — a real position at beat 999,999
///         would have disabled the clamp, and only two of the four drag paths had
///         it at all.
///     </para>
/// </summary>
public static class DragBounds
{
    /// <summary>
    ///     The earliest position among the given groups, or <c>null</c> when they
    ///     are all empty (nothing to bound, so nothing to clamp).
    /// </summary>
    public static FractionalPosition? Earliest(params IEnumerable<FractionalPosition>[] groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        FractionalPosition? earliest = null;
        foreach (var group in groups)
        {
            if (group == null) continue;
            foreach (var position in group)
            {
                if (earliest is not { } current || position < current)
                    earliest = position;
            }
        }

        return earliest;
    }

    /// <summary>
    ///     Shortens <paramref name="delta" /> just enough that
    ///     <paramref name="earliest" /> lands on zero rather than before it.
    ///     Returns it unchanged when nothing would go negative, and when there is
    ///     no payload to bound.
    /// </summary>
    public static FractionalPosition ClampDeltaToStart(FractionalPosition delta, FractionalPosition? earliest)
    {
        if (earliest is not { } min) return delta;
        return min + delta < FractionalPosition.Zero ? FractionalPosition.Zero - min : delta;
    }
}
