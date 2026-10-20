using System.Globalization;
using DotCharter.Core.Utilities;
using DotCharter.Localization;

namespace DotCharter.Presentation.Shared.Library;

/// <summary>
///     Each facet's options, derived from the library it filters: only values the rows hold, in a
///     fixed order per facet, with the unknown bucket ("") offered — last — exactly where a row has
///     one, because a facet that can reject an unknown value must also offer it.
/// </summary>
public static class LibraryFacetOptions
{
    public static IReadOnlyList<(string Value, string Label)> For(
        string facet, IReadOnlyCollection<ILibrarySong> songs, ILocalizationService loc) => facet switch
        {
            LibraryFacets.Tuning => Tunings(songs, loc),
            LibraryFacets.Capo => Capos(songs, loc),
            LibraryFacets.Path => Paths(songs, loc),
            LibraryFacets.Year => Decades(songs, loc),
            LibraryFacets.Difficulty => Present(songs, facet, loc, ("easy", "SongBrowser_Facet_DiffEasy"),
                ("medium", "SongBrowser_Facet_DiffMedium"), ("hard", "SongBrowser_Facet_DiffHard"),
                ("", "SongBrowser_Facet_Unrated")),
            LibraryFacets.Length => Present(songs, facet, loc, ("short", "SongBrowser_Facet_LengthShort"),
                ("mid", "SongBrowser_Facet_LengthMid"), ("long", "SongBrowser_Facet_LengthLong"),
                ("", "SongBrowser_Facet_Unknown")),
            LibraryFacets.Origin => Present(songs, facet, loc, ("stock", "SongBrowser_Facet_Stock"),
                ("custom", "SongBrowser_Facet_Cdlc"), ("", "SongBrowser_Facet_Unknown")),
            // Both statuses on an empty library; played/unplayed always — every row is one or the other.
            LibraryFacets.Status => songs.Count == 0
                ? [("loadable", loc["SongBrowser_Status_Loadable"]), ("noaudio", loc["SongBrowser_Status_NoAudio"])]
                : Present(songs, facet, loc, ("loadable", "SongBrowser_Status_Loadable"),
                    ("noaudio", "SongBrowser_Status_NoAudio")),
            LibraryFacets.Played =>
                [("played", loc["PlayMode_Facet_PlayedOption"]), ("unplayed", loc["PlayMode_Facet_UnplayedOption"])],
            LibraryFacets.Genre => Genres(songs, loc),
            _ => throw new ArgumentOutOfRangeException(nameof(facet), facet, "not a library facet")
        };

    // Every distinct raw tuning is its own option, labelled with its name AND letters, so two
    // spellings of one tuning are two options a user can tell apart and each is reachable.
    private static List<(string, string)> Tunings(IReadOnlyCollection<ILibrarySong> songs, ILocalizationService loc)
        => WithUnknown(songs.Select(LibraryFacets.TuningKeyOf)
            .Where(key => key.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Select(key => (Value: key, Label: TuningNameResolver.Describe(key)))
            .OrderBy(option => option.Label, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(option => option.Value, StringComparer.Ordinal)
            .Select(option => (option.Value, option.Label)),
            songs.Any(song => LibraryFacets.TuningKeyOf(song).Length == 0), loc);

    private static List<(string, string)> Capos(IReadOnlyCollection<ILibrarySong> songs, ILocalizationService loc)
        => WithUnknown(songs.Select(LibraryFacets.CapoKeyOf)
            .Where(key => key.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => int.Parse(key, CultureInfo.InvariantCulture))
            .Select(key => (key, key == "0"
                ? loc["Dialog_SongBrowser_CapoNone"]
                : string.Format(CultureInfo.CurrentCulture, loc["SongBrowser_Facet_CapoAt"], key))),
            songs.Any(song => song.Capo is null), loc);

    private static List<(string, string)> Paths(IReadOnlyCollection<ILibrarySong> songs, ILocalizationService loc)
        => WithUnknown(songs.SelectMany(song => song.ArrangementBadges)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label, StringComparer.CurrentCultureIgnoreCase)
            .Select(label => (label, label)),
            songs.Any(song => song.ArrangementBadges.Count == 0), loc);

    // Newest decade first, the order a music library is browsed in.
    private static List<(string, string)> Decades(IReadOnlyCollection<ILibrarySong> songs, ILocalizationService loc)
        => WithUnknown(songs.Select(song => LibraryFacets.DecadeKey(song.Year))
            .Where(key => key.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(key => key, StringComparer.Ordinal)
            .Select(key => (key, string.Format(CultureInfo.CurrentCulture, loc["SongBrowser_Facet_Decade"], key))),
            songs.Any(song => song.Year <= 0), loc);

    // The key is casefolded; the label keeps the first authored casing met.
    private static List<(string, string)> Genres(IReadOnlyCollection<ILibrarySong> songs, ILocalizationService loc)
        => WithUnknown(songs
            .Select(song => (Key: LibraryFacets.GenreKeyOf(song), Label: ((song as IPlayedLibrarySong)?.Genre ?? "").Trim()))
            .Where(pair => pair.Key.Length > 0)
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => (group.Key, group.First().Label)),
            songs.Any(song => LibraryFacets.GenreKeyOf(song).Length == 0), loc);

    private static List<(string, string)> Present(
        IReadOnlyCollection<ILibrarySong> songs, string facet, ILocalizationService loc,
        params (string Key, string LabelKey)[] buckets)
        => [.. buckets
            .Where(bucket => songs.Any(song => LibraryFacets.KeyOf(song, facet) == bucket.Key))
            .Select(bucket => (bucket.Key, loc[bucket.LabelKey]))];

    private static List<(string, string)> WithUnknown(
        IEnumerable<(string, string)> known, bool hasUnknown, ILocalizationService loc)
    {
        var options = known.ToList();
        if (hasUnknown) options.Add(("", loc["SongBrowser_Facet_Unknown"]));
        return options;
    }
}
