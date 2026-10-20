namespace DotCharter.Services.Gameplay;

/// <summary>
///     Pure math for the dynamic backing mix (0.21 game-feel): maps the live
///     play streak to a target gain for the player's stem, and smooths the
///     current gain toward it click-free. No audio, no clocks — the caller
///     injects the elapsed time, so it is deterministic and unit-testable. The
///     follower that owns the timer and the audio handle is
///     <c>StemDuckFollower</c> in the UI.
/// </summary>
public static class StemDuckMath
{
    /// <summary>Gain the stem ducks to on a fresh miss (streak 0). A fraction of
    /// unity, not silence — the part drops back in the mix without vanishing.</summary>
    public const double DuckFloor = 0.35;

    /// <summary>Streak at which the stem has fully swelled back to unity gain.</summary>
    public const int SwellStreak = 8;

    /// <summary>Smoothing time constant: the gain covers ~63% of the remaining
    /// distance to its target every this-many ms. Short enough that a miss is felt
    /// promptly, long enough that control-rate stepping never zippers.</summary>
    public const double TimeConstantMs = 260.0;

    /// <summary>
    ///     Target gain (in <see cref="DuckFloor"/>..1) for the current streak. A
    ///     miss resets the streak to 0 ⇒ <see cref="DuckFloor"/> (duck); consecutive
    ///     hits climb it linearly back to unity by <see cref="SwellStreak"/> (swell).
    /// </summary>
    public static double TargetGain(int streak)
    {
        if (streak <= 0) return DuckFloor;
        if (streak >= SwellStreak) return 1.0;
        return DuckFloor + (1.0 - DuckFloor) * (streak / (double)SwellStreak);
    }

    /// <summary>
    ///     One control-rate smoothing step: moves <paramref name="current"/> toward
    ///     <paramref name="target"/> by an exponential factor of the elapsed time,
    ///     so the ramp is frame-rate independent and click-free. A non-positive
    ///     <paramref name="dtMs"/> holds; a non-positive time constant snaps.
    /// </summary>
    public static double Approach(double current, double target, double dtMs,
        double timeConstantMs = TimeConstantMs)
    {
        if (dtMs <= 0) return current;
        if (timeConstantMs <= 0) return target;
        var alpha = 1.0 - Math.Exp(-dtMs / timeConstantMs);
        return current + (target - current) * alpha;
    }
}
