using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay;

namespace DotCharter.Tests.Services.Gameplay;

public sealed class RockMeterTests
{
    [Fact]
    public void StartsAtHalf_AndIdleStaysThere()
    {
        var meter = new RockMeter();
        Assert.Equal(RockMeter.StartEnergy, meter.Energy(0), 9);
        // The recovery drift caps at the start energy, so an idle meter is flat.
        Assert.Equal(RockMeter.StartEnergy, meter.Energy(600_000), 9);
    }

    [Fact]
    public void Hit_Raises_And_Miss_DrainsMore()
    {
        var hit = new RockMeter();
        hit.OnVerdict(LivePlayVerdict.Hit, streak: 0, nowMs: 0);
        var gain = hit.Energy(0) - RockMeter.StartEnergy;
        Assert.True(gain > 0);

        var miss = new RockMeter();
        miss.OnVerdict(LivePlayVerdict.Miss, streak: 0, nowMs: 0);
        var drain = RockMeter.StartEnergy - miss.Energy(0);
        Assert.True(drain > gain, "a miss must cost more than one hit gains");
        Assert.Equal(RockMeter.MissDrain, drain, 9); // first miss: no escalation
    }

    [Fact]
    public void StreakScalesHitGain_AndSaturates()
    {
        var cold = new RockMeter();
        cold.OnVerdict(LivePlayVerdict.Hit, streak: 0, nowMs: 0);

        var hot = new RockMeter();
        hot.OnVerdict(LivePlayVerdict.Hit, streak: RockMeter.StreakSaturation, nowMs: 0);

        var beyond = new RockMeter();
        beyond.OnVerdict(LivePlayVerdict.Hit, streak: 10 * RockMeter.StreakSaturation, nowMs: 0);

        Assert.True(hot.Energy(0) > cold.Energy(0));
        Assert.Equal(hot.Energy(0), beyond.Energy(0), 9); // saturated
        // Saturated gain is exactly double the cold gain.
        Assert.Equal(
            2 * (cold.Energy(0) - RockMeter.StartEnergy),
            hot.Energy(0) - RockMeter.StartEnergy, 9);
    }

    [Fact]
    public void WrongTechnique_GainsHalf()
    {
        var clean = new RockMeter();
        clean.OnVerdict(LivePlayVerdict.Hit, streak: 0, nowMs: 0);

        var sloppy = new RockMeter();
        sloppy.OnVerdict(LivePlayVerdict.HitWrongTechnique, streak: 0, nowMs: 0);

        Assert.Equal(
            (clean.Energy(0) - RockMeter.StartEnergy) * RockMeter.WrongTechniqueFactor,
            sloppy.Energy(0) - RockMeter.StartEnergy, 9);
    }

    [Fact]
    public void ConsecutiveMisses_EscalateTheDrain_UpToTheCap()
    {
        var meter = new RockMeter();
        var before = meter.Energy(0);
        var drains = new List<double>();
        for (var i = 0; i < 8; i++)
        {
            meter.OnVerdict(LivePlayVerdict.Miss, streak: 0, nowMs: 0);
            var after = meter.Energy(0);
            drains.Add(before - after);
            before = after;
        }

        // 1st miss ×1.0, then each consecutive one +MissEscalationStep …
        Assert.Equal(RockMeter.MissDrain, drains[0], 9);
        Assert.Equal(RockMeter.MissDrain * (1 + RockMeter.MissEscalationStep), drains[1], 9);
        Assert.Equal(RockMeter.MissDrain * (1 + 2 * RockMeter.MissEscalationStep), drains[2], 9);
        // … capped at ×MissEscalationMax (7th and 8th drain the same).
        Assert.Equal(RockMeter.MissDrain * RockMeter.MissEscalationMax, drains[6], 9);
        Assert.Equal(drains[6], drains[7], 9);
    }

