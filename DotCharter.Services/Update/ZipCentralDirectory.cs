using System.Buffers.Binary;
using System.Text;

namespace DotCharter.Services.Update;

/// <summary>Why a remote archive's directory could not be read. Every value is a
/// reason to fall back to the full download, never a reason to throw.</summary>
public enum ZipDirectoryRefusal
{
    None,

    /// <summary>No end-of-central-directory record in the tail we fetched.</summary>
    NoEndOfCentralDirectory,

    /// <summary>Zip64. Refused rather than misread — see <see cref="ZipCentralDirectory" />.</summary>
    Zip64,

    /// <summary>More members than an app payload may declare.</summary>
    TooManyEntries,

    /// <summary>The central directory is larger than we will buffer.</summary>
    DirectoryTooLarge,

    /// <summary>A record does not parse, or describes bytes outside the archive.</summary>
    Malformed,

    /// <summary>The buffer ended mid-record.</summary>
    Truncated,

    /// <summary>A member name we will not turn into a path.</summary>
    UnsafeEntryName
}

/// <summary>The end-of-central-directory record, as declared.</summary>
/// <param name="EntryCount">Members the archive says it has.</param>
/// <param name="DirectoryOffset">Where the central directory starts.</param>
/// <param name="DirectorySize">How long it is.</param>
/// <param name="RecordOffset">Where the record itself sits in the archive.</param>
public sealed record ZipEndOfCentralDirectory(
    int EntryCount, long DirectoryOffset, long DirectorySize, long RecordOffset);

/// <summary>One member of a remote archive, read out of its central-directory record.</summary>
/// <param name="Name">The member name, forward-slashed, exactly as stored.</param>
/// <param name="LocalHeaderOffset">Where this member's LOCAL header starts.</param>
/// <param name="CompressedSize">Bytes on the wire.</param>
/// <param name="UncompressedSize">Bytes after inflation, as declared.</param>
/// <param name="CompressionMethod">0 = stored, 8 = deflate. Anything else is refused.</param>
public sealed record ZipDirectoryEntry(
    string Name, long LocalHeaderOffset, long CompressedSize, long UncompressedSize, ushort CompressionMethod)
{
    public const ushort Stored = 0;
    public const ushort Deflate = 8;

    /// <summary>A directory member carries no bytes.</summary>
    public bool IsDirectory => Name.EndsWith('/');
}

/// <summary>Outcome of reading an end-of-central-directory record.</summary>
public sealed record ZipEocdResult(ZipDirectoryRefusal Refusal, ZipEndOfCentralDirectory? Record)
{
    public bool Succeeded => Refusal == ZipDirectoryRefusal.None && Record != null;
}

/// <summary>Outcome of parsing a central directory.</summary>
public sealed record ZipDirectoryResult(ZipDirectoryRefusal Refusal, IReadOnlyList<ZipDirectoryEntry> Entries)
{
    public bool Succeeded => Refusal == ZipDirectoryRefusal.None;

    public static ZipDirectoryResult Refused(ZipDirectoryRefusal refusal) => new(refusal, []);
}

/// <summary>
///     Reads a zip's structure out of BYTES that were fetched over HTTP range
///     requests, so a partial update can address the interior of a release asset
///     without downloading it.
///     <para>
///         <b>Every number in here is written by the server.</b> Sizes, offsets,
///         name lengths and the member count are all attacker-controlled in the
///         general case, so each one is bounded against the archive length and the
///         buffer actually in hand, and a failure is a REFUSAL — the caller falls
///         back to the full download. Nothing here throws on malformed input and
///         nothing here allocates from a declared length.
///     </para>
///     <para>
///         <b>Zip64 is refused, not handled.</b> The 32-bit records carry
///         <c>0xFFFF</c> / <c>0xFFFFFFFF</c> sentinels when the real values live in
///         a zip64 record, and reading those sentinels as real numbers is how a
///         parser silently addresses the wrong bytes. A DotCharter release is one
///         folder of ~900 members well under 4 GB, so refusing costs nothing and
///         guessing costs correctness. Same decision, same reason as
///         <see cref="Infrastructure.Storage.BoundedZipExtractor.TryReadDeclaredEntryCount" />.
///     </para>
///     Pure: bytes in, records out.
/// </summary>
public static class ZipCentralDirectory
{
    private const uint EocdSignature = 0x06054b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const uint CentralHeaderSignature = 0x02014b50;
    private const uint LocalHeaderSignature = 0x04034b50;

    private const int EocdBytes = 22;
    private const int CentralHeaderBytes = 46;

