// A scan's result merged into the Library's song list: rows still there keep their instance (and cover), and the difference is counted.
using System.Globalization;
using DotCharter.Services.Library;

namespace DotCharter.UI.ViewModels.Panels;

/// <summary>
///     What a scan changed. <see cref="Songs" /> is the new master list in the scan's order;
///     every song whose entry did not change is the SAME <see cref="PsarcSongListItem" /> as
/// before, so its row, its selection and its decoded cover survive.
/// </summary>
internal sealed record LibraryRescanResult(
    List<PsarcSongListItem> Songs,
    int Added,
    IReadOnlyList<PsarcSongListItem> Removed,
    IReadOnlyDictionary<PsarcSongListItem, PsarcSongListItem> Replaced)
{
    public bool IsUnchanged => Added == 0 && Removed.Count == 0 && Replaced.Count == 0;

    /// <summary>"Refreshed · 12 new, 1 removed" or "Refreshed · no changes", in the current language.</summary>
    public string Describe(ILocalizationService loc)
    {
        if (IsUnchanged) return loc["Library_Refresh_NoChanges"];

        var parts = new List<string>(3);
        if (Added > 0) parts.Add(loc.Format("Library_Refresh_Added", Count(Added)));
        if (Removed.Count > 0) parts.Add(loc.Format("Library_Refresh_Removed", Count(Removed.Count)));
        if (Replaced.Count > 0) parts.Add(loc.Format("Library_Refresh_Updated", Count(Replaced.Count)));
        return loc.Format("Library_Refresh_Changes", string.Join(loc["Library_Refresh_Separator"], parts));
    }

    /// <summary>
    ///     Takes the songs that went (removed, or replaced by a changed entry) out of the visible
    ///     list one by one, so the filter pass after it only INSERTS: <c>SyncWith</c> edits a pure
    ///     removal or a pure insertion in place and answers a mix with one Reset, which re-realizes
    ///     every row on screen and loses the scroll position. Past the edit cap one Reset is the
    ///     cheaper answer, and the pass gives it.
    /// </summary>
    public void RemoveLeavingFrom(IList<PsarcSongListItem> visible)
    {
        if (Removed.Count + Replaced.Count > BulkObservableCollection<PsarcSongListItem>.MaxIncrementalEdits) return;
        foreach (var gone in Removed.Concat(Replaced.Keys)) visible.Remove(gone);
    }

    private static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}

internal static class LibraryRescan
{
    /// <summary>One song in one archive: multi-song packs share a path, so a path-only key would
    /// collapse a whole pack into one song.</summary>
    public static string KeyOf(LibrarySongEntry song) => $"{song.PackagePath}|{song.SongKey}";

    /// <summary>Merges <paramref name="scanned" /> into <paramref name="previous" />: an entry whose
    /// key was listed and whose content is unchanged keeps the listed item; a changed one gets a
    /// new item (its row values are computed once, in the constructor); a new key is added, and a
    /// key the scan does not report is removed. <paramref name="blockOf" /> says whether this
    /// computer can play an entry NOW (an ffmpeg installed since the last scan): an item
    /// whose answer changed is replaced like a changed entry.</summary>
    public static LibraryRescanResult Merge(IEnumerable<PsarcSongListItem> previous, IReadOnlyList<LibrarySongEntry> scanned,
        ILocalizationService localization, Func<LibrarySongEntry, LibrarySongBlock>? blockOf = null)
    {
        blockOf ??= static _ => LibrarySongBlock.None;
        var listed = new Dictionary<string, Queue<PsarcSongListItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in previous)
        {
            var key = KeyOf(item.Song);
            if (!listed.TryGetValue(key, out var queue)) listed[key] = queue = new Queue<PsarcSongListItem>(1);
            queue.Enqueue(item);
        }

        var songs = new List<PsarcSongListItem>(scanned.Count);
        var replaced = new Dictionary<PsarcSongListItem, PsarcSongListItem>(ReferenceEqualityComparer.Instance);
        var added = 0;
        foreach (var entry in scanned)
        {
            if (listed.TryGetValue(KeyOf(entry), out var queue) && queue.TryDequeue(out var existing))
            {
                if (SameEntry(existing.Song, entry) && existing.Block == blockOf(entry))
                {
                    songs.Add(existing);
                    continue;
                }

                var fresh = new PsarcSongListItem(entry, localization, blockOf(entry));
                replaced[existing] = fresh;
                songs.Add(fresh);
                continue;
            }

            songs.Add(new PsarcSongListItem(entry, localization, blockOf(entry)));
            added++;
        }

        return new LibraryRescanResult(songs, added, [.. listed.Values.SelectMany(q => q)], replaced);
    }

    /// <summary>Whether a rescanned entry says the same as the listed one. The library index serves
    /// unchanged archives from its cache as NEW objects, so this compares values. The extracted
    /// cover's cache path is left out: it is where a decoded cover was saved, not what the
    /// archive holds.</summary>
    internal static bool SameEntry(LibrarySongEntry a, LibrarySongEntry b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.PackagePath != b.PackagePath || a.SongKey != b.SongKey) return false;
        if (a.Title != b.Title || a.Artist != b.Artist || a.Album != b.Album || a.Year != b.Year) return false;
        if (a.SongLength != b.SongLength || a.AverageTempo != b.AverageTempo || a.Difficulty != b.Difficulty) return false;
        if (a.TuningSummary != b.TuningSummary || a.CapoSummary != b.CapoSummary) return false;
        if (a.Charter != b.Charter || a.Version != b.Version || a.SongBank != b.SongBank) return false;
        if (a.CoverImageEntryPath != b.CoverImageEntryPath || a.Arrangements.Count != b.Arrangements.Count) return false;

        for (var i = 0; i < a.Arrangements.Count; i++)
        {
            if (!SameArrangement(a.Arrangements[i], b.Arrangements[i])) return false;
        }

        return true;
    }

    private static bool SameArrangement(LibraryArrangementEntry a, LibraryArrangementEntry b) =>
        a.ArrangementName == b.ArrangementName && a.PersistentId == b.PersistentId && a.SngPath == b.SngPath
        && a.RouteMask == b.RouteMask && a.Represent == b.Represent && a.BonusArr == b.BonusArr
        && a.MasterId == b.MasterId && a.Sku == b.Sku && a.NoteCount == b.NoteCount && a.BassPick == b.BassPick
        && a.ScrollSpeed == b.ScrollSpeed && a.Tuning == b.Tuning && a.Capo == b.Capo && a.ToneBase == b.ToneBase
        && a.Tones.Count == b.Tones.Count
        && a.Tones.All(tone => b.Tones.TryGetValue(tone.Key, out var other) && other == tone.Value);
}
