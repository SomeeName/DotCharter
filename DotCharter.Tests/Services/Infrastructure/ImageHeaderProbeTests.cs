using System.Buffers.Binary;
using DotCharter.Services.Infrastructure;

namespace DotCharter.Tests.Services.Infrastructure;

/// <summary>
///     <see cref="ImageHeaderProbe" /> is the one thing standing between an
///     unauthenticated avatar file and a decoder asked to allocate for it, so
///     every format it claims to read is driven here with a hand-built header,
///     and a real PNG (from <see cref="TinyPng" />) proves the PNG arm against
///     bytes a decoder would accept.
/// </summary>
public sealed class ImageHeaderProbeTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(64, 64)]
    [InlineData(1, 100_000)]
    [InlineData(4000, 3)]
    public void ARealPng_ReportsItsDeclaredSize(int w, int h)
    {
        var png = TinyPng.Encode(w, h);
        Assert.True(ImageHeaderProbe.TryReadPixelSize(png, out var pw, out var ph));
        Assert.Equal((w, h), (pw, ph));
        Assert.Equal("png", ImageHeaderProbe.DescribeFormat(png));
    }

    [Fact]
    public void AStream_IsProbedAndRewound()
    {
        using var stream = new MemoryStream(TinyPng.Encode(7, 9));
        stream.Position = 0;
        Assert.True(ImageHeaderProbe.TryReadPixelSize(stream, out var w, out var h));
        Assert.Equal((7, 9), (w, h));
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void Gif_ReadsTheLogicalScreen()
    {
        var gif = new byte[13];
        "GIF89a"u8.CopyTo(gif);
        BinaryPrimitives.WriteUInt16LittleEndian(gif.AsSpan(6, 2), 320);
        BinaryPrimitives.WriteUInt16LittleEndian(gif.AsSpan(8, 2), 200);
        Assert.True(ImageHeaderProbe.TryReadPixelSize(gif, out var w, out var h));
        Assert.Equal((320, 200), (w, h));
    }

    [Theory]
    [InlineData(480, 640, 480, 640)]
    [InlineData(480, -640, 480, 640)] // top-down DIB: negative height is still 640 rows
    public void Bmp_ReadsTheInfoHeader(int width, int height, int expectedW, int expectedH)
    {
        var bmp = new byte[54];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(14, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22, 4), height);
        Assert.True(ImageHeaderProbe.TryReadPixelSize(bmp, out var w, out var h));
        Assert.Equal((expectedW, expectedH), (w, h));
    }

    [Fact]
    public void WebP_ReadsAllThreeChunkShapes()
    {
        // VP8X: 24-bit canvas width-1 / height-1.
        var vp8x = new byte[30];
        "RIFF"u8.CopyTo(vp8x);
        "WEBP"u8.CopyTo(vp8x.AsSpan(8));
        "VP8X"u8.CopyTo(vp8x.AsSpan(12));
        vp8x[24] = 0xFF; vp8x[25] = 0x00; vp8x[26] = 0x00; // 256
        vp8x[27] = 0x3F; vp8x[28] = 0x00; vp8x[29] = 0x00; // 64
        Assert.True(ImageHeaderProbe.TryReadPixelSize(vp8x, out var w, out var h));
        Assert.Equal((256, 64), (w, h));

        // VP8L: signature 0x2F then 14+14 bits, each minus one.
        var vp8l = new byte[30];
        "RIFF"u8.CopyTo(vp8l);
        "WEBP"u8.CopyTo(vp8l.AsSpan(8));
        "VP8L"u8.CopyTo(vp8l.AsSpan(12));
        vp8l[20] = 0x2F;
        BinaryPrimitives.WriteUInt32LittleEndian(vp8l.AsSpan(21, 4), (99u - 1) | ((49u - 1) << 14));
        Assert.True(ImageHeaderProbe.TryReadPixelSize(vp8l, out w, out h));
        Assert.Equal((99, 49), (w, h));

        // VP8 key frame: start code 9D 01 2A, then u16 W,H.
        var vp8 = new byte[30];
        "RIFF"u8.CopyTo(vp8);
        "WEBP"u8.CopyTo(vp8.AsSpan(8));
        "VP8 "u8.CopyTo(vp8.AsSpan(12));
        vp8[23] = 0x9D; vp8[24] = 0x01; vp8[25] = 0x2A;
        BinaryPrimitives.WriteUInt16LittleEndian(vp8.AsSpan(26, 2), 300);
        BinaryPrimitives.WriteUInt16LittleEndian(vp8.AsSpan(28, 2), 150);
        Assert.True(ImageHeaderProbe.TryReadPixelSize(vp8, out w, out h));
        Assert.Equal((300, 150), (w, h));
    }

    [Fact]
    public void Jpeg_SkipsAppSegmentsToTheFrameHeader()
    {
        // SOI, an APP0 segment of 16 bytes, then SOF0 carrying H=600 W=800.
        var jpg = new List<byte> { 0xFF, 0xD8 };
        jpg.AddRange([0xFF, 0xE0, 0x00, 0x10]);
        jpg.AddRange(new byte[14]);
        jpg.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08]);
        jpg.AddRange([0x02, 0x58, 0x03, 0x20]); // 600, 800
        jpg.AddRange(new byte[10]);
        Assert.True(ImageHeaderProbe.TryReadPixelSize(jpg.ToArray(), out var w, out var h));
        Assert.Equal((800, 600), (w, h));
        Assert.Equal("jpeg", ImageHeaderProbe.DescribeFormat(jpg.ToArray()));
    }

    [Fact]
    public void Jpeg_WithNoFrameHeaderBeforeScan_IsUnreadable()
    {
        byte[] jpg = [0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x02, 0x00, 0x00];
        Assert.False(ImageHeaderProbe.TryReadPixelSize(jpg, out _, out _));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47 })] // truncated PNG signature
    [InlineData(new byte[] { (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    public void Garbage_IsUnreadable(byte[] bytes)
    {
        Assert.False(ImageHeaderProbe.TryReadPixelSize(bytes, out var w, out var h));
        Assert.Equal((0, 0), (w, h));
        Assert.Equal("unknown", ImageHeaderProbe.DescribeFormat(bytes));
    }

    [Fact]
    public void APngDeclaringZeroPixels_IsUnreadable()
    {
        var png = TinyPng.Encode(3, 3);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16, 4), 0);
        Assert.False(ImageHeaderProbe.TryReadPixelSize(png, out _, out _));
    }
}
