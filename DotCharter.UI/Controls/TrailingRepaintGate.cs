namespace DotCharter.UI.Controls;

/// <summary>What <see cref="TrailingRepaintGate.Request" /> tells its control to do.</summary>
internal enum RepaintDecision
{
    /// <summary>A repaint is already scheduled and will show this change too.</summary>
    None,

    /// <summary>Repaint now: the leading edge of a burst.</summary>
    Now,

    /// <summary>Inside the interval: schedule ONE repaint for its end, so the last change lands.</summary>
    ScheduleTrailing
}

/// <summary>
///     A repaint gate for DISCRETE changes that arrive in bursts — at most one repaint per
///     <see cref="IntervalMs" />, the first at once and the last guaranteed by one trailing
///     repaint. <see cref="RepaintThrottle" /> is the playhead's gate and has no trailing edge,
///     because the playhead ticks again; a selection does not, so dropping the last change of a
///     marquee burst would leave the map one change stale.
///     <para>
/// <c>MinimapOverviewControl</c> repainted on every selection change without a
///         throttle — a marquee drag repaints the whole map (and walks the selection) on every
///         pointer move. A mutable struct held by value; the clock is passed in.
///     </para>
/// </summary>
internal struct TrailingRepaintGate(long intervalMs)
{
    private long _lastRepaintMs;
    private bool _trailingPending;

    public long IntervalMs { get; } = intervalMs;

    /// <summary>A change arrived at <paramref name="nowMs" />.</summary>
    public RepaintDecision Request(long nowMs)
    {
        if (_trailingPending) return RepaintDecision.None;
        if (_lastRepaintMs == 0 || nowMs - _lastRepaintMs >= IntervalMs)
        {
            _lastRepaintMs = nowMs;
            return RepaintDecision.Now;
        }

        _trailingPending = true;
        return RepaintDecision.ScheduleTrailing;
    }

    /// <summary>The scheduled trailing repaint is running: the gate re-arms from it.</summary>
    public void TrailingFired(long nowMs)
    {
        _trailingPending = false;
        _lastRepaintMs = nowMs;
    }
}
