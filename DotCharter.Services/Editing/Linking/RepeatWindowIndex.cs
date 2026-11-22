using System.Numerics;

namespace DotCharter.Services.Editing.Linking;

/// <summary>
///     Exact prefix ranks for all power-of-two lengths. Two overlapping ranks identify a
///     window of any length, so checking it needs neither a per-window hash nor a bar scan.
///     Callers keep one length's buckets at a time.
/// </summary>
internal sealed class RepeatWindowIndex
{
    private readonly List<int[]> _ranks = [];

    public RepeatWindowIndex(int[] bars)
    {
        _ranks.Add(bars);
        for (var half = 1; half <= bars.Length / 2; half *= 2)
        {
            var previous = _ranks[^1];
            var next = new int[previous.Length - half];
            var ids = new Dictionary<(int, int), int>();
            for (var at = 0; at < next.Length; at++)
            {
                var pair = (previous[at], previous[at + half]);
                if (!ids.TryGetValue(pair, out var id)) ids[pair] = id = ids.Count;
                next[at] = id;
            }

            _ranks.Add(next);
        }
    }

    public (int Head, int Tail) Key(int first, int length)
    {
        var exponent = BitOperations.Log2((uint)length);
        var ranks = _ranks[exponent];
        return (ranks[first], ranks[first + length - (1 << exponent)]);
    }
}
