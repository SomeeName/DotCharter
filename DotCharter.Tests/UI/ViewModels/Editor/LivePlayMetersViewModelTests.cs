using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay;
using DotCharter.Services.Gameplay.Spectator;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     <see cref="LivePlayMetersViewModel" /> on its own, with a fake logger: the
///     arm/disarm lifecycle the HUD binds to, the zone flags each applied value
///     raises, the worker-side sampling the tick snapshot reads, and the per-run
///     fail mark and latch. The once-per-run fail routing through the whole view
///     model is <c>LivePlayRunFailRoutingTests</c>.
/// </summary>
public sealed class LivePlayMetersViewModelTests
{
    [Fact]
    public void Nothing_is_armed_until_a_run_arms_it()
    {
        var meters = new LivePlayMetersViewModel(new FakeAppLogger());

        Assert.False(meters.IsRockMeterActive);
        Assert.False(meters.IsCrowdMeterActive);
        Assert.False(meters.TrySampleRockEnergy(0, out _));
        Assert.False(meters.TrySampleCrowdValue(0, out _));
    }

    [Fact]
    public void Arming_shows_each_meter_at_its_start_value_and_disarming_hides_it()
    {
        var meters = new LivePlayMetersViewModel(new FakeAppLogger());

        meters.ArmRockMeter(playheadMs: 1_000);
        meters.ArmCrowdMeter(playheadMs: 1_000);

        Assert.True(meters.IsRockMeterActive);
        Assert.True(meters.IsCrowdMeterActive);
        Assert.Equal(Math.Round(RockMeter.StartEnergy, 3), meters.RockEnergy);
        Assert.True(meters.TrySampleRockEnergy(1_000, out var energy));
        Assert.Equal(meters.RockEnergy, energy);
        Assert.True(meters.TrySampleCrowdValue(1_000, out var crowd));
        Assert.Equal(meters.CrowdEnthusiasm, crowd);

        meters.DisarmRockMeter();
        meters.DisarmCrowdMeter();

        Assert.False(meters.IsRockMeterActive);
        Assert.False(meters.IsCrowdMeterActive);
        Assert.False(meters.TrySampleRockEnergy(1_000, out _));
        Assert.False(meters.TrySampleCrowdValue(1_000, out _));
    }

    [Fact]
    public void Verdicts_move_only_the_armed_meters()
    {
        var meters = new LivePlayMetersViewModel(new FakeAppLogger());
        meters.ArmRockMeter(playheadMs: 0);
        Assert.True(meters.TrySampleRockEnergy(0, out var before));

        for (var i = 0; i < 5; i++)
        {
            meters.OnVerdict(LivePlayVerdict.Miss, streak: 0, detectedAtMs: 0);
        }

        Assert.True(meters.TrySampleRockEnergy(0, out var after));
        Assert.True(after < before, $"five misses left the energy at {after} (was {before})");
        Assert.False(meters.TrySampleCrowdValue(0, out _));
    }

    [Theory]
    [InlineData(1.0, false, false, false)]
    [InlineData(RockMeter.WarnBelow - 0.001, true, false, false)]
    [InlineData(RockMeter.DangerBelow - 0.001, true, true, false)]
    [InlineData(RockMeter.CriticalBelow - 0.001, true, true, true)]
    public void An_applied_rock_energy_raises_the_zone_flags_of_its_band(double energy, bool warn, bool danger, bool critical)
    {
        var meters = new LivePlayMetersViewModel(new FakeAppLogger());

        meters.ApplyRockEnergy(energy);

        Assert.Equal(energy, meters.RockEnergy);
        Assert.Equal(warn, meters.IsRockEnergyWarn);
        Assert.Equal(danger, meters.IsRockEnergyDanger);
        Assert.Equal(critical, meters.IsRockEnergyCritical);
    }

    [Theory]
    [InlineData(CrowdMeter.HotAbove, true, false)]
    [InlineData(CrowdMeter.ColdBelow - 0.001, false, true)]
    [InlineData((CrowdMeter.HotAbove + CrowdMeter.ColdBelow) / 2, false, false)]
    public void An_applied_crowd_value_raises_the_zone_flags_of_its_band(double value, bool hot, bool cold)
    {
        var meters = new LivePlayMetersViewModel(new FakeAppLogger());

        meters.ApplyCrowdValue(value);

        Assert.Equal(value, meters.CrowdEnthusiasm);
        Assert.Equal(hot, meters.IsCrowdHot);
        Assert.Equal(cold, meters.IsCrowdCold);
    }

    [Fact]
    public void The_fail_mark_belongs_to_one_run()
    {
        var meters = new LivePlayMetersViewModel(new FakeAppLogger());

        meters.MarkRunFailed();
        Assert.True(meters.RunFailed);

        meters.BeginRun();
        Assert.False(meters.RunFailed);
    }

    [Fact]
    public void A_drained_meter_fails_the_run_once_per_run()
    {
        var logger = new FakeAppLogger();
        var meters = new LivePlayMetersViewModel(logger);

        FailArmedRun(meters);
        FailArmedRun(meters);
        Assert.Equal(1, FailLogLines(logger));

        meters.BeginRun();
        FailArmedRun(meters);
        Assert.Equal(2, FailLogLines(logger));
    }

    /// <summary>Arms a fresh meter at t=0, misses until it is empty, then
    /// crosses the debounce inside a verdict. Iteration-capped so a constants
    /// change fails loudly instead of hanging.</summary>
    private static void FailArmedRun(LivePlayMetersViewModel meters)
    {
        meters.ArmRockMeter(playheadMs: 0);
        for (var i = 0; i < 200 && meters.TrySampleRockEnergy(0, out var e) && e >= RockMeter.FailThreshold; i++)
        {
            meters.OnVerdict(LivePlayVerdict.Miss, streak: 0, detectedAtMs: 0);
        }

        meters.OnVerdict(LivePlayVerdict.Miss, streak: 0, detectedAtMs: RockMeter.FailDebounceMs);
    }

    private static int FailLogLines(FakeAppLogger logger)
    {
        return logger.Entries.Count(e => e.Level == "INFO" &&
                                         e.Message.Contains("failed the run", StringComparison.OrdinalIgnoreCase));
    }
}
