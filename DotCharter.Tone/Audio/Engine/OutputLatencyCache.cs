using System.Diagnostics;

namespace DotCharter.Tone.Audio.Engine;

/// <summary>
///     One device-static reading, remembered per engine instance for a short
///     while so the hot paths stop asking the native side for it.
///     <para>
///         Reading <c>SampleRate</c> and <c>OutputLatencySamples</c> in
///         <c>EstimatedOutputLatencyMs</c> — two
///         <c>Acquire → P/Invoke → Release</c> round trips — on every 250 Hz
///         position tick AND once per frame from both renderers'
///         <c>CurrentTimeMs</c> reads: ~1,100 P/Invokes and ~4,400 interlocked
///         operations a second for two numbers that change only when the
///         device is (re)opened.
///     </para>
///     <para>
///         The key is the ENGINE REFERENCE, so a rebuilt engine misses by
///         construction, and the entry also expires after
///         <see cref="RefreshInterval" /> so a driver that changes its buffer
///         under a live engine (an ASIO control panel) is picked up within a
///         second even if nobody calls <see cref="Invalidate" />. An in-place
///         <c>Reconfigure</c> does call it, for immediacy. Pure; the clock is
///         passed in.
///     </para>
/// </summary>
public sealed class OutputLatencyCache
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private static readonly long RefreshTicks = (long)(RefreshInterval.TotalSeconds * Stopwatch.Frequency);

    private object? _engine;
    private long _stampTicks;
    private double _valueMs;

    /// <summary>True — with the remembered value — when <paramref name="engine" />
    /// is the instance the value was read from and the entry is younger than
    /// <see cref="RefreshInterval" /> at <paramref name="nowTicks" />
    /// (<see cref="Stopwatch.GetTimestamp" /> units).</summary>
    public bool TryGet(object engine, long nowTicks, out double valueMs)
    {
        var cachedEngine = Volatile.Read(ref _engine);
        if (!ReferenceEquals(cachedEngine, engine) || nowTicks - Volatile.Read(ref _stampTicks) >= RefreshTicks)
        {
            valueMs = 0.0;
            return false;
        }

        valueMs = _valueMs;
        return true;
    }

    public void Store(object engine, long nowTicks, double valueMs)
    {
        // Engine last: a reader that sees the new engine sees a stamp and a
        // value written for it.
        _valueMs = valueMs;
        Volatile.Write(ref _stampTicks, nowTicks);
        Volatile.Write(ref _engine, engine);
    }

    /// <summary>Forget the entry — the next read goes to the engine.</summary>
    public void Invalidate() => Volatile.Write(ref _engine, null);
}
