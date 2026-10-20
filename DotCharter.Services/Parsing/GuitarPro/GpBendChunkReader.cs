namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
/// Parses Guitar Pro's "bend chunk" byte format from a
/// <see cref="GpLegacyBinaryReader"/>. Identical chunk shape is used both
/// for note bends (whose points are kept) and tremolo-bar bends on a beat
/// (whose points are discarded). Lifted out of <see cref="GpLegacyImporter"/>
/// so the byte layout has its own testable seam.
///
/// Chunk layout (all big-endian per <see cref="GpLegacyBinaryReader"/>):
/// <code>
/// byte    bendType
/// int32   bendHeight (centi-tones, unused by the importer)
/// int32   pointCount
/// pointCount × {
///   int32 offset    // 0..60 over the note's duration
///   int32 value     // 50 = full step
///   byte  vibrato   // unused
/// }
/// </code>
/// The importer stores point values as <c>value / 50.0</c> (i.e. in steps,
/// so 50 → 1.0, 100 → 2.0).
/// </summary>
internal static class GpBendChunkReader
{
    /// <summary>
    ///     Most bend points one chunk may declare. The count is a raw file
    ///     int32, so it must not PRE-SIZE the point list: <c>500_000_000</c>
    ///     would allocate ~8 GB before the first read hit EOF and a negative value
    ///     throw <see cref="ArgumentOutOfRangeException" />. Guitar Pro's bend
    ///     editor tops out at a handful of points; this is far past any real
    ///     chunk and the list still grows on its own if a file somehow exceeds
    ///     it (only the CAPACITY is bounded, never the data).
    /// </summary>
    private const int MaxPointCapacity = 64;

    /// <summary>Capacity to pre-size a point list with, from a file-supplied
    /// count. Never negative, never absurd.</summary>
    private static int SafeCapacity(int pointCount) => Math.Clamp(pointCount, 0, MaxPointCapacity);

    /// <summary>
    /// Read and discard the chunk (used for tremolo-bar dips on beats,
    /// where the points aren't materialized into the chart).
    /// </summary>
    public static void Skip(GpLegacyBinaryReader reader)
    {
        reader.ReadByte();          // bendType
        reader.ReadInt32();         // bendHeight
        var points = reader.ReadInt32();
        for (var i = 0; i < points; i++)
        {
            reader.ReadInt32();     // offset
            reader.ReadInt32();     // value
            reader.ReadByte();      // vibrato
        }
    }

    /// <summary>
    /// Read the chunk and return the bend points as
    /// <c>(offset, stepValue)</c> pairs. The caller is responsible for
    /// associating them with the appropriate <c>GuitarNote</c>.
    /// </summary>
    public static List<(int Offset, double Step)> ReadPoints(GpLegacyBinaryReader reader)
    {
        reader.ReadByte();          // bendType
        reader.ReadInt32();         // bendHeight
        var pointCount = reader.ReadInt32();
        var list = new List<(int Offset, double Step)>(SafeCapacity(pointCount));
        for (var i = 0; i < pointCount; i++)
        {
            var offset = reader.ReadInt32();
            var value = reader.ReadInt32();
            reader.ReadByte();      // vibrato type
            list.Add((offset, value / 50.0));
        }
        return list;
    }

    /// <summary>
    /// Read the chunk and append its points to <paramref name="destination"/>
    /// (allocates the list lazily if null). Used by the note-bend reader,
    /// which accumulates points across multiple bend frames into a single
    /// per-note list.
    /// </summary>
    public static void AppendPoints(GpLegacyBinaryReader reader,
        ref List<(int Offset, double Step)>? destination)
    {
        reader.ReadByte();          // bendType
        reader.ReadInt32();         // bendHeight
        var pointCount = reader.ReadInt32();
        destination ??= new List<(int Offset, double Step)>(SafeCapacity(pointCount));
        for (var i = 0; i < pointCount; i++)
        {
            var offset = reader.ReadInt32();
            var value = reader.ReadInt32();
            reader.ReadByte();      // vibrato type
            destination.Add((offset, value / 50.0));
        }
    }
}
