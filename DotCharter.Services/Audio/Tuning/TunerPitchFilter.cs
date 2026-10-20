namespace DotCharter.Services.Audio.Tuning;

/// <summary>
///     Pure stabilizer between the raw YIN detector and the tuner needle:
///     gates out silence (absolute RMS floor), ignores readings during the
///     attack transient right after a pluck (the first ~50 ms are dominated
///     by pick noise and sharp string stretch, which read sharp), and
///     median-smooths the surviving readings over a short window so a
///     single octave/harmonic glitch can never twitch the needle.
///     <para>
///     Feed it every UI tick — including silent ones, which it needs to see
///     to recognize the next attack. It returns the smoothed frequency to
///     display, or <c>null</c> while there is nothing trustworthy to show.
///     </para>
/// </summary>
public sealed class TunerPitchFilter
{
    /// <summary>Absolute RMS below which the input counts as silence.</summary>
    public float MinRms { get; init; } = 0.005f;

    /// <summary>RMS ratio against the previous tick that marks a fresh
    /// attack (pluck) and starts the transient hold-off.</summary>
    public float AttackJumpRatio { get; init; } = 2.5f;

    /// <summary>How long after a detected attack readings are ignored —
    /// the transient reads sharp before the string settles.</summary>
    public double AttackHoldMs { get; init; } = 50.0;

    /// <summary>Median window length (readings). Odd values center best.</summary>
    public int MedianWindow { get; init; } = 5;

    /// <summary>Readings required before anything is reported: one lone
    /// frame is never evidence, two agreeing frames are.</summary>
    public int MinReadings { get; init; } = 2;

    private readonly List<double> _recent = [];
    private double _holdRemainingMs;
    private float _prevRms = -1f;

    /// <summary>Clears all history (silence, dialog reopen).</summary>
    public void Reset()
    {
        _recent.Clear();
        _holdRemainingMs = 0;
        _prevRms = -1f;
    }

    /// <summary>
    ///     Processes one tick. <paramref name="frequencyHz"/> is the raw
    ///     detector reading (null when the detector had none), <paramref
    ///     name="rms"/> the level of the analyzed window, <paramref
    ///     name="deltaMs"/> the elapsed time since the previous tick.
    ///     Returns the smoothed frequency, or null while gated.
    /// </summary>
    public double? Process(double? frequencyHz, float rms, double deltaMs)
    {
        // Attack detection first: a level jump against the previous tick
        // (from silence or over the previous decay) restarts the hold-off,
        // and whatever the needle was averaging belongs to the old pluck.
        if (_prevRms >= 0f && rms >= MinRms && rms > _prevRms * AttackJumpRatio)
        {
            _holdRemainingMs = AttackHoldMs;
            _recent.Clear();
        }

        _prevRms = rms;

        if (rms < MinRms)
        {
            // Silence between plucks: stale readings must not smooth into
            // the next pluck (the player may have turned the peg meanwhile).
            _recent.Clear();
            _holdRemainingMs = 0;
            return null;
        }

        if (_holdRemainingMs > 0)
        {
            _holdRemainingMs -= Math.Max(0, deltaMs);
            return null;
        }

        if (frequencyHz is not { } freq || freq <= 0)
            return null; // aperiodic frame; keep history, the string still rings

        _recent.Add(freq);
        if (_recent.Count > MedianWindow) _recent.RemoveAt(0);
        if (_recent.Count < Math.Max(1, MinReadings)) return null;

        var sorted = _recent.ToArray();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }
}
