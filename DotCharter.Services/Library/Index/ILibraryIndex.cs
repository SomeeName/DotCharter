namespace DotCharter.Services.Library.Index;

/// <summary>
/// Persistent cache of parsed <see cref="LibrarySongEntry"/> metadata keyed by
/// PSARC file identity (path + size + mtime). Strictly a CACHE: every method
/// is best-effort and must never surface an error to the scan — any failure
/// simply means the caller falls back to parsing the archive from disk.
/// </summary>
public interface ILibraryIndex
{
    /// <summary>
    /// Opens a scan session (one connection for the whole library scan).
    /// Returns <c>null</c> when the index store cannot be opened at all;
    /// the caller then scans without a cache.
    /// </summary>
    ILibraryIndexScan? BeginScan();

    /// <summary>
    /// Best-effort: persist a computed 1–10 difficulty rating for one indexed
    /// song, so unplayed library songs show a rating on the next scan. A library
    /// scan cannot compute it (a <c>.hsan</c> carries no note data), so it is
    /// written the first time a song's chart is fully parsed
    /// (the package source). Silently no-ops on any
    /// failure and when the song is not yet indexed — the index is only a cache.
    /// </summary>
    void TrySetSongRating(string psarcPath, string songKey, double rating);

    /// <summary>
    ///     What the index knows about the library RIGHT NOW, without scanning
    ///     anything. For readouts that want to say how big the library is — the
    ///     Home screen's one-line summary — where triggering a scan would be
    ///     absurd and where "nothing scanned yet" is a real, expected answer.
    ///     <para>
    ///         Best-effort like the rest of this interface: an absent, locked or
    ///         unreadable index answers <see cref="LibraryIndexFacts.Empty" />
    ///         rather than throwing, and NEVER rebuilds or deletes the store —
    ///         a readout has no business doing either.
    ///     </para>
    /// </summary>
    LibraryIndexFacts ReadFacts();

    /// <summary>
    ///     The extracted cover of a song the index knows, by the archive's song
    ///     key, or "" when it has none, its png is gone, or the index cannot be
    ///     read. Read-only and best-effort like <see cref="ReadFacts" />; for the
    ///     Home screen's hero, which may not open an archive.
    /// </summary>
    string FindCoverImage(string songKey) => "";

    /// <summary>
    ///     The extracted cover of an indexed song with this artist and title
    ///     (case-insensitive), or "" — how an own chart finds the library's art
    ///     for the same song. Read-only and best-effort like
    ///     <see cref="FindCoverImage" />.
    /// </summary>
    string FindCoverImageByTitle(string artist, string title) => "";
}

/// <summary>
///     The library's size as the index last saw it.
///     <paramref name="Songs" /> counts DISTINCT songs (one song shipped in two
///     packs is one song); <paramref name="Archives" /> counts indexed
///     <c>.psarc</c> files.
/// </summary>
public readonly record struct LibraryIndexFacts(int Songs, int Archives)
{
    public static LibraryIndexFacts Empty => new(0, 0);

    /// <summary>Nothing has been scanned yet — the caller shows its fresh-install copy.</summary>
    public bool IsEmpty => Songs <= 0;
}

/// <summary>
/// Per-scan session over the index. Not thread-safe; a scan uses it from its
/// single worker thread — the cold scan's parallel parsers never touch it, they
/// hand their results to that thread (<c>LibraryScanParallelParse</c>).
/// Dispose releases the underlying connection.
/// </summary>
public interface ILibraryIndexScan : IDisposable
{
    /// <summary>
    /// Looks up the cached songs for <paramref name="psarcPath"/>. Hits only
    /// when the stored (size, mtime) match the file's current values, i.e.
    /// the archive has not changed since it was parsed. A hit may legitimately
    /// carry an empty list (an archive that parsed fine but contains no songs).
    /// Returns <c>false</c> on miss, stale entry, or ANY read failure.
    /// </summary>
    bool TryGetCachedSongs(
        string psarcPath,
        long fileSize,
        long mtimeUtcTicks,
        out IReadOnlyList<LibrarySongEntry> songs);

    /// <summary>
    /// Replaces the cached rows for <paramref name="psarcPath"/> with the
    /// freshly parsed <paramref name="songs"/> in a single transaction, so a
    /// cancelled scan never leaves a half-written file in the index.
    /// Failures are logged and swallowed.
    /// </summary>
    void UpsertFile(
        string psarcPath,
        long fileSize,
        long mtimeUtcTicks,
        IReadOnlyList<LibrarySongEntry> songs);

    /// <summary>
    /// Removes index rows for files that lived under <paramref name="rootPath"/>
    /// but are not present on disk (<paramref name="presentPsarcPaths"/>
    /// is the current enumeration result). Rows outside the scanned root are
    /// left alone so switching library folders doesn't wipe the other folder's
    /// cache. Failures are logged and swallowed.
    /// </summary>
    void PurgeMissing(string rootPath, IReadOnlyCollection<string> presentPsarcPaths);
}
