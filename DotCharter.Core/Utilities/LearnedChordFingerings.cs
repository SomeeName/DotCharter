using System.Globalization;
using DotCharter.Core.Domain.Guitar;

namespace DotCharter.Core.Utilities;

/// <summary>
///     Chord shapes whose fingering the owner's finished charts settle differently
///     from <see cref="ChordTemplateFingerSetter" />'s heuristic, learned from the
/// TRAIN split of the chart corpus by <c>tools/chart-eval --learn</c>.
///     Still a pure function of the fret shape: the key is the shape moved down to
///     its lowest fretted fret, so one entry serves the shape anywhere on the neck.
///     <para>
///         The table is <see cref="LearnedChordFingeringData" />, a GENERATED file
///         holding aggregated counts only (a shape, a fingering, how many chords
///         and songs chose it) — never anything from a chart. Regenerate it with
///         <c>dotnet run tools/chart-eval/ChartEval.cs -- --corpus &lt;corpus.json&gt;
///         --learn DotCharter.Core/Utilities/LearnedChordFingeringData.cs</c>.
///     </para>
/// </summary>
public static class LearnedChordFingerings
{
    private static readonly Dictionary<string, LearnedChordFingering> Table =
        LearnedChordFingeringData.Shapes.ToDictionary(s => s.Key, StringComparer.Ordinal);

    /// <summary>Every shape the shipped table overrides.</summary>
    public static IReadOnlyCollection<LearnedChordFingering> All => Table.Values;

    /// <summary>
    ///     The table key of <paramref name="template" />'s frets: the string count,
    ///     then one token per string from the lowest — <c>x</c> unused, <c>o</c>
    ///     open, else the fret minus the shape's lowest fretted fret. Null when no
    ///     string is fretted.
    /// </summary>
    public static string? KeyOf(ChordTemplate template, int maxStrings)
    {
        var lowest = int.MaxValue;
        var strings = Math.Max(1, maxStrings);
        foreach (var (stringIndex, fret) in template.Frets)
        {
            if (fret > 0 && fret < lowest) lowest = fret;
            strings = Math.Max(strings, stringIndex + 1);
        }

        if (lowest == int.MaxValue) return null;
        var tokens = new string[strings];
        for (var s = 0; s < strings; s++)
        {
            tokens[s] = !template.Frets.TryGetValue(s, out var fret) ? "x"
                : fret == 0 ? "o"
                : (fret - lowest).ToString(CultureInfo.InvariantCulture);
        }

        return $"{strings}|{string.Join(",", tokens)}";
    }

    /// <summary>
    ///     The learned fingering for <paramref name="template" />'s shape, or null
    ///     when the heuristic is kept.
    /// </summary>
    public static LearnedChordFingering? For(ChordTemplate template, int maxStrings) =>
        KeyOf(template, maxStrings) is { } key && Table.TryGetValue(key, out var learned) ? learned : null;
}

/// <summary>
///     One learned shape: its <see cref="LearnedChordFingerings.KeyOf" /> key, the
///     finger per string from the lowest (<c>-1</c> none, <c>0</c> the thumb), and
///     the evidence — how many TRAIN chords of the shape chose it, out of how many,
///     in how many songs.
/// </summary>
public sealed record LearnedChordFingering(string Key, int[] Fingers, int Chords, int OfChords, int Songs);
