namespace DotCharter.Services.Export.Sng.Model;

/// <summary>One extended string's column of a chord template: fret (-1 = not in the
/// chord, 0 = open) and finger (-1 = none, 0 = thumb, 1..4 = index..pinky).</summary>
internal readonly record struct RsExtColumn(sbyte Fret, sbyte Finger)
{
    public static readonly RsExtColumn None = new(-1, -1);
}

/// <summary>One extended string's technique data inside one ChordNotes record: the
/// per-string fields of the stock 0x948 record (mask, slides, vibrato, used bends).</summary>
internal sealed record RsExtTech(uint Mask, sbyte SlideTo, sbyte SlideUnpitchTo, short Vibrato,
    IReadOnlyList<RsBendValue> Bends)
{
    /// <summary>The entry of a string that is not in the chord or has no technique:
    /// <c>00 00 00 00 ff ff 00 00 00</c>.</summary>
    public static readonly RsExtTech Empty = new(0, -1, -1, 0, []);

    public bool Equals(RsExtTech? other) =>
        other is not null && Mask == other.Mask && SlideTo == other.SlideTo &&
        SlideUnpitchTo == other.SlideUnpitchTo && Vibrato == other.Vibrato && Bends.SequenceEqual(other.Bends);

    public override int GetHashCode() => HashCode.Combine(Mask, SlideTo, SlideUnpitchTo, Vibrato, Bends.Count);
}

/// <summary>
///     The RS7X extension block, version 1 (the rs7string mod's
///     <c>docs/sng-extension.md</c>): appended INSIDE the zlib SNG body right after
///     MetaData, it carries what the stock six-slot chord records cannot — the chord
///     template columns (<c>CHDT</c>) and chord-note technique data (<c>CHNT</c>) of
///     every string past the sixth, listed in <c>STRS</c>. The stock loader stops
///     reading at the end of MetaData, so the game is unaffected by it.
///     <para>
///         The model is general (any string count 7..16, columns 6..n-1); which
///         arrangements may be EXPORTED with it is
///         <see cref="Export.Rocksmith.Rs7StringLayout.ExportRefusal(Arrangement)" />'s call.
///     </para>
/// </summary>
internal sealed class RsExtensionBlock
{
    public const uint Magic = 0x58375352; // "RS7X" read little-endian
    public const ushort Version = 1;
    public const ushort HeaderSize = 16;
    public const int SectionHeaderSize = 12;
    public const byte InstrumentGuitar = 0;
    public const int MaxStrings = 16;
    public const int MaxFret = 24;
    public const int MaxBends = 32;
    /// <summary>Section flag: a reader that does not know the section must reject the block.</summary>
    public const ushort Critical = 1;

    public static ReadOnlySpan<byte> Strs => "STRS"u8;
    public static ReadOnlySpan<byte> Chdt => "CHDT"u8;
    public static ReadOnlySpan<byte> Chnt => "CHNT"u8;

    public byte Instrument { get; init; } = InstrumentGuitar;

    /// <summary>Every string of the instrument (7 for a 7-string).</summary>
    public int StringCount { get; init; }

    /// <summary>The package string index of each extended column, in order (6, 7, ...).</summary>
    public IReadOnlyList<int> Strings { get; init; } = [];

    /// <summary><c>CHDT</c>: [chord template][column]. Null = section absent.</summary>
    public List<RsExtColumn[]>? Templates { get; set; }

    /// <summary><c>CHNT</c>: [ChordNotes record][column]. Null = section absent.</summary>
    public List<RsExtTech[]>? ChordNotes { get; set; }

    /// <summary>An empty guitar block for <paramref name="stringCount" /> strings, both
    /// sections present (a writer always writes all three, even with zero counts).
    /// Bytes: <see cref="RsExtensionBlockWriter" />.</summary>
    public static RsExtensionBlock ForStrings(int stringCount) => new()
    {
        StringCount = stringCount,
        Strings = [.. Enumerable.Range(6, stringCount - 6)],
        Templates = [],
        ChordNotes = []
    };

    /// <summary>The column of package string <paramref name="sngIndex" />, -1 when it has none.</summary>
    public int ColumnOf(int sngIndex)
    {
        for (var c = 0; c < Strings.Count; c++)
            if (Strings[c] == sngIndex) return c;
        return -1;
    }
}
