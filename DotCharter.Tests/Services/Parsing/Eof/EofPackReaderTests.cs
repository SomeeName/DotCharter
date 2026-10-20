using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Eof;

public class EofPackReaderTests
{
    [Fact]
    public void ReadU8_AdvancesPositionAndReturnsByte()
    {
        var reader = new EofPackReader([0x12, 0x34]);
        Assert.Equal(0x12, reader.ReadU8());
        Assert.Equal(1, reader.Position);
        Assert.Equal(0x34, reader.ReadU8());
    }

    [Fact]
    public void ReadU16_IsLittleEndian()
    {
        var reader = new EofPackReader([0x34, 0x12]);
        Assert.Equal(0x1234, reader.ReadU16());
    }

    [Fact]
    public void ReadU32_IsLittleEndian()
    {
        var reader = new EofPackReader([0x78, 0x56, 0x34, 0x12]);
        Assert.Equal(0x12345678u, reader.ReadU32());
    }

    [Fact]
    public void ReadString_LengthPrefixed_ReadsUtf8Bytes()
    {
        // u16 length=5, "Hello"
        var reader = new EofPackReader(
        [
            0x05, 0x00,
            (byte)'H', (byte)'e', (byte)'l', (byte)'l', (byte)'o'
        ]);
        Assert.Equal("Hello", reader.ReadString());
    }

    [Fact]
    public void ReadString_ZeroLength_ReturnsEmpty()
    {
        var reader = new EofPackReader([0x00, 0x00]);
        Assert.Equal(string.Empty, reader.ReadString());
    }

    [Fact]
    public void ReadFixedString_StopsAtNullTerminator()
    {
        var reader = new EofPackReader(
        [
            (byte)'E', (byte)'O', (byte)'F', 0x00, 0x99
        ]);
        Assert.Equal("EOF", reader.ReadFixedString(5));
        Assert.Equal(5, reader.Position);
    }

    [Fact]
    public void Skip_ClampsToEndOfStream()
    {
        var reader = new EofPackReader([0x01, 0x02]);
        reader.Skip(100);
        Assert.True(reader.Eof);
    }

    [Fact]
    public void ReadU8_PastEnd_Throws()
    {
        var reader = new EofPackReader([]);
        Assert.Throws<EndOfStreamException>(() => reader.ReadU8());
    }

    /// <summary>
    ///     A `.eof` block size is read straight off disk, so <c>count</c> is
    ///     file-supplied. Both bounds used to be written
    ///     <c>Position + count &gt; _data.Length</c>, which OVERFLOWS int and
    ///     goes NEGATIVE for a declared size near <c>int.MaxValue</c> — so the
    ///     comparison came out false and the guard did the exact opposite of its
    ///     intent. In <c>Skip</c> that skipped the clamp and left a NEGATIVE
    ///     cursor, which <c>ReadU8</c>'s <c>Position &gt;= Length</c> test then
    ///     accepts.
    /// </summary>
    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(int.MaxValue - 1)]
    [InlineData(0x7000_0000)]
    public void Skip_WithAnOverflowingDeclaredSize_StillClampsToTheEnd(int declared)
    {
        var reader = new EofPackReader([0x01, 0x02, 0x03, 0x04]);
        reader.ReadU8(); // Position = 1, so Position + int.MaxValue overflows

        reader.Skip(declared);

        Assert.Equal(4, reader.Position);
        Assert.True(reader.Position >= 0, $"cursor went negative: {reader.Position}");
        Assert.True(reader.Eof);
        Assert.Throws<EndOfStreamException>(() => reader.ReadU8());
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(int.MaxValue - 1)]
    [InlineData(0x7000_0000)]
    public void ReadBytes_WithAnOverflowingDeclaredSize_ThrowsInsteadOfAllocatingGigabytes(int declared)
    {
        var reader = new EofPackReader([0x01, 0x02, 0x03, 0x04]);
        reader.ReadU8();

        Assert.Throws<EndOfStreamException>(() => reader.ReadBytes(declared));
        Assert.Equal(1, reader.Position);
    }

    [Fact]
    public void ReadBytes_WithANegativeCount_Throws()
    {
        var reader = new EofPackReader([0x01, 0x02]);
        Assert.Throws<EndOfStreamException>(() => reader.ReadBytes(-1));
    }

    [Fact]
    public void ReadBytes_ToTheExactEnd_StillSucceeds()
    {
        // The bound must stay inclusive of the last byte.
        var reader = new EofPackReader([0x01, 0x02, 0x03]);
        reader.ReadU8();
        Assert.Equal([0x02, 0x03], reader.ReadBytes(2));
        Assert.True(reader.Eof);
    }
}
