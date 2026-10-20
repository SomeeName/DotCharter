namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

/// <summary>
/// Stateful, allocation-free change-detector over an <see cref="RsToneSchedule"/>.
/// As the playhead advances, <see cref="TryAdvance"/> outs the new active tone key
/// ONLY when it changed since the last call, so the caller applies a preset exactly
/// once per boundary crossing (and does nothing on the vast majority of ticks where
/// the tone is unchanged — no allocation on that path). Because it re-resolves the
/// (tiny) schedule by binary search every call, a backward / forward seek is handled
/// implicitly: the next tick resolves the correct tone for the new time and fires if
/// it differs. <see cref="Reset"/> forces the next call to re-report the active tone
/// (re-arms a fresh take-over of the chain).
/// </summary>
public sealed class RsToneScanner
{
    // Sentinel that never equals a real (possibly empty) tone key, so a Reset()
    // makes the next TryAdvance report whatever ResolveAt returns as a change.
    private const string Unresolved = "\0__rs_tone_unresolved__";

    private readonly RsToneSchedule _schedule;
    private string _activeKey;

    public RsToneScanner(RsToneSchedule schedule)
    {
        _schedule = schedule;
        _activeKey = schedule.BaseToneKey;
    }

    /// <summary>The current active key without advancing.</summary>
    public string ActiveKey => _activeKey;

    /// <summary>Force the next <see cref="TryAdvance"/> to report the resolved key
    /// as a change (re-arm), regardless of the key that was active.</summary>
    public void Reset() => _activeKey = Unresolved;

    /// <summary>Returns true and outs the new active tone key WHEN it changed since
    /// the last call. Allocation-free; the outed string is interned from the
    /// schedule. On no change, <paramref name="toneKey"/> is the still-active key
    /// and the method returns false.</summary>
    public bool TryAdvance(double timeMs, out string toneKey)
    {
        var resolved = _schedule.ResolveAt(timeMs);
        if (string.Equals(resolved, _activeKey, StringComparison.Ordinal))
        {
            toneKey = _activeKey;
            return false;
        }

        _activeKey = resolved;
        toneKey = resolved;
        return true;
    }
}
