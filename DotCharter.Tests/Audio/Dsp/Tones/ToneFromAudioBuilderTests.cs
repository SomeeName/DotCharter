using DotCharter.Analysis.Tone;
using DotCharter.Services.Audio.Dsp.Tones;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Audio.Dsp.Tones;

/// <summary>
/// The measurements → Rocksmith tone mapper. Every test here runs against the
/// REAL embedded gear catalogue (<see cref="RsGearCatalog.Shared"/>) on purpose:
/// the failure this feature would actually ship is a knob that the chosen gear
/// does not have, or one outside the range the game accepts, and only the real
/// catalogue can catch that.
/// </summary>
public sealed class ToneFromAudioBuilderTests
{
    private static RsGearCatalog Catalog => RsGearCatalog.Shared;

    /// <summary>Positional order mirrors <see cref="ToneAudioProfile"/>; the
    /// defaults are a neutral, mid-bright electric so each test varies only the
    /// measurement it is about.</summary>
    private static ToneAudioProfile Profile(
        double crestDb,
        double flatness,
        double harmonicRatio,
        double centroidHz = 1200,
        double low = 0.33,
        double mid = 0.34,
        double high = 0.33,
        double rms = 0.2,
        double sustain = 0.4)
        => new(rms, -6, crestDb, centroidHz, flatness, harmonicRatio, low, mid, high, sustain);

    // A clean, dynamic electric part: wide crest, almost no spectral flatness,
    // energy sitting on the harmonics.
    private static ToneAudioProfile CleanProfile() =>
        Profile(crestDb: 16, flatness: 0.03, harmonicRatio: 0.90,
            centroidHz: 900, low: 0.35, mid: 0.50, high: 0.15);

    // A squashed, spectrally flat, harmonically smeared part.
    private static ToneAudioProfile DrivenProfile() =>
        Profile(crestDb: 5, flatness: 0.30, harmonicRatio: 0.35,
            centroidHz: 1600, low: 0.30, mid: 0.45, high: 0.25);

    // Undriven, unusually bright and top-heavy — an acoustic, a piezo or a DI.
    private static ToneAudioProfile DirectProfile() =>
        Profile(crestDb: 17, flatness: 0.02, harmonicRatio: 0.92,
            centroidHz: 2600, low: 0.25, mid: 0.45, high: 0.30);

    private static ToneSuggestion Build(ToneAudioProfile profile, string key = "auto_tone")
    {
        var suggestion = ToneFromAudioBuilder.Build(profile, Catalog, key, null);
        Assert.NotNull(suggestion);
        return suggestion!;
    }

    private static float Gain(RsTone tone)
    {
        Assert.True(tone.Amp.TryGetKnob("Gain", out var gain),
            "every amp in the archetype table declares a Gain pot");
        return gain;
    }

    // ------------------------------------------------------------- archetypes

    [Fact]
    public void Clean_span_gets_a_clean_amp_low_gain_and_no_drive_pedal()
    {
        var result = Build(CleanProfile());

        Assert.Equal(ToneArchetype.Clean, result.Archetype);
        Assert.Equal("Amp_TW26", result.Tone.Amp.GearKey);
        Assert.Empty(result.Tone.PrePedals);
        Assert.Empty(result.Tone.PostPedals);

        // The clean band owns the bottom third of the Gain pot's travel.
        var gain = Gain(result.Tone);
        Assert.InRange(gain, 1f, 35f);
    }

    [Fact]
    public void Driven_span_gets_a_high_gain_amp_and_a_drive_pedal_in_front()
    {
        var result = Build(DrivenProfile());

        Assert.Equal(ToneArchetype.HighGain, result.Archetype);
        Assert.Equal("Amp_MarshallDSL100H", result.Tone.Amp.GearKey);

        var pedal = Assert.Single(result.Tone.PrePedals);
        Assert.Equal(ToneFromAudioBuilder.DrivePedalKey, pedal.GearKey);
        Assert.Equal(RsGearRole.PrePedal, pedal.Role);

        // The pedal's own controls are set from the span, not left at defaults.
        Assert.True(pedal.TryGetKnob("Gain", out var pedalGain));
        Assert.True(pedal.TryGetKnob("Tone", out var pedalTone));
        Assert.True(Catalog.TryGetKnob(pedal.GearKey, "Gain", out var gainKnob));
        Assert.True(Catalog.TryGetKnob(pedal.GearKey, "Tone", out var toneKnob));
        Assert.NotEqual(gainKnob.DefaultValue, pedalGain);
        Assert.NotEqual(toneKnob.DefaultValue, pedalTone);

        Assert.True(Gain(result.Tone) > 70f, "the high-gain band owns the top of the Gain pot");
    }

