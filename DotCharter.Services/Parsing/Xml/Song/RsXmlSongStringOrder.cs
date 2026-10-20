using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Services.Parsing.Xml.Song;

/// <summary>
/// The reader half of the rs7string XML order: a 7-/8-string guitar's XML
///     spells indices 0..5 as E A D G B e and the extra strings after them
///     (<see cref="Rs7StringLayout" />); the chart counts lowest string first. Applied once
///     after the file is read, to everything string-indexed. A file with lowest-first
///     indices is not read specially — its indices mean something else now.
/// </summary>
internal static class RsXmlSongStringOrder
{
    /// <summary>The tuning offsets of the chart, from the file's offsets (index = file string).</summary>
    public static int[] TuningToChart(Arrangement arr, int[] fileOffsets) =>
        Rs7StringLayout.IsExtendedGuitar(arr)
            ? [.. Enumerable.Range(0, fileOffsets.Length).Select(d => fileOffsets[Rs7StringLayout.ToSng(d, fileOffsets.Length)])]
            : fileOffsets;

    /// <summary>Renumbers notes, chord notes and chord templates from the file's string
    /// order to the chart's. Does nothing for a 6-string guitar or a bass.</summary>
    public static void ToChart(Arrangement arr)
    {
        if (!Rs7StringLayout.IsExtendedGuitar(arr)) return;
        var count = arr.StringCount;
        foreach (var template in arr.ChordTemplates.Concat(arr.FretHandMuteTemplates))
        {
            template.Frets = Renumbered(template.Frets, count);
            template.Fingers = Renumbered(template.Fingers, count);
        }

        foreach (var level in arr.Levels)
        {
            foreach (var note in level.Notes) note.StringIndex = ToChart(note.StringIndex, count);
            foreach (var chord in level.Chords)
            {
                foreach (var chordNote in chord.ChordNotes) chordNote.StringIndex = ToChart(chordNote.StringIndex, count);
            }
        }
    }

    /// <summary>A column past the declared string count is ignored (spec 5).</summary>
    private static Dictionary<int, int> Renumbered(Dictionary<int, int> byString, int count) =>
        byString.Where(kv => kv.Key >= 0 && kv.Key < count).ToDictionary(kv => ToChart(kv.Key, count), kv => kv.Value);

    /// <summary>A string outside the arrangement keeps its index, as the package side does.</summary>
    private static int ToChart(int fileIndex, int count) =>
        fileIndex >= 0 && fileIndex < count ? Rs7StringLayout.ToDomain(fileIndex, count) : fileIndex;
}
