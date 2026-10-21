using System.Text;

namespace DotCharter.Services.Infrastructure;

/// <summary>
///     Cuts a string to a byte budget without splitting a UTF-8 code point, for the binary
///     formats that carry a fixed or length-prefixed name field (SNG, EOF).
/// </summary>
internal static class Utf8Truncation
{
    /// <summary>The UTF-8 bytes of <paramref name="value" />, at most <paramref name="maxBytes" /> of
    /// them, ending on a code-point boundary (never on a lead byte or inside a sequence).</summary>
    public static byte[] GetBytes(string value, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maxBytes) return bytes;
        return bytes[..BoundaryAtOrBefore(bytes, Math.Max(0, maxBytes))];
    }

    /// <summary>The largest length at or below <paramref name="limit" /> that does not cut a sequence:
    /// a continuation byte (10xxxxxx) at the cut means the code point started earlier.</summary>
    private static int BoundaryAtOrBefore(byte[] bytes, int limit)
    {
        var cut = limit;
        while (cut > 0 && cut < bytes.Length && (bytes[cut] & 0xC0) == 0x80) cut--;
        return cut;
    }
}
