using System.Buffers.Binary;

namespace DotCharter.Services.Infrastructure.Storage;

/// <summary>Bounds real central-directory records before ZipArchive can materialize its index.</summary>
internal static class ZipDirectoryBudget
{
    public static ZipExtractionRefusal Check(Stream stream, int maxEntries)
    {
        var directory = ZipDirectoryMetadata.Read(stream);
        if (directory is not { } info)
        {
            return BoundedZipExtractor.TryReadDeclaredEntryCount(stream) is { } declared && declared > maxEntries
                ? ZipExtractionRefusal.TooManyEntries : ZipExtractionRefusal.Unreadable;
        }
        if (info.Count > (ulong)maxEntries) return ZipExtractionRefusal.TooManyEntries;
        stream.Position = info.Start;
        Span<byte> header = stackalloc byte[46];
        var count = 0;
        while (info.End - stream.Position >= sizeof(uint))
        {
            stream.ReadExactly(header[..sizeof(uint)]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014b50) break;
            stream.Position -= sizeof(uint);
            if (info.End - stream.Position < header.Length) return ZipExtractionRefusal.Unreadable;
            stream.ReadExactly(header);
            if (++count > maxEntries) return ZipExtractionRefusal.TooManyEntries;
            var extra = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]) +
                        BinaryPrimitives.ReadUInt16LittleEndian(header[30..]) +
                        BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
            if (extra > info.End - stream.Position) return ZipExtractionRefusal.Unreadable;
            stream.Position += extra;
        }
        return (ulong)count == info.Count ? ZipExtractionRefusal.None : ZipExtractionRefusal.Unreadable;
    }
}
