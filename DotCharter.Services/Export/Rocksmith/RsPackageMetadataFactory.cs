using System.Globalization;
using DotCharter.Services.Export.Package;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Services.Export.Rocksmith;

/// <summary>
///     Builds a package's <see cref="RsPackageMetadata" /> from the chart: taken out of
///     <see cref="RocksmithPackageBuilder" /> so the validator and the builder share one
///     derivation without reaching into the build. The DLC key rules themselves are
///     <see cref="RocksmithDlcKey" />.
/// </summary>
internal static class RsPackageMetadataFactory
{
    /// <summary>The package metadata (title, artist, album, year, DLC key, average tempo,
    /// charter) the build writes into the manifest — and the export validator checks
    /// before a build.</summary>
    internal static RsPackageMetadata Create(ChartProject project, RocksmithBuildOptions options)
    {
        var title = string.IsNullOrWhiteSpace(project.Chart.SongName) ? "Untitled" : project.Chart.SongName.Trim();

        // Artist / album / year are real Arrangement fields (RsXmlSongReader
        // fills them, and RsXmlSongWriter PREFERS them over the chart-level
        // values). Reading only project.Chart.* meant a psarc-imported chart
        // whose metadata lives on the arrangement shipped as "Unknown Artist"
        // with an empty album in the manifest — the strings the game's song list
        // shows — while the XML beside it carried the real ones.
        var artist = FirstNonBlank(
            FirstArrangementValue(project, a => a.ArtistName),
            project.Chart.Artist,
            "Unknown Artist");
        var artistSort = FirstNonBlank(
            FirstArrangementValue(project, a => a.ArtistNameSort),
            project.Chart.ArtistSort);
        var album = FirstNonBlank(
            FirstArrangementValue(project, a => a.AlbumName),
            project.Chart.AlbumName);

        var dlcKey = string.IsNullOrWhiteSpace(options.DlcKey)
            ? RocksmithDlcKey.Create(artist, title)
            : SanitizeDlcKey(options.DlcKey);

        int? year = null;
        foreach (var arrangement in NonVocalArrangements(project))
        {
            if (arrangement.AlbumYear is > 0)
            {
                year = arrangement.AlbumYear;
                break;
            }
        }

        if (year is null && int.TryParse(project.Chart.AlbumYear, CultureInfo.InvariantCulture, out var parsedYear) && parsedYear > 0)
            year = parsedYear;

        var charter = !string.IsNullOrWhiteSpace(project.Chart.Charter)
            ? project.Chart.Charter.Trim()
            : !string.IsNullOrWhiteSpace(options.FallbackCharter)
                ? options.FallbackCharter.Trim()
                : "DotCharter";

        return new RsPackageMetadata
        {
            DlcKey = dlcKey,
            Title = title,
            Artist = artist,
            ArtistSortOverride = artistSort,
            Album = album,
            JapaneseTitle = project.Chart.JapaneseTitle?.Trim() ?? "",
            JapaneseArtist = project.Chart.JapaneseArtist?.Trim() ?? "",
            Year = year,
            // DERIVED from the beat grid, through the very helper that writes
            // <averageTempo> into the RS-XML. ChartProject.Bpm is the NOMINAL
            // tempo, so reading it here made the game's song list disagree with
            // the XML on every tempo-mapped chart. Same class as songLength: a
            // chart-wide value comes from the chart, not from ambient state.
            AverageTempo = (float)Math.Max(1.0, RsXmlSongWriter.AverageTempo(project.Beats)),
            Charter = charter
        };
    }

    /// <summary>
    ///     The arrangements whose metadata may speak for the SONG. Vocals are
    ///     skipped for the same reason <c>RsXmlSongWriter</c> is only ever handed
    ///     an instrumental arrangement: a vocal track carries no album/artist of
    ///     its own and would only ever contribute blanks.
    /// </summary>
    private static IEnumerable<Arrangement> NonVocalArrangements(ChartProject project)
        => project.Arrangements.Where(a => a.ArrangementType != ArrangementType.Vocal);

    /// <summary>First non-blank value of <paramref name="selector"/> over
    /// <see cref="NonVocalArrangements"/>, mirroring the <c>FirstNonBlank</c>
    /// fall-through <c>RsXmlSongWriter</c> applies per arrangement.</summary>
    private static string FirstArrangementValue(ChartProject project, Func<Arrangement, string?> selector)
    {
        foreach (var arrangement in NonVocalArrangements(project))
        {
            var value = selector(arrangement);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return "";
    }

    private static string FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    /// <summary>A TYPED key: ASCII punctuation and spaces are stripped, but a non-ASCII
    /// character is refused rather than rewritten — the charter chose that key.</summary>
    private static string SanitizeDlcKey(string key)
    {
        if (RocksmithDlcKey.HasNonAscii(key))
        {
            throw new ArgumentException(
                $"DLC key '{key}' contains non-ASCII characters; Rocksmith only lists packages whose key is A-Z, a-z and 0-9.");
        }

        var sanitized = new string(key.Where(char.IsAsciiLetterOrDigit).ToArray());
        if (sanitized.Length < RocksmithDlcKey.MinimumLength)
            throw new ArgumentException($"DLC key '{key}' must contain at least 6 alphanumeric characters.");
        return sanitized;
    }
}
