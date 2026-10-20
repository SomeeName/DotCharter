using Avalonia;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
///     The pure geometry half of <see cref="FireChipStrip" /> — the song lists'
///     `L R B V` arrangement-chip cluster as ONE drawn control instead of an
///     <c>ItemsControl</c> over per-chip <c>Border</c>s.
///     <para>
///         The numbers mirror the `FireTag Square` mark's tokens
///         (`FireTagSquareSize` 22, `FireSpaceSm` 6, `FireSizeTag` 9,
///         `FireRadiusTag` 5) — a chip drawn here must be indistinguishable from
///         one built from the class, because the detail rails still build
///         Border-based tags. <c>FireChipStripTests</c> pins these constants
///         against the token dictionary so the two cannot drift apart.
///     </para>
/// </summary>
public static class FireChipStripGeometry
{
    /// <summary>`FireTagSquareSize` — the fixed square, because letters have
    /// different advance widths and the columns right of the chip cluster must
    /// keep lining up down a 2,000-row list.</summary>
    public const double DefaultChipSize = 22;

    /// <summary>`FireSpaceSm` — the gap both row templates used between chips.</summary>
    public const double DefaultGap = 6;

    /// <summary>`FireSizeTag` — the letter's font size.</summary>
    public const double FontSize = 10.5;

    /// <summary>`FireRadiusTag` — the chip's corner radius.</summary>
    public const double CornerRadius = 4;

    /// <summary>Width of a strip of <paramref name="count" /> chips; 0 for none.</summary>
    public static double RowWidth(int count, double chipSize, double gap)
        => count <= 0 ? 0 : count * chipSize + (count - 1) * gap;

    /// <summary>The <paramref name="index" />th chip's square, top-aligned.</summary>
    public static Rect ChipRect(int index, double chipSize, double gap)
        => new(index * (chipSize + gap), 0, chipSize, chipSize);
}
