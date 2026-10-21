using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using DotCharter.Services.Infrastructure;
using SkiaSharp;

namespace DotCharter.Services.Parsing.Images;

internal static class DdsImageConverter
{
    /// <summary>Largest edge we will decode. Rocksmith album art tops out at
    /// 512; 8192 is generous headroom while still bounding the decode buffer
    /// (8192² × 4 B = 256 MiB) for a file we did not create.</summary>
    private const int MaxDimension = ImageHeaderProbe.MaxDecodeDimension;

    private const int HeaderSize = 128;
    private const int Dx10HeaderSize = 20;
    private const uint DdpfFourCc = 0x4;
    private const uint DdpfRgb = 0x40;

    public static bool IsUsablePng(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            return false;

        try
        {
            using var codec = SKCodec.Create(path);
            return codec != null && codec.Info.Width > 0 && codec.Info.Height > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryConvertToPng(string ddsPath, string pngPath)
    {
        try
        {
            var data = File.ReadAllBytes(ddsPath);
            var image = Decode(data);
            var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);

            using var bitmap = new SKBitmap(info);
            Marshal.Copy(image.Pixels, 0, bitmap.GetPixels(), image.Pixels.Length);

            using var skImage = SKImage.FromBitmap(bitmap);
            using var pngData = skImage.Encode(SKEncodedImageFormat.Png, 100);
            if (pngData == null)
                return false;

            var tempPath = pngPath + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(pngPath) ?? ".");
            using (var output = File.Create(tempPath))
            {
                pngData.SaveTo(output);
            }

            File.Move(tempPath, pngPath, true);
            return IsUsablePng(pngPath);
        }
        catch
        {
            try
            {
                if (File.Exists(pngPath))
                    File.Delete(pngPath);
            }
            catch { /* ignored */ }

            return false;
        }
    }

