namespace DotCharter.Services.Package.DcSong;

/// <summary>The two image types a package may carry, told apart by their first bytes.</summary>
public static class DcSongImages
{
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The media type the bytes ARE (never what a file name says), or null.</summary>
    public static string? Sniff(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(PngSignature)) return Png;
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return Jpeg;
        return null;
    }
}
