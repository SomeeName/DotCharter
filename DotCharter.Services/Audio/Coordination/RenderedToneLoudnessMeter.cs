using DotCharter.Services.Ai.Match;
using DotCharter.Tone.Audio.Dsp.Loudness;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Services.Audio.Coordination;

/// <summary>
///     Measures how loud a tone chain REALLY is: renders <see cref="CalibrationPhrase" />
///     through it offline and reads <see cref="IntegratedLoudness" />. What the
/// Rocksmith-tone levelling is built on, because the static walker
///     (<see cref="DcRigLoudnessNormalizer" />) cannot see a cab IR raising a
///     saturated amp's peak, nor tell a clean tone's crest from a driven one's.
/// </summary>
public interface IToneLoudnessMeter
{
    /// <summary>The cached measurement of <paramref name="body" />, or null when it
    /// has not been measured in this session. Cheap; any thread.</summary>
    ToneLoudness? TryGetCached(IReadOnlyList<DcRigModule> body);

    /// <summary>
    ///     Measures <paramref name="body" /> (cached per chain, see
    ///     <see cref="ToneLoudnessKey" />). Never runs on the caller's thread; never
    ///     throws. Null when no offline rig is available here — the caller keeps its
    ///     estimate.
    /// </summary>
    Task<ToneLoudness?> MeasureAsync(IReadOnlyList<DcRigModule> body, CancellationToken ct = default);
}

/// <summary>
///     <see cref="IToneLoudnessMeter" /> over the app's device-less offline rig
///     (<see cref="IOfflineRigHost" />, the AI matcher's null-device engine, not the
///     live one): nothing touches the audio thread or the live chain.
///     <para>
///         <b>Off every thread that matters.</b> One measurement at a time, on a pool
///         thread lowered to <see cref="ThreadPriority.BelowNormal" /> for the length of
///         the render. Measured over the owner's 580 converted tones: a
///         median of 0.84 s per tone (p90 1.2 s), drain included, on a box other test
///         runs were sharing — so a song's two to four tones are levelled a few
///         seconds after its pre-play screen opens. Requests for a chain already
///         being measured share the one task.
///     </para>
///     <para>
///         <b>Drained first.</b> The host rebuilds the chain for every render, but the
///         rig lets a REMOVED reverb or delay ring out (the fidelity harness measured
///         a dry tone 11 dB too loud after a hall), so the meter first renders silence
///         through an empty rig until the output is silent, bounded at 30 s.
///     </para>
///     <para>
///         <b>Cache:</b> in memory for the life of the process, bounded at
///         <see cref="MaxCachedChains" /> chains (oldest dropped first). A song's
///         tones are measured once and every later prepare or play reads the cache.
///     </para>
/// </summary>
public sealed class RenderedToneLoudnessMeter : IToneLoudnessMeter
{
    /// <summary>Plenty for a session: the owner's whole library is 580 tones.</summary>
    public const int MaxCachedChains = 1024;

    private const int DrainChunk = CalibrationPhrase.SampleRate / 4;
    private const int MaxDrainChunks = 120; // 30 s
    private const float SilentLinear = 1e-6f;

    /// <summary>
    ///     Silence rendered after the rebuild and before the phrase, and left out of
    ///     the measurement: the applier adds every node bypassed and the chain fades
    ///     it in over 10 ms, during which the DRY input leaks through. Measured: a
    ///     quiet chain's peak read the leak (-25 dBFS) instead of its own output.
    /// </summary>
    private const int PreRollSamples = CalibrationPhrase.SampleRate / 4;

    private readonly IOfflineRigHost _host;
    private readonly IAppLogger? _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ToneLoudness> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly Dictionary<string, Task<ToneLoudness?>> _inFlight = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _renderOne = new(1, 1);
    private volatile bool _unavailable;

    public RenderedToneLoudnessMeter(IOfflineRigHost host, IAppLogger? logger = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _logger = logger;
    }

