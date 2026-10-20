namespace DotCharter.Services.Library;

/// <summary>
///     Pure, deterministic collapse of duplicate library rows.
///
///     A real library can hold byte-for-byte COPIES of a psarc under different
///     file names (a misnamed clone of another package, a PC
///     vs. Mac variant of the same DLC, …). Every song inside such a copy scans
///     correctly, so the SAME song identity — <c>(SongKey, Charter, Version)</c>,
///     the shared-leaderboard key — appears as two or three interchangeable rows
///     in the song lists. This policy folds those rows down to one.
///
///     Identity casing mirrors <c>LibraryDoctorSong.Identity</c> (and the
///     leaderboard key it mirrors): each component is trimmed and lower-cased
///     invariantly, so the collapse covers EXACTLY the duplicates LibraryDoctor
///     flags as <c>DuplicatePackage</c>. LibraryDoctor itself is the diagnosis
///     surface and deliberately keeps reporting the duplicates — the lists
///     collapse; the doctor does not.
///
///     Winner rule: within an identity collision, a PC package beats a Mac one
///     (<c>_m.psarc</c>), and otherwise keep the entry whose
///     <see cref="LibrarySongEntry.PackagePath"/> is the LEXICOGRAPHICALLY SMALLEST
///     (ordinal). Copies of one PLATFORM are interchangeable, so any is a valid
///     representative and the smallest path is picked purely because it is
///     STABLE across scans (independent of enumeration order). The PC copy still
///     wins a collision: plain smallest-path would pick
///     <c>x_m.psarc</c> over <c>x_p.psarc</c> ('m' &lt; 'p'), which a loader reading
///     PC SNGs only could not load. The
///     loader also reads Mac SNGs (the package reader tries the Mac key),
///     so a Mac package — a Mac-only DLC included — loads too; preferring PC is
///     a tie-break, not a requirement. Rows that differ in ANY identity component
///     are never collapsed (a multi-song pack's songs all share a path but carry
///     distinct SongKeys, so they survive intact); rows with a blank SongKey are
///     never collapsed either (they cannot be identified — same as the doctor).
/// </summary>
public static class LibraryDedupe
{
    /// <summary>Collapses duplicate-identity <see cref="LibrarySongEntry"/> rows,
    /// keeping the smallest-path winner per identity.</summary>
    public static IReadOnlyList<LibrarySongEntry> CollapseByIdentity(IEnumerable<LibrarySongEntry> entries)
        => CollapseByIdentity(entries, static e => e);

    /// <summary>
    ///     Collapses a list of items that each wrap a <see cref="LibrarySongEntry"/>
    ///     (via <paramref name="entrySelector"/>), so both song-list surfaces —
    ///     Play Mode's <c>PlaySongItem</c> rows and the editor browser's
    ///     <c>PsarcSongListItem</c> rows — can dedupe their own wrapper type and
    ///     stay in agreement. First-appearance order of each surviving identity is
    ///     preserved; the smallest-path member replaces the winner in place, so the
    ///     result never depends on input order.
    /// </summary>
    public static IReadOnlyList<T> CollapseByIdentity<T>(
        IEnumerable<T> items, Func<T, LibrarySongEntry> entrySelector)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(entrySelector);

        var result = new List<T>();
        // identity -> index of the current winner inside result.
        var winnerIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            var entry = entrySelector(item);

            // Un-keyed rows can't be de-duplicated — keep every one, as the doctor does.
            if (string.IsNullOrWhiteSpace(entry.SongKey))
            {
                result.Add(item);
                continue;
            }

            var id = IdentityKey(entry);
            if (winnerIndex.TryGetValue(id, out var existing))
            {
                var incumbent = entrySelector(result[existing]);
                // PC before Mac, then smaller ordinal path; ties (incl. the same
                // file twice) keep the incumbent.
                if (Beats(entry.PackagePath, incumbent.PackagePath))
                    result[existing] = item;
            }
            else
            {
                winnerIndex[id] = result.Count;
                result.Add(item);
            }
        }

        return result;
    }

    private static bool Beats(string candidate, string incumbent)
    {
        var candidateMac = IsMacPackage(candidate);
        if (candidateMac != IsMacPackage(incumbent)) return !candidateMac;
        return string.CompareOrdinal(candidate, incumbent) < 0;
    }

    /// <summary>Rocksmith's platform suffix: <c>_m.psarc</c> is the Mac package,
    /// <c>_p.psarc</c> the PC one.</summary>
    private static bool IsMacPackage(string path)
        => path.EndsWith("_m.psarc", StringComparison.OrdinalIgnoreCase);

    /// <summary>Case-insensitive logical identity — mirrors
    /// <c>LibraryDoctorSong.Identity</c> / the shared-leaderboard key, plus the
    /// source kind: a <c>.dcsong</c> exported from a <c>.psarc</c> keeps its key and
    /// charter, and is a different file to play (its own chart and audio), not a copy.</summary>
    private static string IdentityKey(LibrarySongEntry e)
        => $"{e.SongKey.Trim().ToLowerInvariant()}|{e.Charter.Trim().ToLowerInvariant()}|{e.Version.Trim().ToLowerInvariant()}|{e.SourceKind}";
}
