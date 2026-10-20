namespace DotCharter.Rendering.Data;

/// <summary>
///     Pure math for the Live Play timing-tier flash (PERFECT / GREAT / GOOD),
///     lifted out of the render driver and the HUD so the one rule that must
///     never bend — tier None never flashes — lives in a single, unit-tested
///     place. Works in raw bytes (the <c>LiveTimingTier</c> ordinal, 0 = None)
///     because DotCharter.Rendering does not, and must not, reference the
///     Services layer that owns the enum.
/// </summary>
public static class TimingFlashMath
{
    /// <summary>How long a tier flash stays up after the hit, in SONG ms — so it
    /// freezes with the transport when paused. Matched to the EARLY/LATE flash so
    /// a hit that trips both (e.g. Great AND Early) fades out together.</summary>
    public const double FlashDurationMs = 600.0;

    /// <summary>
    ///     The sticky <c>(tier, time)</c> the driver should hold after seeing one
    ///     judged event. A tier of 0 (<c>None</c> — a miss or a CQT pull-verified
    ///     hit, neither of which has an honest attack time) leaves the previous
    ///     flash UNTOUCHED rather than clearing it, so a miss landing right after a
    ///     Perfect never blanks the cue. This is the same gate the driver already
    ///     applies to the early/late direction, restated once so it is testable
    ///     without standing up the whole render loop.
    /// </summary>
    public static (byte Tier, double AtMs) NextSticky(
        byte currentTier, double currentAtMs, byte eventTier, double eventAtMs)
        => eventTier == 0 ? (currentTier, currentAtMs) : (eventTier, eventAtMs);

    /// <summary>
    ///     Whether the HUD should draw the tier flash this frame: never for tier 0
    ///     (None), and only while the flash is still inside its fade window — age in
    ///     <c>[0, <see cref="FlashDurationMs"/>]</c>. A negative age means the
    ///     transport seeked back behind the hit, which is also nothing to show.
    /// </summary>
    public static bool ShouldFlash(byte tier, double ageMs)
        => tier != 0 && ageMs >= 0 && ageMs <= FlashDurationMs;
}
