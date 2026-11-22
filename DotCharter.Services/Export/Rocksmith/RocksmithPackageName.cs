namespace DotCharter.Services.Export.Rocksmith;

/// <summary>The shared file-name policy for release and test packages.</summary>
public static class RocksmithPackageName
{
    /// <summary>Artist/title, release or test version, optional extended-range tag, then platform suffix.</summary>
    public static string For(ChartProject project, string version, bool mac = false, bool extended = false,
        bool test = false, string testVersionTag = "")
    {
        var stem = test
            ? ArtistTitle(project) + "_" + RocksmithTestExport.Version
                + (testVersionTag.Length == 0 ? "" : "_" + testVersionTag)
            : RocksmithReleaseName.For(project, version);
        var pc = ExtendedRangePackageName.Apply(stem + "_p.psarc", extended);
        return mac ? pc[..^"_p.psarc".Length] + "_m.psarc" : pc;
    }

    /// <summary>The shared name without the stock PC platform suffix, for a test plan's file stem.</summary>
    public static string Stem(ChartProject project, string version, bool test = false, string testVersionTag = "") =>
        For(project, version, test: test, testVersionTag: testVersionTag)[..^"_p.psarc".Length];

    /// <summary>Only this artist/title's release family; callers still require ownership before removing a file.</summary>
    public static bool IsReleaseFor(ChartProject project, string path)
    {
        var name = Path.GetFileName(path);
        var prefix = ArtistTitle(project) + "_v";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var mac = name.EndsWith("_m.psarc", StringComparison.OrdinalIgnoreCase);
        if (!mac && !name.EndsWith("_p.psarc", StringComparison.OrdinalIgnoreCase)) return false;
        var version = name[prefix.Length..^"_p.psarc".Length];
        if (version.EndsWith("_rsxr", StringComparison.OrdinalIgnoreCase)) version = version[..^"_rsxr".Length];
        return version.Length > 0 && !version.EndsWith("_rsxr", StringComparison.OrdinalIgnoreCase) &&
            version.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    }

    private static string ArtistTitle(ChartProject project)
    {
        var releaseStem = RocksmithReleaseName.For(project, RocksmithTestExport.Version);
        var suffix = "_v" + RocksmithTestExport.Version;
        return releaseStem[..^suffix.Length];
    }
}
