using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Audio.Dsp.Tones;

public sealed class RsGearCatalogTests
{
    private static readonly RsGearCatalog Catalog = RsGearCatalog.Shared;

    [Fact]
    public void CountsMatchTheShippedFile()
    {
        Assert.Equal(63, Catalog.Count(RsGearType.Amp));
        Assert.Equal(450, Catalog.Count(RsGearType.Cabinet));
        Assert.Equal(83, Catalog.Count(RsGearType.Pedal));
        Assert.Equal(17, Catalog.Count(RsGearType.Rack));
    }

    [Fact]
    public void TryGetKnob_ResolvesRangeBySuffix()
    {
        Assert.True(Catalog.TryGetKnob("Amp_MarshallPlexi", "Bass", out var knob));
        Assert.Equal(1f, knob.MinValue, 0.001);
        Assert.Equal(100f, knob.MaxValue, 0.001);
        Assert.Equal(RsKnobUnit.Number, knob.UnitType);
        Assert.Equal("Bass", knob.Pot);
    }

    [Fact]
    public void TryGetKnobByFullKey_ResolvesInOneHop()
    {
        Assert.True(Catalog.TryGetKnobByFullKey("Pedal_ModDelay_Time", out var knob));
        Assert.Equal(RsKnobUnit.Ms, knob.UnitType);
        Assert.Equal(10f, knob.MinValue, 0.001);
        Assert.Equal(1000f, knob.MaxValue, 0.001);
    }

    [Theory]
    [InlineData("Amp_MarshallPlexi_Bass", RsKnobUnit.Number)]
    [InlineData("Pedal_ModDelay_Feedback", RsKnobUnit.Percent)]
    [InlineData("Rack_StudioEQ_Bass", RsKnobUnit.Db)]
    [InlineData("Pedal_ModDelay_Rate", RsKnobUnit.Hz)]
    [InlineData("Rack_StudioEQ_TrebleFreq", RsKnobUnit.Khz)]
    [InlineData("Pedal_ModDelay_Time", RsKnobUnit.Ms)]
    [InlineData("Rack_StudioEQ_LoMidQ", RsKnobUnit.Enum)]
    public void UnitTypes_MapAcrossEveryDeclaredUnit(string fullKey, RsKnobUnit expected)
    {
        Assert.True(Catalog.TryGetKnobByFullKey(fullKey, out var knob));
        Assert.Equal(expected, knob.UnitType);
    }

    [Fact]
    public void EnumKnob_CarriesEnumValues()
    {
        Assert.True(Catalog.TryGetKnobByFullKey("Bass_Amp_BT975B_LoShift", out var knob));
        Assert.Equal(RsKnobUnit.Enum, knob.UnitType);
        Assert.NotNull(knob.EnumValues);
        Assert.Equal(new[] { "Off", "On" }, knob.EnumValues);
    }

    [Fact]
    public void Cabinet_HasNullKnobs()
    {
        Assert.True(Catalog.TryGetGear("Cab_Marshall1960TV_Ribbon_Cone", out var cab));
        Assert.Equal("Cabinets", cab.Type);
        Assert.Null(cab.Knobs);
    }

    [Fact]
    public void GetCategory_IsAuthoritativeForRouting()
    {
        // Pedal_ModDelay's tone-JSON Category is a loc-id ("30779"); the
        // catalogue's category is the real routing family.
        Assert.Equal("Delay", Catalog.GetCategory("Pedal_ModDelay"));
        Assert.Equal("Distortion", Catalog.GetCategory("Pedal_GermaniumDrive"));
        Assert.Equal("Amp", Catalog.GetCategory("Amp_MarshallPlexi"));
    }

    [Fact]
    public void UnknownKey_ReturnsFalseAndNullCategory()
    {
        Assert.False(Catalog.TryGetGear("Amp_DoesNotExist", out _));
        Assert.False(Catalog.TryGetKnob("Amp_DoesNotExist", "Gain", out _));
        Assert.Null(Catalog.GetCategory("Amp_DoesNotExist"));
    }
}
