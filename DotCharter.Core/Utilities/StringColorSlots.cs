namespace DotCharter.Core.Utilities;

/// <summary>
/// THE string → colour-slot mapping. Every surface that colours by string —
///     the 2D timeline lanes, notes and gutter, the song map, the phrase preview, the
///     chord box and diagram, the inspector chips, the 3D highway's strings, notes,
///     sustains and hit effects — indexes its palette with <see cref="For" />, never
///     with the raw string index.
///     <para>
///         A colour names a string's IDENTITY, not its lane: low E is red on a 6-, 7- and
///         8-string alike. Slots 0–5 are the six standard strings (low E red, A yellow,
///         D blue, G orange, B green, high E purple); slot 6 is the 7th string (low B,
///         light grey, the rs7string mod's <c>string6_color</c>) and slot 7 the 8th
///         (low F#, a darker grey). Extra strings extend DOWN, as in
///         <see cref="StandardOpenStrings" />, so on a 7-string string index 0 is the low B.
///     </para>
///     <para>
///         The arrangement type does not enter: up to six strings the standard table is
///         the same for bass and guitar (a 4-string bass is E A D G, a 5-/6-string reads
///         on upward to B and C), so a bass's strings keep the slots they had. Only a
///         count past six adds low strings.
///     </para>
/// </summary>
public static class StringColorSlots
{
    /// <summary>Slots in a string palette: six standard strings and two extra low strings.</summary>
    public const int SlotCount = 8;

    /// <summary>The 7th string's slot (low B of a 7- or 8-string).</summary>
    public const int SeventhStringSlot = 6;

    /// <summary>The 8th string's slot (low F# of an 8-string).</summary>
    public const int EighthStringSlot = 7;

    private const int StandardStrings = 6;

    /// <summary>
    ///     The palette slot of string <paramref name="stringIndex" /> (0 = the lowest) on an
    ///     instrument of <paramref name="stringCount" /> strings. A count of zero or less reads
    ///     as six. Always in <c>[0, SlotCount)</c>.
    /// </summary>
    public static int For(int stringIndex, int stringCount)
    {
        if (stringIndex < 0) return 0;
        if (stringCount <= 0) stringCount = StandardOpenStrings.DefaultStringCount;

        var extra = Math.Max(0, stringCount - StandardStrings);
        if (stringIndex < extra)
        {
            // The string just below the low E is the 7th; the one below that the 8th.
            var below = extra - 1 - stringIndex;
            return below == 0 ? SeventhStringSlot : EighthStringSlot;
        }

        // A standard string, or a string the chart does not have (past the top) —
        // the latter keeps the modulo it always had.
        return (stringIndex - extra) % SlotCount;
    }
}
