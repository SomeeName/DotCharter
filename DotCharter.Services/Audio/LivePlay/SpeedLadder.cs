namespace DotCharter.Services.Audio.LivePlay;

/// <summary>
///     Rocksmith-style riff-repeater speed ladder: practice starts slowed
///     down and every clean pass steps the playback speed up until the song
///     runs at 100%; a badly failed pass steps back down. Pure and
///     unit-testable — the session applies <see cref="CurrentSpeed"/> to the
///     transport's rubberband time-stretch.
/// </summary>
public sealed class SpeedLadder
{
    public const double MinSpeed = 0.25;
    public const double MaxSpeed = 1.0;

    /// <summary>Speed the ladder starts at (fraction, e.g. 0.6 = 60%).</summary>
    public double StartSpeed { get; init; } = 0.6;

    /// <summary>Step added after a clean pass (e.g. 0.05 = +5%).</summary>
    public double StepUp { get; init; } = 0.05;

    /// <summary>Step removed after a failed pass.</summary>
    public double StepDown { get; init; } = 0.05;

    /// <summary>Accuracy a pass needs to count as clean and step up.</summary>
    public double CleanPassAccuracy { get; init; } = 0.95;

    /// <summary>Accuracy below which a pass steps the ladder back down.
    /// Between the two thresholds the speed holds.</summary>
    public double FailPassAccuracy { get; init; } = 0.70;

    private double _currentSpeed = double.NaN;

    /// <summary>Current practice speed as a fraction of real time.</summary>
    public double CurrentSpeed
    {
        get
        {
            if (double.IsNaN(_currentSpeed)) _currentSpeed = Clamp(StartSpeed);
            return _currentSpeed;
        }
    }

    public bool AtFullSpeed => CurrentSpeed >= MaxSpeed - 1e-9;

    /// <summary>Number of clean passes completed at full speed — the ladder's
    /// exit signal ("you can play it at 100% now").</summary>
    public int CleanFullSpeedPasses { get; private set; }

    /// <summary>
    ///     Registers one completed practice pass and returns the speed the
    ///     NEXT pass should run at. Passes with no judged sounds hold speed
    ///     (an empty loop region must not ratchet the ladder).
    /// </summary>
    public double CompletePass(double accuracy, int judgedSounds)
    {
        var current = CurrentSpeed;
        if (judgedSounds <= 0) return current;

        if (accuracy >= CleanPassAccuracy)
        {
            if (AtFullSpeed) CleanFullSpeedPasses++;
            else _currentSpeed = Clamp(current + StepUp);
        }
        else if (accuracy < FailPassAccuracy)
        {
            _currentSpeed = Clamp(current - StepDown);
        }

        return CurrentSpeed;
    }

    public void Reset()
    {
        _currentSpeed = Clamp(StartSpeed);
        CleanFullSpeedPasses = 0;
    }

    private static double Clamp(double speed) => Math.Clamp(speed, MinSpeed, MaxSpeed);
}
