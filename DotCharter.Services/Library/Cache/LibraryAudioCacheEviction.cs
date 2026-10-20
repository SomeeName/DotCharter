namespace DotCharter.Services.Library.Cache;

/// <summary>
/// Whether a PSARC audio-cache folder can still be HIT.
/// <para>
/// The cache key embeds the source archive's last-write ticks
/// (<c>{psarcName}_{pathHash16}_{lastWriteTicks}</c>), so the question is
/// decidable from disk rather than guessed at: an archive that moved, vanished
/// or was re-downloaded produces a DIFFERENT folder name forever after, and the
/// old one can never be computed again by anything.
/// </para>
/// </summary>
public enum LibraryAudioCacheReachability
{
    /// <summary>The source archive is still at the recorded path with the
    /// recorded timestamp, so this folder is a live cache entry.</summary>
    Reachable,

    /// <summary>No source record in the folder — written before the marker
    /// existed, or the marker was lost. Cannot be PROVEN dead, so it is only
    /// ever reclaimed by the size cap, never by orphan reclamation.</summary>
    Unknown,

    /// <summary>Provably dead: the archive is gone, its timestamp moved, or a
    /// newer folder exists for the same archive path. Nothing will ever compute
    /// this folder's name again, so it is reclaimed FIRST and unconditionally.
    /// </summary>
    Orphaned,
}

/// <summary>One candidate folder, as the pure planner sees it.</summary>
/// <param name="Name">The folder name (not a path) — the planner never touches disk.</param>
/// <param name="Bytes">Total size of everything under it.</param>
/// <param name="LastUsedUtc">When the app last opened this song's audio. Read from
/// the source marker's write time, which the store rewrites on every use; a folder
/// with no marker falls back to the directory's own write time.</param>
/// <param name="Reachability">See <see cref="LibraryAudioCacheReachability"/>.</param>
public readonly record struct LibraryAudioCacheEntry(
    string Name,
    long Bytes,
    DateTime LastUsedUtc,
    LibraryAudioCacheReachability Reachability = LibraryAudioCacheReachability.Unknown);

/// <summary>What a sweep would delete, split by WHY, so the caller can log the
/// two reasons apart (they mean very different things about the library).</summary>
/// <param name="Orphans">Unreachable folders. Reclaimed regardless of the cap.</param>
/// <param name="Evicted">Live folders given up to get back under the cap, least
/// recently used first.</param>
/// <param name="BytesBefore">Total occupancy before the sweep.</param>
/// <param name="BytesAfter">Total occupancy the sweep would leave behind.</param>
public readonly record struct LibraryAudioCacheEvictionPlan(
    IReadOnlyList<LibraryAudioCacheEntry> Orphans,
    IReadOnlyList<LibraryAudioCacheEntry> Evicted,
    long BytesBefore,
    long BytesAfter)
{
    public static readonly LibraryAudioCacheEvictionPlan Empty = new([], [], 0, 0);

    /// <summary>Everything to delete, orphans first — the order the store applies.</summary>
    public IEnumerable<LibraryAudioCacheEntry> All => Orphans.Concat(Evicted);

    public int Count => Orphans.Count + Evicted.Count;

    public long BytesReclaimed => BytesBefore - BytesAfter;

    public bool IsEmpty => Count == 0;
}

/// <summary>
/// Decides what a PSARC audio-cache sweep removes. PURE: folder names, sizes and
/// timestamps in, a delete list out — no filesystem, no clock, no logger.
/// <para>
/// <b>This cache auto-evicts, and that is the OPPOSITE call from
/// <c>StemCacheStore</c> — deliberately.</b> A stem archive is minutes of
/// separation the user already paid for, so
/// <c>StemCacheStore.MeasureUsage</c> documents that adding a cap there would be
/// a bug. A PSARC audio folder is a decoded WAV: re-earning it costs ONE decode
/// of an archive the user still has on disk. It is exactly the cache that should
/// evict, and nothing ever removed a byte of it — 127.6 GB across 2,543 folders
/// on the owner's machine before this type existed.
/// </para>
/// <para>
/// Two passes, in this order:
/// </para>
/// <list type="number">
///   <item>
///     <b>Orphan reclamation.</b> Every <see cref="LibraryAudioCacheReachability.Orphaned"/>
///     folder goes, whether or not the cache is over its cap. These can never be
///     hit again, so keeping one has no upside at any size.
///   </item>
///   <item>
///     <b>LRU down to the cap.</b> If the survivors still exceed
///     <c>capBytes</c>, give up the least recently used first. Ties break on the
///     folder NAME so a sweep is deterministic — two folders written in the same
///     filesystem timestamp tick are common (a batch import), and a plan that
///     depends on enumeration order is untestable.
///   </item>
/// </list>
/// </summary>
public sealed class LibraryAudioCacheEvictionPlanner
{
    /// <param name="entries">Every folder currently under the cache root.</param>
    /// <param name="capBytes">The size ceiling. Clamped at zero; a cap of zero
    /// means "keep only what is protected", which is what <c>Clear</c> does by
    /// other means.</param>
    /// <param name="protectedName">The folder for the song being loaded right
    /// now. NEVER evicted — deleting it mid-load is the one way this sweep could
    /// break a running app, and the size pass would otherwise pick it: a folder
    /// written seconds ago competes on last-use, but on a cold cache it can be
    /// the only candidate.</param>
    public LibraryAudioCacheEvictionPlan Plan(
        IReadOnlyList<LibraryAudioCacheEntry> entries,
        long capBytes,
        string? protectedName = null)
    {
        if (entries.Count == 0) return LibraryAudioCacheEvictionPlan.Empty;

        var cap = Math.Max(0, capBytes);
        var bytesBefore = entries.Sum(e => e.Bytes);
        var running = bytesBefore;

        bool IsProtected(LibraryAudioCacheEntry e) =>
            protectedName != null && string.Equals(e.Name, protectedName, StringComparison.OrdinalIgnoreCase);

        var orphans = entries
            .Where(e => e.Reachability == LibraryAudioCacheReachability.Orphaned && !IsProtected(e))
            .OrderBy(e => e.LastUsedUtc)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();

        foreach (var orphan in orphans) running -= orphan.Bytes;

        var evicted = new List<LibraryAudioCacheEntry>();
        if (running > cap)
        {
            var survivors = entries
                .Where(e => e.Reachability != LibraryAudioCacheReachability.Orphaned && !IsProtected(e))
                .OrderBy(e => e.LastUsedUtc)
                .ThenBy(e => e.Name, StringComparer.Ordinal);

            foreach (var entry in survivors)
            {
                if (running <= cap) break;
                evicted.Add(entry);
                running -= entry.Bytes;
            }
        }

        return new LibraryAudioCacheEvictionPlan(orphans, evicted, bytesBefore, running);
    }
}
