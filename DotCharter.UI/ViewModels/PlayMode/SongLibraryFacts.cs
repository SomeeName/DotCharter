using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
///     One artist run of the song list. <c>PlayModeViewModel.SongList</c> stays a FLAT
///     collection — the ListBox's selection, keyboard navigation and
///     virtualization all key on it — so the group is a read-model over the
///     already-ordered rows, and its header is STAMPED onto the first row of
///     each run (<see cref="PlaySongItem.GroupHeader" />). That is what puts a
///     real "3 DOORS DOWN · 9 SONGS" header in the list without turning
///     `SelectedItem` into an `object`.
/// </summary>
public sealed record SongArtistGroup(string Artist, int Count);

/// <summary>Pure artist grouping for the song list. No Avalonia, no services —
/// it takes the ORDERED rows the filter pass produced and answers "where do the
/// runs start".</summary>
public static class SongArtistGrouping
{
    /// <summary>The runs of consecutive rows sharing an artist, in list order.
    /// Only meaningful when the list is sorted BY artist; the caller decides.
    /// <para>Generic so the editor's Rocksmith song browser
    /// (<c>PsarcSongListItem</c>) groups through the SAME code as the Play Mode
    /// library — two hand-written run-finders is how the two library screens
    /// drifted apart in the first place.</para></summary>
    public static IReadOnlyList<SongArtistGroup> Build<T>(IReadOnlyList<T> ordered, Func<T, string> artistOf,
        ILocalizationService localization)
    {
        var groups = new List<SongArtistGroup>();
        var index = 0;
        while (index < ordered.Count)
        {
            var artist = KeyOf(artistOf(ordered[index]), localization);
            var count = 1;
            while (index + count < ordered.Count &&
                   string.Equals(KeyOf(artistOf(ordered[index + count]), localization), artist,
                       StringComparison.CurrentCultureIgnoreCase))
            {
                count++;
            }

            groups.Add(new SongArtistGroup(artist, count));
            index += count;
        }

        return groups;
    }

    public static IReadOnlyList<SongArtistGroup> Build(IReadOnlyList<PlaySongItem> ordered, ILocalizationService localization)
        => Build(ordered, static song => song.Artist, localization);

    /// <summary>Stamps each run's header onto its FIRST row and clears it from
    /// every other row. <paramref name="grouped" /> false clears the whole list —
    /// a "sorted by title" list must not sprout artist headers.</summary>
    public static void Stamp<T>(IReadOnlyList<T> ordered, bool grouped, Func<T, string> artistOf,
        Action<T, string, string> apply, Func<string, int, string> countLabel, ILocalizationService localization)
    {
        foreach (var song in ordered) apply(song, "", "");

        if (!grouped) return;

        var at = 0;
        foreach (var group in Build(ordered, artistOf, localization))
        {
            apply(ordered[at], group.Artist, countLabel(group.Artist, group.Count));
            at += group.Count;
        }
    }

    public static void Stamp(IReadOnlyList<PlaySongItem> ordered, bool grouped,
        Func<string, int, string> countLabel, ILocalizationService localization)
        => Stamp(ordered, grouped, static song => song.Artist,
            static (song, header, count) =>
            {
                song.GroupHeader = header;
                song.GroupCountLabel = count;
            }, countLabel, localization);

    private static string KeyOf(string? artist, ILocalizationService localization) =>
        string.IsNullOrWhiteSpace(artist)
            ? localization["PlayMode_SongSelect_UnknownArtist"]
            : artist.Trim();
}

/// <summary>One arrangement chip on a LIST ROW: the single letter it prints and
/// the full path name it keeps as its tooltip. Built by
/// <c>PlayModeViewModel.BuildArrangementChips</c>.</summary>
public sealed record ArrangementChip(string Initial, string Label);
/// <summary>One arrangement of the selected song in the detail rail: its path
/// label and its REAL note count (manifest <c>NotesHard</c>, carried through the
/// library index). <paramref name="HasNoteCount" /> is false when the package
/// carried no count — the tile then shows the path alone rather than a zero.
/// The rail's arrangement pick (<see cref="IsSelected"/>) is what decides
/// which path the pre-play screen opens on.</summary>
public sealed record SongArrangementFact(string Label, string NoteCountLabel, bool HasNoteCount,
    string TuningLabel = "", bool HasTuning = false)
{
    public bool IsSelected { get; init; }
}

/// <summary>Per-arrangement chart facts for the library detail rail.</summary>
public interface ISongArrangementFacts
{
    IReadOnlyList<SongArrangementFact> For(PlaySongItem song);
}

/// <summary>
///     The real per-arrangement note counts, read straight off the library
///     index. Rocksmith writes <c>NotesHard</c> into the <c>.hsan</c> header the
///     scan already parses (and DotCharter's own exporter writes the same
///     attribute), so this costs no extra archive work — it is carried
///     through <c>LibraryArrangementEntry</c>.
///     <para>
///         A song with no library entry behind it (an own chart, a peer-only
///         song) has arrangement LABELS but no counts, and says so per tile.
///     </para>
/// </summary>
public sealed class SongArrangementFacts : ISongArrangementFacts
{
    public IReadOnlyList<SongArrangementFact> For(PlaySongItem song)
    {
        var entries = song.Inner?.Song.Arrangements;
        if (entries is { Count: > 0 })
        {
            var list = new List<SongArrangementFact>(entries.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                // Several arrangements can share one PATH label (alternate cuts
                // of the same route); the rail lists paths, so the first one's
                // count is the one shown — the same collapse
                // `LibrarySongEntry.ArrangementSummary` already does.
                var label = entry.PathLabel;
                if (label.Length == 0 || !seen.Add(label)) continue;
                // Per-arrangement tuning (schema v8): the tile states ITS tuning —
                // but only where it differs from the song's, which the rail already
                // prints under the tiles, and never on Vocals, which has none. Every
                // tile would repeat "E Stand…" (cut off); the Library's
                // path list follows the same rule.
                var own = SongRowFacts.TuningCell(entry.Tuning, entry.Capo, song.Localization);
                var tuning = (entry.RouteMask & 7) != 0 && own != song.TuningCellLabel ? own : "";
                list.Add(new SongArrangementFact(label,
                    entry.NoteCount > 0 ? entry.NoteCount.ToString("N0") : "",
                    entry.NoteCount > 0,
                    tuning, tuning.Length > 0));
            }

            if (list.Count > 0) return list;
        }

        // Own charts: the labels are all there is.
        var badges = song.ArrangementBadges;
        if (badges.Count == 0) return [];
        var fallback = new List<SongArrangementFact>(badges.Count);
        foreach (var label in badges) fallback.Add(new SongArrangementFact(label, "", false));
        return fallback;
    }
}
