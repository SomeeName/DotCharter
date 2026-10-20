using Avalonia.Media;
using DotCharter.Rendering.Data;
using DotCharter.UI.Controls.Themes;

namespace DotCharter.UI.Controls.Layers.Helpers;

/// <summary>
///     Section colours. The 2D timeline's SECTIONS chips are the 3D view's section purple
///     (<see cref="HudEventBadges.SectionColor" />), so a section reads the same in both editors. The
///     song map's band colours sections by NAME — a stable hash over a palette read from
///     <see cref="ChartTheme" />, so every "chorus" in a song is one colour on the overview.
/// </summary>
public static class SectionColors
{
    /// <summary>How many colours names are spread over — the song map's brush list is in this order.</summary>
    public const int Count = 5;

    /// <summary>The palette slot for <paramref name="name" />: a case-insensitive string hash.</summary>
    public static int IndexFor(string name)
    {
        var hash = 0;
        foreach (var c in name) hash = hash * 31 + char.ToUpperInvariant(c);
        return (hash & 0x7fffffff) % Count;
    }

    /// <summary>The palette colour in slot <paramref name="index" />, in the song map's order.</summary>
    public static Color Base(int index) => index switch
    {
        0 => ChartTheme.SkyBlueColor,
        1 => ChartTheme.MagentaColor,
        2 => ChartTheme.GoldColor,
        3 => ChartTheme.SpringGreenColor,
        _ => ChartTheme.CyanColor
    };

    /// <summary>A timeline section chip's fill: the 3D view's purple, a step lighter when emphasised
    /// (hovered, selected, or the section the playhead is in).</summary>
    public static Color ChipFill(bool emphasised) => emphasised
        ? Lift(HudEventBadges.SectionColor, 8)
        : HudEventBadges.SectionColor;

    /// <summary>A section's drop line down the lanes: the chip's purple, quieter.</summary>
    public static Color DropLine() => Color.FromArgb(140, HudEventBadges.SectionColor.R,
        HudEventBadges.SectionColor.G, HudEventBadges.SectionColor.B);

    private static Color Lift(Color c, int by) => Color.FromArgb(255, (byte)Math.Min(255, c.R + by),
        (byte)Math.Min(255, c.G + by), (byte)Math.Min(255, c.B + by));
}
