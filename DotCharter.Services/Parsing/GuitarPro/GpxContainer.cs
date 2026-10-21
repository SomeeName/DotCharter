using System.Buffers.Binary;
using System.Text;
using DotCharter.Services.Infrastructure;

namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     Reads <c>score.gpif</c> out of a Guitar Pro 6 <c>.gpx</c> file. GP6 does not use a
///     zip: the file is a BCFS sector file system, usually compressed as BCFZ.
///     <list type="bullet">
///         <item><b>BCFZ</b> — <c>"BCFZ"</c>, the uncompressed length (int32 LE), then a
///         bit stream read most-significant bit first. A <c>1</c> bit starts a back
///         reference: a 4-bit word size <c>w</c>, then a <c>w</c>-bit offset and a
///         <c>w</c>-bit length, both least-significant bit first, copying
///         <c>min(offset, length)</c> bytes from <c>offset</c> bytes back. A <c>0</c> bit
///         starts a literal run: a 2-bit (LSB first) count of bytes, 8 bits each.</item>
///         <item><b>BCFS</b> — <c>"BCFS"</c>, then 4 KiB sectors. A sector starting with
///         int32 <c>2</c> is a file entry: its name (127 bytes, NUL-padded) at 0x04, its
///         size at 0x8C, and from 0x94 a zero-terminated list of the data sectors' indexes.</item>
///     </list>
///     The layout is the one the open readers (alphaTab, TuxGuitar) implement. Every read is
///     bounds-checked: a hostile file ends as <see cref="InvalidDataException" />, never as an
///     out-of-range read, a runaway allocation or an endless loop.
/// </summary>
internal static class GpxContainer
{
    private const int SectorSize = 0x1000;
    private const int FileEntry = 2;
    private const int NameOffset = 0x04;
    private const int NameLength = 127;
    private const int SizeOffset = 0x8C;
    private const int SectorListOffset = 0x94;

    /// <summary>Largest uncompressed file system accepted. GP6 scores are a few hundred KiB.</summary>
    private const int MaxUncompressedBytes = 256 * 1024 * 1024;

    private static ReadOnlySpan<byte> Bcfz => "BCFZ"u8;
    private static ReadOnlySpan<byte> Bcfs => "BCFS"u8;

    /// <summary>True when the file starts like a GP6 container (BCFZ or BCFS).</summary>
    public static bool IsGpx(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            return stream.ReadAtLeast(head, 4, throwOnEndOfStream: false) == 4 &&
                   (head.SequenceEqual(Bcfz) || head.SequenceEqual(Bcfs));
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>The bytes of <c>score.gpif</c>, or null when the container has none.</summary>
    public static byte[]? ReadScore(string path) => ReadScore(path, ImportSizeLimits.GpxContainerBytes);

    /// <summary>The same, refusing a container file longer than <paramref name="maxBytes" />
    /// before reading it.</summary>
    internal static byte[]? ReadScore(string path, long maxBytes)
    {
        ImportSizeLimits.RequireWithin(new FileInfo(path).Length, maxBytes, "The Guitar Pro file");
        return ReadFile(File.ReadAllBytes(path), "score.gpif");
    }

    /// <summary>The bytes of the named file in a BCFZ or BCFS container, or null.</summary>
    internal static byte[]? ReadFile(byte[] container, string name)
    {
        var fileSystem = container.AsSpan().StartsWith(Bcfz) ? Decompress(container) : container;
        if (!fileSystem.AsSpan().StartsWith(Bcfs))
            throw new InvalidDataException("Not a Guitar Pro 6 file system (no BCFS header).");
        return FindFile(fileSystem.AsSpan(Bcfs.Length), name);
    }

    /// <summary>Inflates a BCFZ stream (the bytes include the <c>"BCFZ"</c> header) into the
    /// BCFS file system it holds.</summary>
    internal static byte[] Decompress(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8 || !data.StartsWith(Bcfz))
            throw new InvalidDataException("Not a BCFZ stream.");
        var expected = BinaryPrimitives.ReadInt32LittleEndian(data[4..8]);
        if (expected is < 0 or > MaxUncompressedBytes)
            throw new InvalidDataException($"BCFZ declares {expected} uncompressed bytes.");

        // Grown on demand: the declared length is the file's claim, not a budget to allocate up front.
        var output = new byte[Math.Min(expected, 1024 * 1024)];
        var length = 0;
        var bits = new BitReader(data[8..]);
        while (length < expected)
        {
            if (bits.ReadMsbFirst(1) == 1)
            {
                var wordSize = bits.ReadMsbFirst(4);
                var offset = bits.ReadLsbFirst(wordSize);
                var size = bits.ReadLsbFirst(wordSize);
                if (bits.Exhausted) break;
                if (offset > length) throw new InvalidDataException("BCFZ back reference before the start.");
                var count = Math.Min(Math.Min(offset, size), expected - length);
                output = Reserve(output, length + count);
                output.AsSpan(length - offset, count).CopyTo(output.AsSpan(length));
                length += count;
            }
            else
            {
                var count = bits.ReadLsbFirst(2);
                for (var i = 0; i < count && length < expected; i++)
                {
                    var value = bits.ReadMsbFirst(8);
                    if (bits.Exhausted) break;
                    output = Reserve(output, length + 1);
                    output[length++] = (byte)value;
                }
            }

            // The stream may end before the declared length (Guitar Pro pads the last
            // byte); what was inflated so far is the file system.
            if (bits.Exhausted) break;
        }

        return length == output.Length ? output : output[..length];
    }

