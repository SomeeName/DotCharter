using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Services.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio;

/// <summary>ZIP64 offsets are virtual: no large file or allocation is needed to validate a large directory offset.</summary>
public sealed class Zip64DirectoryBudgetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_zip64_directory_above_four_GiB_is_bounded_before_indexing(bool sentinelCount)
    {
        const long start = (long)uint.MaxValue + 1025;
        var record = new byte[47];
        BinaryPrimitives.WriteUInt32LittleEndian(record, 0x02014b50);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(28), 1);
        record[46] = (byte)'x';
        var tail = EndRecords(start, record.Length, sentinelCount);
        using var stream = new SparseDirectoryStream(start, record, tail);
        Assert.True(stream.Length > uint.MaxValue);
        Assert.InRange(record.Length + tail.Length, 1, 256);
        Assert.Equal(ZipExtractionRefusal.None, ZipDirectoryBudget.Check(stream, 64));
    }

    [Fact]
    public void A_small_zip64_stem_archive_reads_its_manifest_normally()
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open(), Encoding.UTF8))
            writer.Write(new StemCacheManifest { Key = "zip64" }.ToJson());
        var original = bytes.ToArray();
        var end = original.AsSpan(original.Length - 22);
        var start = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
        var tail = EndRecords(start, size, sentinelCount: true);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dcstems");
        try
        {
            File.WriteAllBytes(path, [.. original.AsSpan(0, original.Length - 22), .. tail]);
            Assert.Equal("zip64", StemArchive.ReadManifest(path)?.Key);
        }
        finally { File.Delete(path); }
    }

    private static byte[] EndRecords(long start, long size, bool sentinelCount)
    {
        var bytes = new byte[98];
        var end64 = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(end64, 0x06064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(end64[4..], 44);
        BinaryPrimitives.WriteUInt16LittleEndian(end64[12..], 45);
        BinaryPrimitives.WriteUInt16LittleEndian(end64[14..], 45);
        BinaryPrimitives.WriteUInt64LittleEndian(end64[24..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(end64[32..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(end64[40..], (ulong)size);
        BinaryPrimitives.WriteUInt64LittleEndian(end64[48..], (ulong)start);
        var locator = bytes.AsSpan(56);
        BinaryPrimitives.WriteUInt32LittleEndian(locator, 0x07064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(locator[8..], (ulong)(start + size));
        BinaryPrimitives.WriteUInt32LittleEndian(locator[16..], 1);
        var end = bytes.AsSpan(76);
        BinaryPrimitives.WriteUInt32LittleEndian(end, 0x06054b50);
        var count = sentinelCount ? ushort.MaxValue : (ushort)1;
        BinaryPrimitives.WriteUInt16LittleEndian(end[8..], count);
        BinaryPrimitives.WriteUInt16LittleEndian(end[10..], count);
        BinaryPrimitives.WriteUInt32LittleEndian(end[12..], uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(end[16..], uint.MaxValue);
        return bytes;
    }

    private sealed class SparseDirectoryStream(long start, byte[] directory, byte[] tail) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => start + directory.Length + tail.Length;
        public override long Position { get; set; }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin)
            => Position = (origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? Position : Length) + offset;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, Length - Position);
            var span = buffer[..count];
            span.Clear();
            Copy(directory, start, span);
            Copy(tail, start + directory.Length, span);
            Position += count;
            return count;
        }

        private void Copy(byte[] source, long at, Span<byte> destination)
        {
            var from = Math.Max(Position, at);
            var to = Math.Min(Position + destination.Length, at + source.Length);
            if (to > from) source.AsSpan((int)(from - at), (int)(to - from)).CopyTo(destination[(int)(from - Position)..]);
        }
    }
}
