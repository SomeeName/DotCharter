using DotCharter.Services.Infrastructure.Psarc.Write;

namespace DotCharter.Tests.Services.Parsing.Psarc;

public sealed partial class PsarcWriterTests
{
    [Fact]
    public void Rejects_entries_with_empty_names()
    {
        using var output = new MemoryStream();
        Assert.Throws<ArgumentException>(() =>
            PsarcWriter.Write(output, [new PsarcWriteEntry("", "x"u8.ToArray())]));
    }

    [Fact]
    public void Header_matches_the_official_layout()
    {
        var path = TempPsarcPath();
        PsarcWriter.WriteFile(path, [new PsarcWriteEntry("a.txt", "a"u8.ToArray())]);

        var bytes = File.ReadAllBytes(path);
        Assert.Equal("PSAR"u8.ToArray(), bytes[..4]);
        Assert.Equal([0, 1, 0, 4], bytes[4..8]); // v1.4, big-endian uint16s
        Assert.Equal("zlib"u8.ToArray(), bytes[8..12]);
        // ToC entry size = 30
        Assert.Equal([0, 0, 0, 30], bytes[16..20]);
        // Entry count = 2 (manifest + a.txt)
        Assert.Equal([0, 0, 0, 2], bytes[20..24]);
        // Block size alloc = 65536
        Assert.Equal([0, 1, 0, 0], bytes[24..28]);
        // Encrypted TOC flag
        Assert.Equal([0, 0, 0, 4], bytes[28..32]);
    }
}
