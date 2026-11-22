namespace DotCharter.Services.Editing.Validation;

/// <summary>
///     When the validator calls two times "the same": both rounded to the nearest whole millisecond,
///     then at most <see cref="SlopMs" /> apart, so floating-point round-trips do not produce false
///     misses. <see cref="ArrangementValidationContext.IsOnToneChange" /> (I30) and the I30 fix
///     (<see cref="Tones.ToneChangeNudge" />) both go through it, so the fix matches exactly the
///     tone changes the warning flags and never moves a change onto a note the warning would flag.
/// </summary>
public static class ValidationTimeMatch
{
    /// <summary>How many whole milliseconds two rounded times may differ and still coincide.</summary>
    public const long SlopMs = 1;

    /// <summary><paramref name="timeMs" /> rounded to the whole millisecond the validator keys on.</summary>
    public static long RoundMs(double timeMs) => (long)Math.Round(timeMs);

    /// <summary>True when the validator treats <paramref name="aMs" /> and <paramref name="bMs" /> as one time.</summary>
    public static bool Coincide(double aMs, double bMs) => Math.Abs(RoundMs(aMs) - RoundMs(bMs)) <= SlopMs;
}
