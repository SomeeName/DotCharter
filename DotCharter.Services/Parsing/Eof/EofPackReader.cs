using System.Text;

namespace DotCharter.Services.Parsing.Eof;

/// <summary>
///     Forward-only reader over a decompressed Allegro PACKFILE byte stream.
///     Mirrors EOF's pack_getc / pack_igetw / pack_igetl primitives:
///     all multi-byte numerics are little-endian.
/// </summary>
public sealed class EofPackReader
{
    private readonly byte[] _data;

    public EofPackReader(byte[] data) => _data = data;

    public int Position { get; private set; }

    public int Length => _data.Length;
    public bool Eof => Position >= _data.Length;
    public int Remaining => _data.Length - Position;

    public byte ReadU8()
    {
        if (Position >= _data.Length) throw new EndOfStreamException("EOF parser ran past end of stream.");
        return _data[Position++];
    }

    public ushort ReadU16()
    {
        var lo = ReadU8();
        var hi = ReadU8();
        return (ushort)(lo | (hi << 8));
    }

    public uint ReadU32()
    {
        uint b0 = ReadU8();
        uint b1 = ReadU8();
        uint b2 = ReadU8();
        uint b3 = ReadU8();
        return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
    }

    /// <summary>
    ///     Reads <paramref name="count" /> bytes, or throws.
    ///     <para>
    ///         The bound is written as <c>count &gt; Remaining</c>, NEVER as
    ///         <c>Position + count &gt; Length</c>: the file supplies
    ///         <paramref name="count" /> (a `.eof` block size is read straight off
    ///         disk), so <c>Position + count</c> OVERFLOWS <see cref="int" /> and
    ///         goes negative for a declared size near <see cref="int.MaxValue" />
    ///         — which makes the comparison FALSE and lets the read through, the
    ///         exact opposite of its intent. `new byte[0x7FFFFFFF]` is then a
    ///         2 GB allocation from a handful of file bytes.
    ///     </para>
    /// </summary>
    public byte[] ReadBytes(int count)
    {
        if (count < 0 || count > Remaining)
            throw new EndOfStreamException($"EOF parser ran past end of stream (need {count}, have {Remaining}).");
        var slice = new byte[count];
        Buffer.BlockCopy(_data, Position, slice, 0, count);
        Position += count;
        return slice;
    }

    /// <summary>Advances the cursor, clamping at the end. Same overflow rule as
    /// <see cref="ReadBytes" /> — and here it was worse than a bad allocation:
    /// the overflowed comparison skipped the clamp and ran
    /// <c>Position += count</c>, leaving a NEGATIVE cursor that
    /// <see cref="ReadU8" />'s <c>Position &gt;= Length</c> test then happily
    /// accepts.</summary>
    public void Skip(int count)
    {
        if (count <= 0) return;
        if (count > Remaining) Position = _data.Length;
        else Position += count;
    }

    public string ReadString()
    {
        var len = ReadU16();
        if (len == 0) return string.Empty;
        var bytes = ReadBytes(len);
        return Encoding.UTF8.GetString(bytes);
    }

    public string ReadFixedString(int count)
    {
        var bytes = ReadBytes(count);
        var nul = Array.IndexOf(bytes, (byte)0);
        if (nul >= 0) return Encoding.ASCII.GetString(bytes, 0, nul);
        return Encoding.ASCII.GetString(bytes);
    }
}
