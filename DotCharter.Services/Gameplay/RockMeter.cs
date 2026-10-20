using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Services.Gameplay;

/// <summary>
///     Rocksmith-style performance-stakes meter (0..1) behind the opt-in Play
///     Mode fail-out. PURE and deterministic, like its sibling
///     <see cref="Spectator.CrowdMeter" />: time is always passed in explicitly
///     (chart/transport ms — any monotonic ms clock works, and a paused
///     transport simply stops time), there are no internal clocks, no
///     randomness and no I/O, so the same event sequence always produces the
///     same curve and every rule is unit-testable.
///     <para>
///     Model: energy starts at <see cref="StartEnergy" />. Hits raise it a
///     little (streak-scaled, saturating at <see cref="StreakSaturation" />),
///     misses drain it harder — and CONSECUTIVE misses escalate (each adds
///     <see cref="MissEscalationStep" /> to the drain multiplier up to
///     <see cref="MissEscalationMax" />), so sustained silence while notes
///     pass collapses the run within a handful of sounds while a single flub
///     inside otherwise clean playing barely dents it. Empty chart stretches
///     neither drain nor heal beyond a slow upward drift capped at
///     <see cref="RecoveryCeiling" /> — a quiet bridge can re-center a wounded
///     run toward neutral but can never manufacture headroom, and a healthy
///     run is left untouched (no decay from above). Only hits reach 1.0.
///     </para>
///     <para>
///     Failure: energy below <see cref="FailThreshold" /> sustained for
///     <see cref="FailDebounceMs" /> latches <see cref="HasFailed" /> and
///     raises <see cref="Failed" /> exactly once. The debounce exists so an
///     instantaneous dip the player is already recovering from (hits outpace
///     the drift) is not a fail; because drain only ever comes from misses,
///     sitting below the threshold that long always means recent missing.
///     Once failed the meter is inert — the run is over.
///     </para>
/// </summary>
public sealed class RockMeter
{
    /// <summary>Energy a run starts with — half a bar of headroom either way.</summary>
    public const double StartEnergy = 0.5;

    /// <summary>Below this, the fail debounce runs (see <see cref="FailDebounceMs" />).</summary>
    public const double FailThreshold = 0.05;

    /// <summary>How long energy must stay below <see cref="FailThreshold" />
    /// before the run fails. Roughly two beats at 120 BPM: long enough that a
    /// dip being actively recovered survives, short enough that a collapsed
    /// run ends promptly.</summary>
    public const double FailDebounceMs = 900.0;

    /// <summary>Raise of a clean hit at streak 0. Deliberately small: climbing
    /// back from near-death takes a couple of phrases of clean playing.</summary>
    public const double HitGain = 0.015;

    /// <summary>Wrong-technique hits still land the note — half the gain
    /// (mirrors the crowd meter's judgment of them).</summary>
    public const double WrongTechniqueFactor = 0.5;

    /// <summary>Streak at which the hit gain saturates (gain doubles). Matches
    /// the scale of the crowd meter's streak bonus.</summary>
    public const int StreakSaturation = 25;

    /// <summary>Drain of an isolated miss — two cold hits' worth, because
    /// stakes mean mistakes cost more than successes pay.</summary>
    public const double MissDrain = 0.03;

    /// <summary>Each consecutive miss adds this to the drain multiplier
    /// (1st miss ×1.0, 2nd ×1.25, 3rd ×1.5, …). A hit resets the run of misses.</summary>
    public const double MissEscalationStep = 0.25;

    /// <summary>Escalation cap (reached on the 7th consecutive miss): from the
    /// start energy, total silence fails after ~9–10 passing notes — a few
    /// seconds at typical chart densities — without a lucky low-density chart
    /// dragging it out forever.</summary>
    public const double MissEscalationMax = 2.5;

    /// <summary>Passive upward drift per second, applied only below
    /// <see cref="RecoveryCeiling" />. Slow on purpose: ~0.6/minute, so it
    /// re-centers across a no-note stretch but cannot outrun missing (one
    /// escalated miss costs ~7 s of drift).</summary>
    public const double RecoveryDriftPerSecond = 0.01;

    /// <summary>The drift never heals past this (the start energy): empty
    /// stretches are neutral, not free healing. Only hits go higher.</summary>
    public const double RecoveryCeiling = StartEnergy;

    // ---- HUD zone boundaries (token colors: Success → Warning → Danger). ----

    /// <summary>Below this the HUD shifts Success → Warning.</summary>
    public const double WarnBelow = 0.45;

    /// <summary>Below this the HUD shifts Warning → Danger.</summary>
    public const double DangerBelow = 0.25;

    /// <summary>Below this the HUD adds the critical emphasis.</summary>
    public const double CriticalBelow = 0.12;

    private double _energy = StartEnergy;
    private double _lastMs;
    private int _consecutiveMisses;
    private double _belowSinceMs = double.NaN;