    [Fact]
    public void AHit_ResetsTheMissEscalation()
    {
        var meter = new RockMeter();
        meter.OnVerdict(LivePlayVerdict.Miss, 0, 0);
        meter.OnVerdict(LivePlayVerdict.Miss, 0, 0);
        meter.OnVerdict(LivePlayVerdict.Hit, 1, 0);

        var before = meter.Energy(0);
        meter.OnVerdict(LivePlayVerdict.Miss, 0, 0);
        // Back to the base drain — the hit broke the consecutive run.
        Assert.Equal(RockMeter.MissDrain, before - meter.Energy(0), 9);
    }

    [Fact]
    public void EmptyStretch_DriftsSlowlyUp_NeverPastTheCeiling()
    {
        var meter = new RockMeter();
        // Wound it below the ceiling.
        for (var i = 0; i < 5; i++) meter.OnVerdict(LivePlayVerdict.Miss, 0, nowMs: 0);
        var wounded = meter.Energy(0);
        Assert.True(wounded < RockMeter.RecoveryCeiling);

        // One second of empty chart heals exactly the drift rate…
        Assert.Equal(wounded + RockMeter.RecoveryDriftPerSecond, meter.Energy(1000), 9);
        // …and a very long stretch heals to the ceiling, NEVER past it.
        Assert.Equal(RockMeter.RecoveryCeiling, meter.Energy(3_600_000), 9);
    }

    [Fact]
    public void HealthyMeter_DoesNotDecay_AndDoesNotDriftUp()
    {
        var meter = new RockMeter();
        for (var i = 0; i < 30; i++) meter.OnVerdict(LivePlayVerdict.Hit, i, nowMs: 0);
        var healthy = meter.Energy(0);
        Assert.True(healthy > RockMeter.RecoveryCeiling);

        // Above the ceiling, time changes nothing: no decay, no free healing.
        Assert.Equal(healthy, meter.Energy(600_000), 9);
    }

    [Fact]
    public void Hits_NeverHealPastOne()
    {
        var meter = new RockMeter();
        for (var i = 0; i < 500; i++)
            meter.OnVerdict(LivePlayVerdict.Hit, RockMeter.StreakSaturation, nowMs: 0);
        Assert.Equal(1.0, meter.Energy(0), 9);
    }

    [Fact]
    public void Misses_NeverDrainBelowZero()
    {
        var meter = new RockMeter();
        for (var i = 0; i < 500; i++) meter.OnVerdict(LivePlayVerdict.Miss, 0, nowMs: 0);
        Assert.Equal(0.0, meter.Energy(0), 9);
    }

    [Fact]
    public void Fail_LatchesAfterTheDebounce_NotInstantly()
    {
        var meter = new RockMeter();
        var failedEvents = 0;
        meter.Failed += () => failedEvents++;

        // Collapse to zero at t=0.
        for (var i = 0; i < 20; i++) meter.OnVerdict(LivePlayVerdict.Miss, 0, nowMs: 0);
        Assert.False(meter.HasFailed);

        Assert.False(meter.Tick(RockMeter.FailDebounceMs - 1)); // still inside the debounce
        Assert.False(meter.HasFailed);

        Assert.True(meter.Tick(RockMeter.FailDebounceMs)); // debounce elapsed → latch
        Assert.True(meter.HasFailed);
        Assert.Equal(1, failedEvents);

        Assert.False(meter.Tick(RockMeter.FailDebounceMs + 5000)); // latches only once
        Assert.Equal(1, failedEvents);
    }

    [Fact]
    public void DipRecoveredByHits_DoesNotFail()
    {
        var meter = new RockMeter();
        for (var i = 0; i < 20; i++) meter.OnVerdict(LivePlayVerdict.Miss, 0, nowMs: 0);
        Assert.True(meter.Energy(0) < RockMeter.FailThreshold);

        // Inside the debounce, the player rallies back above the threshold…
        for (var i = 0; i < 10; i++)
            meter.OnVerdict(LivePlayVerdict.Hit, i, nowMs: 200);
        Assert.True(meter.Energy(200) >= RockMeter.FailThreshold);

        // …so the debounce restarts rather than firing late.
        Assert.False(meter.Tick(RockMeter.FailDebounceMs + 500));
        Assert.False(meter.HasFailed);
    }

