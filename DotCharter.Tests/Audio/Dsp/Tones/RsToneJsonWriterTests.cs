using System.Text.Json.Nodes;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Audio.Dsp.Tones;

public sealed class RsToneJsonWriterTests
{
    // -------------------------------------------------- round-trip (fixtures)

    [Theory]
    [InlineData("default_lead.json")]
    [InlineData("default_rhythm.json")]
    [InlineData("default_bass.json")]
    public void RoundTrip_RealFixture_IsStructurallyLossless(string fixture)
    {
        var original = ToneFixtures.ParseFixture(fixture);

        var written = RsToneJsonWriter.Write(original);
        Assert.True(RsToneJsonParser.TryParse(written, out var reparsedNullable));
        var reparsed = reparsedNullable!;

        AssertTonesEqual(original, reparsed);
    }

    [Fact]
    public void RoundTrip_CleanToneSample_IsStructurallyLosslessAndEmitsValidManifestBody()
    {
        // The exact minimal "cleanTone" body a manifest embeds (verbatim from
        // RocksmithPackageBuilderTests): Amp-only GearList (no Cabinet slot), no
        // ToneDescriptors, a bare "Volume" knob key (not the <GearKey>_<Pot>
        // shape) and a string Volume — the edges the fuller fixtures don't hit.
        const string cleanTone =
            """
            {"GearList":{"Amp":{"Type":"Amps","KnobValues":{"Volume":-12.0},"Key":"Amp_Default"}},
             "IsCustom":true,"Volume":"-12.000","Key":"dc_clean","Name":"DC Clean","SortOrder":0.0}
            """;

        Assert.True(RsToneJsonParser.TryParse(cleanTone, out var parsed));
        var tone = parsed!;
        Assert.Equal("dc_clean", tone.Key);
        Assert.Equal("DC Clean", tone.Name);
        Assert.Equal(-12f, tone.VolumeDb, 0.001);
        Assert.Equal(0f, tone.SortOrder);
        Assert.Equal("Amp_Default", tone.Amp.GearKey);
        // Missing Cabinet is normalized to the RS default on the first parse.
        Assert.Equal(RsToneJsonParser.DefaultCabinetKey, tone.Cabinet.GearKey);

        var written = RsToneJsonWriter.Write(tone);

        // The emitted body is a valid manifest tone: every key the game reads is
        // present with the right shape.
        var node = JsonNode.Parse(written)!.AsObject();
        var gearList = node["GearList"]!.AsObject();
        Assert.True(gearList.ContainsKey("Amp"));
        Assert.True(gearList.ContainsKey("Cabinet")); // writer always emits amp+cab
        Assert.Equal("Amp_Default", gearList["Amp"]!["Key"]!.GetValue<string>());
        Assert.Equal("Amps", gearList["Amp"]!["Type"]!.GetValue<string>());
        Assert.Equal(-12d, gearList["Amp"]!["KnobValues"]!["Volume"]!.GetValue<double>(), 0.001);
        Assert.Equal("dc_clean", node["Key"]!.GetValue<string>());
        Assert.Equal("DC Clean", node["Name"]!.GetValue<string>());
        Assert.Equal("-12.000", node["Volume"]!.GetValue<string>());
        Assert.True(node["IsCustom"]!.GetValue<bool>());
        Assert.Equal(" - ", node["NameSeparator"]!.GetValue<string>());
        Assert.Equal(0, node["SortOrder"]!.GetValue<int>());
        Assert.NotNull(node["ToneDescriptors"]!.AsArray());

        // parse -> write -> parse is stable from the normalized parse on.
        Assert.True(RsToneJsonParser.TryParse(written, out var reparsed));
        AssertTonesEqual(tone, reparsed!);
    }

    [Fact]
    public void RoundTrip_PreservesSparseSlotLayout()
    {
        // Only PrePedal3 occupied — the slot number must survive a round-trip.
        const string json = """
            {"GearList":{"Amp":{"Type":"Amps","KnobValues":{"Amp_Test_Gain":5},"Key":"Amp_Test"},
            "Cabinet":{"Type":"Cabinets","KnobValues":{},"Key":"Cab_Test"},
            "PrePedal3":{"Type":"Pedals","KnobValues":{"Pedal_X_Tone":1.5},"Key":"Pedal_X"}},
            "ToneDescriptors":[],"Volume":"-10.0","Key":"K","Name":"N"}
            """;

        Assert.True(RsToneJsonParser.TryParse(json, out var tone));
        var written = RsToneJsonWriter.Write(tone!);

        var node = JsonNode.Parse(written)!.AsObject();
        var gearList = node["GearList"]!.AsObject();
        Assert.True(gearList.ContainsKey("PrePedal3"));
        Assert.False(gearList.ContainsKey("PrePedal1"));

        Assert.True(RsToneJsonParser.TryParse(written, out var reparsed));
        Assert.Equal(3, Assert.Single(reparsed!.PrePedals).SlotIndex);
    }

    [Fact]
    public void RoundTrip_PreservesSortOrder()
    {
        const string json = """
            {"GearList":{"Amp":{"Type":"Amps","KnobValues":{},"Key":"Amp_Test"},
            "Cabinet":{"Type":"Cabinets","KnobValues":{},"Key":"Cab_Test"}},
            "ToneDescriptors":[],"Volume":"-12.0","Key":"K","Name":"N","SortOrder":2.5}
            """;

        Assert.True(RsToneJsonParser.TryParse(json, out var tone));
        Assert.Equal(2.5f, tone!.SortOrder);

        Assert.True(RsToneJsonParser.TryParse(RsToneJsonWriter.Write(tone), out var reparsed));
        Assert.Equal(2.5f, reparsed!.SortOrder);
    }

