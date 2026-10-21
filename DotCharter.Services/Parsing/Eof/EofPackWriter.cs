using System.Text;
using DotCharter.Services.Infrastructure;

namespace DotCharter.Services.Parsing.Eof;

/// <summary>
///     Forward-only little-endian writer producing the byte stream
///     <see cref="EofPackReader"/> reads. Mirrors EOF's <c>pack_putc</c> /
///     <c>pack_iputw</c> / <c>pack_iputl</c> primitives as used by
///     <c>eof_save_song</c> in <c>song.c</c>.
///     <para>
///     EOF saves projects UNCOMPRESSED — <c>eof_save_song</c> opens the file
///     with <c>pack_fopen(fn, "w")</c> (plain write, no Allegro PACKFILE
///     magic/LZSS layer) and <c>eof_load_song</c> reads with mode
///     <c>"r"</c> — so no compression wrapper is applied here either.
///     </para>
/// </summary>
public sealed class EofPackWriter
{
    private readonly MemoryStream _ms = new();

    public int Length => (int)_ms.Length;

    public void WriteU8(byte value) => _ms.WriteByte(value);

    public void WriteU16(ushort value)
    {
        _ms.WriteByte((byte)(value & 0xFF));
        _ms.WriteByte((byte)((value >> 8) & 0xFF));
    }

    public void WriteU32(uint value)
    {
        _ms.WriteByte((byte)(value & 0xFF));
        _ms.WriteByte((byte)((value >> 8) & 0xFF));
        _ms.WriteByte((byte)((value >> 16) & 0xFF));
        _ms.WriteByte((byte)((value >> 24) & 0xFF));
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes) => _ms.Write(bytes);

    /// <summary>
    ///     u16 byte-length-prefixed UTF-8 string — mirrors
    ///     <c>eof_save_song_string_pf</c> (and <see cref="EofPackReader.ReadString"/>).
    ///     A null/empty string writes a zero length and no payload. A name past the 16-bit
    ///     length is cut on a code-point boundary: a long title must not abort the whole save.
    /// </summary>
    public void WriteString(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            WriteU16(0);
            return;
        }

        var bytes = Utf8Truncation.GetBytes(value, ushort.MaxValue);
        WriteU16((ushort)bytes.Length);
        _ms.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Fixed-size ASCII field, NUL padded (the 16-byte file header).</summary>
    public void WriteFixedString(string value, int count)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        for (var i = 0; i < count; i++)
            _ms.WriteByte(i < bytes.Length ? bytes[i] : (byte)0);
    }

    public byte[] ToArray() => _ms.ToArray();
}
