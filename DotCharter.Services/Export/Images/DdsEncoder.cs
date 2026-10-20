using System.Buffers.Binary;
using SkiaSharp;

namespace DotCharter.Services.Export.Images;

/// <summary>
/// Managed DXT1 (BC1) DDS encoder for Rocksmith 2014 album art.
/// Writes a 128-byte DDS header (no mipmaps) followed by 4-color-mode BC1 blocks.
/// </summary>
public static class DdsEncoder
{
    private const int HeaderSize = 128;
    private const int BlockBytes = 8;

    // DDS_HEADER dwFlags
    private const uint DdsdCaps = 0x1;
    private const uint DdsdHeight = 0x2;
    private const uint DdsdWidth = 0x4;
    private const uint DdsdPixelFormat = 0x1000;
    private const uint DdsdLinearSize = 0x80000;

    // DDS_PIXELFORMAT dwFlags / DDS_HEADER dwCaps
    private const uint DdpfFourCc = 0x4;
    private const uint DdsCapsTexture = 0x1000;

    /// <summary>
    /// Encodes the bitmap as a complete DXT1-compressed DDS file (header + BC1 blocks, no mipmaps).
    /// </summary>
    public static byte[] EncodeDxt1(SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        var width = bitmap.Width;
        var height = bitmap.Height;
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Bitmap must have positive dimensions.", nameof(bitmap));

        var blocksWide = (width + 3) / 4;
        var blocksHigh = (height + 3) / 4;
        var dataSize = checked(blocksWide * blocksHigh * BlockBytes);

        var output = new byte[HeaderSize + dataSize];
        WriteHeader(output, width, height, dataSize);

        Span<uint> blockPixels = stackalloc uint[16];
        var offset = HeaderSize;

        for (var blockY = 0; blockY < blocksHigh; blockY++)
        {
            for (var blockX = 0; blockX < blocksWide; blockX++)
            {
                for (var y = 0; y < 4; y++)
                {
                    var py = Math.Min(blockY * 4 + y, height - 1);
                    for (var x = 0; x < 4; x++)
                    {
                        var px = Math.Min(blockX * 4 + x, width - 1);
                        var color = bitmap.GetPixel(px, py);
                        blockPixels[y * 4 + x] = (uint)(color.Red | (color.Green << 8) | (color.Blue << 16));
                    }
                }

                EncodeBc1Block(blockPixels, output.AsSpan(offset, BlockBytes));
                offset += BlockBytes;
            }
        }

        return output;
    }

