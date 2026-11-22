using DotCharter.Core.Domain.Project;

namespace DotCharter.Core.Utilities;

/// <summary>Display spelling only. Stored tuning letters and tuning recognition keep their own contracts.</summary>
public static class TuningDisplayNames
{
    private static readonly string[] Sharps = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
    private static readonly string[] Flats = ["C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B"];

    public static string PitchClass(long midi, bool flats) =>
        (flats ? Flats : Sharps)[(int)((midi % 12 + 12) % 12)];

    /// <summary>Spells generated chord roots and slash basses; never apply to authored names.</summary>
    public static string ChordSuggestion(string name, Arrangement arrangement, ChordKeySignature? key = null)
    {
        var flats = PreferFlats(arrangement);
        return string.Join("/", name.Split('/').Select(part => ChordRoot(part, flats, key)));
    }

    private static string ChordRoot(string part, bool flats, ChordKeySignature? key)
    {
        if (part.Length == 0 || part[0] is < 'A' or > 'G') return part;
        var length = part.Length > 1 && part[1] is '#' or 'b' ? 2 : 1;
        var pitchClass = TuningNameResolver.NoteToPitchClass(part[..length]);
        return pitchClass < 0 ? part
            : PitchClass(pitchClass, key is { } signature ? ChordKeyEstimator.SpellsFlat(signature, pitchClass) : flats) + part[length..];
    }

    public static bool PreferFlats(int count, IReadOnlyList<int> offsets, bool bass = false)
    {
        var standard = ExtendedRangeTunings.StandardOffsets(count, bass);
        var lowered = false;
        for (var i = 0; i < count; i++)
        {
            var step = i < offsets.Count ? offsets[i] : 0;
            if (step > standard[i]) return false;
            lowered |= step < standard[i];
        }
        return lowered;
    }

    public static bool PreferFlats(Arrangement arrangement)
    {
        var offsets = DisplayOffsets(arrangement);
        return offsets.Count == 0 ? arrangement.Tuning.Any(note => note.Length > 1 && note[1] == 'b')
            : PreferFlats(arrangement.StringCount, offsets, ArrangementInstrument.IsBassPart(arrangement));
    }

    public static string Pitch(int midi, Arrangement arrangement, int stringIndex = -1)
    {
        if (stringIndex >= 0 && For(arrangement) is { } names && stringIndex < names.Length)
        {
            var name = names[stringIndex];
            if (TuningNameResolver.NoteToPitchClass(name) == (midi % 12 + 12) % 12)
                return $"{char.ToUpperInvariant(name[0])}{name[1..]}{midi / 12 - 1}";
        }
        return $"{PitchClass(midi, PreferFlats(arrangement))}{midi / 12 - 1}";
    }

    /// <summary>Open-string letters, lowest first; an unchanged standard string keeps its standard name.</summary>
    public static string[] For(int count, IReadOnlyList<int> offsets, bool bass = false, int transposeSemitones = 0)
    {
        var pitches = TuningUtilities.GetStandardMidiPitches(count);
        var standard = ExtendedRangeTunings.StandardOffsets(count, bass);
        var flats = PreferFlats(count, offsets, bass);
        var names = new string[count];
        for (var i = 0; i < count; i++)
        {
            var step = i < offsets.Count ? offsets[i] : 0;
            names[i] = PitchClass(pitches[i] + (long)step + transposeSemitones,
                flats && (step != standard[i] || transposeSemitones != 0));
        }
        return names;
    }

    public static string[]? For(Arrangement? arrangement, int transposeSemitones = 0)
    {
        if (arrangement == null) return null;
        var offsets = DisplayOffsets(arrangement);
        if (offsets.Count == 0) return [.. arrangement.Tuning];
        return For(arrangement.StringCount, offsets, ArrangementInstrument.IsBassPart(arrangement), transposeSemitones);
    }

    private static IReadOnlyList<int> DisplayOffsets(Arrangement arrangement)
    {
        var offsets = TuningOffsetResolution.Resolve(arrangement);
        return offsets.Count == 0 && arrangement.Tuning.Length == arrangement.StringCount
            ? TuningNameResolver.KnownOffsetsFor(arrangement.Tuning) ?? offsets : offsets;
    }

    /// <summary>Display letters from a library's stored note summary, without rewriting its index.</summary>
    public static string Summary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return "";
        var notes = summary.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var pcs = notes.Select(TuningNameResolver.NoteToPitchClass).ToArray();
        if (pcs.Any(pc => pc < 0)) return summary.Trim();
        var bass = notes.Length == 5 || TuningNameResolver.Resolve(notes)?.StartsWith("6-String Bass", StringComparison.Ordinal) == true;
        var offsets = SummaryOffsets(notes, bass);
        return string.Join(" ", For(notes.Length, offsets, bass));
    }

    public static string Describe(string? summary)
    {
        var notes = Summary(summary);
        if (notes.Length == 0) return "";
        var name = NameForSummary(summary);
        return name == null ? notes : $"{name} ({notes})";
    }

    public static string? NameForSummary(string? summary)
    {
        var name = TuningNameResolver.Resolve(summary);
        if (name == null) return null;
        var notes = Summary(summary).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bass = notes.Length == 5 || name.StartsWith("6-String Bass", StringComparison.Ordinal);
        var offsets = SummaryOffsets(notes, bass);
        var flats = PreferFlats(notes.Length, offsets, bass);
        return string.Join(" ", name.Split(' ').Select(word => TuningNameResolver.NoteToPitchClass(word) is >= 0 and var pc
            ? PitchClass(pc, flats) : word));
    }

    private static int[] SummaryOffsets(string[] notes, bool bass)
    {
        if (TuningNameResolver.KnownOffsetsFor(notes) is { } known) return known;
        var pitches = TuningUtilities.GetStandardMidiPitches(notes.Length);
        var standard = ExtendedRangeTunings.StandardOffsets(notes.Length, bass);
        return notes.Select((note, i) => standard[i]
            + (TuningNameResolver.NoteToPitchClass(note) - pitches[i] - standard[i] + 1206) % 12 - 6).ToArray();
    }
}
