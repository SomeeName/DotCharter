using DotCharter.Services.Audio.Coordination;
using DotCharter.Tone.Audio.Dsp.Loudness;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Services.Audio.Coordination;

/// <summary>
///     An <see cref="IToneLoudnessMeter" /> with no engine: answers
///     <see cref="Answer" /> for every chain, caches like the real one, and — when
///     <see cref="Gated" /> — holds every measurement until <see cref="Release" />, so a
///     test can look at the rig while the measurement is still out.
/// </summary>
internal sealed class FakeToneLoudnessMeter : IToneLoudnessMeter
{
    private readonly Dictionary<string, ToneLoudness> _cache = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _measures;

    public Func<IReadOnlyList<DcRigModule>, ToneLoudness> Answer { get; init; } = _ => new ToneLoudness(-40, -30);

    public bool Gated { get; init; }

    /// <summary>MeasureAsync calls that were not answered from the cache.</summary>
    public int Measures => Volatile.Read(ref _measures);

    public void Release() => _release.TrySetResult();

    public ToneLoudness? TryGetCached(IReadOnlyList<DcRigModule> body)
    {
        lock (_cache) return _cache.TryGetValue(ToneLoudnessKey.For(body), out var hit) ? hit : null;
    }

    public async Task<ToneLoudness?> MeasureAsync(IReadOnlyList<DcRigModule> body, CancellationToken ct = default)
    {
        var key = ToneLoudnessKey.For(body);
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var hit)) return hit;
        }

        Interlocked.Increment(ref _measures);
        if (Gated) await _release.Task.WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        var answer = Answer(body);
        lock (_cache) _cache[key] = answer;
        return answer;
    }
}
