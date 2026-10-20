using Avalonia.Media;

namespace DotCharter.Rendering.Data;

/// <summary>
///     The colours the 3D stage lights paint for each showlight note. Tuned for the
///     dark sky, not copied from RSCharter: the old values made near pairs
///     (green / light green / lime, blue / light blue) unreadable as different
///     events. <c>ShowlightPaletteTests</c> pins a minimum CIE76 distance between
///     every pair in each lane and a minimum lightness for the beam colours.
/// </summary>
public static class ShowlightPalette
{
    public const int BeamsOff = 42;
    public const int FirstBeamPlus = 56;
    public const int LastBeamPlus = 59;
    public const int LasersOn = 66;
    public const int LasersOff = 67;

    /// <summary>The curtain body colour for a FOG_* note (24-35).</summary>
    public static Color FogColor(int note) => note switch
    {
        24 => Rgb(0x2B, 0xD9, 0x6B), // Green
        25 => Rgb(0xFF, 0x6F, 0xB5), // Pink
        26 => Rgb(0x16, 0xC9, 0xB4), // Teal
        27 => Rgb(0xFF, 0x8C, 0x1A), // Orange
        28 => Rgb(0x2F, 0x5B, 0xFF), // Blue
        29 => Rgb(0xBD, 0xF0, 0x1A), // Lime
        30 => Rgb(0xE0, 0x2C, 0xF0), // Magenta
        31 => Rgb(0x9C, 0xFF, 0xB0), // Light Green
        32 => Rgb(0xFF, 0x30, 0x40), // Red
        33 => Rgb(0x78, 0xC8, 0xFF), // Light Blue
        34 => Rgb(0xFF, 0xDC, 0x3C), // Yellow
        35 => Rgb(0x8A, 0x3C, 0xFF), // Purple
        _ => Rgb(0x9C, 0xFF, 0xB0)   // the catalogue default, FOG_LIGHT_GREEN
    };

    /// <summary>The curtain tip colour for a BEAMS_* note (48-59). BEAMS_OFF has
    /// no colour of its own — the sky uses a lighter shade of the fog instead.</summary>
    public static Color BeamColor(int note) => note switch
    {
        48 => Rgb(0x46, 0xFF, 0x7E), // Green
        49 => Rgb(0xFF, 0x4F, 0x9A), // Pink
        50 => Rgb(0x9C, 0xC2, 0xFF), // Light Blue
        51 => Rgb(0xFF, 0xA0, 0x40), // Orange
        52 => Rgb(0x3F, 0x6B, 0xFF), // Blue
        53 => Rgb(0xFF, 0xE8, 0x5A), // Yellow
        54 => Rgb(0xFF, 0x52, 0xF2), // Magenta
        55 => Rgb(0x2C, 0xE6, 0xFF), // Teal
        56 => Rgb(0xFF, 0x6A, 0x6A), // Red+
        57 => Rgb(0x5A, 0x86, 0xFF), // Blue+
        58 => Rgb(0xFF, 0xD2, 0x3C), // Yellow+
        59 => Rgb(0xB0, 0x60, 0xFF), // Purple+
        _ => Rgb(0, 0, 0)            // BEAMS_OFF
    };

    public static bool IsBeamPlus(int note) => note is >= FirstBeamPlus and <= LastBeamPlus;

    /// <summary>The colour a projected <c>RenderShowlight</c> carries: the fog or
    /// beam colour, white for LASERS_ON, black for the two "off" notes.</summary>
    public static Color ColorFromNote(int note) => note switch
    {
        >= 24 and <= 35 => FogColor(note),
        >= 48 and <= 59 => BeamColor(note),
        LasersOn => Rgb(0xF2, 0xF8, 0xFF),
        BeamsOff or LasersOff => Rgb(0, 0, 0),
        _ => FogColor(note)
    };

    /// <summary>The BEAMS_OFF tip: the fog colour lifted 45 % toward white, the
    /// same hue, so a beams-off sky reads as one colour.</summary>
    public static Color LighterShade(Color c) => Color.FromArgb(255,
        (byte)(c.R + (255 - c.R) * 0.45f),
        (byte)(c.G + (255 - c.G) * 0.45f),
        (byte)(c.B + (255 - c.B) * 0.45f));

    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);
}