    [Fact]
    public void Undriven_bright_top_heavy_span_gets_the_direct_archetype()
    {
        var result = Build(DirectProfile());

        Assert.Equal(ToneArchetype.Direct, result.Archetype);
        Assert.Equal("DI_Amp_TubePre", result.Tone.Amp.GearKey);
        Assert.Equal("Cab_PA600C", result.Tone.Cabinet.GearKey);
        Assert.Empty(result.Tone.PrePedals);

        // The DI preamp has no Presence pot. The mapper must SKIP it, not invent
        // one — a knob key the gear does not declare is what makes the game
        // reject a tone.
        Assert.DoesNotContain(result.Tone.Amp.KnobValues.Keys,
            k => k.EndsWith("_Pres", StringComparison.Ordinal));
    }

    [Fact]
    public void Crunch_band_sits_between_the_clean_and_high_gain_amps()
    {
        var result = Build(Profile(crestDb: 9, flatness: 0.15, harmonicRatio: 0.60));

        Assert.Equal(ToneArchetype.Crunch, result.Archetype);
        Assert.Equal("Amp_MarshallJCM800", result.Tone.Amp.GearKey);
        Assert.Empty(result.Tone.PrePedals);
    }

    // ----------------------------------------------------------- monotonicity

    /// <summary>
    /// The property that makes the feature feel sane: with the spectral SHAPE
    /// held fixed, more drive never means less gain. Both rows sweep the three
    /// measurements Drive folds — crest, flatness, harmonic ratio — monotonically;
    /// the bright/top-heavy row additionally crosses the Direct → Clean archetype
    /// boundary, which is the crossing most likely to invert a threshold table.
    /// </summary>
    [Theory]
    [InlineData(900.0, 0.35, 0.50, 0.15)]   // dark, mid-heavy: never Direct
    [InlineData(2600.0, 0.25, 0.45, 0.30)]  // bright, top-heavy: starts Direct
    public void Gain_never_falls_as_drive_rises(double centroidHz, double low, double mid, double high)
    {
        var previousDrive = -1.0;
        var previousGain = float.MinValue;
        var archetypes = new List<ToneArchetype>();

        for (var i = 0; i <= 40; i++)
        {
            var t = i / 40.0;
            var profile = Profile(
                crestDb: 20 - 18 * t,
                flatness: 0.35 * t,
                harmonicRatio: 0.95 - 0.75 * t,
                centroidHz: centroidHz, low: low, mid: mid, high: high);

            // Guard the sweep itself: if Drive were not rising, the assertion
            // below would be vacuous.
            Assert.True(profile.Drive >= previousDrive,
                $"sweep step {i} lowered Drive ({profile.Drive} < {previousDrive})");
            previousDrive = profile.Drive;

            var result = Build(profile);
            var gain = Gain(result.Tone);
            Assert.True(gain >= previousGain,
                $"step {i} (drive {profile.Drive:0.000}, {result.Archetype}) lowered gain {previousGain} → {gain}");
            previousGain = gain;
            archetypes.Add(result.Archetype);
        }

        // …and the sweep really did move through several archetypes, so the
        // cross-boundary case is exercised rather than assumed.
        Assert.True(archetypes.Distinct().Count() >= 3,
            "sweep should cross at least three archetypes, saw: "
            + string.Join(", ", archetypes.Distinct()));
    }

    // -------------------------------------------------------------- brightness

    [Fact]
    public void Brighter_span_gets_more_treble_and_presence()
    {
        // A crunch-band drive so both spans land on the same amp (which has both
        // a Treble and a Presence pot) and only the centroid differs.
        var dark = Build(Profile(crestDb: 9, flatness: 0.15, harmonicRatio: 0.60, centroidHz: 400));
        var bright = Build(Profile(crestDb: 9, flatness: 0.15, harmonicRatio: 0.60, centroidHz: 3200));

        Assert.Equal(dark.Tone.Amp.GearKey, bright.Tone.Amp.GearKey);

        Assert.True(dark.Tone.Amp.TryGetKnob("Treble", out var darkTreble));
        Assert.True(bright.Tone.Amp.TryGetKnob("Treble", out var brightTreble));
        Assert.True(brightTreble > darkTreble, $"treble {brightTreble} should exceed {darkTreble}");

        Assert.True(dark.Tone.Amp.TryGetKnob("Pres", out var darkPres));
        Assert.True(bright.Tone.Amp.TryGetKnob("Pres", out var brightPres));
        Assert.True(brightPres > darkPres, $"presence {brightPres} should exceed {darkPres}");
    }

