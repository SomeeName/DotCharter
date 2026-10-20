using System.Globalization;
using DotCharter.Services.Library;

namespace DotCharter.Presentation.Shared.Library;

/// <summary>
///     The facet catalogue: each facet's stable key, its title, and the canonical bucket key a row
///     answers for it. "" is the explicit UNKNOWN bucket, offered as an option of its own.
/// </summary>
public static class LibraryFacets
{
    public const string Tuning = "tuning";
    public const string Capo = "capo";
    public const string Path = "path";
    public const string Year = "year";
    public const string Difficulty = "difficulty";
    public const string Length = "length";
    public const string Status = "status";
    public const string Origin = "origin";
    public const string Played = "played";
    public const string Genre = "genre";

    private const double ShortMaxSeconds = 180;
    private const double MediumMaxSeconds = 300;

    private static readonly string[] UnknownOnly = [""];

    /// <summary>The resx key of a facet's popover title.</summary>
    public static string TitleKey(string facet) => facet switch
    {
        Tuning => "SongBrowser_Col_Tuning",
        Capo => "SongBrowser_Facet_Capo",
        Path => "SongBrowser_Col_Paths",
        Year => "SongBrowser_Col_Year",
        Difficulty => "SongBrowser_Col_Difficulty",
        Length => "SongBrowser_Col_Time",
        Status => "SongBrowser_Col_Status",
        Origin => "SongBrowser_Facet_Origin",
        Played => "PlayMode_Facet_Played",
        Genre => "PlayMode_Facet_Genre",
        _ => throw new ArgumentOutOfRangeException(nameof(facet), facet, "not a library facet")
    };

    /// <summary>The bucket key of a single-valued facet; <see cref="ValuesOf" /> covers PATHS too.</summary>
    public static string KeyOf(ILibrarySong song, string facet) => facet switch
    {
        Tuning => TuningKeyOf(song),
        Capo => CapoKeyOf(song),
        Year => DecadeKey(song.Year),
        Difficulty => DifficultyKeyOf(song),
        Length => LengthKeyOf(song),
        Status => StatusKeyOf(song),
        Origin => OriginKeyOf(song),
        Played => PlayedKeyOf(song),
        Genre => GenreKeyOf(song),
        _ => ""
    };

    /// <summary>Every bucket a row falls in: one, except PATHS, where a row with no paths answers "".</summary>
    public static IReadOnlyList<string> ValuesOf(ILibrarySong song, string facet)
    {
        if (facet != Path) return [KeyOf(song, facet)];
        var badges = song.ArrangementBadges;
        return badges.Count > 0 ? badges : UnknownOnly;
    }

    public static string TuningKeyOf(ILibrarySong song)
        => string.IsNullOrWhiteSpace(song.TuningSummary) ? "" : song.TuningSummary;

    public static string CapoKeyOf(ILibrarySong song) => song.Capo?.ToString(CultureInfo.InvariantCulture) ?? "";

    public static string LengthKeyOf(ILibrarySong song) => LengthKey(song.LengthSeconds);

    public static string DifficultyKeyOf(ILibrarySong song) => DifficultyKey(song.Difficulty);

    public static string OriginKeyOf(ILibrarySong song) => OriginKey(song.Origin);

    public static string StatusKeyOf(ILibrarySong song) => song.CanLoad ? "loadable" : "noaudio";

    public static string PlayedKeyOf(ILibrarySong song)
        => song is IPlayedLibrarySong { HasBest: true } ? "played" : "unplayed";

    /// <summary>Casefolded, so "Metal" and "metal" are one genre; the option keeps an authored label.</summary>
    public static string GenreKeyOf(ILibrarySong song)
        => ((song as IPlayedLibrarySong)?.Genre ?? "").Trim().ToLowerInvariant();

    /// <summary>Under 3 min / 3–5 min / over 5 min.</summary>
    public static string LengthKey(double seconds) => seconds switch
    {
        <= 0 => "",
        < ShortMaxSeconds => "short",
        <= MediumMaxSeconds => "mid",
        _ => "long"
    };

    /// <summary>The bands the option labels print: below 4, 4–7, above 7 — so 7.0 is medium.</summary>
    public static string DifficultyKey(double rating) => rating switch
    {
        <= 0 => "",
        < 4 => "easy",
        <= 7 => "medium",
        _ => "hard"
    };

    public static string DecadeKey(int year)
        => year > 0 ? (year / 10 * 10).ToString(CultureInfo.InvariantCulture) : "";

    public static string OriginKey(LibraryOrigin? origin) => origin switch
    {
        LibraryOrigin.Stock => "stock",
        LibraryOrigin.Custom => "custom",
        _ => ""
    };
}
