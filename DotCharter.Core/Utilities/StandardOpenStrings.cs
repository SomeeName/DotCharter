namespace DotCharter.Core.Utilities;

/// <summary>
///     THE standard open-string table: where each string of an untuned instrument
///     sits, LOWEST string first, in semitones above the instrument's low E (E2 =
///     MIDI 40 on a guitar path, E1 = MIDI 28 on a bass path). Every consumer that
///     turns a string index into a pitch reads it from here — the tuner, the gutter,
///     live judging, refret, the chord tools, the property deriver, the sounding
///     pitch rule and <see cref="TuningUtilities.GetStandardMidiPitches" /> — so a
///     tuning offset means the same note everywhere.
///     <para>
///         <b>Extra strings extend DOWN.</b> A 7-string is B1 E2 A2 D3 G3 B3 E4 and an
///         8-string F#1 B1 E2 … and a 9-string C#1 F#1 B1 E2 …: the convention of every tab format, the tuning
///         names and Rocksmith's RS7 layout. Five private copies of
///         <c>[0, 5, 10, 15, 19, 24, 29]</c> would extend UP instead (a high A4
///         7th string), which would shift every string of a 7-string chart by a fourth
/// in the tuner, the gutter and live judging.
///         <c>Infrastructure/OpenStringTableLintTests</c> fails on a new copy.
///     </para>
///     <para>
///         Up to six strings the table is the 6-string guitar's, truncated — a
///         4-string bass is E A D G, and a 5-string reads E A D G B (and every importer,
///         GP included, measures against it — <see cref="TuningUtilities.GetStandardMidiPitches" />).
///         A string index past the instrument keeps climbing the six-string neck
///         (…, 24, 29, then fourths), which is what the old copies answered for it.
///     </para>
/// </summary>
public static class StandardOpenStrings
{
    /// <summary>E2, the low E every guitar-path offset is measured from.</summary>
    public const int GuitarLowEMidi = 40;

    /// <summary>E1, the low E every bass-path offset is measured from.</summary>
    public const int BassLowEMidi = 28;

    /// <summary>The most strings an arrangement carries (<c>StringEditMask</c>).</summary>
    public const int MaxStringCount = 9;

    /// <summary>String count assumed when an arrangement states none.</summary>
    public const int DefaultStringCount = 6;

    private static readonly int[] UpToSix = [0, 5, 10, 15, 19, 24];
    private static readonly int[] Seven = [-5, 0, 5, 10, 15, 19, 24];
    private static readonly int[] Eight = [-10, -5, 0, 5, 10, 15, 19, 24];
    private static readonly int[] Nine = [-15, -10, -5, 0, 5, 10, 15, 19, 24];

    /// <summary>The low E a path's offsets are measured from (E1 for bass, E2 otherwise).</summary>
    public static int LowEMidi(bool bass) => bass ? BassLowEMidi : GuitarLowEMidi;

    /// <summary>
    ///     Semitones above the low E of string <paramref name="stringIndex" /> (lowest =
    ///     0) on a standard-tuned instrument of <paramref name="stringCount" /> strings.
    ///     A count of zero or less reads as <see cref="DefaultStringCount" />.
    /// </summary>
    public static int SemitonesAboveLowE(int stringCount, int stringIndex)
    {
        if (stringCount <= 0) stringCount = DefaultStringCount;
        var row = RowFor(stringCount);
        if (stringIndex >= 0 && stringIndex < stringCount)
            return stringIndex < row.Length ? row[stringIndex] : row[^1];

        // Off the instrument (a note on a string the chart does not have): keep
        // climbing the six-string neck, exactly as the per-consumer tables did.
        return stringIndex switch
        {
            >= 0 and < 6 => UpToSix[stringIndex],
            6 => 29,
            _ => stringIndex * 5
        };
    }

    /// <summary>The whole row, lowest string first (<see cref="SemitonesAboveLowE(int, int)" /> per string).</summary>
    public static int[] SemitonesAboveLowE(int stringCount)
    {
        if (stringCount <= 0) return [];
        var result = new int[stringCount];
        for (var i = 0; i < stringCount; i++) result[i] = SemitonesAboveLowE(stringCount, i);
        return result;
    }

    // Past nine strings the top E repeats (row[^1]), which is what the tuning
    // namer has always padded with.
    private static int[] RowFor(int stringCount) => stringCount switch
    {
        <= 6 => UpToSix,
        7 => Seven,
        8 => Eight,
        _ => Nine
    };
}
