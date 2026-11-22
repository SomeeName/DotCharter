using System.Buffers.Binary;

namespace DotCharter.Services.Infrastructure.Storage;

/// <summary>Reads bounded ZIP/ZIP64 end records without allocating a member index.</summary>
internal static class ZipDirectoryMetadata
{
    public static (long Start, long End, ulong Count)? Read(Stream stream)
    {
        const int endSize = 22;
        if (stream.Length < endSize) return null;
        var absoluteEnd = LastEndRecord(stream);
        if (absoluteEnd < 0 || stream.Length - absoluteEnd < endSize) return null;
        Span<byte> end = stackalloc byte[endSize];
        stream.Position = absoluteEnd;
        stream.ReadExactly(end);
        if (absoluteEnd + endSize + BinaryPrimitives.ReadUInt16LittleEndian(end[20..]) > stream.Length) return null;
        var disk = BinaryPrimitives.ReadUInt16LittleEndian(end[4..]);
        if (disk != BinaryPrimitives.ReadUInt16LittleEndian(end[6..])) return null;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
        if (BinaryPrimitives.ReadUInt16LittleEndian(end[8..]) != count) return null;
        var start = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
        if (disk == ushort.MaxValue || start == uint.MaxValue || count == ushort.MaxValue)
        {
            if (absoluteEnd < 20) return null;
            if (HasZip64Locator(stream, absoluteEnd)) return ReadZip64(stream, absoluteEnd);
        }
        return (long)start + length <= absoluteEnd ? (start, absoluteEnd, count) : null;
    }

    private static long LastEndRecord(Stream stream)
    {
        // Mirror .NET 10 ZipHelper, including ReadAtLeast's short-stream over-read.
        const int searchBytes = ushort.MaxValue + sizeof(uint);
        var buffer = new byte[4096];
        var signature = "PK\u0005\u0006"u8;
        stream.Seek(-18, SeekOrigin.End);
        var searched = 0;
        while (searched < searchBytes)
        {
            var overlap = searched == 0 ? 0 : signature.Length;
            var size = Math.Min(buffer.Length, searchBytes - searched + overlap);
            var read = ReadBackwards(stream, buffer.AsSpan(0, size), overlap);
            var index = buffer.AsSpan(0, read).LastIndexOf(signature);
            if (index >= 0) return stream.Position + index;
            searched += read - overlap;
            if (read < size) break;
        }
        return -1;
    }

    private static int ReadBackwards(Stream stream, Span<byte> buffer, int overlap)
    {
        if (stream.Position >= buffer.Length)
        {
            stream.Seek(overlap - buffer.Length, SeekOrigin.Current);
            var read = stream.ReadAtLeast(buffer, buffer.Length);
            stream.Seek(-buffer.Length, SeekOrigin.Current);
            return read;
        }
        var minimum = (int)stream.Position;
        stream.Position = 0;
        var available = stream.ReadAtLeast(buffer, minimum);
        stream.Position = 0;
        return available;
    }

    private static bool HasZip64Locator(Stream stream, long endPosition)
    {
        // SeekBackwardsToSignature(..., 4) from EOCD-16 examines exactly EOCD-20..EOCD-16.
        Span<byte> signature = stackalloc byte[4];
        stream.Position = endPosition - 20;
        stream.ReadExactly(signature);
        return BinaryPrimitives.ReadUInt32LittleEndian(signature) == 0x07064b50;
    }

    private static (long Start, long End, ulong Count)? ReadZip64(Stream stream, long endPosition)
    {
        Span<byte> locator = stackalloc byte[20];
        stream.Position = endPosition - locator.Length;
        stream.ReadExactly(locator);
        var at = BinaryPrimitives.ReadUInt64LittleEndian(locator[8..]);
        if (stream.Length < 56 || at > (ulong)(stream.Length - 56)) return null;
        Span<byte> end = stackalloc byte[56];
        stream.Position = (long)at;
        stream.ReadExactly(end);
        if (BinaryPrimitives.ReadUInt32LittleEndian(end) != 0x06064b50) return null;
        var count = BinaryPrimitives.ReadUInt64LittleEndian(end[32..]);
        if (BinaryPrimitives.ReadUInt64LittleEndian(end[24..]) != count) return null;
        var length = BinaryPrimitives.ReadUInt64LittleEndian(end[40..]);
        var start = BinaryPrimitives.ReadUInt64LittleEndian(end[48..]);
        var limit = (ulong)Math.Min((long)at, endPosition);
        return start <= limit && length <= limit - start ? ((long)start, (long)limit, count) : null;
    }
}
