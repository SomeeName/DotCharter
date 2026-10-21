using DotCharter.Core.Domain.Project;

namespace DotCharter.Core.Utilities;

public static class TuningUtilities
{
    /// <summary>
    ///     MIDI pitches of the reference ("standard") tuning an offset vector of
    ///     <paramref name="stringCount" /> strings is measured against, LOWEST
    ///     string first. This is the SAME table
    ///     <see cref="GetTuningNames" /> names against — deliberately one table,
    ///     because an importer that subtracts a different reference than the
    ///     namer adds produces a tuning whose offsets and letters disagree.
    ///     <para>
    ///     That is exactly what happened to 8-string tabs: every tab importer
    ///     had 4 / 5 / 7 rows and fell back to the SIX-string reference for 8,
    ///     while the namer already knew 8 starts on F#. A standard-tuned
    ///     8-string gpif imported as offsets <c>-10 -10 -10 -10 -9 -9 0 0</c>
    ///     and came out named <c>G# C# F# B F Bb B E</c> — wrong in both halves
    ///     and not even consistent with each other. Past 8 strings the reference
    ///     repeats the 6-string top E, which is what the namer has always
    ///     padded with.
    ///     </para>
    ///     <para>
    ///     <b>5 reads E A D G B</b> (the 6-string row truncated), for every
    ///     importer: the GP importers subtract this row too, so a standard
    ///     B E A D G bass imports as offsets <c>-5 -5 -5 -5 -4</c> (see the
    ///     <see cref="GetStandardMidiPitchesFor" /> for the octave).
    ///     </para>
    /// </summary>
    public static int[] GetStandardMidiPitches(int stringCount)
    {
        // The rows live in StandardOpenStrings: 4 = bass E1 A1 D2 G2,
        // 7 = B1 E2 A2 D3 G3 B3 E4, 8 = F#1 B1 E2 ..., every other count the
        // 6-string guitar row truncated, and past 8 the top E repeats.
        var lowE = stringCount == 4 ? StandardOpenStrings.BassLowEMidi : StandardOpenStrings.GuitarLowEMidi;
        var pitches = StandardOpenStrings.SemitonesAboveLowE(stringCount);
        for (var i = 0; i < pitches.Length; i++) pitches[i] += lowE;
        return pitches;
    }

    /// <summary>
    ///     <see cref="GetStandardMidiPitches" /> as the arrangement of
    ///     <paramref name="type" /> sounds it. A bass drops an octave
    ///     (<c>SoundingPitch</c> subtracts 12 for one), which only shows for 5
    ///     strings: the 4-string row is already the bass octave, so a 5-string bass
    ///     reads E1 A1 D2 G2 B2 and a B0 E1 A1 D2 G2 tuning is
    ///     <c>-5 -5 -5 -5 -4</c> against it.
    /// </summary>
    public static int[] GetStandardMidiPitchesFor(int stringCount, ArrangementType type)
    {
        var pitches = GetStandardMidiPitches(stringCount);
        if (stringCount == 5 && type == ArrangementType.Bass)
            for (var i = 0; i < pitches.Length; i++) pitches[i] -= 12;
        return pitches;
    }

    /// <summary>
    ///     The open-string letters of the reference tuning, LOWEST string first, for a
    /// row of per-string offset boxes: <c>E A D G B e</c>. A top string that
    ///     repeats the lowest string's letter is written lower-case, the way guitarists
    ///     tell the two E strings apart.
    /// </summary>
    public static string[] GetStringLabels(int stringCount)
    {
        var labels = GetTuningNames(stringCount, new int[Math.Max(0, stringCount)]);
        if (labels.Length > 1 && labels[^1] == labels[0]) labels[^1] = labels[^1].ToLowerInvariant();
        return labels;
    }

    /// <summary>The note string <paramref name="stringIndex" /> (lowest = 0) sounds at <paramref name="step" /> half steps from the reference.</summary>
    public static string GetResultingNote(int stringCount, int stringIndex, int step)
    {
        if (stringIndex < 0 || stringIndex >= stringCount) return "";
        var offsets = new int[stringCount];
        offsets[stringIndex] = step;
        return GetTuningNames(stringCount, offsets)[stringIndex];
    }

