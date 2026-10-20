namespace DotCharter.UI.Controls;

/// <summary>
///     The one repaint gate for a control that is driven by the playhead: at
///     most one <c>InvalidateVisual</c> per <see cref="IntervalMs" />, however
///     often the source ticks.
///     <para>
///         <c>ProjectState.CurrentTimeMs</c> is published at up to 60 Hz, and a
///         strip that repaints on every publish repaints 60 times a second to
///         move a 2 px line. <c>MinimapOverviewControl</c> and
///         <c>LivePlaySectionGraph</c> would otherwise repaint their whole
///         strip on every tick. Both go through this struct, so the interval
///         and the semantics cannot drift apart.
///     </para>
///     <para>
///         A mutable struct, held by value in a field: no allocation, no
///         indirection, and the clock is passed in so the gate is testable
///         without waiting.
///     </para>
/// </summary>
internal struct RepaintThrottle(long intervalMs)
{
    /// <summary>~30 Hz — the rate the minimap repaints at.</summary>
    public const long DefaultIntervalMs = 33;

    private long _lastPassMs;

    public long IntervalMs { get; } = intervalMs;

    /// <summary>True — and the gate re-arms — when <paramref name="nowMs" /> is
    /// at least <see cref="IntervalMs" /> past the last pass. The first call
    /// always passes.</summary>
    public bool TryPass(long nowMs)
    {
        if (_lastPassMs != 0 && nowMs - _lastPassMs < IntervalMs) return false;
        _lastPassMs = nowMs;
        return true;
    }
}
