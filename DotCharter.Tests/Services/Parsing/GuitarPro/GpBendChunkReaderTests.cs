using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

public class GpBendChunkReaderTests
{
    private static GpLegacyBinaryReader MakeReader(byte[] payload)
        => new(new MemoryStream(payload));

    private static byte[] BuildChunk(byte type, int height, params (int Offset, int Value, byte Vibrato)[] points)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(type);
        WriteInt32(bw, height);
        WriteInt32(bw, points.Length);
        foreach (var (offset, value, vibrato) in points)
        {
            WriteInt32(bw, offset);
            WriteInt32(bw, value);
            bw.Write(vibrato);
        }
        return ms.ToArray();
    }

    private static void WriteInt32(BinaryWriter bw, int v) =>
        // GpLegacyBinaryReader is little-endian (BinaryReader-default), matching BinaryWriter.
        bw.Write(v);

    [Fact]
    public void Skip_AdvancesPastEntireChunk()
    {
        var chunk = BuildChunk(1, 100, (0, 0, 0), (30, 50, 0), (60, 100, 0));
        var trailing = new byte[] { 0xAB };
        var bytes = chunk.Concat(trailing).ToArray();
        var reader = MakeReader(bytes);

        GpBendChunkReader.Skip(reader);

        Assert.Equal(0xAB, reader.ReadByte());
    }

    [Fact]
    public void ReadPoints_ConvertsValuesByFiftieths()
    {
        var chunk = BuildChunk(1, 100, (0, 0, 0), (30, 50, 0), (60, 100, 0));
        var reader = MakeReader(chunk);

        var pts = GpBendChunkReader.ReadPoints(reader);

        Assert.Equal(3, pts.Count);
        Assert.Equal((0, 0.0), pts[0]);
        Assert.Equal((30, 1.0), pts[1]);
        Assert.Equal((60, 2.0), pts[2]);
    }

    [Fact]
    public void ReadPoints_EmptyChunk_ReturnsEmptyList()
    {
        var reader = MakeReader(BuildChunk(1, 0));
        var pts = GpBendChunkReader.ReadPoints(reader);
        Assert.Empty(pts);
    }

    [Fact]
    public void AppendPoints_AppendsToExistingList()
    {
        var existing = new List<(int Offset, double Step)> { (0, 0.0) };
        var listRef = existing;
        var reader = MakeReader(BuildChunk(1, 100, (15, 25, 0), (30, 75, 0)));

        GpBendChunkReader.AppendPoints(reader, ref listRef);

        Assert.Same(existing, listRef);
        Assert.Equal(3, listRef!.Count);
        Assert.Equal((15, 0.5), listRef[1]);
        Assert.Equal((30, 1.5), listRef[2]);
    }

    [Fact]
    public void AppendPoints_AllocatesListWhenNull()
    {
        List<(int Offset, double Step)>? listRef = null;
        var reader = MakeReader(BuildChunk(1, 100, (10, 50, 0)));

        GpBendChunkReader.AppendPoints(reader, ref listRef);

        Assert.NotNull(listRef);
        Assert.Single(listRef);
        Assert.Equal((10, 1.0), listRef[0]);
    }
}
