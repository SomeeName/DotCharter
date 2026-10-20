using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Audio.Dsp.Tones;

/// <summary>
/// The pure helpers behind the Tone Designer (plan 2.6): key validation and
/// uniquing, catalogue slicing for the gear pickers, the fixed descriptor
/// catalogue and the default authored tone. All logic here must stay
/// UI-independent — the dialog view model is a thin binding shell over it.
/// </summary>
public sealed class RsToneAuthoringTests
{
    // ------------------------------------------------------------- key rules

    [Theory]
    [InlineData("clean")]
    [InlineData("Lead_Tone_2")]
    [InlineData("a")]
    [InlineData("A1_b2_C3")]
    public void IsValidKey_accepts_identifier_like_keys(string key)
        => Assert.True(RsToneAuthoring.IsValidKey(key));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1clean")]     // must start with a letter
    [InlineData("_clean")]
    [InlineData("my tone")]    // no spaces
    [InlineData("dist-orted")] // no dashes
    [InlineData("töne")]       // ASCII only
    public void IsValidKey_rejects_non_identifier_keys(string? key)
        => Assert.False(RsToneAuthoring.IsValidKey(key));

    [Fact]
    public void IsValidKey_rejects_over_long_keys()
        => Assert.False(RsToneAuthoring.IsValidKey(new string('a', RsToneAuthoring.MaxKeyLength + 1)));

    [Theory]
    [InlineData("My Lead Tone", "My_Lead_Tone")]
    [InlineData("  clean  ", "clean")]
    [InlineData("2nd-solo", "tone_2nd_solo")]
    [InlineData("äöü", "tone")]
    [InlineData("", "tone")]
    [InlineData(null, "tone")]
    public void SanitizeKey_folds_arbitrary_input_into_a_valid_key(string? raw, string expected)
    {
        var key = RsToneAuthoring.SanitizeKey(raw);
        Assert.Equal(expected, key);
        Assert.True(RsToneAuthoring.IsValidKey(key));
    }

    [Fact]
    public void SanitizeKey_is_idempotent_for_valid_keys()
        => Assert.Equal("Lead_2", RsToneAuthoring.SanitizeKey("Lead_2"));

    [Fact]
    public void MakeUniqueKey_suffixes_until_free_and_ignores_case()
    {
        var existing = new[] { "clean", "Clean_2" };
        Assert.Equal("clean_3", RsToneAuthoring.MakeUniqueKey("clean", existing));
        Assert.Equal("lead", RsToneAuthoring.MakeUniqueKey("lead", existing));
    }

    [Fact]
    public void MakeUniqueKey_respects_the_length_cap_when_suffixing()
    {
        var baseKey = new string('a', RsToneAuthoring.MaxKeyLength);
        var unique = RsToneAuthoring.MakeUniqueKey(baseKey, new[] { baseKey });
        Assert.True(unique.Length <= RsToneAuthoring.MaxKeyLength);
        Assert.EndsWith("_2", unique);
        Assert.True(RsToneAuthoring.IsValidKey(unique));
    }

    // ------------------------------------------------------ gear slot slices

    [Theory]
    [InlineData(RsGearType.Amp, 63)]
    [InlineData(RsGearType.Cabinet, 450)]
    [InlineData(RsGearType.Pedal, 83)]
    [InlineData(RsGearType.Rack, 17)]
    public void GearOptions_slices_the_full_catalogue_by_type(RsGearType type, int expectedCount)
    {
        var options = RsToneAuthoring.GearOptions(RsGearCatalog.Shared, type);
        Assert.Equal(expectedCount, options.Count);
        Assert.All(options, g => Assert.False(string.IsNullOrEmpty(g.Key)));
    }

    [Fact]
    public void GearOptions_is_sorted_by_display_name()
    {
        var amps = RsToneAuthoring.GearOptions(RsGearCatalog.Shared, RsGearType.Amp);
        var sorted = amps.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        Assert.Equal(sorted.Select(g => g.Key), amps.Select(g => g.Key));
    }

    [Fact]
    public void GearOptions_on_an_empty_catalogue_is_empty_not_throwing()
        => Assert.Empty(RsToneAuthoring.GearOptions(RsGearCatalog.Empty, RsGearType.Amp));

    // ------------------------------------------------------------ descriptors