    /// <param name="startMs">Timestamp the meter starts at (the run's start
    /// position on whatever clock the caller feeds — transport ms in the app).</param>
    public RockMeter(double startMs = 0) => _lastMs = startMs;

    /// <summary>Latched once energy stayed below <see cref="FailThreshold" />
    /// for <see cref="FailDebounceMs" />. Never unlatches — a failed run is over.</summary>
    public bool HasFailed { get; private set; }

    /// <summary>Raised exactly once, at the moment <see cref="HasFailed" />
    /// latches (from whichever of <see cref="OnVerdict" /> / <see cref="Tick" />
    /// crossed the debounce). Synchronous; subscribers marshal themselves.</summary>
    public event Action? Failed;

    /// <summary>Energy at <paramref name="nowMs" /> (0..1), applying the
    /// recovery drift since the last event. Read-only: does not advance
    /// internal state, so repeated samples at the same time agree.</summary>
    public double Energy(double nowMs) => Drifted(nowMs);

    /// <summary>Feeds one judged sound — the SAME verdict stream scoring
    /// consumes (every judged <see cref="LivePlayEvent" /> of the session tick,
    /// misses from silent stretches included). <paramref name="streak" /> is
    /// the player's streak AFTER the verdict; time must not run backwards (a
    /// stale timestamp is clamped to the last seen one). Inert once failed.</summary>
    public void OnVerdict(LivePlayVerdict verdict, int streak, double nowMs)
    {
        if (HasFailed) return;
        Advance(nowMs);

        var kick = verdict switch
        {
            LivePlayVerdict.Hit => HitGain * (1.0 + StreakFactor(streak)),
            LivePlayVerdict.HitWrongTechnique =>
                HitGain * WrongTechniqueFactor * (1.0 + StreakFactor(streak)),
            _ => -MissDrain * Math.Min(1.0 + MissEscalationStep * _consecutiveMisses,
                MissEscalationMax)
        };
        _consecutiveMisses = verdict == LivePlayVerdict.Miss ? _consecutiveMisses + 1 : 0;

        _energy = Math.Clamp(_energy + kick, 0.0, 1.0);
        UpdateFailLatch();
    }

    /// <summary>
    ///     A hit already fed as <see cref="LivePlayVerdict.Hit" /> was downgraded afterwards to
    /// <see cref="LivePlayVerdict.HitWrongTechnique" /> (the contour judge's NotMet): takes
    ///     back exactly what the full hit gave over a half hit at <paramref name="streakAtCredit" />
    ///     (<see cref="TechniqueDowngradeCost" />). Never more — a downgrade is not a miss — and
    ///     it runs the fail debounce like any verdict. Inert once failed.
    /// </summary>
    public void OnTechniqueDowngrade(int streakAtCredit, double nowMs)
    {
        if (HasFailed) return;
        Advance(nowMs);
        _energy = Math.Clamp(_energy - TechniqueDowngradeCost(streakAtCredit), 0.0, 1.0);
        UpdateFailLatch();
    }

    /// <summary>A full hit's gain minus a wrong-technique hit's, at <paramref name="streak" />.</summary>
    public static double TechniqueDowngradeCost(int streak) =>
        HitGain * (1.0 - WrongTechniqueFactor) * (1.0 + StreakFactor(streak));

    /// <summary>Advances time between verdicts (drift + the fail debounce).
    /// Call from the session tick while the transport is playing. Returns true
    /// exactly when THIS call latched the failure (also raises
    /// <see cref="Failed" />); false forever after.</summary>
    public bool Tick(double nowMs)
    {
        if (HasFailed) return false;
        Advance(nowMs);
        return UpdateFailLatch();
    }

    private static double StreakFactor(int streak)
        => Math.Clamp(streak, 0, StreakSaturation) / (double)StreakSaturation;

    private void Advance(double nowMs)
    {
        _energy = Drifted(nowMs);
        _lastMs = Math.Max(_lastMs, nowMs);
    }

    private double Drifted(double nowMs)
    {
        var elapsed = nowMs - _lastMs;
        if (elapsed <= 0) return _energy; // stale/backwards time (e.g. a resume rewind): no drift
        if (_energy >= RecoveryCeiling) return _energy; // never decays, never drifts past the cap
        return Math.Min(RecoveryCeiling, _energy + RecoveryDriftPerSecond * (elapsed / 1000.0));
    }

    /// <summary>Runs the below-threshold debounce against the clamped clock
    /// (<c>_lastMs</c>). True exactly once, when the failure latches.</summary>
    private bool UpdateFailLatch()
    {
        if (_energy >= FailThreshold)
        {
            _belowSinceMs = double.NaN;
            return false;
        }

        if (double.IsNaN(_belowSinceMs))
        {
            _belowSinceMs = _lastMs;
            return false;
        }

        if (_lastMs - _belowSinceMs < FailDebounceMs) return false;

        HasFailed = true;
        Failed?.Invoke();
        return true;
    }
}