    [Fact]
    public void Parser_ToleratesQuotedSortOrder()
    {
        const string json = """
            {"GearList":{"Amp":{"Type":"Amps","KnobValues":{},"Key":"Amp_Test"},
            "Cabinet":{"Type":"Cabinets","KnobValues":{},"Key":"Cab_Test"}},
            "ToneDescriptors":[],"Volume":"-12.0","Key":"K","Name":"N","SortOrder":"1"}
            """;

        Assert.True(RsToneJsonParser.TryParse(json, out var tone));
        Assert.Equal(1f, tone!.SortOrder);
    }

    // ------------------------------------------------------------ golden body

    [Fact]
    public void Write_GoldenBody_MatchesManifestToneShape()
    {
        var tone = new RsTone(
            "Test_Key",
            "Test Tone",
            -12f,
            new[] { "$[35720]CLEAN" },
            new RsAmp("Amp_MarshallPlexi", null,
                new Dictionary<string, float> { ["Amp_MarshallPlexi_Gain"] = 70f }),
            new RsCab("Cab_Marshall1960TV_Ribbon_Cone", null,
                new Dictionary<string, float>()),
            new[]
            {
                new RsPedal(RsGearRole.PrePedal, 1, "Pedal_GermaniumDrive", RsGearType.Pedal,
                    "Distortion", new Dictionary<string, float> { ["Pedal_GermaniumDrive_Tone"] = 36f })
            },
            new[]
            {
                new RsPedal(RsGearRole.Rack, 1, "Rack_StudioCompressor", RsGearType.Rack,
                    null, new Dictionary<string, float> { ["Rack_StudioCompressor_Ratio"] = 3f })
            },
            Array.Empty<RsPedal>())
        {
            SortOrder = 0f
        };

        const string expected =
            """{"GearList":{"Rack1":{"Type":"Racks","KnobValues":{"Rack_StudioCompressor_Ratio":3},"Key":"Rack_StudioCompressor"},"Amp":{"Type":"Amps","KnobValues":{"Amp_MarshallPlexi_Gain":70},"Key":"Amp_MarshallPlexi"},"Cabinet":{"Type":"Cabinets","KnobValues":{},"Key":"Cab_Marshall1960TV_Ribbon_Cone"},"PrePedal1":{"Type":"Pedals","KnobValues":{"Pedal_GermaniumDrive_Tone":36},"Key":"Pedal_GermaniumDrive","Category":"Distortion"}},"ToneDescriptors":["$[35720]CLEAN"],"NameSeparator":" - ","IsCustom":true,"Volume":"-12.000","Key":"Test_Key","Name":"Test Tone","SortOrder":0}""";

        Assert.Equal(expected, RsToneJsonWriter.Write(tone));
    }

    [Fact]
    public void Write_VolumeIsThreeDecimalInvariantString()
    {
        var tone = ToneFixtures.ParseFixture("default_bass.json"); // Volume "-12.0"
        var node = JsonNode.Parse(RsToneJsonWriter.Write(tone))!.AsObject();

        Assert.Equal("-12.000", node["Volume"]!.GetValue<string>());
        Assert.True(node["IsCustom"]!.GetValue<bool>());
        Assert.Equal(" - ", node["NameSeparator"]!.GetValue<string>());
    }

    [Fact]
    public void Write_OmitsSortOrderWhenAbsent()
    {
        var tone = ToneFixtures.ParseFixture("default_lead.json"); // no SortOrder in fixture
        var node = JsonNode.Parse(RsToneJsonWriter.Write(tone))!.AsObject();

        Assert.False(node.ContainsKey("SortOrder"));
        Assert.False(node.ContainsKey("MacVolume"));
    }

    // ----------------------------------------------------------- equality

    private static void AssertTonesEqual(RsTone expected, RsTone actual)
    {
        Assert.Equal(expected.Key, actual.Key);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.VolumeDb, actual.VolumeDb, 0.001);
        Assert.Equal(expected.ToneDescriptors, actual.ToneDescriptors);
        Assert.Equal(expected.SortOrder, actual.SortOrder);

        AssertGearEqual(expected.Amp, actual.Amp);
        AssertGearEqual(expected.Cabinet, actual.Cabinet);
        AssertPedalsEqual(expected.PrePedals, actual.PrePedals);
        AssertPedalsEqual(expected.Racks, actual.Racks);
        AssertPedalsEqual(expected.PostPedals, actual.PostPedals);
    }

    private static void AssertPedalsEqual(IReadOnlyList<RsPedal> expected, IReadOnlyList<RsPedal> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Role, actual[i].Role);
            Assert.Equal(expected[i].SlotIndex, actual[i].SlotIndex);
            AssertGearEqual(expected[i], actual[i]);
        }
    }

    private static void AssertGearEqual(RsGear expected, RsGear actual)
    {
        Assert.Equal(expected.GearKey, actual.GearKey);
        Assert.Equal(expected.Type, actual.Type);
        Assert.Equal(expected.Category, actual.Category);
        Assert.Equal(expected.KnobValues.Count, actual.KnobValues.Count);
        foreach (var (key, value) in expected.KnobValues)
        {
            Assert.True(actual.KnobValues.TryGetValue(key, out var actualValue),
                $"knob '{key}' missing after round-trip");
            Assert.Equal(value, actualValue, 0.0001);
        }
    }
}