    /// <summary>Fixed part of a local file header, before the name and extra field.</summary>
    public const int LocalHeaderFixedBytes = 30;

    /// <summary>Largest central directory we will buffer. 16,384 members with
    /// 260-byte names is under 5 MB, so this is roomy for a real payload and still
    /// a hard cap on what a server can make us hold.</summary>
    public const long MaxDirectoryBytes = 8L * 1024 * 1024;

    /// <summary>
    ///     Finds and reads the end-of-central-directory record inside
    ///     <paramref name="tail" />, the LAST <c>tail.Length</c> bytes of an archive
    ///     of <paramref name="archiveLength" /> bytes.
    /// </summary>
    public static ZipEocdResult TryReadEndOfCentralDirectory(ReadOnlySpan<byte> tail, long archiveLength)
    {
        if (archiveLength < EocdBytes || tail.Length < EocdBytes || tail.Length > archiveLength)
            return new ZipEocdResult(ZipDirectoryRefusal.NoEndOfCentralDirectory, null);

        var tailStart = archiveLength - tail.Length;

        for (var i = tail.Length - EocdBytes; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail[i..]) != EocdSignature) continue;

            // The record is the last thing in the file bar its own comment. Without
            // this the scan happily "finds" the signature inside compressed data.
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail[(i + 20)..]);
            if (i + EocdBytes + commentLength != tail.Length) continue;

            var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(tail[(i + 10)..]);
            var directorySize = BinaryPrimitives.ReadUInt32LittleEndian(tail[(i + 12)..]);
            var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail[(i + 16)..]);

            // Both zip64 tells: the sentinels in this record, and the locator that
            // sits immediately in front of it.
            if (entryCount == ushort.MaxValue || directorySize == uint.MaxValue ||
                directoryOffset == uint.MaxValue)
            {
                return new ZipEocdResult(ZipDirectoryRefusal.Zip64, null);
            }

            if (i >= 20 && BinaryPrimitives.ReadUInt32LittleEndian(tail[(i - 20)..]) == Zip64LocatorSignature)
                return new ZipEocdResult(ZipDirectoryRefusal.Zip64, null);

            // Also refused when the disk fields say this is one volume of several.
            if (BinaryPrimitives.ReadUInt16LittleEndian(tail[(i + 4)..]) != 0 ||
                BinaryPrimitives.ReadUInt16LittleEndian(tail[(i + 6)..]) != 0)
            {
                return new ZipEocdResult(ZipDirectoryRefusal.Malformed, null);
            }

            var recordOffset = tailStart + i;

            // WIDEN BEFORE ADDING. Both operands are uint, so `directoryOffset +
            // directorySize` was uint arithmetic and WRAPPED: an offset near
            // 0xFFFFFFFF (legal — only the exact sentinel is rejected above as
            // zip64) plus any size sums to a small number that sails past the
            // "inside the archive" check, and the caller then range-fetches the
            // directory from an offset nothing bounded. The size is capped at 8 MB
            // one line up, so the wrap needs an offset above ~4287 MB — well
            // inside what a server can declare.
            var end = (long)directoryOffset + directorySize;
            if (entryCount == 0 ||
                directorySize == 0 ||
                directorySize > MaxDirectoryBytes ||
                end > recordOffset)
            {
                return new ZipEocdResult(
                    directorySize > MaxDirectoryBytes
                        ? ZipDirectoryRefusal.DirectoryTooLarge
                        : ZipDirectoryRefusal.Malformed,
                    null);
            }

            return new ZipEocdResult(ZipDirectoryRefusal.None,
                new ZipEndOfCentralDirectory(entryCount, directoryOffset, directorySize, recordOffset));
        }

        return new ZipEocdResult(ZipDirectoryRefusal.NoEndOfCentralDirectory, null);
    }

    /// <summary>
    ///     Parses the central directory itself.
    ///     <paramref name="directory" /> must be exactly the
    ///     <see cref="ZipEndOfCentralDirectory.DirectorySize" /> bytes starting at
    ///     <see cref="ZipEndOfCentralDirectory.DirectoryOffset" />.
    /// </summary>
    public static ZipDirectoryResult Parse(
        ReadOnlySpan<byte> directory, ZipEndOfCentralDirectory eocd, long archiveLength, int maxEntries)
    {
        ArgumentNullException.ThrowIfNull(eocd);

        if (eocd.EntryCount > maxEntries) return ZipDirectoryResult.Refused(ZipDirectoryRefusal.TooManyEntries);
        if (directory.Length != eocd.DirectorySize) return ZipDirectoryResult.Refused(ZipDirectoryRefusal.Truncated);

        var entries = new List<ZipDirectoryEntry>(eocd.EntryCount);
        var position = 0;

        for (var index = 0; index < eocd.EntryCount; index++)
        {
            if (position + CentralHeaderBytes > directory.Length)
                return ZipDirectoryResult.Refused(ZipDirectoryRefusal.Truncated);

            var record = directory[position..];
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != CentralHeaderSignature)
                return ZipDirectoryResult.Refused(ZipDirectoryRefusal.Malformed);

            var method = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
            var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(record[20..]);
            var uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(record[24..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[28..]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(record[30..]);
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(record[32..]);
            var diskStart = BinaryPrimitives.ReadUInt16LittleEndian(record[34..]);
            var localHeaderOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[42..]);

            // Any of these three as a sentinel means the real value is in a zip64
            // extra field. Reading the sentinel would address the wrong bytes.
            if (compressedSize == uint.MaxValue || uncompressedSize == uint.MaxValue ||
                localHeaderOffset == uint.MaxValue)
            {
                return ZipDirectoryResult.Refused(ZipDirectoryRefusal.Zip64);
            }

            if (diskStart != 0) return ZipDirectoryResult.Refused(ZipDirectoryRefusal.Malformed);
            if (nameLength == 0) return ZipDirectoryResult.Refused(ZipDirectoryRefusal.Malformed);

            var recordLength = (long)CentralHeaderBytes + nameLength + extraLength + commentLength;
            if (position + recordLength > directory.Length)
                return ZipDirectoryResult.Refused(ZipDirectoryRefusal.Truncated);

            // The member's bytes have to be INSIDE the archive and in front of the
            // directory that describes them.
            if (localHeaderOffset >= eocd.DirectoryOffset ||
                compressedSize > archiveLength ||
                localHeaderOffset + LocalHeaderFixedBytes + nameLength + compressedSize > eocd.DirectoryOffset)
            {
                return ZipDirectoryResult.Refused(ZipDirectoryRefusal.Malformed);
            }

            // Flag bit 11 declares a UTF-8 name and is deliberately not consulted:
            // everything a DotCharter release ships is ASCII, where UTF-8 and CP437
            // agree, and decoding as UTF-8 unconditionally cannot widen the set of
            // names that pass NormalizeEntryName below.
            var name = Encoding.UTF8.GetString(record.Slice(CentralHeaderBytes, nameLength));
            var safeName = NormalizeEntryName(name);
            if (safeName == null) return ZipDirectoryResult.Refused(ZipDirectoryRefusal.UnsafeEntryName);

            entries.Add(new ZipDirectoryEntry(
                safeName, localHeaderOffset, compressedSize, uncompressedSize, method));

            position += (int)recordLength;
        }

        return new ZipDirectoryResult(ZipDirectoryRefusal.None, entries);
    }

    /// <summary>
    ///     Where a member's compressed bytes begin, read from its LOCAL header.
    ///     <para>
    ///         The local header's extra field is NOT the central one's — writers
    ///         routinely put different bytes there — so the data offset cannot be
    ///         derived from the central record and has to be read from the bytes
    ///         actually fetched. Null when <paramref name="header" /> is not a local
    ///         header for a member of <paramref name="expectedCompressedSize" />
    ///         bytes.
    ///     </para>
    /// </summary>
    public static int? TryReadLocalDataOffset(ReadOnlySpan<byte> header, long expectedCompressedSize)
    {
        if (header.Length < LocalHeaderFixedBytes) return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != LocalHeaderSignature) return null;

        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[26..]);
        var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
        var dataOffset = (long)LocalHeaderFixedBytes + nameLength + extraLength;

        return dataOffset + expectedCompressedSize > header.Length ? null : (int)dataOffset;
    }

    /// <summary>
    ///     Forward-slashes a member name and refuses anything that would not be a
    ///     safe relative path. Returns null for a refusal.
    /// </summary>
    public static string? NormalizeEntryName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;

        var path = name.Replace('\\', '/');
        if (path.Any(c => char.IsControl(c))) return null;
        if (path.StartsWith('/') || Path.IsPathRooted(path)) return null;

        // A trailing slash marks a directory member and is legal; an empty segment
        // anywhere else, a "." or a ".." is not.
        var body = path.EndsWith('/') ? path[..^1] : path;
        if (body.Length == 0) return null;
        if (body.Split('/').Any(s => s.Length == 0 || s == "." || s == "..")) return null;

        return path;
    }
}
