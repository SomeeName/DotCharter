namespace DotCharter.Services.Editing.Snapping;

/// <summary>
///     Step the playhead to the previous / next GRID LINE.
///     <para>
///         The editor had no such verb: <c>←</c>/<c>→</c> move a fixed number of
///         MILLISECONDS (100, or 50/500 with a modifier), so on a 6/8 bar or
///         after a tempo change the playhead never lines up with the grid the
///         notes snap to. EOF puts this on <c>Up</c>/<c>Down</c>
///         (<c>editor.c:1890-1893</c>, <c>:1993-1996</c>) and rscharter on
///         <c>Ctrl+Alt+←/→</c>; it is the first row of the audit's frequent-
///         operations table and the only one DotCharter answered with "none".
///     </para>
///     <para>
///         Deliberately built on <c>ITempoService.SnapToGrid</c> — the NEAREST
///         grid line — rather than on <c>ChartEditorViewModel.SnapToGrid</c>,
///         which magnet-snaps onto nearby SOUNDS. Seeking to a sound is
///         <c>PgUp</c>/<c>PgDn</c>'s job and already exists; this key is about
///         the grid.
///     </para>
/// </summary>
public static class GridNavigation
{
    /// <summary>Half a millisecond: positions are ms doubles, and "already on
    /// the line" must not be mistaken for "just past it" or the key stalls.</summary>
    private const double OnTheLineEpsilonMs = 0.5;

    /// <summary>
    ///     <paramref name="nearest" /> / <paramref name="nearestMs" /> are the
    ///     result of <c>ITempoService.SnapToGrid</c> at
    ///     <paramref name="timeMs" />. Returns the first grid line strictly
    ///     ahead of (<paramref name="direction" /> &gt; 0) or behind it, never
    ///     before the start of the chart.
    /// </summary>
    public static FractionalPosition Step(
        double timeMs,
        FractionalPosition nearest,
        double nearestMs,
        int gridDivisor,
        int direction)
    {
        var divisor = Math.Max(1, gridDivisor);
        var step = new Fraction(direction >= 0 ? 1 : -1, divisor);

        // The nearest line already answers the question when it lies on the far
        // side of the playhead; only when we are ON (or past) it does the
        // playhead have to move a whole grid step.
        var alreadyAhead = direction >= 0
            ? nearestMs > timeMs + OnTheLineEpsilonMs
            : nearestMs < timeMs - OnTheLineEpsilonMs;

        var target = alreadyAhead ? nearest : nearest + step;
        return target < FractionalPosition.Zero ? FractionalPosition.Zero : target;
    }
}
