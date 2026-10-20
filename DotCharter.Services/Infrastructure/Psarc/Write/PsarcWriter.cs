using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace DotCharter.Services.Infrastructure.Psarc.Write;

/// <summary>A named entry to be packed into a PSARC archive. The data stream
/// must be seekable; it is read from position 0 and disposed by the writer.</summary>
public sealed class PsarcWriteEntry : IDisposable
{
    public PsarcWriteEntry(string name, Stream data)
    {
        Name = name;
        Data = data;
    }

    public PsarcWriteEntry(string name, byte[] data)
        : this(name, new MemoryStream(data, false))
    {
    }

    /// <summary>Archive-relative path using '/' separators (e.g. "songs/bin/generic/arr_lead.sng").</summary>
    public string Name { get; }

    public Stream Data { get; }

    public void Dispose() => Data.Dispose();
}

/// <summary>
/// Rocksmith 2014 PSARC archive builder — the write side of the format; it
/// references nothing of the reader. Produces a v1.4 zlib archive with a 64 KiB
/// block table and (by default) an AES-CFB encrypted table of contents,
/// exactly like official game archives.
/// Ported from Rocksmith2014.NET: PSARC.fs (Edit/Create), Entry.fs, Header.fs,
/// Compression.fs (blockZip) and Utils.fs (usePlain).
/// </summary>
public static class PsarcWriter
{
    private const int HeaderLength = 32;
    private const int TocEntrySize = 30;
    private const int BlockSize = 65536;
    /// <summary>Bytes used per block-size table slot for a 64 KiB block size (uint16).</summary>
    private const int ZType = 2;

    /// <summary>Entry name suffixes stored without zlib compression:
    /// wem = packed vorbis, sng = already zlib-packed, appid = tiny,
    /// 7z = already compressed. Reference: Rocksmith2014.NET PSARC Utils.fs.</summary>
    private static readonly string[] PlainSuffixes = [".wem", ".sng", "appid", "7z"];

    /// <summary>
    ///     Writes the archive to <paramref name="path" />, replacing any existing file
    ///     ATOMICALLY.
    /// </summary>
    /// <remarks>
    ///     The staging file matters. <see cref="FileMode.Create" /> truncates the
    ///     destination the moment it opens, and the caller's path is the one the user
    ///     picked — usually an existing, working package. Writing straight into it meant
    ///     that any failure part-way through (a disk filling up, an IO error, a build
    ///     cancelled, the process killed) destroyed the good package and left a truncated
    ///     stub in its place. The window is not small: a PCM-audio package is tens of MB.
    ///     Now the bytes go to a sibling temp file and only replace the destination once
    ///     they are all safely written.
    /// </remarks>
    public static void WriteFile(string path, IReadOnlyList<PsarcWriteEntry> entries, bool encryptToc = true)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // Sibling, not %TEMP%: File.Move across volumes is a copy, not an atomic rename.
        var staging = path + ".writing";
        try
        {
            using (var file = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            {
                Write(file, entries, encryptToc);
            }

            AtomicFile.ReplaceWithRetry(staging, path);
        }
        finally
        {
            // Only reached with the staging file still present when the write threw —
            // a successful Move has already consumed it.
            try
            {
                if (File.Exists(staging)) File.Delete(staging);
            }
            catch
            {
                // Best-effort cleanup; never mask the real failure.
            }
        }
    }

    public static void Write(Stream output, IReadOnlyList<PsarcWriteEntry> entries, bool encryptToc = true)
    {
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                throw new ArgumentException("PSARC entries must have non-empty names.", nameof(entries));
            // Encoding.ASCII REPLACES a non-ASCII char with '?', so the name list said
            // "dcavemujica???a_lead.sng" while every manifest named the real file and the game
            // resolved nothing. A name the format cannot spell fails the write, loudly.
            if (!entry.Name.All(char.IsAscii))
                throw new ArgumentException($"PSARC entry name '{entry.Name}' is not ASCII; the archive cannot spell it.", nameof(entries));
        }

        // Entry 0 is the name manifest; it has an all-zero name digest and is
        // hidden from the manifest itself.
        using var manifestEntry = new PsarcWriteEntry(string.Empty, CreateManifestData(entries));
        var allEntries = new List<PsarcWriteEntry>(entries.Count + 1) { manifestEntry };
        allEntries.AddRange(entries);

        var blocks = new List<DataBlock>();
        var zLengths = new List<uint>();
        var protoEntries = new List<ProtoEntry>(allEntries.Count);

