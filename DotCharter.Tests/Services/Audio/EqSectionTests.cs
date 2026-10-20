using DotCharter.Tone.Audio.Equalizer;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The section's ONE-SOURCE-OF-TRUTH rules: the badge count, the solo and the
///     drawn curve all have to be the same predicate, evaluated once.
/// </summary>
public sealed class EqSectionTests
{
    [Fact]
    public void AFreshSection_HasSixBands_AndEveryOneOfThemIsOff()
    {
        var section = new EqSection();

        Assert.Equal(EqLimits.BandCount, section.Bands.Count);
        Assert.All(section.Bands, b => Assert.False(b.Enabled));
        Assert.Equal(0, section.ActiveCount);
    }

    [Fact]
    public void AFreshSection_MatchesTheEnginesOwnStartingState() =>
        // The native engine boots with every band off; a section that started
        // anywhere else would mean the UI showed a curve nothing was running
        // until the first push.
        Assert.Empty(new EqSection().AudibleBands);

    [Fact]
    public void TheBadgeCount_CountsExactlyTheBandsTheCurveIsDrawnFrom()
    {
        var section = new EqSection();
        section.LoadPreset("FindTheGuitar");

        Assert.Equal(section.AudibleBands.Count, section.ActiveCount);
        Assert.Equal(5, section.ActiveCount); // the preset leaves the low pass off
    }

    [Fact]
    public void MasterPowerOff_SilencesEveryBand_ForBothTheCurveAndTheBadge()
    {
        var section = new EqSection();
        section.LoadPreset("FindTheGuitar");
        Assert.NotEqual(0, section.ActiveCount);

        section.IsEnabled = false;

        Assert.Empty(section.AudibleBands);
        Assert.Equal(0, section.ActiveCount);
    }

    [Fact]
    public void Solo_LeavesExactlyTheSoloedBand_EvenWhenItIsSwitchedOff()
    {
        var section = new EqSection();
        section.LoadPreset("FindTheGuitar");
        section.SetBandEnabled(5, false);

        section.ToggleSolo(5);

        var audible = Assert.Single(section.AudibleBands);
        Assert.Equal(section.Bands[5], audible);
        Assert.Equal(1, section.ActiveCount);
    }

    [Fact]
    public void Solo_TogglesOffWhenTheSameBandIsSoloedTwice()
    {
        var section = new EqSection();
        section.LoadPreset("FindTheGuitar");

        section.ToggleSolo(2);
        Assert.Equal(2, section.SoloedBand);

        section.ToggleSolo(2);
        Assert.Equal(-1, section.SoloedBand);
        Assert.Equal(5, section.ActiveCount);
    }

    [Fact]
    public void Solo_MovesRatherThanAccumulating() =>
        // The engine's solo is a single index, so the model must never be able
        // to describe two soloed bands.
        Assert.Equal(3, SoloedTwice());

    private static int SoloedTwice()
    {
        var section = new EqSection();
        section.ToggleSolo(1);
        section.ToggleSolo(3);
        return section.SoloedBand;
    }

    [Fact]
    public void SetBand_ClampsWhateverItIsGiven()
    {
        var section = new EqSection();
        section.SetBand(0, new EqBand(EqBandType.Bell, 1e9, 1e9, 1e9, true));

        Assert.Equal(EqLimits.MaxFrequencyHz, section.Bands[0].FrequencyHz, 6);
        Assert.Equal(EqLimits.MaxQ, section.Bands[0].Q, 6);
        Assert.Equal(EqLimits.MaxGainDb, section.Bands[0].GainDb, 6);
    }

    [Fact]
    public void SetBand_IgnoresAnIndexOutsideTheSection()
    {
        var section = new EqSection();
        var before = section.Bands.ToList();

        section.SetBand(-1, new EqBand(EqBandType.Bell, 1000, 1, 6, true));
        section.SetBand(99, new EqBand(EqBandType.Bell, 1000, 1, 6, true));

        Assert.Equal(before, section.Bands);
    }

