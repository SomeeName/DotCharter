using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.Tone.Parsing.Psarc;

namespace DotCharter.Tests.Audio.Dsp.Tones;

public sealed class RsToneJsonParserTests
{
    // ------------------------------------------------------ real fixtures

    [Fact]
    public void DefaultLead_ParsesIdentityAndVolume()
    {
        var tone = ToneFixtures.ParseFixture("default_lead.json");

        Assert.Equal("Default_Lead", tone.Key);
        Assert.Equal("Lead", tone.Name);
        Assert.Equal(-19.5f, tone.VolumeDb, 0.001);
        Assert.Contains("$[35724]LEAD", tone.ToneDescriptors);
    }

    [Fact]
    public void DefaultLead_ExtractsAmpSlotWithSixKnobs()
    {
        var tone = ToneFixtures.ParseFixture("default_lead.json");

        Assert.IsType<RsAmp>(tone.Amp);
        Assert.Equal("Amp_MarshallPlexi", tone.Amp.GearKey);
        Assert.Equal(RsGearType.Amp, tone.Amp.Type);
        Assert.Equal(6, tone.Amp.KnobValues.Count);
        Assert.True(tone.Amp.TryGetKnob("Loudness1", out var l1));
        Assert.Equal(88f, l1, 0.001);
        Assert.True(tone.Amp.TryGetKnob("Loudness2", out var l2));
        Assert.Equal(59f, l2, 0.001);
    }

    [Fact]
    public void DefaultLead_CabinetHasEmptyKnobs()
    {
        var tone = ToneFixtures.ParseFixture("default_lead.json");

        Assert.IsType<RsCab>(tone.Cabinet);
        Assert.Equal("Cab_Marshall1960TV_Ribbon_Cone", tone.Cabinet.GearKey);
        Assert.Empty(tone.Cabinet.KnobValues);
    }

    [Fact]
    public void DefaultLead_ExtractsPrePostAndRackSlots()
    {
        var tone = ToneFixtures.ParseFixture("default_lead.json");

        var pre = Assert.Single(tone.PrePedals);
        Assert.Equal("Pedal_GermaniumDrive", pre.GearKey);
        Assert.Equal(RsGearRole.PrePedal, pre.Role);
        Assert.Equal(1, pre.SlotIndex);
        Assert.Equal("Distortion", pre.Category); // raw JSON category

        var post = Assert.Single(tone.PostPedals);
        Assert.Equal("Pedal_ModDelay", post.GearKey);
        Assert.Equal(RsGearRole.PostPedal, post.Role);
        Assert.True(post.TryGetKnob("Time", out var time));
        Assert.Equal(360f, time, 0.001);
        Assert.True(post.TryGetKnob("Rate", out var rate));
        Assert.Equal(0.5f, rate, 0.001);
        Assert.Equal("30779", post.Category); // loc-id, verbatim, NOT trusted for routing

        Assert.Equal(2, tone.Racks.Count);
        Assert.Equal("Rack_StudioEQ", tone.Racks[0].GearKey);
        Assert.True(tone.Racks[0].TryGetKnob("LoMid", out var loMid));
        Assert.Equal(-5f, loMid, 0.001);
        Assert.Equal("Rack_StudioChamber", tone.Racks[1].GearKey);
    }

    [Fact]
    public void DefaultLead_InSignalOrderFollowsRoutingOrder()
    {
        var tone = ToneFixtures.ParseFixture("default_lead.json");

        var keys = tone.InSignalOrder().Select(g => g.GearKey).ToArray();
        Assert.Equal(new[]
        {
            "Pedal_GermaniumDrive",       // PrePedal1
            "Amp_MarshallPlexi",          // Amp
            "Cab_Marshall1960TV_Ribbon_Cone", // Cabinet
            "Rack_StudioEQ",              // Rack1
            "Rack_StudioChamber",         // Rack2
            "Pedal_ModDelay"              // PostPedal1
        }, keys);
    }

    [Fact]
    public void DefaultBass_HandlesBassPrefixedKeys()
    {
        var tone = ToneFixtures.ParseFixture("default_bass.json");

        Assert.Equal("Bass_Amp_EdenWT550", tone.Amp.GearKey);
        // Pot suffix composition works despite the "Bass_" prefix.
        Assert.True(tone.Amp.TryGetKnob("Gain", out var gain));
        Assert.Equal(14f, gain, 0.001);
        // Pots() strips only the gear-key prefix, leaving single-token suffixes.
        Assert.Contains(tone.Amp.Pots(), t => t.Pot == "Gain");
        Assert.Contains(tone.Amp.Pots(), t => t.Pot == "LoFreq");
    }