    private static DdsImage Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || data[0] != 'D' || data[1] != 'D' || data[2] != 'S' || data[3] != ' ')
            throw new InvalidDataException("Invalid DDS file header.");

        var headerLength = BinaryPrimitives.ReadUInt32LittleEndian(data[4..8]);
        if (headerLength != 124)
            throw new InvalidDataException($"Unsupported DDS header length: {headerLength}.");

        var height = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[12..16]));
        var width = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[16..20]));
        if (width <= 0 || height <= 0)
            throw new InvalidDataException($"Invalid DDS dimensions: {width}x{height}.");

        var pixelFlags = BinaryPrimitives.ReadUInt32LittleEndian(data[80..84]);
        var fourCc = BinaryPrimitives.ReadUInt32LittleEndian(data[84..88]);
        var rgbBitCount = BinaryPrimitives.ReadUInt32LittleEndian(data[88..92]);
        var rMask = BinaryPrimitives.ReadUInt32LittleEndian(data[92..96]);
        var gMask = BinaryPrimitives.ReadUInt32LittleEndian(data[96..100]);
        var bMask = BinaryPrimitives.ReadUInt32LittleEndian(data[100..104]);
        var aMask = BinaryPrimitives.ReadUInt32LittleEndian(data[104..108]);

        var dataOffset = HeaderSize;
        if ((pixelFlags & DdpfFourCc) != 0 && fourCc == FourCc("DX10"))
        {
            if (data.Length < HeaderSize + Dx10HeaderSize)
                throw new InvalidDataException("DDS DX10 header is truncated.");

            fourCc = DxgiFormatToFourCc(BinaryPrimitives.ReadUInt32LittleEndian(data[128..132]));
            dataOffset += Dx10HeaderSize;
        }

        if (data.Length <= dataOffset)
            throw new InvalidDataException("DDS image data is missing.");

        // Bound the dimensions BEFORE they size an allocation. These are two
        // raw uint32s out of an untrusted file header, and album art is scanned
        // in bulk during a library import: a 148-byte .dds claiming 16384x16384
        // spiked a gigabyte apiece. `checked` only converted that into an
        // OverflowException after the fact — it never stopped a merely-large
        // (but still absurd) allocation from succeeding.
        if (width is <= 0 or > MaxDimension || height is <= 0 or > MaxDimension)
        {
            throw new InvalidDataException(
                $"DDS dimensions {width}x{height} are outside the supported range (1..{MaxDimension}).");
        }

        var payload = data[dataOffset..];
        var pixels = new byte[checked(width * height * 4)];

        if ((pixelFlags & DdpfFourCc) != 0)
        {
            if (fourCc == FourCc("DXT1"))
                DecodeBc1(payload, width, height, pixels);
            else if (fourCc == FourCc("DXT3"))
                DecodeBc2(payload, width, height, pixels);
            else if (fourCc == FourCc("DXT5"))
                DecodeBc3(payload, width, height, pixels);
            else
                throw new NotSupportedException($"Unsupported DDS compression format: {FourCcToString(fourCc)}.");
        }
        else if ((pixelFlags & DdpfRgb) != 0)
        {
            DecodeUncompressed(payload, width, height, rgbBitCount, rMask, gMask, bMask, aMask, pixels);
        }
        else
        {
            throw new NotSupportedException("Unsupported DDS pixel format.");
        }

        return new DdsImage(width, height, pixels);
    }

    private static uint DxgiFormatToFourCc(uint dxgiFormat)
    {
        return dxgiFormat switch
        {
            70 or 71 or 72 => FourCc("DXT1"),
            73 or 74 or 75 => FourCc("DXT3"),
            76 or 77 or 78 => FourCc("DXT5"),
            _ => throw new NotSupportedException($"Unsupported DDS DXGI format: {dxgiFormat}.")
        };
    }

    private static void DecodeBc1(ReadOnlySpan<byte> data, int width, int height, byte[] pixels) => DecodeBlockCompressed(data, width, height, pixels, 8, DecodeBc1Block);

    private static void DecodeBc2(ReadOnlySpan<byte> data, int width, int height, byte[] pixels) => DecodeBlockCompressed(data, width, height, pixels, 16, DecodeBc2Block);

    private static void DecodeBc3(ReadOnlySpan<byte> data, int width, int height, byte[] pixels) => DecodeBlockCompressed(data, width, height, pixels, 16, DecodeBc3Block);

    private static void DecodeBlockCompressed(
        ReadOnlySpan<byte> data,
        int width,
        int height,
        byte[] pixels,
        int blockBytes,
        DecodeBlockHandler decodeBlock)
    {
        var blocksWide = (width + 3) / 4;
        var blocksHigh = (height + 3) / 4;
        var blockPixels = new Rgba32[16];
        var requiredBytes = checked(blocksWide * blocksHigh * blockBytes);
        if (data.Length < requiredBytes)
            throw new InvalidDataException("DDS image data is truncated.");

        var offset = 0;
        for (var blockY = 0; blockY < blocksHigh; blockY++)
        {
            for (var blockX = 0; blockX < blocksWide; blockX++)
            {
                decodeBlock(data.Slice(offset, blockBytes), blockPixels);
                offset += blockBytes;

                for (var y = 0; y < 4; y++)
                {
                    var py = blockY * 4 + y;
                    if (py >= height) continue;

                    for (var x = 0; x < 4; x++)
                    {
                        var px = blockX * 4 + x;
                        if (px >= width) continue;

                        var source = blockPixels[y * 4 + x];
                        var dest = (py * width + px) * 4;
                        pixels[dest] = source.R;
                        pixels[dest + 1] = source.G;
                        pixels[dest + 2] = source.B;
                        pixels[dest + 3] = source.A;
                    }
                }
            }
        }
    }

    private static void DecodeBc1Block(ReadOnlySpan<byte> block, Span<Rgba32> pixels)
    {
        Span<Rgba32> colors = stackalloc Rgba32[4];
        var color0 = BinaryPrimitives.ReadUInt16LittleEndian(block[..2]);
        var color1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..4]);
        BuildBc1Palette(color0, color1, color0 > color1, colors);

        var indices = BinaryPrimitives.ReadUInt32LittleEndian(block[4..8]);
        for (var i = 0; i < 16; i++)
            pixels[i] = colors[(int)((indices >> (i * 2)) & 0x3)];
    }

    private static void DecodeBc2Block(ReadOnlySpan<byte> block, Span<Rgba32> pixels)
    {
        Span<Rgba32> colors = stackalloc Rgba32[4];
        var color0 = BinaryPrimitives.ReadUInt16LittleEndian(block[8..10]);
        var color1 = BinaryPrimitives.ReadUInt16LittleEndian(block[10..12]);
        BuildBc1Palette(color0, color1, true, colors);

        var indices = BinaryPrimitives.ReadUInt32LittleEndian(block[12..16]);
        for (var i = 0; i < 16; i++)
        {
            var color = colors[(int)((indices >> (i * 2)) & 0x3)];
            var alphaNibble = (block[i / 2] >> (i % 2 * 4)) & 0xF;
            pixels[i] = color with { A = (byte)(alphaNibble * 17) };
        }
    }

    private static void DecodeBc3Block(ReadOnlySpan<byte> block, Span<Rgba32> pixels)
    {
        Span<byte> alphas = stackalloc byte[8];
        alphas[0] = block[0];
        alphas[1] = block[1];
        if (alphas[0] > alphas[1])
        {
            alphas[2] = (byte)((6 * alphas[0] + alphas[1]) / 7);
            alphas[3] = (byte)((5 * alphas[0] + 2 * alphas[1]) / 7);
            alphas[4] = (byte)((4 * alphas[0] + 3 * alphas[1]) / 7);
            alphas[5] = (byte)((3 * alphas[0] + 4 * alphas[1]) / 7);
            alphas[6] = (byte)((2 * alphas[0] + 5 * alphas[1]) / 7);
            alphas[7] = (byte)((alphas[0] + 6 * alphas[1]) / 7);
        }
        else
        {
            alphas[2] = (byte)((4 * alphas[0] + alphas[1]) / 5);
            alphas[3] = (byte)((3 * alphas[0] + 2 * alphas[1]) / 5);
            alphas[4] = (byte)((2 * alphas[0] + 3 * alphas[1]) / 5);
            alphas[5] = (byte)((alphas[0] + 4 * alphas[1]) / 5);
            alphas[6] = 0;
            alphas[7] = 255;
        }

        ulong alphaBits = 0;
        for (var i = 0; i < 6; i++)
            alphaBits |= (ulong)block[2 + i] << (8 * i);

        Span<Rgba32> colors = stackalloc Rgba32[4];
        var color0 = BinaryPrimitives.ReadUInt16LittleEndian(block[8..10]);
        var color1 = BinaryPrimitives.ReadUInt16LittleEndian(block[10..12]);
        BuildBc1Palette(color0, color1, true, colors);

        var indices = BinaryPrimitives.ReadUInt32LittleEndian(block[12..16]);
        for (var i = 0; i < 16; i++)
        {
            var color = colors[(int)((indices >> (i * 2)) & 0x3)];
            var alphaIndex = (int)((alphaBits >> (i * 3)) & 0x7);
            pixels[i] = color with { A = alphas[alphaIndex] };
        }
    }

    private static void BuildBc1Palette(ushort color0, ushort color1, bool fourColorMode, Span<Rgba32> colors)
    {
        colors[0] = DecodeRgb565(color0);
        colors[1] = DecodeRgb565(color1);

        if (fourColorMode)
        {
            colors[2] = new Rgba32(
                (byte)((2 * colors[0].R + colors[1].R) / 3),
                (byte)((2 * colors[0].G + colors[1].G) / 3),
                (byte)((2 * colors[0].B + colors[1].B) / 3),
                255);
            colors[3] = new Rgba32(
                (byte)((colors[0].R + 2 * colors[1].R) / 3),
                (byte)((colors[0].G + 2 * colors[1].G) / 3),
                (byte)((colors[0].B + 2 * colors[1].B) / 3),
                255);
        }
        else
        {
            colors[2] = new Rgba32(
                (byte)((colors[0].R + colors[1].R) / 2),
                (byte)((colors[0].G + colors[1].G) / 2),
                (byte)((colors[0].B + colors[1].B) / 2),
                255);
            colors[3] = new Rgba32(0, 0, 0, 0);
        }
    }

    private static Rgba32 DecodeRgb565(ushort value)
    {
        var r = (value >> 11) & 0x1F;
        var g = (value >> 5) & 0x3F;
        var b = value & 0x1F;
        return new Rgba32(
            (byte)((r << 3) | (r >> 2)),
            (byte)((g << 2) | (g >> 4)),
            (byte)((b << 3) | (b >> 2)),
            255);
    }

    private static void DecodeUncompressed(
        ReadOnlySpan<byte> data,
        int width,
        int height,
        uint bitCount,
        uint rMask,
        uint gMask,
        uint bMask,
        uint aMask,
        byte[] pixels)
    {
        if (bitCount is not (24 or 32))
            throw new NotSupportedException($"Unsupported DDS RGB bit depth: {bitCount}.");

        var bytesPerPixel = checked((int)bitCount / 8);
        var rowBytes = checked(width * bytesPerPixel);
        var requiredBytes = checked(rowBytes * height);
        if (data.Length < requiredBytes)
            throw new InvalidDataException("DDS RGB image data is truncated.");

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var source = y * rowBytes + x * bytesPerPixel;
                var value = bytesPerPixel switch
                {
                    3 => (uint)(data[source] | (data[source + 1] << 8) | (data[source + 2] << 16)),
                    4 => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(source, 4)),
                    _ => throw new UnreachableException($"Unexpected bytesPerPixel: {bytesPerPixel}.")
                };

                var dest = (y * width + x) * 4;
                pixels[dest] = ExtractChannel(value, rMask);
                pixels[dest + 1] = ExtractChannel(value, gMask);
                pixels[dest + 2] = ExtractChannel(value, bMask);
                pixels[dest + 3] = aMask != 0 ? ExtractChannel(value, aMask) : (byte)255;
            }
        }
    }

    private static byte ExtractChannel(uint value, uint mask)
    {
        if (mask == 0)
            return 0;

        var shift = BitOperations.TrailingZeroCount(mask);
        var max = mask >> shift;
        var raw = (value & mask) >> shift;
        if (max == 0)
            return 0;

        return (byte)(raw * 255UL / max);
    }

    private static uint FourCc(string value)
    {
        if (value.Length != 4)
            // ReSharper disable once LocalizableElement -- developer-facing argument validation.
            throw new ArgumentException("DDS FourCC values must be four characters long.", nameof(value));

        return value[0] |
               ((uint)value[1] << 8) |
               ((uint)value[2] << 16) |
               ((uint)value[3] << 24);
    }

    private static string FourCcToString(uint value)
    {
        return new string(
        [
            (char)(value & 0xFF),
            (char)((value >> 8) & 0xFF),
            (char)((value >> 16) & 0xFF),
            (char)((value >> 24) & 0xFF)
        ]);
    }

    private readonly record struct DdsImage(int Width, int Height, byte[] Pixels);

    private readonly record struct Rgba32(byte R, byte G, byte B, byte A);

    private delegate void DecodeBlockHandler(ReadOnlySpan<byte> block, Span<Rgba32> pixels);
}