    /// <summary>
    /// Loads an image, resizes it to <paramref name="size"/> x <paramref name="size"/>
    /// (ignoring the source aspect ratio) and encodes it as a DXT1 DDS file.
    /// </summary>
    public static byte[] EncodeCoverArt(string sourceImagePath, int size)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceImagePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        using var source = Decode(sourceImagePath);
        return EncodeCoverArt(source, size, sourceImagePath);
    }

    /// <summary>
    ///     Decodes the cover art ONCE and encodes it at every requested size.
    ///     <para>
    ///         A package needs three (64/128/256), and calling the single-size
    ///         overload three times decoded the same JPEG/PNG three times — the
    ///         decode is the whole cost: 63 ms of the 189 ms album-art phase per
    ///         size on a 1.25 MB cover, i.e. essentially all of it. Resizing the
    ///         one decoded bitmap produces the SAME bytes, because the
    ///         single-size overload resizes from exactly this bitmap too.
    ///     </para>
    /// </summary>
    public static IEnumerable<(int Size, byte[] Data)> EncodeCoverArtSizes(
        string sourceImagePath, IReadOnlyList<int> sizes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceImagePath);
        ArgumentNullException.ThrowIfNull(sizes);
        foreach (var size in sizes) ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        using var source = Decode(sourceImagePath);
        var results = new List<(int, byte[])>(sizes.Count);
        foreach (var size in sizes) results.Add((size, EncodeCoverArt(source, size, sourceImagePath)));
        return results;
    }

    private static SKBitmap Decode(string sourceImagePath) =>
        SKBitmap.Decode(sourceImagePath)
        ?? throw new InvalidDataException($"Could not decode image: {sourceImagePath}");

    private static byte[] EncodeCoverArt(SKBitmap source, int size, string sourceImagePath)
    {
        if (source.Width == size && source.Height == size)
            return EncodeDxt1(source);

        var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var resized = source.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell))
            ?? throw new InvalidDataException($"Could not resize image: {sourceImagePath}");

        return EncodeDxt1(resized);
    }

    private static void WriteHeader(Span<byte> output, int width, int height, int linearSize)
    {
        output[0] = (byte)'D';
        output[1] = (byte)'D';
        output[2] = (byte)'S';
        output[3] = (byte)' ';

        BinaryPrimitives.WriteUInt32LittleEndian(output[4..], 124);
        BinaryPrimitives.WriteUInt32LittleEndian(
            output[8..], DdsdCaps | DdsdHeight | DdsdWidth | DdsdPixelFormat | DdsdLinearSize);
        BinaryPrimitives.WriteUInt32LittleEndian(output[12..], (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(output[16..], (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(output[20..], (uint)linearSize);
        // dwDepth (24), dwMipMapCount (28), dwReserved1[11] (32..75): zero.

        // DDS_PIXELFORMAT at offset 76.
        BinaryPrimitives.WriteUInt32LittleEndian(output[76..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(output[80..], DdpfFourCc);
        output[84] = (byte)'D';
        output[85] = (byte)'X';
        output[86] = (byte)'T';
        output[87] = (byte)'1';
        // dwRGBBitCount and channel masks (88..107): zero for FourCC formats.

        BinaryPrimitives.WriteUInt32LittleEndian(output[108..], DdsCapsTexture);
        // dwCaps2..dwReserved2 (112..127): zero.
    }

    /// <summary>
    /// Compresses one 4x4 block of packed 0x00BBGGRR pixels into 8 BC1 bytes (4-color mode).
    /// </summary>
    private static void EncodeBc1Block(ReadOnlySpan<uint> pixels, Span<byte> block)
    {
        // Bounding box of the block's colors.
        int minR = 255, minG = 255, minB = 255;
        int maxR = 0, maxG = 0, maxB = 0;
        for (var i = 0; i < 16; i++)
        {
            var r = (int)(pixels[i] & 0xFF);
            var g = (int)((pixels[i] >> 8) & 0xFF);
            var b = (int)((pixels[i] >> 16) & 0xFF);
            if (r < minR) minR = r;
            if (g < minG) minG = g;
            if (b < minB) minB = b;
            if (r > maxR) maxR = r;
            if (g > maxG) maxG = g;
            if (b > maxB) maxB = b;
        }

        // Project every pixel onto the min->max axis and take the extremes as endpoints,
        // so outlier-heavy blocks still pick actual block colors as endpoints.
        var axisR = maxR - minR;
        var axisG = maxG - minG;
        var axisB = maxB - minB;

        var loDot = int.MaxValue;
        var hiDot = int.MinValue;
        var loColor = pixels[0];
        var hiColor = pixels[0];
        for (var i = 0; i < 16; i++)
        {
            var r = (int)(pixels[i] & 0xFF);
            var g = (int)((pixels[i] >> 8) & 0xFF);
            var b = (int)((pixels[i] >> 16) & 0xFF);
            var dot = r * axisR + g * axisG + b * axisB;
            if (dot < loDot)
            {
                loDot = dot;
                loColor = pixels[i];
            }
            if (dot > hiDot)
            {
                hiDot = dot;
                hiColor = pixels[i];
            }
        }

        var color0 = EncodeRgb565(hiColor);
        var color1 = EncodeRgb565(loColor);

        if (color0 == color1)
        {
            // Effectively a solid block: a single palette entry represents it exactly.
            BinaryPrimitives.WriteUInt16LittleEndian(block[..2], color0);
            BinaryPrimitives.WriteUInt16LittleEndian(block[2..4], color1);
            BinaryPrimitives.WriteUInt32LittleEndian(block[4..8], 0);
            return;
        }

        // Always use 4-color mode: color0 must be strictly greater than color1.
        if (color0 < color1)
            (color0, color1) = (color1, color0);

        // Build the palette exactly like a BC1 decoder does, then map pixels to the nearest entry.
        Span<int> paletteR = stackalloc int[4];
        Span<int> paletteG = stackalloc int[4];
        Span<int> paletteB = stackalloc int[4];
        DecodeRgb565(color0, out paletteR[0], out paletteG[0], out paletteB[0]);
        DecodeRgb565(color1, out paletteR[1], out paletteG[1], out paletteB[1]);
        paletteR[2] = (2 * paletteR[0] + paletteR[1]) / 3;
        paletteG[2] = (2 * paletteG[0] + paletteG[1]) / 3;
        paletteB[2] = (2 * paletteB[0] + paletteB[1]) / 3;
        paletteR[3] = (paletteR[0] + 2 * paletteR[1]) / 3;
        paletteG[3] = (paletteG[0] + 2 * paletteG[1]) / 3;
        paletteB[3] = (paletteB[0] + 2 * paletteB[1]) / 3;

        uint indices = 0;
        for (var i = 0; i < 16; i++)
        {
            var r = (int)(pixels[i] & 0xFF);
            var g = (int)((pixels[i] >> 8) & 0xFF);
            var b = (int)((pixels[i] >> 16) & 0xFF);

            var bestIndex = 0;
            var bestError = int.MaxValue;
            for (var p = 0; p < 4; p++)
            {
                var dr = r - paletteR[p];
                var dg = g - paletteG[p];
                var db = b - paletteB[p];
                var error = dr * dr + dg * dg + db * db;
                if (error < bestError)
                {
                    bestError = error;
                    bestIndex = p;
                }
            }

            indices |= (uint)bestIndex << (i * 2);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(block[..2], color0);
        BinaryPrimitives.WriteUInt16LittleEndian(block[2..4], color1);
        BinaryPrimitives.WriteUInt32LittleEndian(block[4..8], indices);
    }

    private static ushort EncodeRgb565(uint packedRgb)
    {
        var r = (int)(packedRgb & 0xFF);
        var g = (int)((packedRgb >> 8) & 0xFF);
        var b = (int)((packedRgb >> 16) & 0xFF);
        return (ushort)((((r * 31 + 127) / 255) << 11) |
                        (((g * 63 + 127) / 255) << 5) |
                        ((b * 31 + 127) / 255));
    }

    private static void DecodeRgb565(ushort value, out int r, out int g, out int b)
    {
        var r5 = (value >> 11) & 0x1F;
        var g6 = (value >> 5) & 0x3F;
        var b5 = value & 0x1F;
        r = (r5 << 3) | (r5 >> 2);
        g = (g6 << 2) | (g6 >> 4);
        b = (b5 << 3) | (b5 >> 2);
    }
}
