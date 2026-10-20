namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

/// <summary>
/// Immutable, pure resolver of "which tone key is active at time t". The
/// boundaries are the chart's <c>ToneChange</c>s pre-resolved to ms; before the
/// first boundary the base tone (the arrangement's StartingTone / ToneBase)
/// applies. Built by <c>RsTonePlaybackCoordinator</c> from
/// <c>Arrangement.ToneChanges</c>. Zero deps — trivially unit-testable.
/// </summary>
public sealed class RsToneSchedule
{
    private readonly double[] _times;
    private readonly string[] _keys;

    public RsToneSchedule(string baseToneKey, IReadOnlyList<(double TimeMs, string ToneKey)> boundaries)
    {
        BaseToneKey = baseToneKey ?? "";

        // Defensive: sort ascending by time — callers should pass sorted, but a
        // dirty chart may not, and binary search demands monotonic times.
        var sorted = boundaries.OrderBy(b => b.TimeMs).ToArray();
        _times = new double[sorted.Length];
        _keys = new string[sorted.Length];
        for (var i = 0; i < sorted.Length; i++)
        {
            _times[i] = sorted[i].TimeMs;
            _keys[i] = sorted[i].ToneKey ?? "";
        }
    }

    /// <summary>The tone active before the first boundary (arrangement base tone).</summary>
    public string BaseToneKey { get; }

    /// <summary>No boundaries — the base tone applies for the whole song.</summary>
    public bool IsEmpty => _times.Length == 0;

    /// <summary>The tone key active at <paramref name="timeMs"/>: the key of the
    /// last boundary at or before t, else the base tone. Binary search — O(log n).</summary>
    public string ResolveAt(double timeMs)
    {
        int lo = 0, hi = _times.Length - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (_times[mid] <= timeMs)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found >= 0 ? _keys[found] : BaseToneKey;
    }
}
