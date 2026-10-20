namespace DotCharter.Services.Export.Package;

/// <summary>
/// What differs between the PC and the Mac package of one song, and nothing else
///     does: the SNG folder and key, the audio folder, the sound bank's platform tag (the Mac
///     SNG also carries one) and the file suffix. Manifests, the xblock, the album art, the
///     banks and the wems are the same bytes on both — Rocksmith2014.NET <c>Platform.fs</c>
///     (<c>getPathPart</c>), <c>GraphItem.fs</c> (<c>sng</c>, <c>bnk</c>) and
///     <c>PlatformConverter.fs</c> (<c>pcToMac</c>), checked against official PC/Mac pairs.
/// </summary>
internal sealed record RsPlatformLayout(bool IsMac, string SngFolder, string AudioFolder, string PlatformTag, string Suffix)
{
    public static readonly RsPlatformLayout Pc = new(false, "generic", "windows", "dx9", "_p");

    public static readonly RsPlatformLayout Mac = new(true, "macos", "mac", "macos", "_m");

    public string SngEntry(string lowerKey, string partition) => $"songs/bin/{SngFolder}/{lowerKey}_{partition}.sng";

    public string AudioEntry(string fileName) => $"audio/{AudioFolder}/{fileName}";
}
