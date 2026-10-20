using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Audio.Dsp.Tones;

/// <summary>The tone files DLC Builder writes (JSON body, Tone2014 XML, .rs2dlc project) become project tone bodies.</summary>
public sealed class RsToneFileReaderTests
{
    private static RsTone Parsed(RsToneFileTone tone)
    {
        Assert.True(RsToneJsonParser.TryParse(tone.Json, out var parsed), "the stored body must parse back");
        return parsed!;
    }

    [Fact]
    public void ToolkitXml_BecomesAToneBody_WithGearKnobsSlotsVolumeAndDescriptors()
    {
        var tone = Assert.Single(RsToneFileReader.Read(ToneFileFixtures.ToolkitXml));

        Assert.Equal(("Test", "Test Tone"), (tone.Key, tone.Name));
        var parsed = Parsed(tone);
        Assert.Equal(-12f, parsed.VolumeDb);
        Assert.Equal("Amp_OrangeAD50", parsed.Amp.GearKey);
        Assert.Equal(88f, parsed.Amp.KnobValues["Amp_OrangeAD50_Gain"]);
        Assert.Equal(75.5f, parsed.Amp.KnobValues["Amp_OrangeAD50_Bass"]);
        Assert.Equal("Cab_OrangeJimmyBean_57_Cone", parsed.Cabinet.GearKey);
        var pedal = Assert.Single(parsed.PrePedals);
        Assert.Equal(("Pedal_Chorus20", 2), (pedal.GearKey, pedal.SlotIndex));
        Assert.Empty(parsed.PostPedals);
        Assert.Equal(["$[35720]CLEAN"], parsed.ToneDescriptors);
    }

    [Fact]
    public void Rocksmith2014NetXml_InItsOwnNamespace_AlsoReads()
    {
        var xml = ToneFileFixtures.ToolkitXml.Replace(
            "RocksmithToolkitLib.DLCPackage.Manifest2014.Tone", "Rocksmith2014.Common.Manifest");

        Assert.Equal("Test", Assert.Single(RsToneFileReader.Read(xml)).Key);
    }

    [Fact]
    public void AJsonToneBody_IsKeptVerbatim_SkinsIncluded()
    {
        var body = ToneFileFixtures.ToneBody("guitar");

        var tone = Assert.Single(RsToneFileReader.Read(body));

        Assert.Equal(("guitar", "guitar"), (tone.Key, tone.Name));
        Assert.Contains("urn:image:dds:gear_amp_orangead50_2", tone.Json, StringComparison.Ordinal);
        Assert.Equal(-19.8f, Parsed(tone).VolumeDb);
    }

    [Fact]
    public void AProjectsTonesArray_YieldsEveryTone_AndIgnoresTheRest()
    {
        var project = ToneFileFixtures.Project(ToneFileFixtures.ToneBody("guitar"), ToneFileFixtures.ToneBody("bass", "-21.9"));

        var tones = RsToneFileReader.Read("\uFEFF" + project);

        Assert.Equal(["guitar", "bass"], tones.Select(t => t.Key));
        Assert.Equal(-21.9f, Parsed(tones[1]).VolumeDb);
    }

    [Fact]
    public void AKeylessBody_TakesTheFileNameAsItsKey()
    {
        var keyless = ToneFileFixtures.ToneBody("x").Replace("\"Key\": \"x\",", "").Replace("\"Name\": \"x\"", "\"Name\": \"\"");

        var tone = Assert.Single(RsToneFileReader.Read(keyless, RsToneFileReader.FallbackKeyFor(@"C:\tones\My Lead.tone2014.json")));

        Assert.Equal(("My_Lead", "My_Lead"), (tone.Key, tone.Name));
        Assert.Equal("My_Lead", Parsed(tone).Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("plain text")]
    [InlineData("{ not json")]
    [InlineData("""{"Hello":"world"}""")]
    [InlineData("""{"Tones":[]}""")]
    [InlineData("<Other />")]
    [InlineData("<Tone2014><GearList><Cabinet><PedalKey>c</PedalKey></Cabinet></GearList></Tone2014>")]
    [InlineData("<Tone2014")]
    public void ANonToneFile_IsRefusedWithAFormatException(string text)
        => Assert.Throws<FormatException>(() => RsToneFileReader.Read(text));
}