    public static string[] GetTuningNames(int stringCount, int[] offsets)
    {
        string[] standardNotes = ["C", "C#", "D", "Eb", "E", "F", "F#", "G", "G#", "A", "Bb", "B"];

        // Pitch classes of the standard tuning for this instrument, LOWEST string
        // first — derived from the ONE reference table (GetStandardMidiPitches) so
        // a namer/importer drift like the 8-string one cannot come back. Past 6
        // strings the reference is not the 6-string one with E repeated: a
        // 7-string starts on B and an 8-string on F#.
        //
        // 5 stays on the 6-string table (E A D G B), and every importer measures a
        // 5-string track against it: the GP importers turn a B E A D G bass into
        // offsets -5 -5 -5 -5 -4, the RS SNG path already read E A D G B.
        var stdPitches = GetStandardMidiPitches(stringCount);

        var names = new string[stringCount];
        for (var i = 0; i < stringCount; i++)
        {
            var pitch = (i < stdPitches.Length ? stdPitches[i] % 12 : 4) + (i < offsets.Length ? offsets[i] : 0);
            while (pitch < 0) pitch += 12;
            names[i] = standardNotes[pitch % 12];
        }

        return names;
    }

    /// <summary>
    ///     Maps a rscharter TuningType enum name to per-string offsets for the given
    ///     string count, <b>assuming E standard when the name is not in the table</b>.
    ///     <para>
    ///         <b>That assumption is a confident wrong answer, so call
    ///         <see cref="TryGetOffsetsForTuningType" /> unless you actively want a
    ///         default.</b> An unknown name is indistinguishable here from
    ///         "E_STANDARD", and the tuning table uses Rocksmith's low-note spelling
    ///         (Drop C# is <c>E_FLAT_DROP_D_FLAT</c>, Drop B is
    ///         <c>D_FLAT_DROP_B</c>), which almost nothing else calls it — so a name
    ///         arriving from another tool, a hand-edited <c>.rscp</c> or a UI preset
    ///         misses the table easily and every string then reads as standard.
    ///     </para>
    ///     <para>
    ///         Two callers DO want the default and say so at the call site:
    ///         <c>RefretPlanner</c> and the MIDI import dialog both need some
    ///         open-string vector to do fret arithmetic against, and standard is the
    ///         same default their own per-string fallback already used.
    ///     </para>
    /// </summary>
    public static int[] GetOffsetsForTuningType(string tuningType, int stringCount)
        => TryGetOffsetsForTuningType(tuningType, stringCount, out var offsets)
            ? offsets
            : Slice([0, 0, 0, 0, 0, 0, 0, 0, 0], stringCount);