    [Fact]
    public void Descriptor_catalogue_matches_the_rocksmith2014_net_table()
    {
        Assert.Equal(32, RsToneAuthoring.Descriptors.Count);
        Assert.Equal(RsToneAuthoring.Descriptors.Count,
            RsToneAuthoring.Descriptors.Select(d => d.UIName).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(RsToneAuthoring.Descriptors, d => d.UIName == "$[35720]CLEAN" && d.Name == "Clean");
        Assert.Contains(RsToneAuthoring.Descriptors, d => d.UIName == "$[35724]LEAD" && d.Name == "Lead");
        Assert.Contains(RsToneAuthoring.Descriptors, d => d.UIName == "$[35755]HIGH GAIN");
        // Every UIName carries the $[locId]NAME shape the game resolves.
        Assert.All(RsToneAuthoring.Descriptors,
            d => Assert.Matches(@"^\$\[\d+\][A-Z0-9 \-]+$", d.UIName));
    }

    [Fact]
    public void FindDescriptor_resolves_known_uinames_and_null_for_unknown()
    {
        Assert.Equal("Clean", RsToneAuthoring.FindDescriptor("$[35720]CLEAN")?.Name);
        Assert.Null(RsToneAuthoring.FindDescriptor("$[99999]MADE UP"));
        Assert.Null(RsToneAuthoring.FindDescriptor(null));
        Assert.Null(RsToneAuthoring.FindDescriptor(""));
    }

    // ------------------------------------------------------------ knob rules

    [Fact]
    public void DefaultKnobValues_pins_every_knob_to_its_catalogue_default()
    {
        var amp = RsToneAuthoring.GearOptions(RsGearCatalog.Shared, RsGearType.Amp)[0];
        Assert.NotNull(amp.Knobs);

        var values = RsToneAuthoring.DefaultKnobValues(amp);
        Assert.Equal(amp.Knobs!.Count, values.Count);
        foreach (var knob in amp.Knobs!)
            Assert.Equal(knob.DefaultValue, values[knob.Key]);
    }

    [Fact]
    public void DefaultKnobValues_of_a_knobless_cabinet_is_empty()
    {
        var cab = RsToneAuthoring.GearOptions(RsGearCatalog.Shared, RsGearType.Cabinet)[0];
        Assert.Empty(RsToneAuthoring.DefaultKnobValues(cab));
    }

    [Fact]
    public void ClampKnobValue_clamps_into_the_declared_range()
    {
        var knob = new RsGearKnob("Gain", "Amp_Test_Gain", RsKnobUnit.Number, 0f, 10f, 0.1f, 5f, null);
        Assert.Equal(0f, RsToneAuthoring.ClampKnobValue(knob, -3f));
        Assert.Equal(10f, RsToneAuthoring.ClampKnobValue(knob, 42f));
        Assert.Equal(7.5f, RsToneAuthoring.ClampKnobValue(knob, 7.5f));
    }

    [Fact]
    public void ClampVolumeDb_stays_in_the_dlc_builder_range()
    {
        Assert.Equal(RsToneAuthoring.MinVolumeDb, RsToneAuthoring.ClampVolumeDb(-90f));
        Assert.Equal(RsToneAuthoring.MaxVolumeDb, RsToneAuthoring.ClampVolumeDb(5f));
        Assert.Equal(-12f, RsToneAuthoring.ClampVolumeDb(-12f));
    }

    // ---------------------------------------------------------- default tone

    [Fact]
    public void CreateDefaultTone_round_trips_through_the_writer_and_parser()
    {
        var tone = RsToneAuthoring.CreateDefaultTone("my_tone", "My Tone", RsGearCatalog.Shared);

        Assert.Equal("my_tone", tone.Key);
        Assert.Equal("My Tone", tone.Name);
        Assert.Equal(RsToneAuthoring.DefaultVolumeDb, tone.VolumeDb);
        Assert.Equal(new[] { RsToneAuthoring.CleanDescriptorUIName }, tone.ToneDescriptors);
        Assert.Empty(tone.PrePedals);
        Assert.Empty(tone.Racks);
        Assert.Empty(tone.PostPedals);

        // The default amp is a real catalogue amp at default knob values.
        Assert.True(RsGearCatalog.Shared.TryGetGear(tone.Amp.GearKey, out var ampGear));
        Assert.Equal(ampGear!.Knobs!.Count, tone.Amp.KnobValues.Count);
        Assert.Equal(RsToneJsonParser.DefaultCabinetKey, tone.Cabinet.GearKey);

        var json = RsToneJsonWriter.Write(tone);
        Assert.True(RsToneJsonParser.TryParse(json, out var reparsed));
        Assert.Equal(tone.Key, reparsed!.Key);
        Assert.Equal(tone.Amp.GearKey, reparsed.Amp.GearKey);
        Assert.Equal(tone.Cabinet.GearKey, reparsed.Cabinet.GearKey);
        Assert.Equal(tone.VolumeDb, reparsed.VolumeDb);
    }

    [Fact]
    public void CreateDefaultTone_degrades_gracefully_on_an_empty_catalogue()
    {
        var tone = RsToneAuthoring.CreateDefaultTone("k", "n", RsGearCatalog.Empty);
        Assert.Equal(RsToneJsonParser.SyntheticAmpKey, tone.Amp.GearKey);
        Assert.Equal(RsToneJsonParser.DefaultCabinetKey, tone.Cabinet.GearKey);
    }
}
