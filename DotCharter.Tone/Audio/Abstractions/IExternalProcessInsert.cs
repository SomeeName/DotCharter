namespace DotCharter.Tone.Audio.Abstractions;

/// <summary>
///     Where an external process insert meets another process: the shared-memory block's
///     name and the format it carries. Generic on purpose — nothing here knows which
///     program sits on the other side.
/// </summary>
/// <param name="SharedMemoryName">The named file mapping (<c>Local\…</c>).</param>
/// <param name="SampleRate">The rate the other process renders at; the insert converts to and from the engine's.</param>
/// <param name="BlockFrames">The other process's block size, as it reported it.</param>
public sealed record ExternalInsertEndpoint(string SharedMemoryName, int SampleRate, int BlockFrames);

/// <summary>The insert's counters, read off the native module (never computed on the audio thread by managed code).</summary>
/// <param name="LatencyFrames">Added latency at the ENGINE's rate: the ring's extra block plus resampling.</param>
/// <param name="EngineSampleRate">The rate <paramref name="LatencyFrames" /> is counted in.</param>
/// <param name="Underruns">Blocks the audio callback found no processed audio for (it played the dry signal).</param>
/// <param name="Overruns">Blocks the callback could not hand over because the input ring was full.</param>
public sealed record ExternalInsertStats(int LatencyFrames, int EngineSampleRate, long Underruns, long Overruns)
{
    public double LatencyMs => EngineSampleRate > 0 ? LatencyFrames * 1000.0 / EngineSampleRate : 0;
}

/// <summary>
///     A send/return node that hands the live input to another process through shared
///     memory and plays back what that process returns. The audio exchange is NATIVE
///     (docs/agents/services/tonelab-bridge.md § Audio path): the callback never waits on
///     the other process, reads the returned block without blocking one block late, and
///     falls back to the dry signal on an underrun. This seam only opens, closes and reads
///     counters; it moves no audio.
/// </summary>
public interface IExternalProcessInsert
{
    /// <summary>False while the native engine has no insert module (every build before the ABI bump).</summary>
    bool IsSupported { get; }

    bool IsOpen { get; }

    /// <summary>Maps the block and starts the exchange; false when the block is missing or its header does not match.</summary>
    bool Open(ExternalInsertEndpoint endpoint);

    void Close();

    /// <summary>The counters while open, else null.</summary>
    ExternalInsertStats? ReadStats();
}
