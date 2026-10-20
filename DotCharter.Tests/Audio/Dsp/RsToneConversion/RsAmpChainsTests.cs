using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Audio.Dsp.RsToneConversion;

/// <summary>
///     The managed index of Rocksmith's own amp chains (#88), now built from the
///     player's install (#755): what the builder makes of an amp bank — the row
///     order, each knob's range and switch-ness, the stages its curves move, the Mac
///     stand-ins for Trash — on the synthetic install (<see cref="SyntheticRsGame" />),
///     and how <see cref="RsAmpChains" /> serves it. That a REAL install yields one
///     row per catalogue amp, in the native table's order, is
///     <c>LocalRsGameTablesTest</c>'s.
/// </summary>
public sealed class RsAmpChainsTests
{
    private static IReadOnlyList<RsAmpModel> Rows => SyntheticRsGame.Tables.AmpModels;

    private static RsAmpModel Row(string key) => Rows[SyntheticRsGame.Model(key)];

    [Fact]
    public void TheRows_AreTheAmpManifests_SortedByKeyOrdinal()
        => Assert.Equal(
            ["Amp_MarshallPlexi", "Amp_OrangeAD50", "Amp_TestA", "Bass_Amp_EdenWT550", "Bass_Amp_TestB", "DI_Amp_TestC"],
            Rows.Select(m => m.Key));

    [Fact]
    public void ARow_CarriesNameBankAndSlots_AndSkipsAKnobSlotWithNoRtpc()
    {
        var a = Row("Amp_TestA");
        Assert.Equal("Test Amp A", a.Name);                 // trimmed
        Assert.Equal("amp_testa.bnk", a.Bank);
        Assert.Equal(3, a.Slots);
        Assert.Equal(["Amp_TestA_Gain", "Amp_TestA_Bass", "Amp_TestA_Mode"], a.Knobs.Select(k => k.Key));
        Assert.Equal((1f, 10f, 5f), (a.Knobs[0].Min, a.Knobs[0].Max, a.Knobs[0].Default));
    }

    [Fact]
    public void EnumAndSwitchKnobs_AreDiscrete()
    {
        Assert.Equal([false, false, true], Row("Amp_TestA").Knobs.Select(k => k.Discrete));
        Assert.Equal([false, true], Row("Bass_Amp_TestB").Knobs.Select(k => k.Discrete));   // Type 2
    }

    [Fact]
    public void TheStages_AreWhatEachKnobsCurvesMove_AcrossTheChain()
    {
        var a = Row("Amp_TestA").Knobs;
        // Gain: drive (GD 61), a Mac stand-in EQ band (GD 2) and the FutzBox distortion (2).
        Assert.Equal(RsAmpStage.GuitarDistortion | RsAmpStage.Eq | RsAmpStage.FutzDistortion, a[0].Stages);
        Assert.Equal(RsAmpStage.Eq | RsAmpStage.FutzGain, a[1].Stages);
        Assert.Equal(RsAmpStage.GuitarDistortion, a[2].Stages);
        Assert.Equal(RsAmpStage.Compressor, Row("Bass_Amp_TestB").Knobs[1].Stages);
        Assert.Equal(RsAmpStage.Eq, Row("DI_Amp_TestC").Knobs[0].Stages);
        Assert.Equal(RsAmpStage.FutzFilter, Row("DI_Amp_TestC").Knobs[1].Stages);
    }

    [Fact]
    public void AnSCurveSegment_FlagsItsKnob_Unpublished()
        => Assert.Equal([true, false, false], Row("Amp_TestA").Knobs.Select(k => k.UnpublishedShape));

    [Fact]
    public void ATrashSlot_RunsItsMacStandIn_NamedOrFoundByTheFallbackName()
    {
        Assert.Equal(1, Row("Amp_TestA").MacSwaps);        // named by the manifest's Name_MacOS
        Assert.Equal(1, Row("Bass_Amp_TestB").MacSwaps);   // found as MacOS_<Key>_n
        Assert.Equal(0, Row("DI_Amp_TestC").MacSwaps);
        // Bass_Amp_TestB's Drive reaches the chain only through its stand-in.
        Assert.Equal(RsAmpStage.GuitarDistortion, Row("Bass_Amp_TestB").Knobs[0].Stages);
    }

    [Fact]
    public void TryGetModel_AndKnobNames_ServeTheTablesInUse()
    {
        using (SyntheticRsGame.Use())
        {
            Assert.True(RsAmpChains.IsAvailable);
            Assert.True(RsAmpChains.TryGetModel("Bass_Amp_TestB", out var model, out var row));
            Assert.Equal(4, model);
            Assert.Same(Rows[4], row);
            Assert.False(RsAmpChains.TryGetModel("Amp_NotThere", out _, out _));
            // Not in the gear catalogue: the knob's own key suffix.
            Assert.Equal(["Gain", "Bass", "Mode"], RsAmpChains.KnobNames(2));
            Assert.Empty(RsAmpChains.KnobNames(Rows.Count));
        }

        using (SyntheticRsGame.UseNone())
        {
            Assert.False(RsAmpChains.IsAvailable);
            Assert.False(RsAmpKnobs.Available);
            Assert.False(RsAmpChains.TryGetModel("Amp_TestA", out _, out _));
            Assert.Empty(RsAmpKnobs.NamesFor(0));
        }
    }

    [Fact]
    public void Position_IsTheKnobsTravel_Clamped()
    {
        var gain = Row("Amp_TestA").Knobs[0];   // 1..10
        Assert.Equal(0f, RsAmpChains.Position(gain, 1f));
        Assert.Equal(1f, RsAmpChains.Position(gain, 10f));
        Assert.Equal(4f / 9f, RsAmpChains.Position(gain, 5f), 1e-6f);
        Assert.Equal(0f, RsAmpChains.Position(gain, -5f));
        Assert.Equal(1f, RsAmpChains.Position(gain, 500f));
    }
}
