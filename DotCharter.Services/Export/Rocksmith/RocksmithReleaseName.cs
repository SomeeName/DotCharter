using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DotCharter.Services.Export.Rocksmith;

/// <summary>
///     A release package's file name, the way DLC Builder names it
///     (<c>ReleasePackageBuilder.fs</c>): <c>&lt;Artist&gt;_&lt;Title&gt;_v&lt;Version, . as _&gt;</c>
///     sanitised by <c>StringValidator.fileName</c>. The platform suffix (<c>_p.psarc</c>,
///     <c>_m.psarc</c>) is the caller's.
///     <para>
///         Each name is its authored sort value when it has one, else the name as written.
///         Unlike DLC Builder's <c>convertToSortField</c> fallback, a leading "the "/"a "/"an "
///         stays: "A Song Of Romance" names the file <c>A-Song-Of-Romance</c> (owner decision).
///         A sort that only MIRRORS the name (this app's writer stores the artist there when
///         none was authored) counts as not authored.
///     </para>
/// </summary>
public static class RocksmithReleaseName
{
    /// <summary>The release file name for <paramref name="project" /> at <paramref name="version" />, without the suffix.</summary>
    public static string For(ChartProject project, string version)
    {
        var meta = RsPackageMetadataFactory.Create(project,
            new RocksmithBuildOptions { OutputPath = "", AudioPath = "" });
        return Create(meta.Artist, meta.ArtistSortOverride, meta.Title, "", version);
    }

    /// <summary>
    ///     <c>sprintf "%s_%s_v%s" artist title (version.Replace('.', '_')) |> StringValidator.fileName</c>.
    ///     A blank version is DLC Builder's default, "1".
    /// </summary>
    public static string Create(string artist, string artistSort, string title, string titleSort, string version)
    {
        var artistPart = Authored(artistSort, artist) ? artistSort : artist;
        var titlePart = Authored(titleSort, title) ? titleSort : title;
        var versionPart = (string.IsNullOrWhiteSpace(version) ? "1" : version.Trim()).Replace('.', '_');
        return FileName($"{artistPart}_{titlePart}_v{versionPart}");
    }

    /// <summary>
    ///     <paramref name="outputPath" /> with the release name of <paramref name="newVersion" /> in place of that of
    ///     <paramref name="oldVersion" />, when its file name still is the derived one; null when the path is the
    ///     user's own (or blank) and keeps its name.
    /// </summary>
    public static string? Rename(string outputPath, ChartProject project, string oldVersion, string newVersion, bool mac)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) return null;
        var file = Path.GetFileName(outputPath);
        var oldStem = For(project, oldVersion);
        if (!file.Equals(oldStem + "_p.psarc", StringComparison.OrdinalIgnoreCase) &&
            !file.Equals(oldStem + "_m.psarc", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return ExportPathDefaults.WithPlatformSuffix(
            Path.Combine(Path.GetDirectoryName(outputPath) ?? "", For(project, newVersion) + ".psarc"), mac);
    }

    private static bool Authored(string sort, string name) =>
        !string.IsNullOrWhiteSpace(sort) && !string.Equals(sort.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary><c>StringValidator.fileName</c>: no diacritics, only letters, digits, space, "_" and "-", whitespace runs as one dash.</summary>
    public static string FileName(string input)
    {
        var filtered = Regex.Replace(RemoveDiacritics(input), "[^ 0-9a-zA-Z_-]", "");
        return Regex.Replace(filtered, @"\s+", "-");
    }

    private static string RemoveDiacritics(string input)
    {
        var decomposed = input.Normalize(NormalizationForm.FormD);
        var kept = new string(decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        return kept.Normalize(NormalizationForm.FormC);
    }
}