        try
        {
            foreach (var entry in allEntries)
            {
                var zIndexBegin = (uint)zLengths.Count;
                var plainLength = (ulong)entry.Data.Length;
                var packedSize = UsePlain(entry.Name)
                    ? AddPlainBlocks(entry.Data, blocks, zLengths)
                    : DeflateBlocks(entry.Data, blocks, zLengths);

                protoEntries.Add(new ProtoEntry(Md5NameDigest(entry.Name), zIndexBegin, plainLength, packedSize));
            }

            var tocLength = HeaderLength + protoEntries.Count * TocEntrySize + zLengths.Count * ZType;

            WriteHeader(output, tocLength, protoEntries.Count, encryptToc);
            WriteToc(output, protoEntries, zLengths, (ulong)tocLength, encryptToc);

            foreach (var block in blocks)
                block.CopyTo(output);

            output.Flush();
        }
        finally
        {
            foreach (var block in blocks)
                block.Dispose();
        }
    }

    /// <summary>ASCII that THROWS on a character it cannot encode, never substitutes '?'.</summary>
    private static readonly Encoding StrictAscii =
        Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    private static MemoryStream CreateManifestData(IReadOnlyList<PsarcWriteEntry> entries)
    {
        // Names joined with '\n', ASCII, no trailing newline.
        var manifest = string.Join('\n', entries.Select(e => e.Name));
        return new MemoryStream(StrictAscii.GetBytes(manifest), false);
    }

    private static bool UsePlain(string name) => PlainSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal));

    private static byte[] Md5NameDigest(string name) => string.IsNullOrEmpty(name) ? new byte[16] : MD5.HashData(StrictAscii.GetBytes(name));

    /// <summary>Stores the data unzipped; the block table records the raw chunk sizes
    /// (a full 64 KiB chunk becomes the sentinel 0 when the table is written).</summary>
    private static ulong AddPlainBlocks(Stream data, List<DataBlock> blocks, List<uint> zLengths)
    {
        blocks.Add(DataBlock.Owned(data));

        if (data.Length <= BlockSize)
        {
            zLengths.Add((uint)data.Length);
            return (ulong)data.Length;
        }

        var blockCount = (int)(data.Length / BlockSize);
        var lastBlockSize = data.Length - (long)blockCount * BlockSize;
        for (var i = 0; i < blockCount; i++) zLengths.Add(BlockSize);
        if (lastBlockSize != 0) zLengths.Add((uint)lastBlockSize);
        return (ulong)data.Length;
    }

    /// <summary>Splits the data into 64 KiB chunks and zlib-packs each; a chunk whose
    /// packed form is not smaller than the block size is stored raw.
    /// Reference: Rocksmith2014.NET Compression.fs `blockZip`.</summary>
    private static ulong DeflateBlocks(Stream data, List<DataBlock> blocks, List<uint> zLengths)
    {
        var packedTotal = 0UL;
        data.Position = 0;
        while (data.Position < data.Length)
        {
            var size = (int)Math.Min(data.Length - data.Position, BlockSize);
            var plain = new byte[size];
            data.ReadExactly(plain);

            var zipped = new MemoryStream();
            using (var zlib = new ZLibStream(zipped, CompressionLevel.SmallestSize, true))
            {
                zlib.Write(plain);
            }

            if (zipped.Length < BlockSize)
            {
                blocks.Add(DataBlock.Owned(zipped));
                zLengths.Add((uint)zipped.Length);
                packedTotal += (ulong)zipped.Length;
            }
            else
            {
                // Edge case: packing didn't help — store the chunk raw.
                zipped.Dispose();
                blocks.Add(DataBlock.Owned(new MemoryStream(plain, false)));
                zLengths.Add((uint)size);
                packedTotal += (ulong)size;
            }
        }

        data.Dispose();
        return packedTotal;
    }

    private static void WriteHeader(Stream output, int tocLength, int entryCount, bool encryptToc)
    {
        Span<byte> header = stackalloc byte[HeaderLength];
        "PSAR"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1); // VersionMajor
        BinaryPrimitives.WriteUInt16BigEndian(header[6..], 4); // VersionMinor
        "zlib"u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteUInt32BigEndian(header[12..], (uint)tocLength);
        BinaryPrimitives.WriteUInt32BigEndian(header[16..], TocEntrySize);
        BinaryPrimitives.WriteUInt32BigEndian(header[20..], (uint)entryCount);
        BinaryPrimitives.WriteUInt32BigEndian(header[24..], BlockSize);
        BinaryPrimitives.WriteUInt32BigEndian(header[28..], encryptToc ? 4u : 0u);
        output.Write(header);
    }

    private static void WriteToc(
        Stream output,
        List<ProtoEntry> protoEntries,
        List<uint> zLengths,
        ulong firstEntryOffset,
        bool encryptToc)
    {
        var tocBytes = new byte[protoEntries.Count * TocEntrySize + zLengths.Count * ZType];
        var span = tocBytes.AsSpan();
        var position = 0;

        var offset = firstEntryOffset;
        foreach (var proto in protoEntries)
        {
            proto.NameDigest.CopyTo(span[position..]);
            position += 16;
            BinaryPrimitives.WriteUInt32BigEndian(span[position..], proto.ZIndexBegin);
            position += 4;
            WriteUInt40Be(span[position..], proto.Length);
            position += 5;
            WriteUInt40Be(span[position..], offset);
            position += 5;

            offset += proto.PackedSize;
        }

        foreach (var zLength in zLengths)
        {
            // A full 64 KiB block is stored as the sentinel value 0.
            var stored = zLength == BlockSize ? 0u : zLength;
            BinaryPrimitives.WriteUInt16BigEndian(span[position..], (ushort)stored);
            position += ZType;
        }

        if (encryptToc)
            output.Write(PsarcTocEncryptor.Encrypt(tocBytes));
        else
            output.Write(tocBytes);
    }

    private static void WriteUInt40Be(Span<byte> target, ulong value)
    {
        if (value > 0xFF_FFFF_FFFFUL)
            throw new InvalidDataException($"Value {value} does not fit a PSARC 40-bit field.");
        target[0] = (byte)(value >> 32);
        target[1] = (byte)(value >> 24);
        target[2] = (byte)(value >> 16);
        target[3] = (byte)(value >> 8);
        target[4] = (byte)value;
    }

    private sealed record ProtoEntry(byte[] NameDigest, uint ZIndexBegin, ulong Length, ulong PackedSize);

    private sealed class DataBlock : IDisposable
    {
        private readonly Stream _data;

        private DataBlock(Stream data) => _data = data;

        public static DataBlock Owned(Stream data) => new(data);

        public void CopyTo(Stream output)
        {
            _data.Position = 0;
            _data.CopyTo(output);
        }

        public void Dispose() => _data.Dispose();
    }
}