    [Fact]
    public void Mic_position_follows_brightness_on_the_same_cabinet_model()
    {
        var dark = Build(Profile(crestDb: 9, flatness: 0.15, harmonicRatio: 0.60, centroidHz: 300));
        var mid = Build(Profile(crestDb: 9, flatness: 0.15, harmonicRatio: 0.60, centroidHz: 1600));
        var bright = Build(Profile(crestDb: 9, flatness: 0.15, harmonicRatio: 0.60, centroidHz: 3400));

        Assert.Equal("Cab_Marshall1960TV_57_OffAxis", dark.Tone.Cabinet.GearKey);
        Assert.Equal("Cab_Marshall1960TV_57_Edge", mid.Tone.Cabinet.GearKey);
        Assert.Equal("Cab_Marshall1960TV_57_Cone", bright.Tone.Cabinet.GearKey);
    }

    [Fact]
    public void Bass_knob_follows_the_low_band()
    {
        var thin = Build(Profile(crestDb: 9, flatness: 0.15, harmonicRatio: 0.60,
            low: 0.10, mid: 0.60, high: 0.30));
        var fat = Build(Profile(crestDb: 9, flatness: 0.15, harmonicRatio: 0.60,
            low: 0.60, mid: 0.20, high: 0.20));

        Assert.True(thin.Tone.Amp.TryGetKnob("Bass", out var thinBass));
        Assert.True(fat.Tone.Amp.TryGetKnob("Bass", out var fatBass));
        Assert.True(fatBass > thinBass, $"bass {fatBass} should exceed {thinBass}");
    }

    // ---------------------------------------------------------- knob validity

    public static TheoryData<double, double, double, double, double, double, double> KnobRangeProfiles() => new()
    {
        //  crest flat  harm  centroid  low   mid   high
        { 18.0, 0.01, 0.95, 300.0, 0.60, 0.30, 0.10 },
        { 16.0, 0.03, 0.90, 900.0, 0.35, 0.50, 0.15 },
        { 17.0, 0.02, 0.92, 2600.0, 0.25, 0.45, 0.30 },
        { 12.0, 0.08, 0.80, 1400.0, 0.33, 0.34, 0.33 },
        { 9.0, 0.15, 0.60, 400.0, 0.20, 0.55, 0.25 },
        { 9.0, 0.15, 0.60, 3400.0, 0.10, 0.30, 0.60 },
        { 5.0, 0.30, 0.35, 1600.0, 0.30, 0.45, 0.25 },
        { 2.0, 0.40, 0.10, 5000.0, 0.05, 0.15, 0.80 }
    };

    [Theory]
    [MemberData(nameof(KnobRangeProfiles))]
    public void Every_knob_set_exists_on_the_chosen_gear_and_is_within_its_catalogue_range(
        double crestDb, double flatness, double harmonicRatio,
        double centroidHz, double low, double mid, double high)
    {
        var result = Build(Profile(crestDb, flatness, harmonicRatio, centroidHz, low, mid, high));

        foreach (var gear in result.Tone.InSignalOrder())
        {
            Assert.True(Catalog.TryGetGear(gear.GearKey, out var data),
                $"{gear.GearKey} is not in the real catalogue");

            foreach (var (fullKey, value) in gear.KnobValues)
            {
                Assert.True(Catalog.TryGetKnobByFullKey(fullKey, out var knob),
                    $"{fullKey} is not a knob the catalogue declares");
                Assert.Contains(data.Knobs ?? [], k => k.Key == fullKey);

                var lo = Math.Min(knob.MinValue, knob.MaxValue);
                var hi = Math.Max(knob.MinValue, knob.MaxValue);
                Assert.InRange(value, lo, hi);
            }
        }
    }

    // -------------------------------------------------------------- round-trip

