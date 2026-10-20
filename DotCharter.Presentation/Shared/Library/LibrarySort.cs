namespace DotCharter.Presentation.Shared.Library;

/// <summary>What a song list can be ordered by.</summary>
public enum LibrarySortColumn
{
    Artist,
    Title,
    Year,
    Length,
    Pack,
    Difficulty,
    Tuning,
    RecentlyPlayed,
    BestScore
}

/// <summary>One sort mode: a column, and whether its natural (▲) direction is highest first.</summary>
public readonly record struct LibrarySortKey(LibrarySortColumn Column, bool HighestFirst = false);

/// <summary>
///     One screen's sort modes (the index its <c>SortMode</c> holds) and header captions, over the one
///     ordering both song lists share. In BOTH directions an unknown value (no year, length, rating,
///     tuning, run or score) sorts LAST, and every tie falls through to artist then title (title then
///     artist for the title column), in the direction the user picked.
/// </summary>
public sealed class LibrarySort
{
    private readonly LibrarySortKey[] _modes;
    private readonly Dictionary<string, int> _captions;

    private LibrarySort(LibrarySortKey[] modes, Dictionary<string, int> captions)
    {
        _modes = modes;
        _captions = captions;
    }

    /// <summary>The Library: artist, title, year, length, pack, difficulty, tuning — each lowest first.</summary>
    public static LibrarySort Library { get; } = new(
        [
            new(LibrarySortColumn.Artist), new(LibrarySortColumn.Title), new(LibrarySortColumn.Year),
            new(LibrarySortColumn.Length), new(LibrarySortColumn.Pack), new(LibrarySortColumn.Difficulty),
            new(LibrarySortColumn.Tuning)
        ],
        new(StringComparer.Ordinal)
        {
            ["song"] = 0, ["title"] = 1, ["year"] = 2, ["time"] = 3, ["pack"] = 4, ["difficulty"] = 5, ["tuning"] = 6
        });

    /// <summary>Song Select's combo: artist, title, recent, best, difficulty (all three highest
    /// first), tuning, length.</summary>
    public static LibrarySort SongSelect { get; } = new(
        [
            new(LibrarySortColumn.Artist), new(LibrarySortColumn.Title),
            new(LibrarySortColumn.RecentlyPlayed, HighestFirst: true), new(LibrarySortColumn.BestScore, HighestFirst: true),
            new(LibrarySortColumn.Difficulty, HighestFirst: true), new(LibrarySortColumn.Tuning),
            new(LibrarySortColumn.Length)
        ],
        new(StringComparer.Ordinal) { ["song"] = 0, ["record"] = 3, ["tuning"] = 5, ["time"] = 6 });

    /// <summary>The mode a header caption sorts by; -1 for a caption that does not sort.</summary>
    public int ModeOf(string? caption) => caption != null && _captions.TryGetValue(caption, out var mode) ? mode : -1;

    /// <summary>A mode's key; an unknown mode sorts by artist.</summary>
    public LibrarySortKey KeyOf(int mode) => mode >= 0 && mode < _modes.Length ? _modes[mode] : _modes[0];

    /// <summary>The state a click on <paramref name="caption" /> leads to: its column ascending, or the
    /// direction flipped when it is already the sort. Null for a caption that does not sort.</summary>
    public (int Mode, bool Descending)? Click(string? caption, int sortMode, bool descending)
    {
        var mode = ModeOf(caption);
        if (mode < 0) return null;
        return mode == sortMode ? (mode, !descending) : (mode, false);
    }

    /// <summary>▲ / ▼ beside the active column's caption; empty elsewhere.</summary>
    public static string Glyph(int sortMode, bool descending, int mode)
        => sortMode != mode ? "" : descending ? "▼" : "▲";

    public IEnumerable<T> Order<T>(IEnumerable<T> songs, int mode, bool descending) where T : ILibrarySong
        => Order(songs, KeyOf(mode), descending);

    public static IOrderedEnumerable<T> Order<T>(IEnumerable<T> songs, LibrarySortKey key, bool descending)
        where T : ILibrarySong
    {
        var byName = StringComparer.CurrentCultureIgnoreCase;
        var valueDescending = descending ^ key.HighestFirst;

        switch (key.Column)
        {
            case LibrarySortColumn.Artist:
                return Then(By(songs, s => s.Artist, descending, byName), s => s.Title, descending, byName);
            case LibrarySortColumn.Title:
                return Then(By(songs, s => s.Title, descending, byName), s => s.Artist, descending, byName);
            case LibrarySortColumn.Pack:
                return Ties(By(songs, s => s.PackName, valueDescending, byName), descending);
        }

        var ordered = key.Column switch
        {
            LibrarySortColumn.Year => Then(songs.OrderBy(s => s.Year <= 0), s => s.Year, valueDescending),
            LibrarySortColumn.Length => Then(songs.OrderBy(s => s.LengthSeconds <= 0), s => s.LengthSeconds, valueDescending),
            LibrarySortColumn.Difficulty => Then(songs.OrderBy(s => s.Difficulty <= 0), s => s.Difficulty, valueDescending),
            LibrarySortColumn.Tuning => Then(songs.OrderBy(s => s.TuningCellLabel.Length == 0),
                s => s.TuningCellLabel, valueDescending, byName),
            LibrarySortColumn.RecentlyPlayed => Then(songs.OrderBy(s => LastPlayed(s) == default),
                s => LastPlayed(s), valueDescending),
            LibrarySortColumn.BestScore => Then(songs.OrderBy(s => BestScore(s) < 0), s => BestScore(s), valueDescending),
            _ => throw new ArgumentOutOfRangeException(nameof(key), key.Column, "not a sort column")
        };
        return Ties(ordered, descending);
    }

    private static IOrderedEnumerable<T> Ties<T>(IOrderedEnumerable<T> ordered, bool descending) where T : ILibrarySong
    {
        var byName = StringComparer.CurrentCultureIgnoreCase;
        return Then(Then(ordered, s => s.Artist, descending, byName), s => s.Title, descending, byName);
    }

    private static IOrderedEnumerable<T> By<T, TKey>(
        IEnumerable<T> songs, Func<T, TKey> key, bool descending, IComparer<TKey>? comparer = null)
        => descending ? songs.OrderByDescending(key, comparer) : songs.OrderBy(key, comparer);

    private static IOrderedEnumerable<T> Then<T, TKey>(
        IOrderedEnumerable<T> songs, Func<T, TKey> key, bool descending, IComparer<TKey>? comparer = null)
        => descending ? songs.ThenByDescending(key, comparer) : songs.ThenBy(key, comparer);

    private static DateTime LastPlayed(ILibrarySong song) => (song as IPlayedLibrarySong)?.LastPlayedUtc ?? default;

    private static long BestScore(ILibrarySong song) => (song as IPlayedLibrarySong)?.BestScore ?? -1;
}