    private int _renderCount;

    /// <summary>How many renders this meter has run (test seam: a cache hit is not one).</summary>
    internal int RenderCount => Volatile.Read(ref _renderCount);

    /// <inheritdoc />
    public ToneLoudness? TryGetCached(IReadOnlyList<DcRigModule> body)
    {
        var key = ToneLoudnessKey.For(body);
        lock (_gate) return _cache.TryGetValue(key, out var hit) ? hit : null;
    }

    /// <inheritdoc />
    public Task<ToneLoudness?> MeasureAsync(IReadOnlyList<DcRigModule> body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (_unavailable) return Task.FromResult<ToneLoudness?>(null);

        var key = ToneLoudnessKey.For(body);
        var snapshot = body.ToList();
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var hit)) return Task.FromResult<ToneLoudness?>(hit);
            if (_inFlight.TryGetValue(key, out var running)) return running;

            var task = Task.Run(() => MeasureLockedAsync(key, snapshot, ct), CancellationToken.None);
            _inFlight[key] = task;
            return task;
        }
    }

    private async Task<ToneLoudness?> MeasureLockedAsync(string key, List<DcRigModule> body, CancellationToken ct)
    {
        try
        {
            await _renderOne.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                lock (_gate)
                {
                    if (_cache.TryGetValue(key, out var hit)) return hit;
                }

                var measured = RenderAndMeasure(body);
                if (measured is { } m) Remember(key, m);
                return measured;
            }
            finally
            {
                _renderOne.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, "Tone loudness: the offline measurement failed; the estimate stays.", ex);
            return null;
        }
        finally
        {
            lock (_gate) _inFlight.Remove(key);
        }
    }

    private ToneLoudness? RenderAndMeasure(List<DcRigModule> body)
    {
        var thread = Thread.CurrentThread;
        var priority = thread.Priority;
        thread.Priority = ThreadPriority.BelowNormal;
        try
        {
            if (!_host.IsAvailable)
            {
                _unavailable = true;
                _logger?.Info(AppLogCategory.Audio,
                    "Tone loudness: no offline rig (" + _host.Describe() + "); converted tones keep the estimate.");
                return null;
            }

            Drain();
            var phrase = CalibrationPhrase.Samples;
            var left = new float[PreRollSamples + phrase.Length];
            phrase.CopyTo(left.AsSpan(PreRollSamples));
            var right = (float[])left.Clone();
            _host.Render(new DcRigPreset(body), left, right, CalibrationPhrase.SampleRate);
            Interlocked.Increment(ref _renderCount);

            var wet = left.AsSpan(PreRollSamples);
            for (var i = 0; i < wet.Length; i++)
            {
                if (!float.IsFinite(wet[i])) wet[i] = 0f;
            }

            return new ToneLoudness(
                IntegratedLoudness.Measure(wet, CalibrationPhrase.SampleRate),
                IntegratedLoudness.PeakDbfs(wet));
        }
        finally
        {
            thread.Priority = priority;
        }
    }

    /// <summary>Silence through an empty rig until whatever the previous render left
    /// ringing has died away (see the class doc).</summary>
    private void Drain()
    {
        var empty = new DcRigPreset([]);
        var l = new float[DrainChunk];
        var r = new float[DrainChunk];
        for (var i = 0; i < MaxDrainChunks; i++)
        {
            Array.Clear(l);
            Array.Clear(r);
            _host.Render(empty, l, r, CalibrationPhrase.SampleRate);
            var peak = 0f;
            foreach (var v in l) peak = MathF.Max(peak, MathF.Abs(v));
            if (peak < SilentLinear) return;
        }
    }

    private void Remember(string key, ToneLoudness value)
    {
        lock (_gate)
        {
            if (_cache.ContainsKey(key)) return;
            _cache[key] = value;
            _order.Enqueue(key);
            while (_order.Count > MaxCachedChains) _cache.Remove(_order.Dequeue());
        }
    }
}
