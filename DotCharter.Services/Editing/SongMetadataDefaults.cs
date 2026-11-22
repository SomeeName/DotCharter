using DotCharter.Services.Export;
using DotCharter.Services.Export.DlcBuilder;
using DotCharter.Services.Infrastructure;
using DotCharter.Services.Online.MusicBrainz;

namespace DotCharter.Services.Editing;

/// <summary>
///     The values a song's metadata takes when the charter leaves them alone, which the New project
///     dialog and the Song metadata dialog show prefilled (DLC Builder's sort fields and DLC key) and
///     the exporters derive the same way. A value the charter leaves equal to its default is stored
///     blank, so it keeps following the artist and title.
/// </summary>
public static class SongMetadataDefaults
{
    /// <summary>The sort name of an artist: official table, then the article dropped.</summary>
    public static string ArtistSort(string? artist) => Rs2DlcStrings.ArtistSortValue(artist?.Trim());

    /// <summary>The sort name of a title: the article dropped.</summary>
    public static string TitleSort(string? title) => Rs2DlcStrings.TitleSortValue(title?.Trim());

    /// <summary>The sort name of an album: the album itself.</summary>
    public static string AlbumSort(string? album) => Rs2DlcStrings.AlbumSortValue(album?.Trim());

    /// <summary>The DLC key a package built from this artist and title takes.</summary>
    public static string DlcKey(string? artist, string? title) => RocksmithDlcKey.Create(artist, title);

    /// <summary>The tool line a built package names (<c>DotCharter v1.3.0</c>); shown read-only beside the author.</summary>
    public static string ToolVersion => $"DotCharter v{AppVersionInfo.Current}";

    /// <summary>What to store for <paramref name="field" />: blank while it equals the derived value.</summary>
    public static string Authored(string? field, string derived)
    {
        var trimmed = field?.Trim() ?? "";
        return string.Equals(trimmed, derived, StringComparison.Ordinal) ? "" : trimmed;
    }

    /// <summary>
    ///     The "move it to the original-script field" offer: a main name holding non-Latin text goes to
    ///     <paramref name="original" /> and the main one is filled from <paramref name="latin" /> (the official
    ///     MusicBrainz name; empty while none is known, which leaves the main field for the charter to type).
    ///     A main name already in Latin script is left alone.
    /// </summary>
    public static (string Main, string Original) MoveToOriginalScript(string? main, string? original, string? latin) =>
        ScriptDetection.NeedsRomanization(main)
            ? (latin?.Trim() ?? "", main!.Trim())
            : (main ?? "", original ?? "");
}
