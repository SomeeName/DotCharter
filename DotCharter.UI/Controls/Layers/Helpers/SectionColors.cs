using Avalonia.Media;
using DotCharter.Rendering.Data;

namespace DotCharter.UI.Controls.Layers.Helpers;

/// <summary>
///     Colours for the 2D timeline's section chips and drop lines, based on the 3D view's
///     <see cref="HudEventBadges.SectionColor" />.
/// </summary>
public static class SectionColors
{
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
