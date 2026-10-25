namespace DotCharter.Core.Utilities;

/// <summary>
///     The standard and drop tunings of the instruments rscharter's <c>TuningType</c> table
///     cannot name: the 5- and 6-string bass and the 7-, 8- and 9-string guitar. That table is a
///     six-string vocabulary sliced to the string count (<see cref="TuningUtilities" />), so its
///     drops lower the six-string low E wherever it sits (<c>E_DROP_D</c> on a 7-string is
///     B D A D G B E), and it has no row for a bass in B E A D G at all.
///     <para>
///         Each entry is the instrument's standard (<see cref="StandardOffsets" />) moved by one
///         shift on every string, with a further whole step on the LOWEST string for a drop
///         tuning. Standards are named for the six-string equivalent and drops for the lowest
///         note, the convention <see cref="TuningNameResolver" /> already uses for the 5-string
///         bass ("5-String Bass Eb Standard" is Bb Eb Ab Db Gb, "5-String Bass Drop A" is
///         A E A D G). The type name is the display name in <c>TuningType</c> spelling
///         (<c>7_STRING_DROP_A</c>); <see cref="IsExtendedName" /> keeps it out of a .rscp.
///     </para>
/// </summary>
public static class ExtendedRangeTunings
{
    /// <summary>One named tuning: offsets against <see cref="TuningUtilities.GetStandardMidiPitches" />.</summary>
    public sealed record Tuning(int StringCount, int[] Offsets, string Name, string TypeName);

    private sealed record Instrument(string Prefix, int[] Standard);

    private static readonly Instrument[] Instruments =
    [
        new("5-String Bass ", [-5, -5, -5, -5, -4]),
        new("6-String Bass ", [-5, -5, -5, -5, -4, -4]),
        new("7-String ", new int[7]),
        new("8-String ", new int[8]),
        new("9-String ", new int[9])
    ];

    /// <summary>The six-string name of each uniform shift, flat-spelled ("Eb Standard").</summary>
    private static readonly Dictionary<int, string> StandardNames = new()
    {
        [1] = "F", [-1] = "Eb", [-2] = "D", [-3] = "Db", [-4] = "C", [-5] = "B", [-6] = "Bb", [-7] = "A"
    };

    /// <summary>Drop names are sharp-spelled, as the six-string family is ("Drop C#").</summary>
    private static readonly string[] SharpNotes = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    public static IReadOnlyList<Tuning> All { get; } = Build();

    private static readonly Dictionary<string, Tuning> ByTypeName =
        All.ToDictionary(t => t.TypeName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     The offsets of the instrument's own standard tuning, lowest string first. Zero except
    ///     on a 5-string (always a bass: B E A D G against the E A D G B reference row) and a
    ///     6-string bass (B E A D G C).
    /// </summary>
    public static int[] StandardOffsets(int stringCount, bool bass) => stringCount switch
    {
        5 => [.. Instruments[0].Standard],
        6 when bass => [.. Instruments[1].Standard],
        _ => new int[Math.Max(0, stringCount)]
    };

    /// <summary>The offsets a type name of this table stands for, at its own string count only.</summary>
    public static bool TryGetOffsets(string? typeName, int stringCount, out int[] offsets)
    {
        if (typeName != null && ByTypeName.TryGetValue(typeName, out var tuning) && tuning.StringCount == stringCount)
        {
            offsets = [.. tuning.Offsets];
            return true;
        }

        offsets = [];
        return false;
    }

    /// <summary>The type name of the tuning <paramref name="offsets" /> spell, or null.</summary>
    public static string? TypeNameFor(IReadOnlyList<int> offsets) =>
        All.FirstOrDefault(t => t.StringCount == offsets.Count && t.Offsets.SequenceEqual(offsets))?.TypeName;

    /// <summary>Whether <paramref name="typeName" /> is one of this table's names, which rscharter cannot read.</summary>
    public static bool IsExtendedName(string? typeName) => typeName != null && ByTypeName.ContainsKey(typeName);

    private static Tuning[] Build()
    {
        var tunings = new List<Tuning>();
        foreach (var instrument in Instruments)
        {
            var count = instrument.Standard.Length;
            var lowest = TuningUtilities.GetStandardMidiPitches(count)[0] + instrument.Standard[0];
            for (var shift = 1; shift >= -7; shift--)
            {
                var name = instrument.Prefix + (shift == 0 ? "Standard" : $"{StandardNames[shift]} Standard");
                tunings.Add(Make(instrument, shift, 0, name));
            }

            for (var shift = 0; shift >= -9; shift--)
            {
                var low = SharpNotes[((lowest + shift - 2) % 12 + 12) % 12];
                tunings.Add(Make(instrument, shift, -2, instrument.Prefix + "Drop " + low));
            }
        }

        return [.. tunings];
    }

    private static Tuning Make(Instrument instrument, int shift, int drop, string name)
    {
        var offsets = instrument.Standard.Select(step => step + shift).ToArray();
        offsets[0] += drop;
        return new Tuning(offsets.Length, offsets, name, TypeNameOf(name));
    }

    /// <summary>"5-String Bass Eb Standard" → <c>5_STRING_BASS_E_FLAT_STANDARD</c>.</summary>
    private static string TypeNameOf(string name) => string.Join("_", name.Replace('-', ' ')
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(word => word switch
        {
            [var note, 'b'] => $"{note}_FLAT",
            [var note, '#'] => $"{note}_SHARP",
            _ => word
        })).ToUpperInvariant();
}
