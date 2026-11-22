using System.Buffers.Binary;
using System.IO.Compression;
using DotCharter.Services.Infrastructure.Storage;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Infrastructure;

/// <summary>The framework's selected directory is the oracle, including embedded signature bytes.</summary>
public sealed class ZipEndRecordSelectionTests
{
    public static TheoryData<int> Seeds
    {
        get
        {
            var cases = new TheoryData<int>();
            for (var i = 0; i < 200; i++) cases.Add(0x5b00 + i);
            return cases;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidStoredPayload_WithSignatureOrNestedZip_IsAcceptedAndExtracts(bool nested)
    {
        var payload = nested ? Create([[1, 2, 3]], CompressionLevel.NoCompression) : new byte[64];
        if (!nested) BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(24), 0x06054b50);
        var bytes = Create([[7], payload], CompressionLevel.NoCompression);
        AssertMatchesRuntime(bytes, 16);
        using var directory = new TestDirectory();
        var archive = Path.Combine(directory.Path, "outer.zip");
        File.WriteAllBytes(archive, bytes);
        var destination = Path.Combine(directory.Path, "out");
        var extracted = BoundedZipExtractor.ExtractBounded(archive, destination, 1024 * 1024, 16);
        Assert.Equal(ZipExtractionRefusal.None, extracted.Refusal);
        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(destination, "entry1")));
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void SeededEntriesCommentsAndSignatures_AgreeWithRuntime(int seed)
    {
        var random = new Random(seed);
        var entries = new List<byte[]>();
        var entryCount = random.Next(1, 7);
        for (var i = 0; i < entryCount; i++)
        {
            var payload = new byte[random.Next(32, 257)];
            random.NextBytes(payload);
            if (seed % 3 != 0) BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(random.Next(payload.Length - 3)), 0x06054b50);
            entries.Add(payload);
        }
        if (seed % 5 == 0) entries[^1] = Create([[42]], CompressionLevel.NoCompression);
        var bytes = Create(entries, seed % 4 == 0 ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
        var mode = seed % 8;
        var comment = new byte[random.Next(0, 64)];
        random.NextBytes(comment);
        if (mode is 2 or 3 or 4 or 5)
        {
            comment = new byte[22 + random.Next(0, 9)];
            bytes.AsSpan(bytes.Length - 22, 22).CopyTo(comment);
            if (mode == 3) SetCount(comment, (ushort)(entries.Count + 1));
            if (mode == 4)
            {
                SetCount(comment, 0);
                BinaryPrimitives.WriteUInt32LittleEndian(comment.AsSpan(12), 0);
                BinaryPrimitives.WriteUInt32LittleEndian(comment.AsSpan(16), 0);
            }
            if (mode == 5) BinaryPrimitives.WriteUInt16LittleEndian(comment.AsSpan(20), 50);
        }
        if (mode == 6) comment = [0x50, 0x4b, 0x05, 0x06];
        AssertMatchesRuntime(WithComment(bytes, comment), 4);
    }

    [Fact]
    public void Zip64DiskSentinel_UsesTheAdjacentLocator()
    {
        var original = Create([[42]], CompressionLevel.NoCompression);
        var at = original.Length - 22;
        var tail = new byte[98];
        BinaryPrimitives.WriteUInt32LittleEndian(tail, 0x06064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(tail.AsSpan(4), 44);
        BinaryPrimitives.WriteUInt64LittleEndian(tail.AsSpan(24), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(tail.AsSpan(32), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(tail.AsSpan(40), BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(at + 12)));
        BinaryPrimitives.WriteUInt64LittleEndian(tail.AsSpan(48), BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(at + 16)));
        BinaryPrimitives.WriteUInt32LittleEndian(tail.AsSpan(56), 0x07064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(tail.AsSpan(64), (ulong)at);
        BinaryPrimitives.WriteUInt32LittleEndian(tail.AsSpan(72), 1);
        original.AsSpan(at, 22).CopyTo(tail.AsSpan(76));
        BinaryPrimitives.WriteUInt16LittleEndian(tail.AsSpan(80), ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(tail.AsSpan(82), ushort.MaxValue);
        AssertMatchesRuntime([.. original.AsSpan(0, at), .. tail], 16);
    }

    internal static void AssertMatchesRuntime(byte[] bytes, int limit)
    {
        Assert.InRange(bytes.Length, 1, 8192);
        var expected = RuntimeCount(bytes);
        using var stream = new MemoryStream(bytes);
        var outcome = ZipDirectoryBudget.Check(stream, limit);
        if (expected is null)
        {
            Assert.NotEqual(ZipExtractionRefusal.None, outcome);
            return;
        }
        if (expected > limit)
        {
            Assert.Equal(ZipExtractionRefusal.TooManyEntries, outcome);
            return;
        }
        Assert.Equal(ZipExtractionRefusal.None, outcome);
        Assert.Equal((ulong)expected.Value, ZipDirectoryMetadata.Read(stream)!.Value.Count);
    }

    private static int? RuntimeCount(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        try
        {
            using var zip = new ZipArchive(stream);
            return zip.Entries.Count;
        }
        catch (InvalidDataException) { return null; }
    }

    private static byte[] Create(IEnumerable<byte[]> entries, CompressionLevel compression)
    {
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            var i = 0;
            foreach (var payload in entries)
            {
                using var entry = archive.CreateEntry("entry" + i++, compression).Open();
                entry.Write(payload);
            }
        }
        return bytes.ToArray();
    }

    private static byte[] WithComment(byte[] bytes, byte[] comment)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 2), (ushort)comment.Length);
        return [.. bytes, .. comment];
    }

    private static void SetCount(Span<byte> record, ushort count)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(record[8..], count);
        BinaryPrimitives.WriteUInt16LittleEndian(record[10..], count);
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("zip-selection-").FullName;
        public void Dispose() => BestEffortDelete.Directory(Path);
    }
}
