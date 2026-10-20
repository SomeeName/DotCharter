namespace DotCharter.Tone.Audio.Abstractions;

/// <summary>
/// Read-only diagnostics surface exposed by <see cref="IAudioPlaybackService"/>.
/// Phase-1 placeholder — the new engine populates these counters in Phase 2.
/// Kept on the interface so callers compile during the rewrite.
/// </summary>
public sealed class AudioDiagnostics
{
    /// <summary>Total number of buffers the audio thread has filled since startup.</summary>
    public long BufferCount { get; internal set; }

    /// <summary>Average buffer fill time in milliseconds across the recent window.</summary>
    public double AverageFillMs { get; internal set; }

    /// <summary>Worst-case buffer fill time in milliseconds across the recent window.</summary>
    public double WorstFillMs { get; internal set; }

    /// <summary>Configured buffer budget (sample count × 1000 / sample rate).</summary>
    public double BudgetMs { get; internal set; }

    /// <summary>True after at least one underrun has been detected since startup.</summary>
    public bool HasUnderrun { get; internal set; }

    /// <summary>When true, the engine prints per-event lines to stdout.</summary>
    public bool ConsoleEnabled { get; set; }
}
