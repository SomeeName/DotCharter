using System.Globalization;

namespace DotCharter.Services.Editing.ChordShapes;

/// <summary>
/// What a chord shape is CALLED wherever the user reads it: its name, or — when it has
///     none — its fret pattern ("x x 8 6 x x"). A placeholder such as "(no name)" on every unnamed
///     row told the user nothing; the frets are what tells two unnamed shapes apart.
/// </summary>
public static class ChordShapeNames
{
    public static bool IsUnnamed(ChordTemplate template) => string.IsNullOrWhiteSpace(template.Name);

    /// <summary>The frets low string to high, <c>x</c> for an unused string.</summary>
    public static string FretPattern(ChordTemplate template, int stringCount) =>
        string.Join(' ', Enumerable.Range(0, Math.Max(1, stringCount))
            .Select(s => template.Frets.TryGetValue(s, out var fret) ? fret.ToString(CultureInfo.InvariantCulture) : "x"));

    /// <summary>The name, or the fret pattern when there is none.</summary>
    public static string DisplayName(ChordTemplate template, int stringCount) =>
        IsUnnamed(template) ? FretPattern(template, stringCount) : template.Name;
}
