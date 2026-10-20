namespace DotCharter.Services.Export.Sng.Model;

/// <summary>The SNG header flags word (after the 0x4A magic). Stock songs carry 3;
/// a plain package (the CDLC enabler's plain-PSARC patch) carries 1.</summary>
internal static class SngFlags
{
    public const uint Compressed = 1;
    public const uint Encrypted = 2;
}