    // --------------------------------------------------- dirty / synthetic

    [Theory]
    [InlineData("-19.5", -19.5f)]
    [InlineData("-19.5 dB", -19.5f)]
    [InlineData("-12,0", -12.0f)]
    [InlineData("‑12,0", -12.0f)] // unicode non-breaking hyphen
    [InlineData("-26.250S", -26.3f)]   // rounds to one decimal, away from zero
    [InlineData("", 0f)]
    [InlineData("garbage", 0f)]
    [InlineData(null, 0f)]
    public void ParseVolume_CleansDirtyStrings(string? raw, float expected) => Assert.Equal(expected, RsToneJsonParser.ParseVolume(raw), 0.001);

    [Fact]
    public void MissingCabinet_FallsBackToDefaultKey()
    {
        const string json = """
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}}},"Key":"T","Name":"T"}
            """;

        Assert.True(RsToneJsonParser.TryParse(json, out var tone));
        Assert.Equal(RsToneJsonParser.DefaultCabinetKey, tone!.Cabinet.GearKey);
    }

    [Fact]
    public void MissingAmp_ProducesSyntheticAmp()
    {
        const string json = """
            {"GearList":{"Cabinet":{"Type":"Cabinets","Key":"Cab_X","KnobValues":{}}},"Key":"T","Name":"T"}
            """;

        Assert.True(RsToneJsonParser.TryParse(json, out var tone));
        Assert.Equal(RsToneJsonParser.SyntheticAmpKey, tone!.Amp.GearKey);
        Assert.Empty(tone.Amp.KnobValues);
    }

    [Fact]
    public void UnknownSlotType_BecomesUnknown()
    {
        const string json = """
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}},
              "PrePedal1":{"Type":"Weird","Key":"Pedal_Mystery","KnobValues":{}}},"Key":"T","Name":"T"}
            """;

        Assert.True(RsToneJsonParser.TryParse(json, out var tone));
        var pre = Assert.Single(tone!.PrePedals);
        Assert.Equal(RsGearType.Unknown, pre.Type);
    }

    [Fact]
    public void KnobValues_TolerateIntAndFloat()
    {
        const string json = """
            {"GearList":{"Amp":{"Type":"Amps","Key":"Amp_X","KnobValues":{"Amp_X_A":360,"Amp_X_B":0.5}}},"Key":"T","Name":"T"}
            """;

        Assert.True(RsToneJsonParser.TryParse(json, out var tone));
        Assert.True(tone!.Amp.TryGetKnob("A", out var a));
        Assert.Equal(360f, a, 0.001);
        Assert.True(tone.Amp.TryGetKnob("B", out var b));
        Assert.Equal(0.5f, b, 0.001);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"NoGearList\":true}")]
    [InlineData("[1,2,3]")]
    [InlineData("")]
    public void TryParse_ReturnsFalseOnMalformedOrMissingGearList(string json)
    {
        Assert.False(RsToneJsonParser.TryParse(json, out var tone));
        Assert.Null(tone);
    }

    [Fact]
    public void Parse_FromUnparseableDefinition_ReturnsMinimalTone()
    {
        var tone = RsToneJsonParser.Parse(new PsarcToneDefinition("MyKey", "MyName", "totally broken"));

        Assert.Equal("MyKey", tone.Key);
        Assert.Equal("MyName", tone.Name);
        Assert.Equal(RsToneJsonParser.SyntheticAmpKey, tone.Amp.GearKey);
        Assert.Equal(RsToneJsonParser.DefaultCabinetKey, tone.Cabinet.GearKey);
    }

    [Fact]
    public void Parse_UsesDefinitionKeyNameAsFallback()
    {
        // GearList present but root has no Key/Name → falls back to def.
        const string json = """{"GearList":{"Amp":{"Type":"Amps","Key":"Amp_Twin","KnobValues":{}}}}""";
        var tone = RsToneJsonParser.Parse(new PsarcToneDefinition("FallbackKey", "FallbackName", json));

        Assert.Equal("FallbackKey", tone.Key);
        Assert.Equal("FallbackName", tone.Name);
        Assert.Equal("Amp_Twin", tone.Amp.GearKey);
    }
}
