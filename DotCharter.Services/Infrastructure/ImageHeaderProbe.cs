using System.Buffers.Binary;

namespace DotCharter.Services.Infrastructure;

/// <summary>
///     Reads the pixel dimensions out of a raster file's HEADER — PNG, JPEG,
///     GIF, BMP and WebP — without decoding a single pixel and without any image
///     library, so <c>DotCharter.Services</c> stays image-free and the check can
///     run BEFORE a decoder is asked to allocate.
///     <para>
///         Exists because "decode to width 96" bounds only one axis: a 1 × 100,000
///         PNG (a few hundred compressed bytes, well inside every byte cap) decodes
///         to 96 × 9,600,000 — ~3.7 GB of RGBA — and both the LAN avatar cache and
///         the profile picture path would hand exactly such a file to the decoder.
///         The formats here are the ones a decoder on the receiving end would
///         accept; anything else is reported as unrecognised and the caller
///         decides.
///     </para>
/// </summary>
public static class ImageHeaderProbe
{
    /// <summary>Bytes a caller needs to hand in for every format here to be
    /// decidable. JPEG is the outlier — its SOF marker sits behind APP/COM
    /// segments (EXIF thumbnails, ICC profiles) that routinely run to tens of
    /// KB, so a probe reads this much and gives up past it.</summary>
    public const int MaxProbeBytes = 128 * 1024;

    /// <summary>
    ///     True with the declared pixel size for a recognised PNG / JPEG / GIF /
    ///     BMP / WebP header. False for anything else — an unknown format, a
    ///     truncated header, or a header whose declared size is nonsensical
    ///     (zero or negative).
    /// </summary>
    public static bool TryReadPixelSize(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (TryPng(bytes, out width, out height) || TryGif(bytes, out width, out height) ||
            TryBmp(bytes, out width, out height) || TryWebP(bytes, out width, out height) ||
            TryJpeg(bytes, out width, out height))
        {
            return width > 0 && height > 0;
        }

        width = 0;
        height = 0;
        return false;
    }

