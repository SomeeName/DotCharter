using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay;
using DotCharter.Services.Gameplay.Spectator;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #730: the HUD side of a technique downgrade. <see cref="LivePlayMetersViewModel" /> takes
///     the hit's extra over a half hit back off both armed meters, and the downgrade's feedback
///     event is a wrong-technique marker stamped at the ruling (the 3D highway's half; the drain
///     test covers the background crowd).
/// </summary>
public sealed class LivePlayTechniqueDowngradeTests
{
    [Fact]
    public void ADowngrade_MovesBothHudMetersByTheHitHalfHitDifference()
    {
        const int streak = 12;
        var downgraded = Armed();
        downgraded.OnVerdict(LivePlayVerdict.Hit, streak, 100);
        Assert.True(downgraded.TrySampleRockEnergy(100, out var rockAfterHit));
        Assert.True(downgraded.TrySampleCrowdValue(100, out var crowdAfterHit));

        downgraded.OnTechniqueDowngrade(streak, 100);

        var half = Armed();
        half.OnVerdict(LivePlayVerdict.HitWrongTechnique, streak, 100);
        Assert.True(downgraded.TrySampleRockEnergy(100, out var rock));
        Assert.True(downgraded.TrySampleCrowdValue(100, out var crowd));
        Assert.True(half.TrySampleRockEnergy(100, out var rockHalf));
        Assert.True(half.TrySampleCrowdValue(100, out var crowdHalf));

        Assert.Equal(rockHalf, rock, 3);
        Assert.Equal(crowdHalf, crowd, 3);
        Assert.Equal(Math.Round(RockMeter.TechniqueDowngradeCost(streak), 3), rockAfterHit - rock, 3);
        Assert.Equal(Math.Round(CrowdMeter.TechniqueDowngradeCost(streak), 3), crowdAfterHit - crowd, 3);
    }

    [Fact]
    public void ADowngradeOnUnarmedMeters_DoesNothing()
    {
        var meters = new LivePlayMetersViewModel(new FakeAppLogger());

        meters.OnTechniqueDowngrade(5, 100);

        Assert.False(meters.TrySampleRockEnergy(100, out _));
        Assert.False(meters.TrySampleCrowdValue(100, out _));
    }

    [Fact]
    public void TheDowngradeFeedback_IsAWrongTechniqueMarkerAtTheRuling()
    {
        var sound = new ExpectedSound { TimeMs = 1_000, StringIndex = 3, Fret = 5, MidiNotes = [55] };
        var check = new LiveTechniqueJudgment(sound, TechniqueContourVerdict.NotMet, StreakAtCredit: 9);

        var fb = LivePlayFeedbackEvent.ForTechniqueDowngrade(check, ruledAtMs: 1_640);

        Assert.True(fb.IsTechniqueDowngrade);
        Assert.False(fb.IsGrade);
        Assert.Equal(LivePlayVerdict.HitWrongTechnique, fb.Verdict);
        Assert.Equal(1_000, fb.NoteTimeMs);
        Assert.Equal(1_640, fb.JudgedAtMs);
        Assert.Equal(3, fb.StringIndex);
        Assert.Equal(5, fb.Fret);
        Assert.Equal(9, fb.StreakAtCredit);
        Assert.Equal(LiveTimingDirection.None, fb.Direction);
        Assert.Equal(LiveTimingTier.None, fb.Tier);
    }

    private static LivePlayMetersViewModel Armed()
    {
        var meters = new LivePlayMetersViewModel(new FakeAppLogger());
        meters.ArmRockMeter(playheadMs: 0);
        meters.ArmCrowdMeter(playheadMs: 0);
        return meters;
    }
}