    /// <summary>
    ///     The honest half: per-string offsets for a tuning NAME the table knows,
    ///     or <c>false</c> and an empty vector for one it does not.
    ///     Mirrors rscharter's TuningType table (9-entry arrays) and honours the
    ///     legacy aliases it still recognises on read (F_STANDARD →
    ///     LOW_F_STANDARD, C_SHARP_STANDARD → D_FLAT_STANDARD, …); an empty or
    ///     null name means E_STANDARD, which is rscharter's own reader default and
    ///     is therefore KNOWN, not unknown.
    /// </summary>
    public static bool TryGetOffsetsForTuningType(string? tuningType, int stringCount, out int[] offsets)
    {
        // Normalise legacy names rscharter still recognises on read.
        var key = (tuningType ?? string.Empty).ToUpperInvariant() switch
        {
            "F_STANDARD" => "LOW_F_STANDARD",
            "C_SHARP_STANDARD" => "D_FLAT_STANDARD",
            "F_SHARP_STANDARD" => "G_FLAT_STANDARD",
            "G_SHARP_STANDARD" => "A_FLAT_STANDARD",
            "D_SHARP_STANDARD" => "E_FLAT_STANDARD",
            "A_SHARP_STANDARD" => "B_FLAT_STANDARD",
            "" => "E_STANDARD",
            var other => other
        };

        // Each entry mirrors rscharter's 9-string array; the slice below follows rscharter's
        // getTuningValues convention so the lowest (drop) string aligns at index 0.
        var nineStringTuning = key switch
        {
            "HIGH_F_SHARP_STANDARD" => new[] { 2, 2, 2, 2, 2, 2, 2, 2, 2 },
            "HIGH_F_STANDARD" => [1, 1, 1, 1, 1, 1, 1, 1, 1],
            "E_STANDARD" => [0, 0, 0, 0, 0, 0, 0, 0, 0],
            "E_DROP_D" => [0, 0, 0, -2, 0, 0, 0, 0, 0],
            "E_DROP_C" => [0, 0, 0, -4, 0, 0, 0, 0, 0],
            "E_FLAT_STANDARD" => [-1, -1, -1, -1, -1, -1, -1, -1, -1],
            "E_FLAT_DROP_D_FLAT" => [-1, -1, -1, -3, -1, -1, -1, -1, -1],
            "D_STANDARD" => [-2, -2, -2, -2, -2, -2, -2, -2, -2],
            "D_DROP_C" => [-2, -2, -2, -4, -2, -2, -2, -2, -2],
            "D_FLAT_STANDARD" => [-3, -3, -3, -3, -3, -3, -3, -3, -3],
            "D_FLAT_DROP_B" => [-3, -3, -3, -5, -3, -3, -3, -3, -3],
            "C_STANDARD" => [-4, -4, -4, -4, -4, -4, -4, -4, -4],
            "C_DROP_B_FLAT" => [-4, -4, -4, -6, -4, -4, -4, -4, -4],
            "B_STANDARD" => [-5, -5, -5, -5, -5, -5, -5, -5, -5],
            "B_DROP_A" => [-5, -5, -5, -7, -5, -5, -5, -5, -5],
            "B_FLAT_STANDARD" => [-6, -6, -6, -6, -6, -6, -6, -6, -6],
            "B_FLAT_DROP_A_FLAT" => [-6, -6, -6, -8, -6, -6, -6, -6, -6],
            "A_STANDARD" => [-7, -7, -7, -7, -7, -7, -7, -7, -7],
            "A_DROP_G" => [-7, -7, -7, -9, -7, -7, -7, -7, -7],
            "A_FLAT_STANDARD" => [-8, -8, -8, -8, -8, -8, -8, -8, -8],
            "G_STANDARD" => [-9, -9, -9, -9, -9, -9, -9, -9, -9],
            "G_FLAT_STANDARD" => [-10, -10, -10, -10, -10, -10, -10, -10, -10],
            "LOW_F_STANDARD" => [-11, -11, -11, -11, -11, -11, -11, -11, -11],
            "OPEN_D" => [-2, -2, -2, -2, 0, 0, -1, -2, -2],
            "OPEN_E" => [0, 0, 0, 0, 2, 2, 1, 0, 0],
            "OPEN_G" => [-2, -2, -2, -2, -2, 0, 0, 0, -2],
            "OPEN_A" => [0, 0, 0, 0, 0, 2, 2, 2, 0],
            "DADGAD" => [-2, -2, -2, -2, 0, 0, 0, -2, -2],

            // NOT a default. A name the table does not know is a name we cannot
            // turn into pitches, and answering "E standard" here is a confident
            // lie that reaches the string gutter, the tuner and four writers.
            _ => null
        };

        if (nineStringTuning is null)
        {
            offsets = [];
            return false;
        }

        offsets = Slice(nineStringTuning, stringCount);
        return true;
    }

    /// <summary>
    ///     rscharter convention (Tuning.getTuningValues): start = fullSize -
    ///     max(6, strings). For &lt;= 6 strings the slice starts at index 3, so
    ///     4/5-string basses keep the drop/altered low strings; taking the LAST
    ///     <c>stringCount</c> entries instead would collapse every non-uniform
    ///     tuning to standard.
    /// </summary>
    private static int[] Slice(int[] nineStringTuning, int stringCount)
    {
        var clamped = Math.Clamp(stringCount, 1, 9);
        var offsets = new int[clamped];
        var start = nineStringTuning.Length - Math.Max(6, clamped);
        Array.Copy(nineStringTuning, start, offsets, 0, clamped);
        return offsets;
    }
}