    /// <summary>Header probe over a seekable stream: reads up to
    /// <see cref="MaxProbeBytes" /> from the current position and REWINDS to it,
    /// so the same stream can go straight on to the decoder.</summary>
    public static bool TryReadPixelSize(Stream stream, out int width, out int height)
    {
        ArgumentNullException.ThrowIfNull(stream);
        width = 0;
        height = 0;
        if (!stream.CanRead || !stream.CanSeek) return false;

        var origin = stream.Position;
        var buffer = new byte[(int)Math.Min(MaxProbeBytes, Math.Max(0, stream.Length - origin))];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n <= 0) break;
            read += n;
        }

        stream.Position = origin;
        return TryReadPixelSize(buffer.AsSpan(0, read), out width, out height);
    }

    // ---- PNG: 8-byte signature, then IHDR whose first 8 data bytes are W,H (BE) ----

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static bool TryPng(ReadOnlySpan<byte> b, out int w, out int h)
    {
        w = h = 0;
        if (b.Length < 24 || !b[..8].SequenceEqual(PngSignature)) return false;
        if (!b.Slice(12, 4).SequenceEqual("IHDR"u8)) return false;
        var uw = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(16, 4));
        var uh = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(20, 4));
        if (uw == 0 || uh == 0 || uw > int.MaxValue || uh > int.MaxValue) return false;
        w = (int)uw;
        h = (int)uh;
        return true;
    }

    // ---- GIF: "GIF87a"/"GIF89a", then logical screen W,H (LE u16) ----

    private static bool TryGif(ReadOnlySpan<byte> b, out int w, out int h)
    {
        w = h = 0;
        if (b.Length < 10) return false;
        if (!b[..6].SequenceEqual("GIF87a"u8) && !b[..6].SequenceEqual("GIF89a"u8)) return false;
        w = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(6, 2));
        h = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(8, 2));
        return w > 0 && h > 0;
    }

    // ---- BMP: "BM", DIB header at 14; BITMAPINFOHEADER+ carries i32 W,H (H may be negative = top-down) ----

    private static bool TryBmp(ReadOnlySpan<byte> b, out int w, out int h)
    {
        w = h = 0;
        if (b.Length < 26 || b[0] != (byte)'B' || b[1] != (byte)'M') return false;
        var dibSize = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(14, 4));
        if (dibSize == 12)
        {
            // BITMAPCOREHEADER: u16 W,H
            w = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(18, 2));
            h = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(20, 2));
            return w > 0 && h > 0;
        }

        if (dibSize < 16) return false;
        var sw = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(18, 4));
        var sh = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(22, 4));
        if (sw <= 0 || sh == 0 || sh == int.MinValue) return false;
        w = sw;
        h = Math.Abs(sh);
        return true;
    }

    // ---- WebP: RIFF....WEBP then VP8 / VP8L / VP8X chunk ----

    private static bool TryWebP(ReadOnlySpan<byte> b, out int w, out int h)
    {
        w = h = 0;
        if (b.Length < 30 || !b[..4].SequenceEqual("RIFF"u8) || !b.Slice(8, 4).SequenceEqual("WEBP"u8))
            return false;
        var chunk = b.Slice(12, 4);
        if (chunk.SequenceEqual("VP8X"u8))
        {
            // 24-bit canvas width-1 / height-1 at offsets 24 and 27.
            w = (b[24] | (b[25] << 8) | (b[26] << 16)) + 1;
            h = (b[27] | (b[28] << 8) | (b[29] << 16)) + 1;
            return true;
        }

        if (chunk.SequenceEqual("VP8L"u8))
        {
            if (b.Length < 25 || b[20] != 0x2F) return false;
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(21, 4));
            w = (int)(bits & 0x3FFF) + 1;
            h = (int)((bits >> 14) & 0x3FFF) + 1;
            return true;
        }

        if (chunk.SequenceEqual("VP8 "u8))
        {
            // Key frame: 3-byte frame tag, 3-byte start code (9D 01 2A), then u16 W,H (14 bits each).
            if (b.Length < 30 || b[23] != 0x9D || b[24] != 0x01 || b[25] != 0x2A) return false;
            w = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(26, 2)) & 0x3FFF;
            h = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(28, 2)) & 0x3FFF;
            return w > 0 && h > 0;
        }

        return false;
    }

    // ---- JPEG: SOI, then segments until a SOFn (C0–CF except C4/C8/CC) carrying H,W (BE u16) ----

    private static bool TryJpeg(ReadOnlySpan<byte> b, out int w, out int h)
    {
        w = h = 0;
        if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8) return false;
        var i = 2;
        while (i + 4 <= b.Length)
        {
            if (b[i] != 0xFF)
            {
                i++; // padding / garbage between segments
                continue;
            }

            var marker = b[i + 1];
            if (marker == 0xFF)
            {
                i++; // fill byte
                continue;
            }

            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                i += 2; // standalone marker, no length
                continue;
            }

            if (marker is 0xD9 or 0xDA) return false; // EOI / SOS before any SOF

            var length = BinaryPrimitives.ReadUInt16BigEndian(b.Slice(i + 2, 2));
            if (length < 2) return false;
            var isSof = marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;
            if (isSof)
            {
                if (i + 9 > b.Length) return false;
                h = BinaryPrimitives.ReadUInt16BigEndian(b.Slice(i + 5, 2));
                w = BinaryPrimitives.ReadUInt16BigEndian(b.Slice(i + 7, 2));
                return w > 0 && h > 0;
            }

            i += 2 + length;
        }

        return false;
    }

    /// <summary>Diagnostic label for logs: the format the header was recognised
    /// as, or "unknown".</summary>
    public static string DescribeFormat(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(PngSignature)) return "png";
        if (bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
            return "gif";
        if (bytes.Length >= 2 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M') return "bmp";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
            return "webp";
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xD8) return "jpeg";
        return "unknown";
    }
}