    [Fact]
    public void FailedMeter_IsInert()
    {
        var meter = new RockMeter();
        for (var i = 0; i < 20; i++) meter.OnVerdict(LivePlayVerdict.Miss, 0, nowMs: 0);
        meter.Tick(RockMeter.FailDebounceMs);
        Assert.True(meter.HasFailed);

        var frozen = meter.Energy(RockMeter.FailDebounceMs);
        meter.OnVerdict(LivePlayVerdict.Hit, 30, RockMeter.FailDebounceMs + 100);
        Assert.Equal(frozen, meter.Energy(RockMeter.FailDebounceMs), 9);
    }

    [Fact]
    public void BackwardsTime_IsClampedNotExplosive()
    {
        var meter = new RockMeter();
        meter.OnVerdict(LivePlayVerdict.Miss, 0, nowMs: 10_000);
        var at10 = meter.Energy(10_000);

        // A stale timestamp neither drifts nor rewinds (resume-rewind replays).
        Assert.Equal(at10, meter.Energy(5_000), 9);
        meter.OnVerdict(LivePlayVerdict.Miss, 0, nowMs: 5_000); // stale event still kicks
        Assert.True(meter.Energy(10_000) < at10);
    }

    [Fact]
    public void BackwardsTime_CannotShortcutTheDebounce()
    {
        var meter = new RockMeter();
        for (var i = 0; i < 20; i++) meter.OnVerdict(LivePlayVerdict.Miss, 0, nowMs: 10_000);

        // A rewound clock must not count as elapsed debounce time.
        Assert.False(meter.Tick(0));
        Assert.False(meter.Tick(9_000));
        Assert.False(meter.HasFailed);
        Assert.True(meter.Tick(10_000 + RockMeter.FailDebounceMs));
    }

    [Fact]
    public void Deterministic_SameSequenceSameCurve()
    {
        var a = new RockMeter();
        var b = new RockMeter();
        var verdicts = new[]
        {
            LivePlayVerdict.Hit, LivePlayVerdict.Miss, LivePlayVerdict.Miss,
            LivePlayVerdict.HitWrongTechnique, LivePlayVerdict.Hit
        };
        for (var i = 0; i < verdicts.Length; i++)
        {
            a.OnVerdict(verdicts[i], i * 2, i * 400.0);
            a.Tick(i * 400.0 + 200);
            b.OnVerdict(verdicts[i], i * 2, i * 400.0);
            b.Tick(i * 400.0 + 200);
        }

        Assert.Equal(a.Energy(5000), b.Energy(5000), 12);
        Assert.Equal(a.HasFailed, b.HasFailed);
    }

    [Fact]
    public void Energy_IsReadOnly_RepeatedSamplesAgree()
    {
        var meter = new RockMeter();
        meter.OnVerdict(LivePlayVerdict.Miss, 0, nowMs: 0);
        Assert.Equal(meter.Energy(3000), meter.Energy(3000), 12);
    }

    [Fact]
    public void TotalSilence_FailsWithinAHandfulOfNotes()
    {
        // Nobody playing: every passing note is a miss (500 ms apart here).
        // The escalation must collapse the run within ~10 sounds + the debounce.
        var meter = new RockMeter();
        var t = 0.0;
        var failedAt = -1;
        for (var i = 0; i < 16 && !meter.HasFailed; i++, t += 500)
        {
            meter.OnVerdict(LivePlayVerdict.Miss, 0, t);
            if (meter.Tick(t + 450)) failedAt = i;
        }

        Assert.True(meter.HasFailed, "sustained silence must fail the run");
        Assert.InRange(failedAt, 6, 13);
    }

    [Fact]
    public void ZoneBoundaries_AreOrdered()
    {
        Assert.True(RockMeter.CriticalBelow < RockMeter.DangerBelow);
        Assert.True(RockMeter.DangerBelow < RockMeter.WarnBelow);
        Assert.True(RockMeter.WarnBelow < RockMeter.RecoveryCeiling);
        Assert.True(RockMeter.FailThreshold < RockMeter.CriticalBelow);
    }
}