    [Fact]
    public void EditingABand_StopsTheSectionCallingItselfAPreset()
    {
        var section = new EqSection();
        section.LoadPreset("KillTheKick");
        Assert.Equal("KillTheKick", section.PresetKey);

        section.SetBand(0, section.Bands[0] with { FrequencyHz = 123 });

        Assert.Null(section.PresetKey);
    }

    [Fact]
    public void MutingABand_IsNotAPresetChange()
    {
        // Switching a band off to hear what it was doing must not relabel the
        // preset — the preset is still exactly what is loaded.
        var section = new EqSection();
        section.LoadPreset("KillTheKick");

        section.SetBandEnabled(2, false);

        Assert.Equal("KillTheKick", section.PresetKey);
        Assert.Equal(3, section.ActiveCount);
    }

    [Fact]
    public void ResetBand_FlattensGainAndQ_AndKeepsTypeFrequencyAndEnable()
    {
        var section = new EqSection();
        section.SetBand(2, new EqBand(EqBandType.Bell, 3000, 6.5, -11, true));

        section.ResetBand(2);

        Assert.Equal(EqBandType.Bell, section.Bands[2].Type);
        Assert.Equal(3000, section.Bands[2].FrequencyHz, 6);
        Assert.Equal(0.0, section.Bands[2].GainDb, 6);
        Assert.Equal(0.707, section.Bands[2].Q, 6);
        Assert.True(section.Bands[2].Enabled);
    }

    [Fact]
    public void LoadPreset_ClearsAnyStandingSolo() =>
        // A solo left over from the previous preset would silence five of the
        // six bands the user just chose, with no visible cause.
        Assert.Equal(-1, LoadedAfterSolo().SoloedBand);

    private static EqSection LoadedAfterSolo()
    {
        var section = new EqSection();
        section.ToggleSolo(0);
        section.LoadPreset("BassOnly");
        return section;
    }

    [Fact]
    public void LoadPreset_IgnoresAnUnknownKey()
    {
        var section = new EqSection();
        section.LoadPreset("FindTheGuitar");
        var before = section.Bands.ToList();

        section.LoadPreset("NoSuchPresetHasEverExisted");

        Assert.Equal(before, section.Bands);
        Assert.Equal("FindTheGuitar", section.PresetKey);
    }

    [Fact]
    public void TheTrim_ClampsOnWrite()
    {
        var section = new EqSection { TrimDb = 400 };
        Assert.Equal(EqLimits.MaxTrimDb, section.TrimDb, 6);

        section.TrimDb = -400;
        Assert.Equal(-EqLimits.MaxTrimDb, section.TrimDb, 6);
    }

    [Fact]
    public void EveryPresetKey_ResolvesToExactlySixBands()
    {
        Assert.NotEmpty(EqPresets.Keys);
        foreach (var key in EqPresets.Keys)
        {
            var preset = EqPresets.ByKey(key);
            Assert.NotNull(preset);
            Assert.Equal(EqLimits.BandCount, preset.Count);
        }
    }

    [Fact]
    public void EveryPresetBand_IsAlreadyInsideTheEnginesRange() =>
        // A preset that needed clamping would draw differently the instant it
        // was touched, which reads as the EQ moving on its own.
        Assert.All(
            EqPresets.Keys.SelectMany(k => EqPresets.ByKey(k)!),
            band => Assert.Equal(band, band.Clamped()));

    [Fact]
    public void TheFullRangePreset_IsAudiblyNothing() =>
        Assert.All(EqPresets.FullRange, b => Assert.False(b.Enabled));

    [Fact]
    public void ThePresetKeys_StartWithFullRange_TheOffTheOthersDepartFrom() =>
        Assert.Equal(EqPresets.FullRangeKey, EqPresets.Keys[0]);
}
