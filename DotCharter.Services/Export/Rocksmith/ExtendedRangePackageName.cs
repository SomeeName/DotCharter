namespace DotCharter.Services.Export.Rocksmith;

/// <summary>The PC package name for the archive's extended-range marker.</summary>
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

    public static bool IsPsxr(IEnumerable<Arrangement> arrangements) =>
        arrangements.Any(a =>
            RsxrStringLayout.IsExtended(a) && RsxrStringLayout.ExportRefusal(a) == null);
}
