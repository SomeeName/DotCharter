using JuceSharp.AudioIo;

namespace DotCharter.Tools.RigDataset;

/// <summary>
///     The DI pool and the bed pool, as a SORTED list of paths.
///     <para>
///         Sorted, and that is load-bearing: a clip's DI is chosen by index out of
///         this list, so a directory enumeration in filesystem order would make the
///         same seed produce a different corpus on a different machine — or after a
///         defrag. The index is also all the manifest needs to re-derive the choice.
///     </para>
/// </summary>
public sealed class ClipSource
{
    /// <summary>
    ///     How many BYTES of decoded audio one pool may hold. The cache used to be
    ///     bounded by ENTRY COUNT (4,096) and that is not a bound at all: a DI entry
    ///     is a 5 s mono <c>float[]</c> — 240,000 samples, 960 KB — so the ceiling
    ///     was ~3.9 GB per render process, which with <c>--threads 4</c> is four of
    ///     them. Measured 2026-09-13 on a 400-clip probe over the 4,234-file pool:
    ///     114 MB → 680 MB in 350 clips (1.62 MB/clip, i.e. one whole DI file per
    ///     clip), against 83–133 MB FLAT over the same 400 clips on a 20-file pool
    ///     that fits. The overnight 100k run reached 9.5 and 10.3 GB per child.
    ///     <para>
    ///         192 MB holds ~200 DI clips or a handful of long beds, which is what
    ///         the locality actually asks for: the DI is drawn uniformly out of
    ///         thousands of files, so a bigger cache buys hit rate roughly linearly
    ///         in RAM and the miss costs one 480 KB WAV decode — sub-millisecond
    ///         against a ~500 ms render.
    ///     </para>
    /// </summary>
    public const long DefaultCacheBudgetBytes = 192L * 1024 * 1024;

    private readonly string _root;
    private readonly long _budgetBytes;

    // LRU: `_lru` is most-recent-first, `_cache` indexes into it. Evicting the
    // tail one entry at a time is deliberate — the old cap Clear()ed all 4,096
    // at once, which is a ~4 GB LOH sawtooth on top of the ceiling itself.
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly Lock _gate = new();
    private long _cachedBytes;

    private ClipSource(string root, IReadOnlyList<string> relative, long cacheBudgetBytes)
    {
        _root = root;
        Relative = relative;
        _budgetBytes = cacheBudgetBytes;
    }

    /// <summary>Paths relative to the pool root, sorted ordinally.</summary>
    public IReadOnlyList<string> Relative { get; }

    public int Count => Relative.Count;

    /// <summary>Decoded bytes currently held by the cache. Never above the budget
    /// this pool was scanned with. Exposed so the bound can be ASSERTED rather than
    /// believed — <c>ClipSourceCacheTests</c>.</summary>
    public long CachedBytes
    {
        get { lock (_gate) return _cachedBytes; }
    }

    /// <summary>Entries currently held. Diagnostic; the bound is on bytes.</summary>
    public int CachedCount
    {
        get { lock (_gate) return _cache.Count; }
    }

    /// <param name="root">The pool root.</param>
    /// <param name="cacheBudgetBytes">Decoded-audio budget for this pool's read
    /// cache; see <see cref="DefaultCacheBudgetBytes" />. Tests pass a small one.</param>
    public static ClipSource Scan(string root, long cacheBudgetBytes = DefaultCacheBudgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cacheBudgetBytes);
        if (!Directory.Exists(root)) return new ClipSource(root, [], cacheBudgetBytes);
        var rel = Directory.EnumerateFiles(root, "*.wav", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        return new ClipSource(root, rel, cacheBudgetBytes);
    }

    /// <summary>
    ///     A pool that is NOT scanned: <see cref="Relative" /> is empty and only
    ///     <see cref="Read" /> works.
    ///     <para>
    ///         <b>For a caller that is GIVEN its paths</b> — <c>eval-render</c> reads
    ///         the DI paths out of a predictions file, so it needs the bounded read
    ///         cache and nothing else. Scanning would enumerate the whole dataset
    ///         root, which at 150,000 clips is a directory walk and ~15 MB of path
    ///         strings for a list that is never indexed.
    ///     </para>
    /// </summary>
    /// <param name="root">The root the relative paths are resolved against.</param>
    /// <param name="cacheBudgetBytes">Decoded-audio budget; see
    /// <see cref="DefaultCacheBudgetBytes" />.</param>
    /// <returns>An unscanned pool.</returns>
    public static ClipSource Unscanned(string root, long cacheBudgetBytes = DefaultCacheBudgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cacheBudgetBytes);
        return new ClipSource(root, [], cacheBudgetBytes);
    }

    /// <summary>Reads one pool file as mono float at its own rate, cached. The DI pool
    /// is already mono 48 kHz by construction (the fetch script does that once), so
    /// this never resamples — a resampler here would be a second, slower, different
    /// one from the engine's.
    /// <para>
    ///     The cache is an LRU bounded by BYTES (<see cref="DefaultCacheBudgetBytes" />),
    ///     so a pool larger than the budget costs a re-decode, never RAM.
    /// </para></summary>
    public float[] Read(string relative)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(relative, out var hit))
            {
                Touch(hit);
                return hit.Value.Samples;
            }
        }

        var buffer = WavIo.Read(Path.Combine(_root, relative));
        var mono = buffer.Channels == 1 ? buffer.Interleaved : buffer.ToMono();

        lock (_gate)
        {
            // Another thread may have read the same file meanwhile: keep ITS copy,
            // so one path is one array and the cache accounting cannot double-count.
            if (_cache.TryGetValue(relative, out var raced))
            {
                Touch(raced);
                return raced.Value.Samples;
            }

            var bytes = (long)mono.Length * sizeof(float);
            // A single file past the whole budget is returned UNCACHED rather than
            // evicting everything for something that cannot stay anyway.
            if (bytes > _budgetBytes) return mono;

            var node = _lru.AddFirst(new CacheEntry(relative, mono, bytes));
            _cache[relative] = node;
            _cachedBytes += bytes;
            while (_cachedBytes > _budgetBytes && _lru.Last is { } oldest)
            {
                _lru.RemoveLast();
                _cache.Remove(oldest.Value.Key);
                _cachedBytes -= oldest.Value.Bytes;
            }
        }

        return mono;
    }

    // _gate held.
    private void Touch(LinkedListNode<CacheEntry> node)
    {
        if (!ReferenceEquals(_lru.First, node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
        }
    }

    private readonly record struct CacheEntry(string Key, float[] Samples, long Bytes);

    /// <summary>A window of <paramref name="frames" /> samples starting at a random
    /// offset, zero-padded when the source is short.</summary>
    public float[] Window(string relative, int frames, Random rng)
    {
        var src = Read(relative);
        var outBuf = new float[frames];
        if (src.Length == 0) return outBuf;
        var start = src.Length > frames ? rng.Next(src.Length - frames) : 0;
        var n = Math.Min(frames, src.Length - start);
        Array.Copy(src, start, outBuf, 0, n);
        return outBuf;
    }
}
