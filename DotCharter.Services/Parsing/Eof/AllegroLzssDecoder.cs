namespace DotCharter.Services.Parsing.Eof;

/// <summary>
///     Decompresses an Allegro 4 PACKFILE byte stream (LZSS variant by Okumura).
///     Allegro stores compressed files starting with the four-byte magic
///     <c>0x73 0x6C 0x68 0x21</c> ('slh!') followed by an LZSS-compressed payload
///     using N=4096 sliding-window, F=18 max-match, THRESHOLD=2.
/// </summary>
public static class AllegroLzssDecoder
{
    private const int N = 4096;
    private const int F = 18;
    private const int Threshold = 2;

    /// <summary>
    ///     Bytes one <c>slh!</c> payload may decompress to.
    ///     <para>
    ///         The loop consumes its input monotonically, so the output is not
    ///         infinite — but its worst case is 8 flag-selected match pairs
    ///         (16 bytes) plus one flag byte producing 8 × <see cref="F" /> = 144
    ///         bytes, i.e. ~8.5×, and <see cref="MemoryStream.ToArray" /> then
    ///         DOUBLES the peak. A 200 MB crafted `.eof` therefore cost ~1.7 GB of
    ///         stream plus a ~1.7 GB copy, with nothing between it and an OOM.
    ///     </para>
    ///     <para>
    ///         Real Editor-on-Fire projects are single-digit megabytes, so 256 MiB
    ///         is roughly two orders of magnitude of headroom — this exists to
    ///         stop the crafted case, not to police a large chart.
    ///     </para>
    /// </summary>
    public const int MaxDecodedBytes = 256 * 1024 * 1024;

    public static byte[] Decode(byte[] raw) => Decode(raw, MaxDecodedBytes);

    /// <summary>Same decode with an explicit ceiling. Test seam: reaching the
    /// shipped <see cref="MaxDecodedBytes" /> costs half a gigabyte of
    /// allocation, so the LIMIT is a parameter here and its shipped VALUE is
    /// asserted separately.</summary>
    internal static byte[] Decode(byte[] raw, int maxDecodedBytes)
    {
        if (raw.Length < 4) return raw;

        if (HasMagic(raw, EofConstants.AllegroNopackMagic))
        {
            // Uncompressed-with-magic variant: skip 4 magic bytes and return rest.
            var copy = new byte[raw.Length - 4];
            Buffer.BlockCopy(raw, 4, copy, 0, copy.Length);
            return copy;
        }

        if (!HasMagic(raw, EofConstants.AllegroPackMagic))
            return raw; // Assume already uncompressed.

        return Decompress(raw, 4, maxDecodedBytes);
    }

    private static bool HasMagic(byte[] data, byte[] magic)
    {
        for (var i = 0; i < magic.Length; i++)
        {
            if (data[i] != magic[i])
                return false;
        }

        return true;
    }

    private static byte[] Decompress(byte[] src, int srcOffset, int maxDecodedBytes)
    {
        var window = new byte[N];
        for (var k = 0; k < N - F; k++) window[k] = (byte)' ';

        var r = N - F;
        var i = srcOffset;
        // long math: src.Length * 4 overflows int for inputs > ~512 MB and
        // a negative capacity throws instead of reporting a corrupt file.
        var output = new MemoryStream((int)Math.Min(Math.Min((long)src.Length * 4, 16 << 20), maxDecodedBytes));
        var flags = 0;

        while (true)
        {
            flags >>= 1;
            if ((flags & 0x100) == 0)
            {
                if (i >= src.Length) break;
                flags = src[i++] | 0xFF00;
            }

            if ((flags & 1) != 0)
            {
                if (i >= src.Length) break;
                var c = src[i++];
                output.WriteByte(c);
                window[r++] = c;
                r &= N - 1;
            }
            else
            {
                if (i >= src.Length) break;
                int b1 = src[i++];
                if (i >= src.Length) break;
                int b2 = src[i++];

                var pos = b1 | ((b2 & 0xF0) << 4);
                var len = (b2 & 0x0F) + Threshold + 1;

                for (var k = 0; k < len; k++)
                {
                    var c = window[(pos + k) & (N - 1)];
                    output.WriteByte(c);
                    window[r++] = c;
                    r &= N - 1;
                }
            }

            // Checked per ITERATION rather than per byte: the inner match loop
            // adds at most F bytes, so the overshoot is bounded and the hot path
            // stays one comparison per token.
            if (output.Length > maxDecodedBytes)
            {
                throw new InvalidDataException(
                    $"Allegro LZSS payload decompressed past {maxDecodedBytes} bytes from a " +
                    $"{src.Length}-byte source — corrupt or malicious file.");
            }
        }

        return output.ToArray();
    }
}
