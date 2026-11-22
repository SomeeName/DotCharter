using System.Globalization;
using System.Xml.Linq;
using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Services.Parsing.Xml.Song;

/// <summary>
/// The reader half of the rs7string XML order: a 7-/8-string guitar's XML
///     spells indices 0..5 as E A D G B e and the extra strings after them
///     (<see cref="Rs7StringLayout" />); the chart counts lowest string first. Applied once
///     after an unmarked file is read, to everything string-indexed. An RSXR marker
///     selects thickest-first indices and tuning measured from each string's standard note.
///     An unmarked 5-/6-string bass is spelled the same way (0-3 = E A D G, 4 = low B, 5 = high C), but its
///     string attributes alone cannot identify that order (a stock bass lists string4/string5 too): only a file whose
///     <c>&lt;tuning strings="5|6"&gt;</c> says so, which this writer always emits for a bass, is
///     read as one.
/// </summary>
internal static class RsXmlSongStringOrder
{
    /// <summary>Validates an RSXR marker and its instrument's required string count.</summary>
    public static int? RsxrStringCount(XElement tuning, Arrangement arr)
    {
        if (tuning.Attribute(E.ExtendedRange) is not { } marker) return null;
        if (marker.Value != RsxrStringLayout.FormatVersion.ToString(CultureInfo.InvariantCulture))
            throw new InvalidDataException($"Unsupported extendedRange value '{marker.Value}'.");
        var value = tuning.Attribute(E.Strings)?.Value;
        if (!int.TryParse(value, CultureInfo.InvariantCulture, out var count))
            throw new InvalidDataException($"RS Extended Range tuning requires a valid strings count; found '{value ?? "missing"}'.");
        var valid = arr.ArrangementType is ArrangementType.Bass ? count is 5 or 6 : count is 7 or 8;
        if (!valid)
            throw new InvalidDataException($"RS Extended Range strings='{value}' is invalid for {arr.ArrangementType}.");
        return count;
    }

    /// <summary>Converts sounding RSXR bass offsets to the chart's reference row.</summary>
    public static int[] RsxrTuningToChart(Arrangement arr, int[] offsets)
    {
        if (arr.ArrangementType is not ArrangementType.Bass) return offsets;
        var standard = RsxrStringLayout.StandardMidi(arr.ArrangementType, arr.StringCount);
        return [.. offsets.Select((offset, i) => offset + standard[i] - StandardOpenStrings.BassLowEMidi -
            StandardOpenStrings.SemitonesAboveLowE(arr.StringCount, i))];
    }

    /// <summary>Restores thickest-first indices on the projected export copy.</summary>
    public static void ToRsxr(Arrangement arr)
    {
        var count = arr.StringCount;
        var names = arr.Tuning;
        arr.Tuning = [.. Enumerable.Range(0, count).Select(i =>
        {
            var slot = RsxrStringLayout.SlotOf(i, count, arr.ArrangementType);
            return slot < names.Length ? names[slot] : "";
        })];
        arr.TuningPitches = [.. TuningToChart(arr, TuningOffsetResolution.Resolve(arr).ToArray(),
            Rs7StringLayout.IsExtendedBass(arr)).Select(offset => new TuningOffset(offset))];
        int StringOf(int slot) => slot >= 0 && slot < count ? RsxrStringLayout.StringOf(slot, count, arr.ArrangementType) : slot;
        foreach (var template in arr.ChordTemplates.Concat(arr.FretHandMuteTemplates))
        {
            template.Frets = template.Frets.ToDictionary(kv => StringOf(kv.Key), kv => kv.Value);
            template.Fingers = template.Fingers.ToDictionary(kv => StringOf(kv.Key), kv => kv.Value);
        }

        foreach (var level in arr.Levels)
        {
            foreach (var note in level.Notes) note.StringIndex = StringOf(note.StringIndex);
            foreach (var chord in level.Chords)
            {
                foreach (var note in chord.ChordNotes) note.StringIndex = StringOf(note.StringIndex);
            }
        }
    }

    /// <summary>The tuning offsets of the chart, from the file's offsets (index = file string).</summary>
    public static int[] TuningToChart(Arrangement arr, int[] fileOffsets, bool packageBass) =>
        packageBass ? BassTuningToChart(arr, fileOffsets)
        : Rs7StringLayout.IsExtendedGuitar(arr)
            ? [.. Enumerable.Range(0, fileOffsets.Length).Select(d => fileOffsets[Rs7StringLayout.ToSng(d, fileOffsets.Length)])]
            : fileOffsets;

    /// <summary>A package bass's offsets are measured from its own open strings (B0 23, C3 48); the
    /// chart measures from its reference row (<see cref="StandardOpenStrings" />), so the sounding
    /// pitch is kept (the inverse of the export's offset).</summary>
    private static int[] BassTuningToChart(Arrangement arr, int[] fileOffsets)
    {
        var count = fileOffsets.Length;
        return [.. Enumerable.Range(0, count).Select(d =>
        {
            var s = Rs7StringLayout.ToSng(d, count, arr.ArrangementType);
            return fileOffsets[s] + Rs7StringLayout.BassOpenMidi(s) - StandardOpenStrings.BassLowEMidi -
                   StandardOpenStrings.SemitonesAboveLowE(count, d);
        })];
    }

    /// <summary>Whether the file is a 5-/6-string bass in the package order: a bass whose
    /// tuning element states 5 or 6 strings itself.</summary>
    public static bool IsPackageOrderBass(XElement song, Arrangement arr) =>
        arr.ArrangementType is ArrangementType.Bass && song.Element(E.Tuning) is { } tuning &&
        Fmt.AttrIntNullable(tuning, E.Strings) is 5 or 6;

    /// <summary>Renumbers notes, chord notes and chord templates from the file's string
    /// order to the chart's. Does nothing for a 6-string guitar or a bass.</summary>
    public static void ToChart(Arrangement arr, bool packageBass)
    {
        if (!Rs7StringLayout.IsExtendedGuitar(arr) && !packageBass) return;
        var count = arr.StringCount;
        foreach (var template in arr.ChordTemplates.Concat(arr.FretHandMuteTemplates))
        {
            template.Frets = Renumbered(arr, template.Frets, count);
            template.Fingers = Renumbered(arr, template.Fingers, count);
        }

        foreach (var level in arr.Levels)
        {
            foreach (var note in level.Notes) note.StringIndex = ToChart(arr, note.StringIndex, count);
            foreach (var chord in level.Chords)
            {
                foreach (var chordNote in chord.ChordNotes) chordNote.StringIndex = ToChart(arr, chordNote.StringIndex, count);
            }
        }
    }

    /// <summary>A column past the declared string count is ignored (spec 5).</summary>
    private static Dictionary<int, int> Renumbered(Arrangement arr, Dictionary<int, int> byString, int count) =>
        byString.Where(kv => kv.Key >= 0 && kv.Key < count).ToDictionary(kv => ToChart(arr, kv.Key, count), kv => kv.Value);

    /// <summary>A string outside the arrangement keeps its index, as the package side does.</summary>
    private static int ToChart(Arrangement arr, int fileIndex, int count) =>
        fileIndex >= 0 && fileIndex < count ? Rs7StringLayout.ToDomain(fileIndex, count, arr.ArrangementType) : fileIndex;
}
