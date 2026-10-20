namespace DotCharter.UI.ViewModels.Panels;

/// <summary>
///     The song lists' cover PIPELINE policy — what actually decides scroll
///     smoothness on a real library, which no headless filter-pass probe sees.
///     <para>
///         Three rules, each fixing a measured real-app cost:
///     </para>
///     <list type="number">
///         <item>
///             <b>DWELL before decode.</b> A container prepared by a scroll
///             fling is on screen for a frame or two; decoding its cover is
///             pure waste. A load only proceeds once the row has stayed wanted
///             for <see cref="DwellMs" /> — a row that scrolled past never
///             touches the disk.
///         </item>
///         <item>
///             <b>ONE global decode throttle.</b> Per-container loads would be
///             unbounded <c>Task.Run</c>s — a fast fling queues dozens of
///             concurrent decodes (thread-pool saturation + GC churn, felt as
///             scroll lag). Every decode passes the shared
///             <see cref="DecodeThrottle" /> (2 wide, the width the warm batch
///             always used).
///         </item>
///         <item>
///             <b>PARK, don't dispose.</b> Disposing a recycled row's cover
///             immediately made scrolling BACK re-decode everything. A released
///             cover is parked in a byte-capped LRU instead
///             (<see cref="MaxParkedBytes" />); scrolling back inside the
///             budget is free, and only eviction pays the dispose. The budget
///             bounds worst-case retained memory the way dispose-on-clear did,
///             without its churn.
///         </item>
///     </list>
///     <para>
///         All mutations run on the UI thread (container events and the decode
///         publish both land there), so the structure needs no locks. The
///         eviction policy itself is the PURE <see cref="CoverParkPolicy{TKey}" />
///         below, unit-tested without a bitmap (an Avalonia <c>Bitmap</c> needs
///         a Skia platform the default gate deliberately does not boot); this
///         type is the thin shell that reads <c>PixelSize</c> and disposes.
///     </para>
/// </summary>
public sealed class SongCoverCache
{
    /// <summary>How long a row must stay wanted before its decode starts.
    /// Below human dwell perception, above a scroll fling's per-row dwell.</summary>
    public const int DwellMs = 120;

    /// <summary>Concurrent decodes across BOTH lists.</summary>
    public static readonly SemaphoreSlim DecodeThrottle = new(2);

    /// <summary>~64 MB of parked RGBA. Typical capped covers are 256²
    /// (~0.26 MB) so this parks ~240 of them; pathological 640-px covers
    /// (~1.6 MB) still cannot pile past the budget.</summary>
    public const long MaxParkedBytes = 64L * 1024 * 1024;

    /// <summary>The one shared instance — both lists show the SAME
    /// <see cref="PsarcSongListItem" /> instances, so the park must be shared
    /// or each surface would evict against the other's budget blindly.</summary>
    public static SongCoverCache Shared { get; } = new();

    private readonly CoverParkPolicy<PsarcSongListItem> _policy = new(MaxParkedBytes);

    /// <summary>Parked bytes right now — test seam.</summary>
    public long ParkedBytes => _policy.ParkedBytes;

    /// <summary>
    ///     A recycled row stops showing this item: mark it unwanted (so a
    ///     dwell-pending load aborts) and keep any decoded bitmap on the leash
    ///     instead of disposing it, evicting least-recently-parked items past
    ///     the byte budget.
    /// </summary>
    public void Park(PsarcSongListItem item)
    {
        item.MarkCoverUnwanted();

        var bitmap = item.CoverImage;
        if (bitmap == null) return;

        var size = bitmap.PixelSize;
        foreach (var evicted in _policy.Park(item, (long)size.Width * size.Height * 4))
            evicted.DisposeCoverImage();
    }

    /// <summary>The item is on screen again — its bitmap (if parked) is live
    /// once more and leaves the eviction order.</summary>
    public void Unpark(PsarcSongListItem item) => _policy.Unpark(item);
}

/// <summary>Pure byte-budgeted LRU over opaque keys: parking past the budget
/// yields the evicted keys, oldest first; re-parking refreshes recency;
/// unparking removes without eviction. No time, no bitmaps — the whole
/// contract is unit-testable in the default gate.</summary>
public sealed class CoverParkPolicy<TKey> where TKey : notnull
{
    private readonly long _maxBytes;
    private readonly LinkedList<TKey> _order = [];
    private readonly Dictionary<TKey, (LinkedListNode<TKey> Node, long Bytes)> _parked = [];

    public CoverParkPolicy(long maxBytes) => _maxBytes = maxBytes;

    public long ParkedBytes { get; private set; }
    public int ParkedCount => _parked.Count;

    /// <summary>Parks (or re-parks, refreshing recency) and returns the keys
    /// evicted to get back under the budget — oldest first, never the key just
    /// parked unless it alone exceeds the whole budget.</summary>
    public IReadOnlyList<TKey> Park(TKey key, long bytes)
    {
        Unpark(key);
        _parked[key] = (_order.AddLast(key), bytes);
        ParkedBytes += bytes;

        List<TKey>? evicted = null;
        while (ParkedBytes > _maxBytes && _order.First is { } oldest)
        {
            (evicted ??= []).Add(oldest.Value);
            Unpark(oldest.Value);
        }

        return evicted ?? (IReadOnlyList<TKey>)[];
    }

    public void Unpark(TKey key)
    {
        if (!_parked.Remove(key, out var entry)) return;
        _order.Remove(entry.Node);
        ParkedBytes -= entry.Bytes;
    }
}
