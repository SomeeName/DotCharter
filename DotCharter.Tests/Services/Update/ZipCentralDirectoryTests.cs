using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using DotCharter.Services.Update;

namespace DotCharter.Tests.Services.Update;

/// <summary>
///     The zip structures a partial update reads out of a REMOTE asset, over HTTP
///     ranges. Every number in them is written by the server, so the parser's job
///     is as much refusing as reading — and a refusal is what makes the updater
///     fall back to the full download instead of addressing the wrong bytes.
/// </summary>
public sealed class ZipCentralDirectoryTests
{
    // ---- against a real zip -----------------------------------------------

    [Fact]
    public void ARealZip_ParsesToItsMembersWithUsableOffsets()
    {
        var zip = BuildZipOfSizes(("a.txt", 100), ("dir/b.bin", 5000));

        var result = ReadDirectory(zip, maxEntries: 64);

        Assert.True(result.Succeeded, result.Refusal.ToString());
        Assert.Equal(new[] { "a.txt", "dir/b.bin" }, result.Entries.Select(e => e.Name).Order().ToArray());
        foreach (var entry in result.Entries)
        {
            Assert.True(entry.LocalHeaderOffset >= 0);
            Assert.True(entry.CompressedSize > 0);
            // The data offset is only knowable from the LOCAL header — the local
            // extra field is not the central one, so it cannot be derived.
            var offset = ZipCentralDirectory.TryReadLocalDataOffset(
                zip.AsSpan((int)entry.LocalHeaderOffset), entry.CompressedSize);
            Assert.NotNull(offset);
        }
    }

    /// <summary>The bytes a member's local header points at really do inflate to
    /// the content that went in — otherwise every offset above is decoration.</summary>
    [Fact]
    public void AMembersBytesRoundTripThroughItsLocalHeader()
    {
        var content = Encoding.UTF8.GetBytes(new string('x', 4096) + "tail");
        var zip = BuildZip(("payload.bin", content));

        var entry = Assert.Single(ReadDirectory(zip, 64).Entries);
        var dataOffset = ZipCentralDirectory.TryReadLocalDataOffset(
            zip.AsSpan((int)entry.LocalHeaderOffset), entry.CompressedSize);

        using var compressed = new MemoryStream(
            zip, (int)entry.LocalHeaderOffset + dataOffset!.Value, (int)entry.CompressedSize);
        using var inflate = new DeflateStream(compressed, CompressionMode.Decompress);
        using var inflated = new MemoryStream();
        inflate.CopyTo(inflated);

        Assert.Equal(content, inflated.ToArray());
    }

    [Fact]
    public void NotAZip_IsRefusedRatherThanThrown()
    {
        var junk = Filler(512);

        var result = ZipCentralDirectory.TryReadEndOfCentralDirectory(junk, junk.Length);

        Assert.Equal(ZipDirectoryRefusal.NoEndOfCentralDirectory, result.Refusal);
    }

    /// <summary>The record has to be the LAST thing in the file bar its own
    /// comment. Without that check the scan "finds" the signature inside
    /// compressed data and reads four random bytes as a directory offset.</summary>
    [Fact]
    public void ASignatureBuriedInsideTheDataIsNotMistakenForTheRecord()
    {
        var tail = new byte[128];
        BinaryPrimitives.WriteUInt32LittleEndian(tail.AsSpan(20), 0x06054b50);

        Assert.Equal(ZipDirectoryRefusal.NoEndOfCentralDirectory,
            ZipCentralDirectory.TryReadEndOfCentralDirectory(tail, tail.Length).Refusal);
    }

    // ---- hostile end-of-central-directory records --------------------------

    [Fact]
    public void AZip64EntryCountSentinel_IsRefusedNotRead()
    {
        var tail = Eocd(entryCount: ushort.MaxValue, directorySize: 100, directoryOffset: 200);

        Assert.Equal(ZipDirectoryRefusal.Zip64,
            ZipCentralDirectory.TryReadEndOfCentralDirectory(tail, 1000).Refusal);
    }

    [Theory]
    [InlineData(uint.MaxValue, 200u)]
    [InlineData(100u, uint.MaxValue)]
    public void AZip64SizeOrOffsetSentinel_IsRefused(uint directorySize, uint directoryOffset)
    {
        var tail = Eocd(2, directorySize, directoryOffset);

        Assert.Equal(ZipDirectoryRefusal.Zip64,
            ZipCentralDirectory.TryReadEndOfCentralDirectory(tail, 100_000).Refusal);
    }

