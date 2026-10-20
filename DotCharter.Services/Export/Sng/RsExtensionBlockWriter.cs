using System.Buffers.Binary;
using System.Text;
using DotCharter.Services.Export.Sng.Model;

namespace DotCharter.Services.Export.Sng;

/// <summary>The bytes of an <see cref="RsExtensionBlock" /> (RS7X v1): header, then <c>STRS</c>,
/// <c>CHDT</c>, <c>CHNT</c>. The WRITE half; the reader validates the same layout.</summary>
internal static class RsExtensionBlockWriter
{
    /// <summary>The block bytes: header, then <c>STRS</c>, <c>CHDT</c>, <c>CHNT</c> in
    /// that order (a null section is left out).</summary>
    public static byte[] Serialize(RsExtensionBlock block)
    {
        using var body = new MemoryStream();
        using (var w = new BinaryWriter(body, Encoding.ASCII, true))
        {
            WriteSection(w, RsExtensionBlock.Strs, RsExtensionBlock.Critical, p =>
            {
                p.Write(block.Instrument);
                p.Write((byte)block.StringCount);
                p.Write((byte)block.Strings.Count);
                p.Write((byte)0);
                foreach (var s in block.Strings) p.Write((byte)s);
            });
            if (block.Templates != null)
            {
                WriteSection(w, RsExtensionBlock.Chdt, RsExtensionBlock.Critical, p =>
                {
                    p.Write((uint)block.Templates.Count);
                    foreach (var row in block.Templates)
                    {
                        RequireWidth(block, row.Length, "CHDT");
                        foreach (var column in row)
                        {
                            p.Write(column.Fret);
                            p.Write(column.Finger);
                        }
                    }
                });
            }

            if (block.ChordNotes != null)
            {
                WriteSection(w, RsExtensionBlock.Chnt, 0, p =>
                {
                    p.Write((uint)block.ChordNotes.Count);
                    foreach (var row in block.ChordNotes)
                    {
                        RequireWidth(block, row.Length, "CHNT");
                        foreach (var tech in row) WriteTech(p, tech);
                    }
                });
            }
        }

        var sections = (block.Templates != null ? 1 : 0) + (block.ChordNotes != null ? 1 : 0) + 1;
        var bytes = new byte[RsExtensionBlock.HeaderSize + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, RsExtensionBlock.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), RsExtensionBlock.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), RsExtensionBlock.HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)sections);
        body.ToArray().CopyTo(bytes, RsExtensionBlock.HeaderSize);
        return bytes;
    }

    private static void RequireWidth(RsExtensionBlock block, int width, string section)
    {
        if (width != block.Strings.Count)
            throw new InvalidOperationException($"RS7X {section}: a row has {width} columns, STRS lists {block.Strings.Count}.");
    }

    private static void WriteSection(BinaryWriter w, ReadOnlySpan<byte> id, ushort flags, Action<BinaryWriter> payload)
    {
        using var buffer = new MemoryStream();
        using (var p = new BinaryWriter(buffer, Encoding.ASCII, true)) payload(p);
        w.Write(id);
        w.Write((ushort)1);
        w.Write(flags);
        w.Write((uint)buffer.Length);
        w.Write(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static void WriteTech(BinaryWriter p, RsExtTech tech)
    {
        p.Write(tech.Mask);
        p.Write(tech.SlideTo);
        p.Write(tech.SlideUnpitchTo);
        p.Write(tech.Vibrato);
        var count = Math.Min(tech.Bends.Count, RsExtensionBlock.MaxBends);
        p.Write((byte)count);
        for (var i = 0; i < count; i++)
        {
            p.Write(tech.Bends[i].Time);
            p.Write(tech.Bends[i].Step);
            p.Write(0u);
        }
    }
}
