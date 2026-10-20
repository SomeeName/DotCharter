using System.Buffers.Binary;
using System.IO.Compression;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Writes a minimal, spec-valid 8-bit RGBA PNG of any pixel size — the
///     smallest encoder that a real decoder (Skia, and <c>ImageHeaderProbe</c>)
///     accepts. Exists so tests can produce a picture with a HOSTILE shape (a
///     1 × 100,000 strip that fits in a few hundred bytes) without an image
///     library or a fixture file, and without booting a Skia platform in the
///     default gate.
/// </summary>
public static class TinyPng
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>A solid-colour RGBA PNG of <paramref name="width" /> ×
    /// <paramref name="height" /> pixels, filter type 0 on every row.</summary>
    public static byte[] Encode(int width, int height, byte r = 0x40, byte g = 0x80, byte b = 0xC0, byte a = 0xFF)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        var raw = new byte[height * (1 + width * 4)];
        var i = 0;
        for (var y = 0; y < height; y++)
        {
            raw[i++] = 0; // filter: none
            for (var x = 0; x < width; x++)
            {
                raw[i++] = r;
                raw[i++] = g;
                raw[i++] = b;
                raw[i++] = a;
            }
        }

        byte[] deflated;
        using (var buffer = new MemoryStream())
        {
            using (var z = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
            deflated = buffer.ToArray();
        }

        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // colour type: RGBA
        ihdr[10] = 0; // compression
        ihdr[11] = 0; // filter
        ihdr[12] = 0; // interlace

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", deflated);
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = Crc32(typeBytes, data);
        var crcBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        s.Write(crcBytes);
    }

    private static uint Crc32(byte[] a, byte[] b)
    {
        var c = 0xFFFFFFFFu;
        foreach (var x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }
}