    private static byte[] Reserve(byte[] buffer, int needed)
    {
        if (needed <= buffer.Length) return buffer;
        Array.Resize(ref buffer, (int)Math.Min(MaxUncompressedBytes, Math.Max(needed, 2L * buffer.Length)));
        return buffer;
    }

    /// <summary>Walks the sectors of a BCFS file system (after its 4-byte header).</summary>
    private static byte[]? FindFile(ReadOnlySpan<byte> data, string name)
    {
        // Sector 0 is the file system's own header.
        long offset = SectorSize;
        while (offset + SectorListOffset <= data.Length)
        {
            var sector = data[(int)offset..];
            if (BinaryPrimitives.ReadInt32LittleEndian(sector) == FileEntry)
            {
                var entryName = ReadName(sector.Slice(NameOffset, NameLength));
                var size = BinaryPrimitives.ReadInt32LittleEndian(sector[SizeOffset..]);
                var wanted = string.Equals(entryName, name, StringComparison.Ordinal);
                using var content = wanted ? new MemoryStream() : null;
                var lastDataSector = offset;

                for (var pointer = SectorListOffset; pointer + 4 <= SectorSize && pointer + 4 <= sector.Length; pointer += 4)
                {
                    var index = BinaryPrimitives.ReadInt32LittleEndian(sector[pointer..]);
                    if (index == 0) break;
                    var start = (long)index * SectorSize;
                    // A sector past the end (a truncated stream) ends the file where the data does.
                    if (index < 0 || start >= data.Length) break;
                    lastDataSector = Math.Max(lastDataSector, start);
                    content?.Write(data.Slice((int)start, (int)Math.Min(SectorSize, data.Length - start)));
                }

                if (content != null)
                {
                    if (size < 0) throw new InvalidDataException("BCFS file with a negative size.");
                    var bytes = content.ToArray();
                    return size < bytes.Length ? bytes[..size] : bytes;
                }

                // A file's data sectors follow its entry; skip past them so file content
                // that happens to start with the entry marker is never read as an entry.
                offset = lastDataSector;
            }

            offset += SectorSize;
        }

        return null;
    }

    private static string ReadName(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? field : field[..end]);
    }

    /// <summary>Bits of a byte span, most-significant bit of each byte first. Reading past
    /// the end yields zero bits and sets <see cref="Exhausted" />.</summary>
    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private long _position;

        public bool Exhausted { get; private set; }

        private int ReadBit()
        {
            var index = _position >> 3;
            if (index >= _data.Length)
            {
                Exhausted = true;
                return 0;
            }

            var bit = (_data[(int)index] >> (7 - (int)(_position & 7))) & 1;
            _position++;
            return bit;
        }

        public int ReadMsbFirst(int count)
        {
            var value = 0;
            for (var i = 0; i < count; i++) value = (value << 1) | ReadBit();
            return value;
        }

        public int ReadLsbFirst(int count)
        {
            var value = 0;
            for (var i = 0; i < count; i++) value |= ReadBit() << i;
            return value;
        }
    }
}
