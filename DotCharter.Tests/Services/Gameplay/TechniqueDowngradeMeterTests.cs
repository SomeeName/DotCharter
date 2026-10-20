using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay;
using DotCharter.Services.Gameplay.Spectator;

namespace DotCharter.Tests.Services.Gameplay;

/// <summary>
///     #730: a hit is fed to the rock and crowd meters as a full hit the moment it is credited;
///     when the contour judge later rules its technique NotMet, the meters give back exactly the
///     full hit's extra over a half hit, at the streak the hit was fed at — so a hit then a
///     downgrade reads the same as a wrong-technique hit fed in the first place.
/// </summary>
public sealed class TechniqueDowngradeMeterTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(40)]
    public void RockMeter_AHitThenItsDowngrade_EqualsAWrongTechniqueHit(int streak)
    {
        var downgraded = new RockMeter();
        downgraded.OnVerdict(LivePlayVerdict.Hit, streak, 100);
        var afterHit = downgraded.Energy(100);
        downgraded.OnTechniqueDowngrade(streak, 100);

        var half = new RockMeter();
        half.OnVerdict(LivePlayVerdict.HitWrongTechnique, streak, 100);

        Assert.Equal(half.Energy(100), downgraded.Energy(100), 12);
        Assert.Equal(RockMeter.TechniqueDowngradeCost(streak), afterHit - downgraded.Energy(100), 12);
        Assert.True(RockMeter.TechniqueDowngradeCost(streak) > 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(40)]
    public void CrowdMeter_AHitThenItsDowngrade_EqualsAWrongTechniqueHit(int streak)
    {
        var downgraded = new CrowdMeter();
        downgraded.OnVerdict(LivePlayVerdict.Hit, streak, 100);
        var afterHit = downgraded.Value(100);
        downgraded.OnTechniqueDowngrade(streak, 100);

        var half = new CrowdMeter();
        half.OnVerdict(LivePlayVerdict.HitWrongTechnique, streak, 100);

        Assert.Equal(half.Value(100), downgraded.Value(100), 12);
        Assert.Equal(CrowdMeter.TechniqueDowngradeCost(streak), afterHit - downgraded.Value(100), 12);
        Assert.True(CrowdMeter.TechniqueDowngradeCost(streak) > 0);
    }

    /// <summary>A downgrade is never a miss: it costs less than any miss would.</summary>
    [Fact]
    public void ADowngrade_CostsLessThanAMiss()
    {
        Assert.True(RockMeter.TechniqueDowngradeCost(int.MaxValue) < RockMeter.MissDrain);
        Assert.True(CrowdMeter.TechniqueDowngradeCost(int.MaxValue) < CrowdMeter.MissPenalty);
    }

    [Fact]
    public void AFailedRockMeter_IgnoresADowngrade()
    {
        var meter = new RockMeter();
        for (var i = 0; i < 200 && !meter.HasFailed; i++) meter.OnVerdict(LivePlayVerdict.Miss, 0, i * 100.0);
        Assert.True(meter.HasFailed);
        var energy = meter.Energy(20_000);

        meter.OnTechniqueDowngrade(10, 20_000);

        Assert.Equal(energy, meter.Energy(20_000));
    }
}