    /// <summary>A zip64 archive whose 32-bit record happens to fit still carries the
    /// locator immediately in front of it, and that is equally disqualifying.</summary>
    [Fact]
    public void AZip64LocatorInFrontOfTheRecord_IsRefused()
    {
        var record = Eocd(2, 100, 200);
        var tail = new byte[20 + record.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(tail, 0x07064b50);
        record.CopyTo(tail, 20);

        Assert.Equal(ZipDirectoryRefusal.Zip64,
            ZipCentralDirectory.TryReadEndOfCentralDirectory(tail, 100_000).Refusal);
    }

    [Fact]
    public void ADirectoryBiggerThanWeWillBuffer_IsRefused()
    {
        var tail = Eocd(2, (uint)(ZipCentralDirectory.MaxDirectoryBytes + 1), 10);

        Assert.Equal(ZipDirectoryRefusal.DirectoryTooLarge,
            ZipCentralDirectory.TryReadEndOfCentralDirectory(tail, 1_000_000_000).Refusal);
    }

    /// <summary>A directory that claims to extend past its own record describes
    /// bytes that are not in the archive.</summary>
    [Fact]
    public void ADirectoryRunningPastItsOwnRecord_IsRefused()
    {
        var tail = Eocd(2, directorySize: 500, directoryOffset: 900);

        Assert.Equal(ZipDirectoryRefusal.Malformed,
            ZipCentralDirectory.TryReadEndOfCentralDirectory(tail, 1000).Refusal);
    }

    /// <summary>
    ///     The bounds check added the offset to the size in <c>uint</c> arithmetic,
    ///     so the sum WRAPPED. Both are server-declared: an offset near
    ///     <c>0xFFFFFFFF</c> is legal here (only the exact sentinel is rejected, as
    ///     the zip64 tell) and any size then sums to a small number that sails past
    ///     "the directory lies inside the archive" — after which the caller
    ///     range-fetches a directory from an offset nothing bounded. The size cap
    ///     one line above means the wrap needs an offset above ~4287 MB, which is
    ///     just as declarable as any other.
    /// </summary>
    [Fact]
    public void ADirectoryOffsetThatWrapsWhenAddedToItsSize_IsRefused()
    {
        // 0xFFFFF000 + 0x2000 == 0x1000 in uint arithmetic — comfortably "inside"
        // a 1 MB archive, and 4 GB outside it in reality.
        var tail = Eocd(2, directorySize: 0x2000, directoryOffset: 0xFFFFF000);

        var result = ZipCentralDirectory.TryReadEndOfCentralDirectory(tail, 1_000_000);

        Assert.Equal(ZipDirectoryRefusal.Malformed, result.Refusal);
        Assert.Null(result.Record);
    }

    [Fact]
    public void AMultiDiskArchive_IsRefused()
    {
        var tail = Eocd(2, 100, 200, disk: 1);

        Assert.Equal(ZipDirectoryRefusal.Malformed,
            ZipCentralDirectory.TryReadEndOfCentralDirectory(tail, 100_000).Refusal);
    }

    // ---- hostile central directories ---------------------------------------

    [Fact]
    public void MoreMembersThanAllowed_IsRefusedBeforeAnythingIsAllocated()
    {
        var (bytes, record) = Directory(("a.dll", 0u, 10u, 10u, 8));

        Assert.Equal(ZipDirectoryRefusal.TooManyEntries,
            ZipCentralDirectory.Parse(bytes, record with { EntryCount = 99 }, 10_000, maxEntries: 8).Refusal);
    }

    [Fact]
    public void ARecordThatRunsOffTheEndOfTheBuffer_IsTruncatedNotRead()
    {
        var (bytes, record) = Directory(("a.dll", 0u, 10u, 10u, 8));

        Assert.Equal(ZipDirectoryRefusal.Truncated,
            ZipCentralDirectory.Parse(bytes.AsSpan(0, bytes.Length - 4), record with { DirectorySize = bytes.Length - 4 },
                10_000, 64).Refusal);
    }

    [Fact]
    public void AMemberWhoseBytesLieBehindTheDirectory_IsRefused()
    {
        // Local header offset past the directory offset: the member's bytes are
        // not where a member's bytes can be.
        var (bytes, record) = Directory(("a.dll", 9_000u, 10u, 10u, 8));

        Assert.Equal(ZipDirectoryRefusal.Malformed,
            ZipCentralDirectory.Parse(bytes, record, 10_000, 64).Refusal);
    }

    [Fact]
    public void AZip64SentinelOnAMember_IsRefused()
    {
        var (bytes, record) = Directory(("a.dll", 0u, uint.MaxValue, 10u, 8));

        Assert.Equal(ZipDirectoryRefusal.Zip64,
            ZipCentralDirectory.Parse(bytes, record, 10_000_000, 64).Refusal);
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("/rooted.dll")]
    [InlineData("a/../../b.dll")]
    [InlineData("a//b.dll")]
    public void AMemberNameThatWouldEscapeThePayload_IsRefused(string name)
    {
        var (bytes, record) = Directory((name, 0u, 10u, 10u, 8));

        Assert.Equal(ZipDirectoryRefusal.UnsafeEntryName,
            ZipCentralDirectory.Parse(bytes, record, 10_000, 64).Refusal);
    }

    [Fact]
    public void ABackslashedNameIsNormalised_AndADirectoryMemberSurvives()
    {
        Assert.Equal("a/b.dll", ZipCentralDirectory.NormalizeEntryName(@"a\b.dll"));
        Assert.Equal("runtimes/", ZipCentralDirectory.NormalizeEntryName("runtimes/"));
        Assert.Null(ZipCentralDirectory.NormalizeEntryName("/"));
        Assert.Null(ZipCentralDirectory.NormalizeEntryName(""));
    }

    // ---- local headers ------------------------------------------------------

    [Fact]
    public void ALocalHeaderWithoutItsSignature_YieldsNoDataOffset()
    {
        var header = new byte[64];

        Assert.Null(ZipCentralDirectory.TryReadLocalDataOffset(header, 10));
    }

    /// <summary>A header whose declared name + extra field push the data past the
    /// bytes we fetched must not hand back an offset — reading there would read
    /// somebody else's member.</summary>
    [Fact]
    public void ALocalHeaderClaimingMoreRoomThanWeFetched_YieldsNoDataOffset()
    {
        var header = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x04034b50);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), 8);   // name
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), 100); // extra

        Assert.Null(ZipCentralDirectory.TryReadLocalDataOffset(header, 4));
    }

    // ---- fixtures -----------------------------------------------------------

    private static ZipDirectoryResult ReadDirectory(byte[] zip, int maxEntries)
    {
        var eocd = ZipCentralDirectory.TryReadEndOfCentralDirectory(zip, zip.Length);
        Assert.True(eocd.Succeeded, eocd.Refusal.ToString());
        var record = eocd.Record!;
        return ZipCentralDirectory.Parse(
            zip.AsSpan((int)record.DirectoryOffset, (int)record.DirectorySize), record, zip.Length, maxEntries);
    }

    private static byte[] BuildZipOfSizes(params (string Name, int Size)[] members)
        => BuildZip(members.Select(m => (m.Name, Filler(m.Size))).ToArray());

    private static byte[] BuildZip(params (string Name, byte[] Content)[] members)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in members)
            {
                using var entry = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
                entry.Write(content);
            }
        }

        return stream.ToArray();
    }

    private static byte[] Filler(int size)
    {
        // Compressible but not trivially so, and never empty — a zero-length member
        // has a zero-length compressed body and proves nothing about offsets.
        var bytes = new byte[size];
        for (var i = 0; i < size; i++) bytes[i] = (byte)(i % 61);
        return bytes;
    }

    private static byte[] Eocd(int entryCount, uint directorySize, uint directoryOffset, ushort disk = 0)
    {
        var record = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(record, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), disk);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), disk);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(8), (ushort)entryCount);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(10), (ushort)entryCount);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(12), directorySize);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(16), directoryOffset);
        return record;
    }

    /// <summary>Hand-built central directory — the only way to reach the refusals a
    /// real zip writer can never produce.</summary>
    private static (byte[] Bytes, ZipEndOfCentralDirectory Record) Directory(
        params (string Name, uint Offset, uint Compressed, uint Uncompressed, ushort Method)[] members)
    {
        using var stream = new MemoryStream();
        foreach (var (name, offset, compressed, uncompressed, method) in members)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var record = new byte[46 + nameBytes.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(record, 0x02014b50);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(10), method);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(20), compressed);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24), uncompressed);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(28), (ushort)nameBytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(42), offset);
            nameBytes.CopyTo(record.AsSpan(46));
            stream.Write(record);
        }

        var bytes = stream.ToArray();
        return (bytes, new ZipEndOfCentralDirectory(members.Length, 8000, bytes.Length, 8000 + bytes.Length));
    }
}
