using System.Globalization;
using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Services.Export.Sng;

/// <summary>
///     The widths the SNG container fixes, which a chart can exceed: each section's string mask
///     holds one entry per difficulty level, a note's neighbour links are 16-bit ids into its
///     level, and a chord template's frets and fingers are signed bytes.
///     <c>ChartExportValidator</c> reports every one as an export error; the mapper refuses or
///     clamps on its own so a crafted chart never indexes past a table or wraps a value silently.
/// </summary>
internal static class SngFormatLimits
{
    /// <summary>Difficulty levels the per-section string-mask table holds.</summary>
    public const int MaxLevels = 36;

    /// <summary>Notes plus chords one level can number: note ids are signed 16-bit.</summary>
    public const int MaxSoundsPerLevel = short.MaxValue;

    /// <summary>The highest finger a chord template can name (the index finger is 1, the little finger 4).</summary>
    public const int MaxFinger = 4;

    /// <summary>The highest fret a signed-byte slot holds; the validator's own playable range is lower.</summary>
    public const int MaxStoredFret = sbyte.MaxValue;

    /// <summary>Why this arrangement cannot be written, or null when it can.</summary>
    public static string? ExportRefusal(Arrangement arrangement) =>
        arrangement.Levels.Count > MaxLevels
            ? string.Create(CultureInfo.InvariantCulture,
                $"'{arrangement.Name}' has {arrangement.Levels.Count} difficulty levels; a song file holds at most {MaxLevels}.")
            : Rs7StringLayout.ExportRefusal(arrangement);
}
