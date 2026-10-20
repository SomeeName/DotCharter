using System.Diagnostics;

namespace DotCharter.Services.Audio.LivePlay;

public enum LatencyCalibrationState
{
    Idle,
    /// <summary>Between clicks — waiting for the room/ring to settle.</summary>
    Settling,
    /// <summary>Click emitted, waiting for it to come back through the input.</summary>
    WaitingForEcho,
    Complete,
    Failed
}

/// <summary>
///     Latency auto-calibration: the app emits an audible click and waits
///     for it to return through the guitar input (loopback cable, or the
///     pickup/mic hearing the monitors). Emit→detected-onset spread on the
///     shared Stopwatch timebase IS the full round trip a player experiences
///     (output chain + input chain), which is exactly the offset live-play
///     judging must subtract. The median of several clicks is used so one
///     stray onset can't skew the result.
///     <para>
///     Pure state machine on host ticks — the caller emits the actual click,
///     maps detection events to host ticks (capture/host clock sync) and
///     pumps <see cref="Advance"/> from its UI timer.
///     </para>
/// </summary>
public sealed class LatencyCalibrationSession
{
    /// <summary>Clicks to collect before completing.</summary>
    public int ClicksTarget { get; init; } = 5;

    /// <summary>How long one click may take to come back before it counts as missed.</summary>
    public double EchoTimeoutMs { get; init; } = 1500;

    /// <summary>Pause between clicks so ring-out can't be mistaken for the next echo.</summary>
    public double SettleMs { get; init; } = 450;

    /// <summary>Round trips shorter than this are impossible and rejected
    /// (an onset from the player's noodling racing the click).</summary>
    public double MinPlausibleMs { get; init; } = 1.0;

    /// <summary>Missed echoes tolerated before the run fails (nothing plugged
    /// in / input can't hear the output).</summary>
    public int MaxMissedEchoes { get; init; } = 3;

    private readonly List<double> _samples = [];
    private long _clickTicks;
    private long _settleUntilTicks;
    private int _missed;

    public LatencyCalibrationState State { get; private set; } = LatencyCalibrationState.Idle;
    public IReadOnlyList<double> SamplesMs => _samples;
    public int ClicksEmitted { get; private set; }

    /// <summary>Median measured round trip (ms); null until complete.</summary>
    public double? ResultMs
    {
        get
        {
            if (State != LatencyCalibrationState.Complete || _samples.Count == 0) return null;
            var sorted = _samples.OrderBy(static s => s).ToList();
            var mid = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
        }
    }

    public void Start(long nowTicks)
    {
        _samples.Clear();
        _missed = 0;
        ClicksEmitted = 0;
        _settleUntilTicks = nowTicks;
        State = LatencyCalibrationState.Settling;
    }

    public void Cancel() => State = LatencyCalibrationState.Idle;

    /// <summary>True when the caller should emit the next click NOW (and then
    /// call <see cref="NotifyClickEmitted"/> with the emit timestamp).</summary>
    public bool ShouldEmitClick(long nowTicks)
        => State == LatencyCalibrationState.Settling && nowTicks >= _settleUntilTicks;

    public void NotifyClickEmitted(long nowTicks)
    {
        if (State != LatencyCalibrationState.Settling) return;
        _clickTicks = nowTicks;
        ClicksEmitted++;
        State = LatencyCalibrationState.WaitingForEcho;
    }

    /// <summary>Feeds one detected input onset (host ticks). Returns true if
    /// it was accepted as the click's echo.</summary>
    public bool OfferOnset(long onsetHostTicks)
    {
        if (State != LatencyCalibrationState.WaitingForEcho) return false;

        var roundTripMs = (onsetHostTicks - _clickTicks) * 1000.0 / Stopwatch.Frequency;
        if (roundTripMs < MinPlausibleMs || roundTripMs > EchoTimeoutMs) return false;

        _samples.Add(roundTripMs);
        if (_samples.Count >= ClicksTarget)
        {
            State = LatencyCalibrationState.Complete;
        }
        else
        {
            _settleUntilTicks = onsetHostTicks + (long)(SettleMs / 1000.0 * Stopwatch.Frequency);
            State = LatencyCalibrationState.Settling;
        }

        return true;
    }

    /// <summary>Drives timeouts; call from the caller's tick loop.</summary>
    public void Advance(long nowTicks)
    {
        if (State != LatencyCalibrationState.WaitingForEcho) return;
        var waitedMs = (nowTicks - _clickTicks) * 1000.0 / Stopwatch.Frequency;
        if (waitedMs <= EchoTimeoutMs) return;

        if (++_missed > MaxMissedEchoes)
        {
            State = LatencyCalibrationState.Failed;
            return;
        }

        _settleUntilTicks = nowTicks + (long)(SettleMs / 1000.0 * Stopwatch.Frequency);
        State = LatencyCalibrationState.Settling;
    }
}
