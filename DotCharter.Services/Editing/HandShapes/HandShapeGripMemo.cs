namespace DotCharter.Services.Editing.HandShapes;

/// <summary>
///     Which grips one hand can hold, remembered ACROSS suggester runs. Whether a shape can be fingered
///     is a pure function of its frets, the capo, the string count and the fret-spread limit
///     (<see cref="ChordTemplateFingerSetter" /> is "a pure function of the fret shape"), so a verdict never
///     goes stale; an edit that moves one note re-judges the same few thousand grips, and this answers them
///     without building a probe template and running the fingering heuristic (about 1.2 KB each) again.
///     Thread-safe (one short lock) and bounded: past its limit it starts over.
/// </summary>
internal sealed class HandShapeGripMemo(int maxEntries = HandShapeGripMemo.DefaultMaxEntries)
{
    internal const int DefaultMaxEntries = 1 << 16;

    private readonly Dictionary<Key, bool> _judged = [];
    private readonly Lock _gate = new();

    /// <summary>The one the suggester uses.</summary>
    public static HandShapeGripMemo Shared { get; } = new();

    public int Count
    {
        get
        {
            lock (_gate) return _judged.Count;
        }
    }

    public bool TryGet(ulong shape, int capo, int stringCount, int maxSpread, out bool held)
    {
        lock (_gate) return _judged.TryGetValue(new Key(shape, capo, stringCount, maxSpread), out held);
    }

    public void Store(ulong shape, int capo, int stringCount, int maxSpread, bool held)
    {
        lock (_gate)
        {
            if (_judged.Count >= maxEntries) _judged.Clear();
            _judged[new Key(shape, capo, stringCount, maxSpread)] = held;
        }
    }

    /// <summary>Forgets every verdict (a test measuring a cold run).</summary>
    public void Clear()
    {
        lock (_gate) _judged.Clear();
    }

    private readonly record struct Key(ulong Shape, int Capo, int StringCount, int MaxSpread);
}
