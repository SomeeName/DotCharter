using System.Buffers.Binary;
using DotCharter.Services.Export.Images;
using DotCharter.Services.Parsing.Images;
using DotCharter.Tone.Infrastructure.Storage;
using SkiaSharp;

namespace DotCharter.Tests.Services.Export;

public sealed class DdsEncoderTests : IDisposable
{
    private readonly string _tempDirectory =
        Directory.CreateTempSubdirectory("dds-encoder-tests-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDirectory);

    [Fact]
    public void EncodeDxt1_WritesExpectedHeaderLayout()
    {
        const int size = 64;
        using var bitmap = CreateQuadrantBitmap(size);

        var dds = DdsEncoder.EncodeDxt1(bitmap);

        var expectedDataSize = size * size / 2;
        Assert.Equal(128 + expectedDataSize, dds.Length);

        // Magic "DDS ".
        Assert.Equal((byte)'D', dds[0]);
        Assert.Equal((byte)'D', dds[1]);
        Assert.Equal((byte)'S', dds[2]);
        Assert.Equal((byte)' ', dds[3]);

        // dwSize = 124.
        Assert.Equal(124u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(4)));

        // dwFlags = CAPS | HEIGHT | WIDTH | PIXELFORMAT | LINEARSIZE.
        Assert.Equal(0x1u | 0x2u | 0x4u | 0x1000u | 0x80000u,
            BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(8)));

        // Height, width, linear size.
        Assert.Equal((uint)size, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(12)));
        Assert.Equal((uint)size, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(16)));
        Assert.Equal((uint)expectedDataSize, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(20)));

        // No mipmaps.
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(28)));

        // Pixel format: size 32, flags FOURCC, fourCC "DXT1".
        Assert.Equal(32u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(76)));
        Assert.Equal(0x4u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(80)));
        Assert.Equal("DXT1", System.Text.Encoding.ASCII.GetString(dds, 84, 4));

        // dwCaps = DDSCAPS_TEXTURE.
        Assert.Equal(0x1000u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(108)));
    }

    [Fact]
    public void EncodeDxt1_RoundTripsThroughDecoder_WithSmallColorError()
    {
        const int size = 64;
        using var original = CreateQuadrantBitmap(size);

        var dds = DdsEncoder.EncodeDxt1(original);

        var ddsPath = Path.Combine(_tempDirectory, "roundtrip.dds");
        var pngPath = Path.Combine(_tempDirectory, "roundtrip.png");
        File.WriteAllBytes(ddsPath, dds);

        Assert.True(DdsImageConverter.TryConvertToPng(ddsPath, pngPath));

        using var decoded = SKBitmap.Decode(pngPath);
        Assert.NotNull(decoded);
        Assert.Equal(size, decoded.Width);
        Assert.Equal(size, decoded.Height);

        const int tolerance = 12;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var expected = original.GetPixel(x, y);
                var actual = decoded.GetPixel(x, y);
                Assert.True(Math.Abs(expected.Red - actual.Red) <= tolerance,
                    $"Red mismatch at ({x},{y}): expected {expected.Red}, got {actual.Red}");
                Assert.True(Math.Abs(expected.Green - actual.Green) <= tolerance,
                    $"Green mismatch at ({x},{y}): expected {expected.Green}, got {actual.Green}");
                Assert.True(Math.Abs(expected.Blue - actual.Blue) <= tolerance,
                    $"Blue mismatch at ({x},{y}): expected {expected.Blue}, got {actual.Blue}");
                Assert.Equal(255, actual.Alpha);
            }
        }
    }

    [Theory]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(256)]
    public void EncodeCoverArt_ResizesNonSquareSourceToRequestedSize(int size)
    {
        var sourcePath = Path.Combine(_tempDirectory, "source.png");
        WriteNonSquareSourcePng(sourcePath, width: 300, height: 170);

        var dds = DdsEncoder.EncodeCoverArt(sourcePath, size);

        Assert.Equal(128 + size * size / 2, dds.Length);
        Assert.Equal((uint)size, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(12)));
        Assert.Equal((uint)size, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(16)));
        Assert.Equal("DXT1", System.Text.Encoding.ASCII.GetString(dds, 84, 4));

        // The result must decode cleanly with the existing DDS reader.
        var ddsPath = Path.Combine(_tempDirectory, $"cover{size}.dds");
        var pngPath = Path.Combine(_tempDirectory, $"cover{size}.png");
        File.WriteAllBytes(ddsPath, dds);
        Assert.True(DdsImageConverter.TryConvertToPng(ddsPath, pngPath));

        using var decoded = SKBitmap.Decode(pngPath);
        Assert.NotNull(decoded);
        Assert.Equal(size, decoded.Width);
        Assert.Equal(size, decoded.Height);
    }

    private static SKBitmap CreateQuadrantBitmap(int size)
    {
        var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var half = size / 2;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var color = (x < half, y < half) switch
                {
                    (true, true) => new SKColor(255, 0, 0),
                    (false, true) => new SKColor(0, 255, 0),
                    (true, false) => new SKColor(0, 0, 255),
                    (false, false) => new SKColor(255, 255, 255)
                };
                bitmap.SetPixel(x, y, color);
            }
        }

        return bitmap;
    }

    private static void WriteNonSquareSourcePng(string path, int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, new SKColor(
                    (byte)(x * 255 / Math.Max(1, width - 1)),
                    (byte)(y * 255 / Math.Max(1, height - 1)),
                    128));
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }
}
