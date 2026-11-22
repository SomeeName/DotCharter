using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Primitives;

namespace DotCharter.Core.Utilities;

/// <summary>How a nudge of a whole selection in time ends.</summary>
public enum TimeShiftOutcome
{
    /// <summary>There is nothing to move.</summary>
    Empty,

    /// <summary>Every item moves by the full amount.</summary>
    Moved,

    /// <summary>The full amount would cross the first beat, so the group moves only as far as the first beat.</summary>
    Clamped,

    /// <summary>The earliest item already sits on the first beat and the shift is earlier: nothing can move.</summary>
    AtStart
}

/// <summary>What a shift of a selection does: the outcome and the position mapping to apply to EVERY item.</summary>
public readonly record struct TimeShiftPlan(TimeShiftOutcome Outcome, Func<FractionalPosition, FractionalPosition> Map);

/// <summary>
///     Pure time-shift arithmetic for nudging chart items earlier or later. No clock, no
///     services: the grid (a divisor, or the beat map) is injected.
///     <para>
///         A grid step moves by an EXACT fraction of a beat, so an item that sits off the grid
///         keeps its off-grid offset (snapping would lose it). A millisecond shift goes through
///         the beat map and is stored on a 1/19200-beat grid, which is finer than a millisecond
///         for any beat shorter than 19.2 s.
///     </para>
/// </summary>
public static class TimeShift
{
    private const int MillisecondDenominator = 19200;
    private const int DefaultGridDivisor = 16;

    /// <summary>Moves <paramref name="position" /> by <paramref name="steps" /> grid lines (negative = earlier).</summary>
    public static FractionalPosition ByGridSteps(FractionalPosition position, int steps, int gridDivisor) =>
        position + new Fraction(steps, gridDivisor > 0 ? gridDivisor : DefaultGridDivisor);

    /// <summary>Moves <paramref name="position" /> by <paramref name="deltaMs" /> along the beat map.</summary>
    public static FractionalPosition ByMilliseconds(FractionalPosition position, double deltaMs,
        IList<BeatMarker> beats)
    {
        var target = position.GetPositionMs(beats) + deltaMs;
        if (!TimelineMath.TryBeatOffsetFromMs(target, beats, out var beatIndex, out var offsetBeats))
            return position;

        var wholeBeats = Math.Floor(offsetBeats);
        var ticks = (long)Math.Round((offsetBeats - wholeBeats) * MillisecondDenominator);
        return new FractionalPosition(beatIndex + (int)wholeBeats, new Fraction(ticks, MillisecondDenominator));
    }

    /// <summary>
    ///     Plans a millisecond shift, reducing the millisecond delta for EVERY
    ///     position when the earliest member would cross the first beat.
    /// </summary>
    public static TimeShiftPlan PlanMilliseconds(IReadOnlyList<FractionalPosition> starts, double deltaMs,
        IList<BeatMarker> beats)
    {
        var plan = Plan(starts, p => ByMilliseconds(p, deltaMs, beats));
        if (plan.Outcome != TimeShiftOutcome.Clamped) return plan;

        var earliest = starts.Min();
        var clampedDeltaMs = FractionalPosition.Zero.GetPositionMs(beats) - earliest.GetPositionMs(beats);
        return new TimeShiftPlan(TimeShiftOutcome.Clamped, p => ByMilliseconds(p, clampedDeltaMs, beats));
    }

    /// <summary>
    ///     Decides how a beat-relative <paramref name="shift" /> applies to a group whose members start at
    ///     <paramref name="starts" />. The group moves rigidly in beats; an
    ///     earlier shift that would cross the first beat is reduced so the earliest member lands
    ///     exactly on it.
    /// </summary>
    public static TimeShiftPlan Plan(IReadOnlyList<FractionalPosition> starts,
        Func<FractionalPosition, FractionalPosition> shift)
    {
        if (starts.Count == 0) return new TimeShiftPlan(TimeShiftOutcome.Empty, static p => p);

        var earliest = starts[0];
        foreach (var start in starts)
            earliest = FractionalPosition.Min(earliest, start);

        var shiftedEarliest = shift(earliest);
        if (shiftedEarliest >= earliest || shiftedEarliest >= FractionalPosition.Zero)
            return new TimeShiftPlan(TimeShiftOutcome.Moved, shift);
        if (earliest <= FractionalPosition.Zero) return new TimeShiftPlan(TimeShiftOutcome.AtStart, static p => p);
        return new TimeShiftPlan(TimeShiftOutcome.Clamped, p => p - earliest);
    }
}