    [Theory]
    [MemberData(nameof(KnobRangeProfiles))]
    public void Result_round_trips_through_the_writer_and_the_parser(
        double crestDb, double flatness, double harmonicRatio,
        double centroidHz, double low, double mid, double high)
    {
        var built = Build(Profile(crestDb, flatness, harmonicRatio, centroidHz, low, mid, high)).Tone;

        Assert.True(RsToneJsonParser.TryParse(RsToneJsonWriter.Write(built), out var parsed));
        Assert.NotNull(parsed);

        Assert.Equal(built.Key, parsed!.Key);
        Assert.Equal(built.Name, parsed.Name);
        Assert.Equal(built.Amp.GearKey, parsed.Amp.GearKey);
        Assert.Equal(built.Cabinet.GearKey, parsed.Cabinet.GearKey);
        Assert.Equal(built.Amp.KnobValues, parsed.Amp.KnobValues);
        Assert.Equal(built.PrePedals.Count, parsed.PrePedals.Count);
        for (var i = 0; i < built.PrePedals.Count; i++)
        {
            Assert.Equal(built.PrePedals[i].GearKey, parsed.PrePedals[i].GearKey);
            Assert.Equal(built.PrePedals[i].KnobValues, parsed.PrePedals[i].KnobValues);
        }
    }

    // ------------------------------------------------------------------ silence

    [Fact]
    public void Silence_is_refused_rather_than_guessed()
    {
        var silent = Profile(crestDb: 14, flatness: 0.1, harmonicRatio: 0.5,
            rms: ToneAudioProfile.SilenceRms / 10);
        Assert.True(silent.IsSilent);

        Assert.Null(ToneFromAudioBuilder.Build(silent, Catalog, "auto_tone", null));
    }

    [Fact]
    public void A_span_just_above_the_silence_floor_still_produces_a_tone()
    {
        var quiet = Profile(crestDb: 14, flatness: 0.1, harmonicRatio: 0.5,
            rms: ToneAudioProfile.SilenceRms * 1.01);
        Assert.False(quiet.IsSilent);

        Assert.NotNull(ToneFromAudioBuilder.Build(quiet, Catalog, "auto_tone", null));
    }

    // ------------------------------------------------------------- determinism

    [Theory]
    [MemberData(nameof(KnobRangeProfiles))]
    public void Building_twice_gives_an_identical_tone(
        double crestDb, double flatness, double harmonicRatio,
        double centroidHz, double low, double mid, double high)
    {
        var profile = Profile(crestDb, flatness, harmonicRatio, centroidHz, low, mid, high);

        var first = Build(profile);
        var second = Build(profile);

        Assert.Equal(first.Archetype, second.Archetype);
        Assert.Equal(first.Rationale, second.Rationale);
        Assert.Equal(RsToneJsonWriter.Write(first.Tone), RsToneJsonWriter.Write(second.Tone));
    }

    // ------------------------------------------------------------ presentation

    [Fact]
    public void A_blank_name_says_where_the_tone_came_from()
    {
        var result = ToneFromAudioBuilder.Build(CleanProfile(), Catalog, "auto_tone", "   ");
        Assert.NotNull(result);
        Assert.Equal("Clean (from audio)", result!.Tone.Name);
    }

    [Fact]
    public void The_key_is_folded_into_a_valid_rs_tone_key()
    {
        var result = Build(CleanProfile(), "my tone 2!");
        Assert.True(RsToneAuthoring.IsValidKey(result.Tone.Key));
        Assert.Equal("my_tone_2", result.Tone.Key);
    }

    [Fact]
    public void The_rationale_names_the_archetype_and_the_gear()
    {
        var result = Build(DrivenProfile());

        Assert.Contains("High gain", result.Rationale, StringComparison.Ordinal);
        Assert.Contains(result.Tone.Amp.GearKey, result.Rationale, StringComparison.Ordinal);
        Assert.Contains(result.Tone.Cabinet.GearKey, result.Rationale, StringComparison.Ordinal);
        Assert.Contains(ToneFromAudioBuilder.DrivePedalKey, result.Rationale, StringComparison.Ordinal);
        // Honest about what it is.
        Assert.Contains("not a recovered rig", result.Rationale, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ degrade

    [Fact]
    public void An_empty_catalogue_degrades_to_a_structurally_valid_tone_without_throwing()
    {
        var result = ToneFromAudioBuilder.Build(DrivenProfile(), RsGearCatalog.Empty, "auto_tone", null);

        Assert.NotNull(result);
        Assert.Equal(ToneArchetype.HighGain, result!.Archetype);
        Assert.Empty(result.Tone.Amp.KnobValues);   // nothing invented off-catalogue
        Assert.Equal(RsToneJsonParser.DefaultCabinetKey, result.Tone.Cabinet.GearKey);
        Assert.True(RsToneJsonParser.TryParse(RsToneJsonWriter.Write(result.Tone), out _));
    }
}
