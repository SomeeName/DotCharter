using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Eof;

public class EofPackWriterTests
{
    [Fact]
    public void WriteU16_IsLittleEndian()
    {
        var writer = new EofPackWriter();
        writer.WriteU16(0x1234);
        Assert.Equal(new byte[] { 0x34, 0x12 }, writer.ToArray());
    }

    [Fact]
    public void WriteU32_IsLittleEndian()
    {
        var writer = new EofPackWriter();
        writer.WriteU32(0x12345678u);
        Assert.Equal(new byte[] { 0x78, 0x56, 0x34, 0x12 }, writer.ToArray());
    }

    [Fact]
    public void WriteString_LengthPrefixed_Utf8()
    {
        var writer = new EofPackWriter();
        writer.WriteString("Hello");
        Assert.Equal(new byte[] { 0x05, 0x00, (byte)'H', (byte)'e', (byte)'l', (byte)'l', (byte)'o' },
            writer.ToArray());
    }

    [Fact]
    public void WriteString_NullOrEmpty_WritesZeroLength()
    {
        var writer = new EofPackWriter();
        writer.WriteString(null);
        writer.WriteString("");
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00 }, writer.ToArray());
    }

    [Fact]
    public void WriteFixedString_PadsWithNulBytes()
    {
        var writer = new EofPackWriter();
        writer.WriteFixedString("EOF", 5);
        Assert.Equal(new byte[] { (byte)'E', (byte)'O', (byte)'F', 0x00, 0x00 }, writer.ToArray());
    }

    [Fact]
    public void RoundTrip_ThroughPackReader()
    {
        var writer = new EofPackWriter();
        writer.WriteU8(0xAB);
        writer.WriteU16(0xBEEF);
        writer.WriteU32(0xDEADBEEF);
        writer.WriteString("Grüße"); // Multi-byte UTF-8.
        writer.WriteFixedString("EOFSONH", 16);

        var reader = new EofPackReader(writer.ToArray());
        Assert.Equal(0xAB, reader.ReadU8());
        Assert.Equal(0xBEEF, reader.ReadU16());
        Assert.Equal(0xDEADBEEFu, reader.ReadU32());
        Assert.Equal("Grüße", reader.ReadString());
        Assert.Equal("EOFSONH", reader.ReadFixedString(16));
        Assert.True(reader.Eof);
    }
}
