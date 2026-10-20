using System.Diagnostics;
using Avalonia.Threading;
using DotCharter.Services.Gameplay;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
///     Dynamic backing mix (0.21 game-feel): a smooth, run-scoped gain follower
///     that ducks the player's stem when they miss and swells it back as the
///     streak recovers. The target gain comes from the pure
///     <see cref="StemDuckMath"/> off the live streak; a ~50 Hz timer eases the
///     applied gain toward it so the ramp never zippers.
///     <para>
///     It is deliberately NOT the per-run MUTE path (<c>ApplyBackingTrackMutes</c>)
///     and it NEVER writes <c>Stem.Muted</c> or <c>Stem.Volume</c> — it only pushes
///     an engine gain through <see cref="IStemMixer.SetStemVolume"/> on
///     top of the stem's live base volume, and <see cref="Stop"/> restores that
///     base. Stop runs on every exit path (the owner ties it to leaving the Playing
///     screen), so a bailed / paused / retried run can never leave the stem ducked.
///     </para>
/// </summary>
public sealed class StemDuckFollower
{
    private const double TickMs = 20.0; // ~50 Hz control rate

    private readonly IStemMixer? _audio;
    private readonly bool _driveWithTimer;
    private readonly Stopwatch _clock = new();

    private DispatcherTimer? _timer;
    private IReadOnlyList<(string Name, Func<double> BaseVolume)> _stems = [];
    private Func<int>? _streak;
    private double _gain = 1.0;
    private double _lastTickMs;

    /// <param name="audio">Playback engine (null in headless/minimal builds).</param>
    /// <param name="driveWithTimer">Production drives the smoothing from an internal
    /// ~50 Hz <see cref="DispatcherTimer"/>. Tests pass false and call
    /// <see cref="Tick"/> directly, so no Avalonia dispatcher is required.</param>
    public StemDuckFollower(IStemMixer? audio, bool driveWithTimer = true)
    {
        _audio = audio;
        _driveWithTimer = driveWithTimer;
    }

    /// <summary>True while the follower is driving a stem (verification hook).</summary>
    public bool IsActive { get; private set; }

    /// <summary>Current smoothed gain multiplier applied over the base volume.</summary>
    public double CurrentGain => _gain;

    /// <summary>
    ///     Begins following <paramref name="stemName"/>. <paramref name="baseVolume"/>
    ///     is read every tick (so a mid-run mixer drag is respected) and
    ///     <paramref name="streak"/> supplies the live streak. Idempotent — a prior
    ///     run is stopped (and restored) first.
    /// </summary>
    public void Start(string stemName, Func<double> baseVolume, Func<int> streak)
        => Start([(stemName, baseVolume)], streak);

    /// <summary>
    ///     Follows several stems with ONE gain — the instrument the player plays when
    ///     the mixer carries it as more than one stem (Guitar roles' Lead + Rhythm).
    ///     Each keeps its own base volume.
    /// </summary>
    public void Start(IReadOnlyList<(string Name, Func<double> BaseVolume)> stems, Func<int> streak)
    {
        Stop();
        var named = stems.Where(s => !string.IsNullOrEmpty(s.Name)).ToArray();
        if (_audio == null || named.Length == 0) return;

        _stems = named;
        _streak = streak;
        // Seed at the streak's target so the first frame doesn't jump from unity.
        _gain = StemDuckMath.TargetGain(streak());
        IsActive = true;
        Apply();

        if (!_driveWithTimer) return;
        _timer ??= CreateTimer();
        _clock.Restart();
        _lastTickMs = 0;
        _timer.Start();
    }

    /// <summary>
    ///     Parks the timer and restores the stem to its base volume. Safe to call
    ///     when inactive, and safe to call repeatedly — the run-scoped gain is
    ///     dropped so nothing about it persists.
    /// </summary>
    public void Stop()
    {
        _timer?.Stop();
        _clock.Stop();

        if (IsActive && _audio != null)
        {
            foreach (var (name, baseVolume) in _stems)
                _audio.SetStemVolume(name, Math.Clamp(baseVolume(), 0.0, 4.0));
        }

        IsActive = false;
        _gain = 1.0;
        _stems = [];
        _streak = null;
    }

    /// <summary>One smoothing step. Driven by the timer; exposed so the wiring
    /// (duck on miss, swell on streak, restore on stop) is unit-testable without
    /// a live dispatcher.</summary>
    public void Tick(double dtMs)
    {
        if (!IsActive || _audio == null || _stems.Count == 0 || _streak == null)
            return;

        var target = StemDuckMath.TargetGain(_streak());
        _gain = StemDuckMath.Approach(_gain, target, dtMs);
        Apply();
    }

    private void Apply()
    {
        foreach (var (name, baseVolume) in _stems)
            _audio!.SetStemVolume(name, Math.Clamp(baseVolume() * _gain, 0.0, 4.0));
    }

    private DispatcherTimer CreateTimer()
    {
        var t = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(TickMs)
        };
        t.Tick += (_, _) =>
        {
            var now = _clock.Elapsed.TotalMilliseconds;
            var dt = Math.Clamp(now - _lastTickMs, 0, 200);
            _lastTickMs = now;
            Tick(dt);
        };
        return t;
    }
}
