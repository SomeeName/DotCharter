namespace DotCharter.Services.Export.Rocksmith;

/// <summary>The package name for the archive's extended-range marker, PC (<c>_rsxr_p</c>) and Mac (<c>_rsxr_m</c>).</summary>
public static class ExtendedRangePackageName
{
    private const string Suffix = "_p.psarc";
    private const string Tag = "_rsxr";

    public static string Apply(string path, bool tagged)
    {
        if (!path.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
        {
            if (!tagged || !path.EndsWith(".psarc", StringComparison.OrdinalIgnoreCase)) return path;
            var suffixLength = path.EndsWith("_m.psarc", StringComparison.OrdinalIgnoreCase) ? "_m.psarc".Length : ".psarc".Length;
            path = path[..^suffixLength] + Suffix;
        }
        var stem = path[..^Suffix.Length];
        while (stem.EndsWith(Tag, StringComparison.OrdinalIgnoreCase)) stem = stem[..^Tag.Length];
        return stem + (tagged ? Tag : "") + path[^Suffix.Length..];
    }

    /// <summary>The Mac package name for the archive's extended-range marker: <c>_rsxr_m.psarc</c>
    /// when tagged. A name not ending in <c>_m.psarc</c> is left as it is.</summary>
    public static string ApplyMac(string path, bool tagged)
    {
        const string macSuffix = "_m.psarc";
        if (!path.EndsWith(macSuffix, StringComparison.OrdinalIgnoreCase)) return path;
        var stem = path[..^macSuffix.Length];
        while (stem.EndsWith(Tag, StringComparison.OrdinalIgnoreCase)) stem = stem[..^Tag.Length];
        return stem + (tagged ? Tag : "") + path[^macSuffix.Length..];
    }

    /// <summary>The name without the RS Extended Range tag, on either platform's suffix.</summary>
    public static string Untag(string path) => ApplyMac(Apply(path, false), false);

    public static bool IsPsxr(IEnumerable<Arrangement> arrangements) =>
        arrangements.Any(a =>
            RsxrStringLayout.IsExtended(a) && RsxrStringLayout.ExportRefusal(a) == null);
}
