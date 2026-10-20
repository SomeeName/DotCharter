using Avalonia.Media;

namespace DotCharter.Rendering.Data;

/// <summary>
///     Rocksmith-style resume rewind: unpausing backs the song up by a lead-in, so
///     a stretch that was ALREADY judged plays a second time. Those sounds can never
///     be judged again (the Live Play matcher's cursor is past them — they neither
///     score nor miss), so the highway draws them DIMMED: dead notes.
///     <para>
///     Pure, stateless and unit-tested — the renderer needs no mode flag, because
///     the predicate is false everywhere except inside a replay.
///     </para>
/// </summary>
public static class DeadNoteMath
{
    /// <summary>How much of the note colour survives (RGB) and how much of its alpha.</summary>
    public const float DimRgb = 0.35f;
    public const float DimAlpha = 0.55f;

    /// <summary>
    ///     A note AHEAD of the playhead yet already resolved — only possible during a
    ///     resume-rewind replay.
    ///     <para>
    ///     The <c>noteTimeMs &gt;= playheadMs</c> half is ESSENTIAL: without it a long
    ///     sustain still ringing UNDER the playhead (its head is behind the resolved
    ///     horizon by definition) would go dark during NORMAL play.
    ///     </para>
    /// </summary>
    public static bool IsDead(double noteTimeMs, double playheadMs, double resolvedThroughMs)
        => noteTimeMs >= playheadMs && noteTimeMs <= resolvedThroughMs;

    /// <summary>Dimmed variant of a note/tail colour — still readable (the player
    /// must see what is coming), unmistakably inert.</summary>
    public static Color Dim(Color c) => Color.FromArgb(
        (byte)(c.A * DimAlpha), (byte)(c.R * DimRgb), (byte)(c.G * DimRgb), (byte)(c.B * DimRgb));
}
