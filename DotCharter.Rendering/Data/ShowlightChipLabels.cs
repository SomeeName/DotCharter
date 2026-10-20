using System.Globalization;

namespace DotCharter.Rendering.Data;

/// <summary>
///     The localized words for the editor 3D preview's show-light chip:
///     <c>Fog: Green · Beams: off · Lasers: on</c>. This assembly cannot reach the
///     resx (it references Core only), so the UI builds one of these per UI
///     language and hands it in through <see cref="SnapshotUpdateContext" />;
///     <see cref="FrameSnapshot" /> resolves the state at the playhead and calls
///     <see cref="Compose" /> only when that state changes.
///     <c>Format</c> has three placeholders (fog, beams, lasers); <c>Names</c>
///     maps a showlight NOTE to its localized colour name (fog 24-35, beams
///     48-59; plus beams already carry their "+").
/// </summary>
public sealed record ShowlightChipLabels(
    string Format,
    string On,
    string Off,
    string None,
    IReadOnlyDictionary<int, string> Names)
{
    public string Compose(int? fogNote, int? beamNote, int? laserNote)
    {
        var fog = fogNote is { } f ? Name(f) : None;
        var beams = beamNote is { } b && b != ShowlightPalette.BeamsOff ? Name(b) : Off;
        var lasers = laserNote == ShowlightPalette.LasersOn ? On : Off;
        return string.Format(CultureInfo.InvariantCulture, Format, fog, beams, lasers);
    }

    private string Name(int note) => Names.TryGetValue(note, out var n) ? n : None;
}

/// <summary>Where the chip sits: the top-left corner, sized off the frame height
/// like the rest of the HUD so it reads the same in a docked preview and at 4K.</summary>
public static class ShowlightChipLayout
{
    /// <summary>Text scale against the 64 px atlas: ~20 px glyphs on a 900-row frame.</summary>
    public static float TextScale(int height) => Math.Clamp(height, 300, 2400) * 0.022f / 64f;

    public static float Margin(int height) => Math.Clamp(height, 300, 2400) * 14f / 900f;

    public static float PadX(int height) => Math.Clamp(height, 300, 2400) * 12f / 900f;

    public static float PlateHeight(int height) => Math.Clamp(height, 300, 2400) * 34f / 900f;

    /// <summary>The bottom of the plate plus a gap: the HUD's section, phrase,
    /// tone and lyric lines start below this while the chip shows.</summary>
    public static float ReservedTop(int height) => Margin(height) + PlateHeight(height) + Margin(height) * 0.5f;

    /// <summary>Top-left of the text run inside the plate.</summary>
    public static (float X, float Y) TextOrigin(int height)
    {
        var lineHeight = 64f * TextScale(height);
        return (Margin(height) + PadX(height), Margin(height) + (PlateHeight(height) - lineHeight) * 0.5f);
    }
}
