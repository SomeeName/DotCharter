using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Services.Gameplay.Spectator;

/// <summary>
///     Rolling room-enthusiasm metric (0..1) behind the 0.17 spectator crowd
///     meter and the 3D crowd's excitement level. PURE and deterministic:
///     time is always passed in explicitly (chart/transport ms — any
///     monotonic ms clock works), there are no internal clocks, no
///     randomness and no I/O, so the same event sequence always produces the
///     same curve and every rule is unit-testable.
///     <para>
///     Model: the value decays exponentially toward <see cref="Neutral"/>
///     (a waiting crowd is neither dead nor cheering) with a configurable
///     half-life; judged sounds kick it — hits push it up (scaled by the
///     player's current streak, saturating), misses push it down harder than
///     a single hit pushes up, because crowds notice mistakes.
///     </para>
/// </summary>
public sealed class CrowdMeter
{
    /// <summary>Boost of a clean hit at streak 0.</summary>
    public const double HitBoost = 0.02;

    /// <summary>Wrong-technique hits still land the note — half the cheer.</summary>
    public const double WrongTechniqueFactor = 0.5;

    /// <summary>Penalty of a miss (deliberately > <see cref="HitBoost"/>).</summary>
    public const double MissPenalty = 0.06;

    /// <summary>Streak at which the streak bonus saturates (boost triples).</summary>
    public const int StreakSaturation = 30;

    /// <summary>HUD zone: at or above this the crowd reads as ON FIRE
    /// (a sustained clean streak — decay alone never gets here).</summary>
    public const double HotAbove = 0.75;

    /// <summary>HUD zone: below this the crowd reads as turning on the player
    /// (a couple of misses under neutral).</summary>
    public const double ColdBelow = 0.2;

    private readonly double _halfLifeMs;
    private double _value;
    private double _lastMs;

    /// <param name="halfLifeMs">Time for the distance to neutral to halve.</param>
    /// <param name="neutral">Resting enthusiasm of an idle room.</param>
    /// <param name="startMs">Timestamp the meter starts at.</param>
    public CrowdMeter(double halfLifeMs = 6000, double neutral = 0.4, double startMs = 0)
    {
        if (halfLifeMs <= 0) throw new ArgumentOutOfRangeException(nameof(halfLifeMs));
        _halfLifeMs = halfLifeMs;
        Neutral = Math.Clamp(neutral, 0.0, 1.0);
        _value = Neutral;
        _lastMs = startMs;
    }

    /// <summary>Resting value the meter decays toward.</summary>
    public double Neutral { get; }

    /// <summary>Enthusiasm at <paramref name="nowMs"/> (0..1), applying decay
    /// since the last event. Read-only: does not advance internal state, so
    /// repeated samples at the same time agree.</summary>
    public double Value(double nowMs) => Decayed(nowMs);

    /// <summary>Feeds one judged sound (e.g. every <see cref="LivePlayEvent"/>
    /// from the session tick). <paramref name="streak"/> is the player's
    /// streak AFTER the verdict; time must not run backwards (a stale
    /// timestamp is clamped to the last seen one).</summary>
    public void OnVerdict(LivePlayVerdict verdict, int streak, double nowMs)
    {
        Advance(nowMs);
        var kick = verdict switch
        {
            LivePlayVerdict.Hit => HitBoost * (1.0 + 2.0 * StreakFactor(streak)),
            LivePlayVerdict.HitWrongTechnique =>
                HitBoost * WrongTechniqueFactor * (1.0 + 2.0 * StreakFactor(streak)),
            _ => -MissPenalty
        };
        _value = Math.Clamp(_value + kick, 0.0, 1.0);
    }

    /// <summary>A hit already fed as <see cref="LivePlayVerdict.Hit" /> was downgraded afterwards
    /// to <see cref="LivePlayVerdict.HitWrongTechnique" />: takes back exactly the full hit's
    /// extra cheer over a half hit at <paramref name="streakAtCredit" />.</summary>
    public void OnTechniqueDowngrade(int streakAtCredit, double nowMs)
    {
        Advance(nowMs);
        _value = Math.Clamp(_value - TechniqueDowngradeCost(streakAtCredit), 0.0, 1.0);
    }

    /// <summary>A full hit's boost minus a wrong-technique hit's, at <paramref name="streak" />.</summary>
    public static double TechniqueDowngradeCost(int streak) =>
        HitBoost * (1.0 - WrongTechniqueFactor) * (1.0 + 2.0 * StreakFactor(streak));

    /// <summary>Back to the resting state (session start / song change).</summary>
    public void Reset(double nowMs)
    {
        _value = Neutral;
        _lastMs = nowMs;
    }

    /// <summary>
    ///     Pure room combiner for the shared meter: the crowd feeds off the
    ///     hottest player but sags with the room's average — an even mix of
    ///     both. Empty rooms read as an idle crowd (<paramref name="neutral"/>).
    /// </summary>
    public static double CombineRoom(ReadOnlySpan<double> playerValues, double neutral = 0.4)
    {
        if (playerValues.IsEmpty) return Math.Clamp(neutral, 0.0, 1.0);

        var sum = 0.0;
        var max = 0.0;
        foreach (var value in playerValues)
        {
            var clamped = Math.Clamp(value, 0.0, 1.0);
            sum += clamped;
            if (clamped > max) max = clamped;
        }

        return Math.Clamp(0.5 * (sum / playerValues.Length) + 0.5 * max, 0.0, 1.0);
    }

    private static double StreakFactor(int streak)
        => Math.Clamp(streak, 0, StreakSaturation) / (double)StreakSaturation;

    private void Advance(double nowMs)
    {
        _value = Decayed(nowMs);
        _lastMs = Math.Max(_lastMs, nowMs);
    }

    private double Decayed(double nowMs)
    {
        var elapsed = nowMs - _lastMs;
        if (elapsed <= 0) return _value; // stale/backwards time: no decay
        var factor = Math.Pow(0.5, elapsed / _halfLifeMs);
        return Neutral + (_value - Neutral) * factor;
    }
}
