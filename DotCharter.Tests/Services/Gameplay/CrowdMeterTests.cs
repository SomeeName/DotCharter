using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Spectator;

namespace DotCharter.Tests.Services.Gameplay;

public sealed class CrowdMeterTests
{
    [Fact]
    public void StartsAtNeutral()
    {
        var meter = new CrowdMeter();
        Assert.Equal(0.4, meter.Value(0), 6);
        Assert.Equal(0.4, meter.Value(60_000), 6); // idle room stays neutral
    }

    [Fact]
    public void Hit_Boosts_And_Miss_DropsHarder()
    {
        var hitMeter = new CrowdMeter();
        hitMeter.OnVerdict(LivePlayVerdict.Hit, streak: 0, nowMs: 0);
        var boost = hitMeter.Value(0) - 0.4;
        Assert.True(boost > 0);

        var missMeter = new CrowdMeter();
        missMeter.OnVerdict(LivePlayVerdict.Miss, streak: 0, nowMs: 0);
        var drop = 0.4 - missMeter.Value(0);
        Assert.True(drop > boost, "a miss must cost more than one hit gains");
    }

    [Fact]
    public void StreakScalesTheBoost_AndSaturates()
    {
        var cold = new CrowdMeter();
        cold.OnVerdict(LivePlayVerdict.Hit, streak: 0, nowMs: 0);

        var hot = new CrowdMeter();
        hot.OnVerdict(LivePlayVerdict.Hit, streak: 30, nowMs: 0);

        var beyond = new CrowdMeter();
        beyond.OnVerdict(LivePlayVerdict.Hit, streak: 300, nowMs: 0);

        Assert.True(hot.Value(0) > cold.Value(0));
        Assert.Equal(hot.Value(0), beyond.Value(0), 9); // saturated at 30
        // Saturated boost is exactly 3x the cold boost.
        Assert.Equal(3 * (cold.Value(0) - 0.4), hot.Value(0) - 0.4, 9);
    }

    [Fact]
    public void WrongTechnique_CheersHalfAsLoud()
    {
        var clean = new CrowdMeter();
        clean.OnVerdict(LivePlayVerdict.Hit, streak: 0, nowMs: 0);

        var sloppy = new CrowdMeter();
        sloppy.OnVerdict(LivePlayVerdict.HitWrongTechnique, streak: 0, nowMs: 0);

        Assert.Equal((clean.Value(0) - 0.4) * 0.5, sloppy.Value(0) - 0.4, 9);
    }

    [Fact]
    public void DecaysTowardNeutral_WithTheConfiguredHalfLife()
    {
        var meter = new CrowdMeter(halfLifeMs: 6000);
        // Drive it well above neutral.
        for (var i = 0; i < 20; i++) meter.OnVerdict(LivePlayVerdict.Hit, 30, nowMs: 0);
        var peak = meter.Value(0);
        var distance = peak - 0.4;
        Assert.True(distance > 0.2);

        Assert.Equal(0.4 + distance / 2, meter.Value(6000), 9);   // one half-life
        Assert.Equal(0.4 + distance / 4, meter.Value(12000), 9);  // two half-lives
        Assert.Equal(0.4, meter.Value(600_000), 3);               // eventually neutral
    }

    [Fact]
    public void Value_IsReadOnly_RepeatedSamplesAgree()
    {
        var meter = new CrowdMeter();
        meter.OnVerdict(LivePlayVerdict.Hit, 10, nowMs: 0);
        Assert.Equal(meter.Value(3000), meter.Value(3000), 12);
    }

    [Fact]
    public void Deterministic_SameSequenceSameCurve()
    {
        var a = new CrowdMeter();
        var b = new CrowdMeter();
        var verdicts = new[]
            { LivePlayVerdict.Hit, LivePlayVerdict.Miss, LivePlayVerdict.HitWrongTechnique, LivePlayVerdict.Hit };
        for (var i = 0; i < verdicts.Length; i++)
        {
            a.OnVerdict(verdicts[i], i * 3, i * 500.0);
            b.OnVerdict(verdicts[i], i * 3, i * 500.0);
        }

        Assert.Equal(a.Value(5000), b.Value(5000), 12);
    }

    [Fact]
    public void ClampedToUnitRange()
    {
        var euphoric = new CrowdMeter();
        for (var i = 0; i < 500; i++) euphoric.OnVerdict(LivePlayVerdict.Hit, 30, nowMs: 0);
        Assert.Equal(1.0, euphoric.Value(0), 6);

        var hostile = new CrowdMeter();
        for (var i = 0; i < 500; i++) hostile.OnVerdict(LivePlayVerdict.Miss, 0, nowMs: 0);
        Assert.Equal(0.0, hostile.Value(0), 6);
    }

    [Fact]
    public void BackwardsTime_IsClampedNotExplosive()
    {
        var meter = new CrowdMeter();
        meter.OnVerdict(LivePlayVerdict.Hit, 10, nowMs: 10_000);
        var at10 = meter.Value(10_000);

        // A stale timestamp must neither decay nor amplify.
        Assert.Equal(at10, meter.Value(5_000), 12);
        meter.OnVerdict(LivePlayVerdict.Hit, 11, nowMs: 5_000); // stale event
        Assert.True(meter.Value(10_000) > at10);                // still just adds the kick
    }

    [Fact]
    public void Reset_ReturnsToNeutral()
    {
        var meter = new CrowdMeter();
        meter.OnVerdict(LivePlayVerdict.Miss, 0, nowMs: 0);
        meter.Reset(nowMs: 1000);
        Assert.Equal(0.4, meter.Value(1000), 9);
    }

    // ---------- room combiner ----------

    [Fact]
    public void CombineRoom_EmptyRoom_ReadsNeutral()
        => Assert.Equal(0.4, CrowdMeter.CombineRoom([]), 9);

    [Fact]
    public void CombineRoom_MixesAverageAndHottestPlayer()
    {
        // avg = 0.5, max = 0.9 → 0.5*0.5 + 0.5*0.9 = 0.7
        Assert.Equal(0.7, CrowdMeter.CombineRoom([0.1, 0.9]), 9);
        // A single player IS the room.
        Assert.Equal(0.6, CrowdMeter.CombineRoom([0.6]), 9);
    }

    [Fact]
    public void CombineRoom_ClampsRogueInputs() => Assert.InRange(CrowdMeter.CombineRoom([5.0, -3.0]), 0.0, 1.0);
}
