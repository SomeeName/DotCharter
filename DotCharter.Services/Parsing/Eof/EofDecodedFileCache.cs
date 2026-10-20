namespace DotCharter.Services.Parsing.Eof;

/// <summary>
///     A tiny bounded cache of DECOMPRESSED <c>.eof</c> payloads, keyed by
///     (full path, length, last-write ticks).
///     <para>
///         Importing one file decoded its LZSS container TWICE: once for the
///         import dialog's track preview (<see cref="EofImporter.GetAvailableTracks" />)
///         and again for the import itself
///         (<see cref="EofImporter.ImportTracksWithMetadata" />). The decoder is
///         a byte-at-a-time sliding-window loop over a stream that can expand
///         ~8.5×, and both calls happen inside one user gesture.
///     </para>
///     <para>
///         <b>The key includes length AND last-write ticks</b>, so a file edited
///         under us re-decodes: a path alone would serve a stale chart, which is
///         the failure mode a cache like this has to be built not to have. The
///         stamp is read with <see cref="FileInfo" /> at every call — the cache
///         never trusts a previous stat.
///     </para>
///     <para>
///         <b>The returned array is SHARED — treat it as read-only.</b> Both
///         callers hand it straight to <c>EofPackReader</c>, which only reads.
///     </para>
///     <para>
///         Bounded to <see cref="Capacity" /> entries, evicting least-recently
///         used, because the payloads are multi-megabyte: this exists to make one
///         gesture decode once, not to hold a session's worth of charts.
///     </para>
/// </summary>
public sealed class EofDecodedFileCache
{
    /// <summary>Enough for the dialog + the import of one file, plus a little
    /// slack for a user comparing two charts. Deliberately tiny.</summary>
    internal const int Capacity = 4;

    public static EofDecodedFileCache Shared { get; } = new();

    private readonly Lock _gate = new();

    /// <summary>Most-recently-used first.</summary>
    private readonly List<Entry> _entries = [];

    /// <summary>How many times a payload was actually decoded. Test seam — the
    /// point of the cache is that this does NOT grow on a repeated read.</summary>
    internal int DecodeCount { get; private set; }

    /// <summary>Reads and decompresses <paramref name="filePath" />, serving a
    /// cached payload when the file has not changed since it was decoded.</summary>
    public byte[] Read(string filePath) =>
        Read(filePath, static raw => AllegroLzssDecoder.Decode(raw));

    internal byte[] Read(string filePath, Func<byte[], byte[]> decode)
    {
        var info = new FileInfo(filePath);
        var key = new Key(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);

        lock (_gate)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                if (!_entries[i].Key.Equals(key)) continue;
                var hit = _entries[i];
                _entries.RemoveAt(i);
                _entries.Insert(0, hit); // touch: MRU
                return hit.Data;
            }
        }

        // Decode OUTSIDE the lock: it is the expensive half, and two threads
        // racing on the same file cost one redundant decode, not a stall.
        var data = decode(File.ReadAllBytes(filePath));

        lock (_gate)
        {
            DecodeCount++;
            _entries.RemoveAll(e => e.Key.Path == key.Path);
            _entries.Insert(0, new Entry(key, data));
            while (_entries.Count > Capacity) _entries.RemoveAt(_entries.Count - 1);
        }

        return data;
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            DecodeCount = 0;
        }
    }

    private readonly record struct Key(string Path, long Length, long LastWriteTicks);

    private readonly record struct Entry(Key Key, byte[] Data);
}
