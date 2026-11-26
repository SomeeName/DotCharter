using System.Security.Cryptography;
using System.Text;
using DotCharter.Services.Export;

namespace DotCharter.Services.Library;

/// <summary>
///     The DLC key a package opened for editing exports under. The game lists one song per
///     key, so a project that re-exported under the package's own key (the derived default
///     matches it whenever the package was built by DotCharter or by DLC Builder from the same
///     charter, artist and title) would hide the original or be hidden by it. On open the
///     project is given a key of its own: the derived default, with a suffix taken from the
///     package key when the two would clash. A pure function of the song, so every open of one
///     package gives the same key, and the project file keeps it from then on.
/// </summary>
internal static class OpenedPackageDlcKey
{
    /// <summary>Stamps <paramref name="project" />'s DLC key unless it already carries one that
    /// differs from <paramref name="packageKey" />.</summary>
    public static void Apply(ChartProject project, string packageKey)
    {
        if (string.IsNullOrWhiteSpace(packageKey)) return;
        var chart = project.Chart;
        if (!string.IsNullOrWhiteSpace(chart.DlcKey) && !Clashes(chart.DlcKey, packageKey)) return;

        chart.DlcKey = KeyFor(chart.Artist, chart.SongName, packageKey);
    }

    /// <summary>The key a project of this song exports under beside the package <paramref name="packageKey" />:
    /// the derived default, with a suffix taken from the package key when the two would clash, or when
    /// <paramref name="taken" /> says another package already holds the default.</summary>
    public static string KeyFor(string? artist, string? title, string packageKey, Func<string, bool>? taken = null)
    {
        var key = RocksmithDlcKey.Create(artist, title);
        return Clashes(key, packageKey) || taken?.Invoke(key) == true ? key + "Ed" + Suffix(packageKey) : key;
    }

    /// <summary>Equal as the build writes them: ASCII letters and digits, any case.</summary>
    private static bool Clashes(string key, string packageKey) =>
        string.Equals(AsciiKey(key), AsciiKey(packageKey), StringComparison.OrdinalIgnoreCase);

    private static string AsciiKey(string key) => new([.. key.Where(char.IsAsciiLetterOrDigit)]);

    private static string Suffix(string packageKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AsciiKey(packageKey).ToUpperInvariant())), 0, 3);
}
