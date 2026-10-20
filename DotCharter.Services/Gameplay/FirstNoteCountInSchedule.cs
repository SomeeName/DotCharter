namespace DotCharter.Services.Gameplay;

/// <summary>
///     The RUNTIME half of <see cref="FirstNoteCountInPlan" />: which of the armed
///     clicks to hand the engine NOW. The plan decides which beats click; this
///     walks them as the raw playhead comes within reach, because the native
///     engine drops any click scheduled more than ~5 s ahead of its render clock.
///     <para>
///         Pure: no clock, no audio. The Live Play worker asks it once per playing
///         tick — <see cref="RearmAfterSeek" /> when the transport was seeked, then
///         <see cref="TryTakeDue" /> until it answers false — and schedules each
///         click it is handed itself. Not thread-safe on its own: arm, disarm and
///         the walk are serialized by the caller's session gate.
///         Allocation-free except for <see cref="Arm" />.
///     </para>
/// </summary>
public sealed class FirstNoteCountInSchedule
{
    /// <summary>Chart-ms ahead of the raw playhead at which a count-in click is
    /// handed to the engine. Comfortably above the 30 Hz tick jitter and, even at
    /// the 25% practice-speed floor (×4 wall-clock), well under the engine's ~5 s
    /// scheduled-click horizon.</summary>
    public const double LookaheadMs = 500.0;

    /// <summary>Beyond a quarter second late the engine would drop a click anyway,
    /// so a click that far behind the playhead is skipped rather than fired stale
    /// at the head of the block.</summary>
    public const double StaleAfterMs = 250.0;

    private FirstNoteCountInPlan.Click[] _clicks = [];
    private bool[] _scheduled = [];

    /// <summary>Whether any clicks are armed.</summary>
    public bool IsArmed => _clicks.Length > 0;

    /// <summary>Arms the clicks of one run (sorted by time, as
    /// <see cref="FirstNoteCountInPlan.Build" /> returns them). Null or empty
    /// disarms.</summary>
    public void Arm(FirstNoteCountInPlan.Click[]? clicks)
    {
        _clicks = clicks ?? [];
        _scheduled = _clicks.Length > 0 ? new bool[_clicks.Length] : [];
    }

    /// <summary>No run, no pre-note count-in.</summary>
    public void Disarm()
    {
        _clicks = [];
        _scheduled = [];
    }

    /// <summary>A seek (e.g. the resume rewind, which clears the engine's
    /// scheduled clicks) re-arms every handed-out click still ahead of the new
    /// raw playhead.</summary>
    public void RearmAfterSeek(double rawMs)
    {
        for (var i = 0; i < _clicks.Length; i++)
        {
            if (_scheduled[i] && _clicks[i].TimeMs > rawMs)
            {
                _scheduled[i] = false;
            }
        }
    }

    /// <summary>
    ///     The next click that has come within <see cref="LookaheadMs" /> of
    ///     <paramref name="rawMs" /> and was not handed out yet; marks it handed out.
    ///     A click more than <see cref="StaleAfterMs" /> behind the playhead is
    ///     marked and skipped. False when nothing (more) is due this tick.
    /// </summary>
    public bool TryTakeDue(double rawMs, out FirstNoteCountInPlan.Click click)
    {
        for (var i = 0; i < _clicks.Length; i++)
        {
            if (_scheduled[i])
            {
                continue;
            }

            var candidate = _clicks[i];
            if (candidate.TimeMs > rawMs + LookaheadMs)
            {
                break;
            }

            _scheduled[i] = true;
            if (candidate.TimeMs < rawMs - StaleAfterMs)
            {
                continue;
            }

            click = candidate;
            return true;
        }

        click = default;
        return false;
    }
}
