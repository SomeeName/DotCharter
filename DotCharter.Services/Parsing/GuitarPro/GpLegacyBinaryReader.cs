using System.Text;

namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     Low-level helper for the binary Guitar Pro 3/4/5 file format.
///     Wraps a <see cref="Stream"/> and exposes the small set of typed reads
///     that the legacy GP binary format uses (little-endian numbers,
///     length-prefixed Latin-1 strings with optional padding, etc.).
/// </summary>
internal sealed class GpLegacyBinaryReader
{
    private static readonly Encoding Latin1 = Encoding.GetEncoding("ISO-8859-1");
    private readonly Stream _stream;

    public GpLegacyBinaryReader(Stream stream) => _stream = stream;

    public long Position => _stream.Position;

    public byte ReadByte()
    {
        var b = _stream.ReadByte();
        if (b < 0) throw new EndOfStreamException();
        return (byte)b;
    }

    public sbyte ReadSByte() => (sbyte)ReadByte();

    public short ReadInt16()
    {
        var lo = ReadByte();
        var hi = ReadByte();
        return (short)((hi << 8) | lo);
    }

    public int ReadInt32()
    {
        var b0 = ReadByte();
        var b1 = ReadByte();
        var b2 = ReadByte();
        var b3 = ReadByte();
        return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
    }

    public float ReadFloat()
    {
        Span<byte> buf = stackalloc byte[4];
        for (var i = 0; i < 4; i++) buf[i] = ReadByte();
        return BitConverter.ToSingle(buf);
    }

    public double ReadDouble()
    {
        Span<byte> buf = stackalloc byte[8];
        for (var i = 0; i < 8; i++) buf[i] = ReadByte();
        return BitConverter.ToDouble(buf);
    }

    public void Skip(int bytes)
    {
        if (bytes <= 0) return;
        var remaining = bytes;
        Span<byte> tmp = stackalloc byte[256];
        while (remaining > 0)
        {
            var chunk = Math.Min(remaining, tmp.Length);
            var read = _stream.Read(tmp.Slice(0, chunk));
            if (read <= 0) throw new EndOfStreamException();
            remaining -= read;
        }
    }

    public byte[] ReadBytes(int count)
    {
        var buf = new byte[count];
        var off = 0;
        while (off < count)
        {
            var read = _stream.Read(buf, off, count - off);
            if (read <= 0) throw new EndOfStreamException();
            off += read;
        }

        return buf;
    }

    /// <summary>
    ///     Reads a string whose length is stored in a single leading byte and
    ///     whose total chunk size (including that length byte) is known.
    ///     Trailing padding is silently consumed.
    /// </summary>
    public string ReadStringByte(int totalChunkBytes)
    {
        var len = ReadByte();
        var actual = Math.Min(len, Math.Max(0, totalChunkBytes - 1));
        var bytes = ReadBytes(actual);
        var skip = totalChunkBytes - 1 - actual;
        if (skip > 0) Skip(skip);
        return Latin1.GetString(bytes);
    }

    /// <summary>
    ///     Reads a string preceded by a 4-byte block size. The block size is
    ///     "string length + 1" (the length byte itself is included).
    /// </summary>
    public string ReadStringInt()
    {
        var blockSize = ReadInt32();
        var len = ReadByte();
        var actual = Math.Min(len, Math.Max(0, blockSize - 1));
        var bytes = ReadBytes(actual);
        var skip = blockSize - 1 - actual;
        if (skip > 0) Skip(skip);
        return Latin1.GetString(bytes);
    }

    /// <summary>
    ///     Reads a fixed-size string field (block size known from the format
    ///     spec, no leading length byte).
    /// </summary>
    public string ReadStringFixed(int length)
    {
        var bytes = ReadBytes(length);
        var actualLen = bytes.Length;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == 0)
            {
                actualLen = i;
                break;
            }
        }

        return Latin1.GetString(bytes, 0, actualLen);
    }
}
