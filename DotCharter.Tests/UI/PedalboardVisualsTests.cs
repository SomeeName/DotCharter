using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tests.UI;

/// <summary>
/// Pure logic behind the VST-style rig: module→category/colour/knob-style/skin
/// mapping (<see cref="DspModuleVisuals"/>), knob rotation geometry + the 0–10
/// readout mapping (<see cref="KnobMath"/>), the drag-reorder drop math
/// (<see cref="PedalDragMath"/>). No Avalonia app instance required.
/// </summary>
public class PedalboardVisualsTests
{
    [Theory]
    [InlineData("gain", DspModuleCategory.Dynamics)]
    [InlineData("gate", DspModuleCategory.Dynamics)]
    [InlineData("compressor", DspModuleCategory.Dynamics)]
    [InlineData("sustainer", DspModuleCategory.Dynamics)]
    [InlineData("noisereducer", DspModuleCategory.Dynamics)]
    [InlineData("eq", DspModuleCategory.Eq)]
    [InlineData("graphiceq", DspModuleCategory.Eq)]
    [InlineData("wah", DspModuleCategory.Filter)]
    [InlineData("envfilter", DspModuleCategory.Filter)]
    [InlineData("formant", DspModuleCategory.Filter)]
    [InlineData("acousticsim", DspModuleCategory.Filter)]
    [InlineData("overdrive", DspModuleCategory.Drive)]
    [InlineData("distortion", DspModuleCategory.Drive)]
    [InlineData("fuzz", DspModuleCategory.Drive)]
    [InlineData("bitcrusher", DspModuleCategory.Drive)]
    [InlineData("klon", DspModuleCategory.Drive)]
    [InlineData("bigmuff", DspModuleCategory.Drive)]
    [InlineData("amp", DspModuleCategory.Amp)]
    [InlineData("cab", DspModuleCategory.Cab)]
    [InlineData("chorus", DspModuleCategory.Modulation)]
    [InlineData("flanger", DspModuleCategory.Modulation)]
    [InlineData("tremolo", DspModuleCategory.Modulation)]
    [InlineData("phaser", DspModuleCategory.Modulation)]
    [InlineData("rotary", DspModuleCategory.Modulation)]
    [InlineData("vibrato", DspModuleCategory.Modulation)]
    [InlineData("univibe", DspModuleCategory.Modulation)]
    [InlineData("harmtremolo", DspModuleCategory.Modulation)]
    [InlineData("ringmod", DspModuleCategory.Modulation)]
    [InlineData("delay", DspModuleCategory.Time)]
    [InlineData("reverb", DspModuleCategory.Time)]
    [InlineData("tapedelay", DspModuleCategory.Time)]
    [InlineData("platereverb", DspModuleCategory.Time)]
    [InlineData("springreverb", DspModuleCategory.Time)]
    [InlineData("shimmer", DspModuleCategory.Time)]
    [InlineData("octaver", DspModuleCategory.Pitch)]
    [InlineData("harmonizer", DspModuleCategory.Pitch)]
    [InlineData("doubler", DspModuleCategory.Pitch)]
    [InlineData("something-native-added-later", DspModuleCategory.Utility)]
    public void Categorize_maps_known_modules_and_falls_back(string id, DspModuleCategory expected)
        => Assert.Equal(expected, DspModuleVisuals.Categorize(id));

    [Theory]
    // A head's controls go through an anodised BRASS panel; a cab's mic trims
    // through plain chassis steel. Same production line, different metal.
    [InlineData(DspModuleCategory.Amp, KnobStyle.Brass)]
    [InlineData(DspModuleCategory.Cab, KnobStyle.ChickenHead)]
    [InlineData(DspModuleCategory.Drive, KnobStyle.Skirted)]
    [InlineData(DspModuleCategory.Modulation, KnobStyle.Cream)]
    [InlineData(DspModuleCategory.Dynamics, KnobStyle.KnurledRubber)]
    [InlineData(DspModuleCategory.Time, KnobStyle.PointerBar)]
    [InlineData(DspModuleCategory.Pitch, KnobStyle.ChipPointer)]
    [InlineData(DspModuleCategory.Eq, KnobStyle.TopHat)]
    [InlineData(DspModuleCategory.Filter, KnobStyle.TopHat)]
    [InlineData(DspModuleCategory.Routing, KnobStyle.Aluminium)]
    [InlineData(DspModuleCategory.Utility, KnobStyle.Aluminium)]
    public void KnobStyleFor_maps_categories_to_hardware_families(DspModuleCategory c, KnobStyle expected)
        => Assert.Equal(expected, DspModuleVisuals.KnobStyleFor(c));

    [Theory]
    [InlineData(DspModuleCategory.Drive, ChassisFinish.Sparkle)]
    [InlineData(DspModuleCategory.Dynamics, ChassisFinish.Hammer)]
    [InlineData(DspModuleCategory.Time, ChassisFinish.Anodized)]
    [InlineData(DspModuleCategory.Eq, ChassisFinish.Anodized)]
    [InlineData(DspModuleCategory.Modulation, ChassisFinish.Enamel)]
    [InlineData(DspModuleCategory.Pitch, ChassisFinish.Carbon)]
    [InlineData(DspModuleCategory.Filter, ChassisFinish.Cast)]
    [InlineData(DspModuleCategory.Utility, ChassisFinish.Cast)]
    public void ChassisFinishFor_maps_categories_to_a_finish(DspModuleCategory c, ChassisFinish expected)
        => Assert.Equal(expected, DspModuleVisuals.ChassisFinishFor(c));

    [Fact]
    public void ChassisFinishFor_module_id_routes_through_category()
    {
        Assert.Equal(ChassisFinish.Sparkle, DspModuleVisuals.ChassisFinishFor("overdrive"));
        Assert.Equal(ChassisFinish.Hammer, DspModuleVisuals.ChassisFinishFor("compressor"));
        Assert.Equal(ChassisFinish.Anodized, DspModuleVisuals.ChassisFinishFor("delay"));
        Assert.Equal(ChassisFinish.Carbon, DspModuleVisuals.ChassisFinishFor("octaver"));
        Assert.Equal(ChassisFinish.Cast, DspModuleVisuals.ChassisFinishFor("brand-new-module"));
    }

    /// <summary>The owner's brief: no two families share the same chassis finish
    /// AND the same knob hardware — that pairing is what makes a family nameable
    /// at a glance. Checks the families that render the die-cast stompbox
    /// enclosure (Amp/Cab wear tolex, Routing is a patchbay card — they don't).</summary>
    [Fact]
    public void Stompbox_families_have_distinct_finish_plus_knob_pairs()
    {
        DspModuleCategory[] enclosureFamilies =
        [
            DspModuleCategory.Dynamics, DspModuleCategory.Eq, DspModuleCategory.Filter,
            DspModuleCategory.Drive, DspModuleCategory.Modulation, DspModuleCategory.Time,
            DspModuleCategory.Pitch, DspModuleCategory.Utility
        ];

        var seen = new Dictionary<(ChassisFinish, KnobStyle), DspModuleCategory>();
        foreach (var c in enclosureFamilies)
        {
            var pair = (DspModuleVisuals.ChassisFinishFor(c), DspModuleVisuals.KnobStyleFor(c));
            Assert.False(seen.TryGetValue(pair, out var other),
                $"{c} and {other} share finish+knob {pair} — each family needs its own hardware read");
            seen[pair] = c;
        }
    }

    [Fact]
    public void KnobStyleFor_module_id_routes_through_category()
    {
        Assert.Equal(KnobStyle.Skirted, DspModuleVisuals.KnobStyleFor("fuzz"));
        Assert.Equal(KnobStyle.Brass, DspModuleVisuals.KnobStyleFor("amp"));
        Assert.Equal(KnobStyle.ChickenHead, DspModuleVisuals.KnobStyleFor("cab"));
        Assert.Equal(KnobStyle.Cream, DspModuleVisuals.KnobStyleFor("phaser"));
        Assert.Equal(KnobStyle.Aluminium, DspModuleVisuals.KnobStyleFor("whatever-new"));
    }

    [Theory]
    [InlineData("amp", true)]
    [InlineData("cab", false)] // the cab has its own speaker-grille skin since 3.6
    [InlineData("overdrive", false)]
    [InlineData("delay", false)]
    [InlineData("acousticsim", false)] // cab-ish sim, but it is a stompbox
    [InlineData("unknown", false)]
    public void IsAmpHead_only_true_for_the_amp(string id, bool expected)
        => Assert.Equal(expected, DspModuleVisuals.IsAmpHead(id));

    [Theory]
    [InlineData("amp", PedalSkin.AmpHead)]
    [InlineData("namamp", PedalSkin.NeuralAmp)] // its own head: the CAPTURE names the face
    [InlineData("cab", PedalSkin.Cabinet)]
    [InlineData("graphiceq", PedalSkin.GraphicEq)]
    [InlineData("wah", PedalSkin.Wah)]
    [InlineData("overdrive", PedalSkin.Stompbox)]
    [InlineData("delay", PedalSkin.Stompbox)]
    [InlineData("splitter", PedalSkin.Junction)] // routing = a compact junction card
    [InlineData("merger", PedalSkin.Junction)]   // …never a portrait stompbox
    [InlineData("some-future-module", PedalSkin.Stompbox)] // generic fallback stays
    public void SkinFor_specializes_cab_eq_wah_and_falls_back_to_stompbox(string id, PedalSkin expected)
        => Assert.Equal(expected, DspModuleVisuals.SkinFor(id));

    /// <summary>The wide-chassis predicate is the ONE seam the board's lift-scale
    /// cap, the gear-rail ghost gap and the tolex body brush all read. It used to
    /// be spelled inline in each of them.</summary>
    [Theory]
    [InlineData("amp", true)]
    [InlineData("namamp", true)] // a head is a head: same tolex box, same lift cap
    [InlineData("cab", true)]
    [InlineData("overdrive", false)]
    [InlineData("wah", false)]
    [InlineData("graphiceq", false)]
    [InlineData("splitter", false)]
    [InlineData("some-future-module", false)]
    public void IsWideChassis_covers_the_letterbox_family_only(string id, bool expected)
        => Assert.Equal(expected, DspModuleVisuals.IsWideChassis(id));

    [Fact]
    public void Categories_have_distinct_accent_colours()
    {
        var cats = Enum.GetValues<DspModuleCategory>();
        var seen = new HashSet<uint>();
        foreach (var c in cats)
        {
            var col = DspModuleVisuals.AccentColor(c);
            var packed = (uint)(col.R << 16 | col.G << 8 | col.B);
            Assert.True(seen.Add(packed), $"duplicate accent for {c}");
        }
    }

    /// <summary>The mapping gate: adding module #38 to the native catalogue
    /// (mirrored by <see cref="DcRigTargetCatalog"/>) without a deliberate
    /// visual mapping fails here instead of shipping a grey fallback pedal.</summary>
    [Fact]
    public void Every_catalogue_module_has_a_deliberate_visual_mapping()
    {
        Assert.NotEmpty(DcRigTargetCatalog.ModuleTypeIds);
        foreach (var id in DcRigTargetCatalog.ModuleTypeIds)
        {
            Assert.True(DspModuleVisuals.HasVisualMapping(id),
                $"module '{id}' has no visual mapping — it would render as the grey Utility fallback");
            Assert.NotEqual(DspModuleCategory.Utility, DspModuleVisuals.Categorize(id));
        }
    }

    /// <summary>
    /// The gate above only sweeps the MANAGED mirror (<see cref="DcRigTargetCatalog"/>),
    /// and <c>DcRigTargetCatalogTests</c> only checks managed→native — so a module
    /// added to the native factory but NOT to the mirror slips past BOTH: the gear
    /// rail (which is built from the LIVE native catalogue, not the mirror) would
    /// show it as a grey Utility pedal and every test would still be green. Close
    /// the loop natively→visuals. Skips cleanly without the DLL, like its sibling.
    /// </summary>
    [Fact]
    public void Every_NATIVE_module_has_a_visual_mapping_and_a_mirror_entry()
    {
        var native = TryLoadNativeCatalog();
        Assert.SkipWhen(native.Count == 0,
            "native DSP ABI (juce_sharp.dll) not loadable in this test environment");

        var mirror = DcRigTargetCatalog.ModuleTypeIds.ToHashSet(StringComparer.Ordinal);
        var gaps = new List<string>();
        foreach (var module in native)
        {
            if (!DspModuleVisuals.HasVisualMapping(module.Id))
                gaps.Add($"'{module.Id}' has no DspModuleVisuals mapping — it would render as the grey Utility fallback.");
            if (!mirror.Contains(module.Id))
                gaps.Add($"'{module.Id}' is missing from DcRigTargetCatalog — the visuals coverage gate never sees it.");
        }

        Assert.True(gaps.Count == 0,
            $"Native modules the UI cannot dress ({gaps.Count}):\n  " + string.Join("\n  ", gaps));
    }

    private static IReadOnlyList<DotCharter.Tone.Audio.Dsp.DspModuleType> TryLoadNativeCatalog()
    {
        try
        {
            var svc = new DotCharter.Tone.Audio.Dsp.JuceDspChainService();
            svc.LoadCatalog();
            return svc.Catalog;
        }
        catch
        {
            return [];
        }
    }

    [Fact]
    public void Catalogue_modules_have_pairwise_distinct_accents()
    {
        var seen = new Dictionary<uint, string>();
        foreach (var id in DcRigTargetCatalog.ModuleTypeIds)
        {
            var col = DspModuleVisuals.AccentColor(id);
            var packed = (uint)(col.R << 16 | col.G << 8 | col.B);
            Assert.False(seen.TryGetValue(packed, out var other),
                $"'{id}' and '{other}' share accent #{packed:X6} — every pedal needs its own");
            seen[packed] = id;
        }
    }

    // ── KnobMath ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.0, 0.0, 1.0, 0.0)]
    [InlineData(0.5, 0.0, 1.0, 0.5)]
    [InlineData(1.0, 0.0, 1.0, 1.0)]
    [InlineData(-24.0, -24.0, 24.0, 0.0)]
    [InlineData(0.0, -24.0, 24.0, 0.5)]
    [InlineData(24.0, -24.0, 24.0, 1.0)]
    public void FractionFromValue_normalizes(double v, double min, double max, double expected)
        => Assert.Equal(expected, KnobMath.FractionFromValue(v, min, max), 5);

    [Fact]
    public void FractionFromValue_clamps_out_of_range()
    {
        Assert.Equal(0.0, KnobMath.FractionFromValue(-5, 0, 1), 5);
        Assert.Equal(1.0, KnobMath.FractionFromValue(5, 0, 1), 5);
    }

    [Fact]
    public void FractionFromValue_degenerate_range_is_zero()
        => Assert.Equal(0.0, KnobMath.FractionFromValue(3, 5, 5), 5);

    [Fact]
    public void Fraction_and_value_roundtrip()
    {
        var v = KnobMath.ValueFromFraction(0.25, -24, 24);
        Assert.Equal(-12.0, v, 5);
        Assert.Equal(0.25, KnobMath.FractionFromValue(v, -24, 24), 5);
    }

    [Theory]
    [InlineData(0.0, -135.0)]  // min → 7 o'clock
    [InlineData(0.5, 0.0)]     // mid → straight up
    [InlineData(1.0, 135.0)]   // max → 5 o'clock
    public void AngleForFraction_spans_270_degrees(double f, double expected)
        => Assert.Equal(expected, KnobMath.AngleDegreesForFraction(f), 5);

    [Fact]
    public void IndicatorVector_points_up_at_midscale()
    {
        var (dx, dy) = KnobMath.IndicatorVector(0.0);
        Assert.Equal(0.0, dx, 5);
        Assert.Equal(-1.0, dy, 5); // straight up (screen y grows downward)
    }

    [Fact]
    public void ApplyVerticalDrag_up_increases_down_decreases()
    {
        // 80px up over a 160px full-range span == +0.5 fraction.
        var up = KnobMath.ApplyVerticalDrag(0.5, deltaY: -80, min: 0, max: 1, pixelsForFullRange: 160);
        Assert.Equal(1.0, up, 5);

        var down = KnobMath.ApplyVerticalDrag(0.5, deltaY: 80, min: 0, max: 1, pixelsForFullRange: 160);
        Assert.Equal(0.0, down, 5);
    }

    [Fact]
    public void ApplyVerticalDrag_clamps_to_range()
    {
        var v = KnobMath.ApplyVerticalDrag(0.9, deltaY: -1000, min: 0, max: 1, pixelsForFullRange: 160);
        Assert.Equal(1.0, v, 5);
    }

    [Fact]
    public void ApplyVerticalDrag_zero_sensitivity_is_noop()
        => Assert.Equal(0.5, KnobMath.ApplyVerticalDrag(0.5, -100, 0, 1, 0), 5);

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.5, 5.0)]
    [InlineData(0.65, 6.5)]
    [InlineData(1.0, 10.0)]
    public void DisplayValue_ten_scales_unit_range_params(double value, double expected)
        => Assert.Equal(expected, KnobMath.DisplayValue(value, 0, 1, tenScale: true), 5);

    [Fact]
    public void DisplayValue_leaves_non_unit_ranges_alone()
    {
        // A dB param keeps its real value even when ten-scale display is on.
        Assert.Equal(-12.0, KnobMath.DisplayValue(-12, -24, 24, tenScale: true), 5);
        // And a unit range without the option stays 0..1.
        Assert.Equal(0.65, KnobMath.DisplayValue(0.65, 0, 1, tenScale: false), 5);
    }

    [Theory]
    [InlineData(7.0, 0.7)]   // 0–10 dial entry → 0..1 real
    [InlineData(0.0, 0.0)]
    [InlineData(10.0, 1.0)]
    public void ValueFromDisplay_inverts_ten_scale(double display, double expected)
        => Assert.Equal(expected, KnobMath.ValueFromDisplay(display, 0, 1, tenScale: true), 5);

    [Fact]
    public void ValueFromDisplay_roundtrips_with_DisplayValue()
    {
        var display = KnobMath.DisplayValue(0.42, 0, 1, tenScale: true);
        Assert.Equal(0.42, KnobMath.ValueFromDisplay(display, 0, 1, tenScale: true), 5);
        // Non-ten-scale passes through untouched.
        Assert.Equal(-6.0, KnobMath.ValueFromDisplay(-6, -24, 24, tenScale: true), 5);
    }

    [Fact]
    public void TryParseEntry_parses_and_clamps_display_units()
    {
        // "7" on a ten-scale unit param → 0.7 real.
        Assert.True(KnobMath.TryParseEntry("7", 0, 1, tenScale: true, out var v));
        Assert.Equal(0.7, v, 5);

        // A dB param takes real units directly and clamps to range.
        Assert.True(KnobMath.TryParseEntry("40", -24, 24, tenScale: false, out var hi));
        Assert.Equal(24.0, hi, 5);
        Assert.True(KnobMath.TryParseEntry("-99", -24, 24, tenScale: false, out var lo));
        Assert.Equal(-24.0, lo, 5);
    }

    [Fact]
    public void TryParseEntry_clamps_ten_scale_over_range()
    {
        // "15" reads as 1.5 on the 0–10 dial → clamps to the unit max.
        Assert.True(KnobMath.TryParseEntry("15", 0, 1, tenScale: true, out var v));
        Assert.Equal(1.0, v, 5);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("--")]
    public void TryParseEntry_rejects_blank_or_nonnumeric(string? text)
    {
        Assert.False(KnobMath.TryParseEntry(text, 0, 1, tenScale: true, out var v));
        Assert.Equal(0.0, v, 5);
    }

    [Fact]
    public void TryParseEntry_trims_and_accepts_invariant_decimal()
    {
        Assert.True(KnobMath.TryParseEntry("  3.5 ", 0, 10, tenScale: false, out var v));
        Assert.Equal(3.5, v, 5);
    }

    // ── Knob DIAL GEOMETRY (the arc must stay inside its own cell) ───────────
    //
    // Knobs are laid out edge-to-edge (WrapPanel ItemWidth=72 / a 2 px-spaced
    // StackPanel), so anything a knob paints outside its bounds lands on its
    // NEIGHBOUR. The old sizing (min(w-6, h-40, 68)) budgeted for the DIAL only
    // and ignored the value arc + its 10 px glow pen, so every 72x108 knob
    // painted ~8 px over each neighbour and the caption sat under the arc.

    /// <summary>The real pedal knob cell. The arc's outer reach
    /// (radius + arc offset + half the glow pen) must fit the half-width.</summary>
    [Theory]
    [InlineData(72, 108)]  // the PedalKnobTemplate cell
    [InlineData(74, 104)]  // the RotaryKnob ctor default
    [InlineData(60, 90)]
    [InlineData(120, 160)]
    [InlineData(40, 60)]
    public void DialDiameter_keeps_the_value_arc_inside_the_cell(double w, double h)
    {
        var dial = KnobMath.DialDiameter(w, h);
        var outerReach = dial / 2.0 + KnobMath.ArcClearance;

        Assert.True(outerReach <= w / 2.0 + 1e-9,
            $"the arc reaches {outerReach:0.##} px from the centre but the cell only has {w / 2.0:0.##} px — it paints on the neighbouring knob");
        Assert.True(KnobMath.DialCenterY(dial) - outerReach >= -1e-9,
            "the arc pokes out of the TOP of the cell");
    }

    [Theory]
    [InlineData(72, 108)]
    [InlineData(74, 104)]
    [InlineData(120, 160)]
    public void TextTop_sits_below_the_arc_and_leaves_room_for_both_lines(double w, double h)
    {
        var dial = KnobMath.DialDiameter(w, h);
        var arcBottom = KnobMath.DialCenterY(dial) + dial / 2.0 + KnobMath.ArcClearance;

        Assert.True(KnobMath.TextTop(dial) >= arcBottom - 1e-9,
            "the caption would be drawn under the value arc");
        Assert.True(KnobMath.TextTop(dial) + KnobMath.TextReserve <= h + 1e-9,
            "the caption + readout run off the bottom of the cell");
    }

    [Fact]
    public void DialDiameter_never_collapses_or_exceeds_the_cap()
    {
        Assert.Equal(12.0, KnobMath.DialDiameter(1, 1), 5);        // degenerate → floor
        Assert.Equal(12.0, KnobMath.DialDiameter(0, 0), 5);
        Assert.Equal(KnobMath.MaxDial, KnobMath.DialDiameter(400, 400), 5); // capped
    }

    // ── PedalDragMath ────────────────────────────────────────────────────────

    [Fact]
    public void ComputeDropIndex_picks_nearest_slot()
    {
        var centers = new[] { 40.0, 120.0, 200.0, 280.0 };
        Assert.Equal(0, PedalDragMath.ComputeDropIndex(10, centers));
        Assert.Equal(1, PedalDragMath.ComputeDropIndex(115, centers));
        Assert.Equal(2, PedalDragMath.ComputeDropIndex(210, centers));
        Assert.Equal(3, PedalDragMath.ComputeDropIndex(999, centers));
    }

    [Fact]
    public void ComputeDropIndex_ties_go_left()
    {
        var centers = new[] { 0.0, 100.0 };
        Assert.Equal(0, PedalDragMath.ComputeDropIndex(50, centers));
    }

    [Fact]
    public void ComputeDropIndex_empty_returns_minus_one()
        => Assert.Equal(-1, PedalDragMath.ComputeDropIndex(50, Array.Empty<double>()));

    // ── ComputeInsertIndex (rail→board drop) ─────────────────────────────────

    [Fact]
    public void ComputeInsertIndex_yields_one_of_n_plus_one_gaps()
    {
        var centers = new[] { 40.0, 120.0, 200.0 };
        Assert.Equal(0, PedalDragMath.ComputeInsertIndex(10, centers));   // before first
        Assert.Equal(1, PedalDragMath.ComputeInsertIndex(80, centers));   // between 0 and 1
        Assert.Equal(2, PedalDragMath.ComputeInsertIndex(150, centers));  // between 1 and 2
        Assert.Equal(3, PedalDragMath.ComputeInsertIndex(999, centers));  // after last
    }

    [Fact]
    public void ComputeInsertIndex_on_a_centre_inserts_before_it()
        => Assert.Equal(1, PedalDragMath.ComputeInsertIndex(120, new[] { 40.0, 120.0, 200.0 }));

    [Fact]
    public void ComputeInsertIndex_empty_board_is_slot_zero()
        => Assert.Equal(0, PedalDragMath.ComputeInsertIndex(50, Array.Empty<double>()));

    // ── Amp/cab face badge (model / voicing readout) ─────────────────────────

    [Theory]
    [InlineData("amp", "voicing")]
    [InlineData("cab", "model")]
    [InlineData("overdrive", null)]
    [InlineData("namamp", null)] // NAM amp exposes no voicing knob — no badge
    [InlineData("unknown", null)]
    public void BadgeParamId_only_amp_and_cab_carry_a_badge(string id, string? expected)
        => Assert.Equal(expected, DspModuleVisuals.BadgeParamId(id));

    [Theory]
    [InlineData("amp", 0f, "Pedalboard_AmpVoicing_Clean")]
    [InlineData("amp", 1f, "Pedalboard_AmpVoicing_Crunch")]
    [InlineData("amp", 2f, "Pedalboard_AmpVoicing_Lead")]
    [InlineData("amp", 3f, "Pedalboard_AmpVoicing_Chime")]      // ABI v23 voicings 3–6
    [InlineData("amp", 4f, "Pedalboard_AmpVoicing_Rectifier")]
    [InlineData("amp", 5f, "Pedalboard_AmpVoicing_Jazz")]
    [InlineData("amp", 6f, "Pedalboard_AmpVoicing_Brown")]
    [InlineData("amp", 1.4f, "Pedalboard_AmpVoicing_Crunch")] // rounds
    [InlineData("cab", 0f, "Pedalboard_CabModel_1x12")]
    [InlineData("cab", 1f, "Pedalboard_CabModel_2x12")]
    [InlineData("cab", 2f, "Pedalboard_CabModel_4x12")]
    [InlineData("cab", 3f, "Pedalboard_CabModel_Vintage")]
    public void BadgeResourceKey_maps_native_int_semantics(string id, float value, string expected)
        => Assert.Equal(expected, DspModuleVisuals.BadgeResourceKey(id, value));

    [Theory]
    [InlineData("amp", 9f)]       // out of the known range
    [InlineData("cab", -1f)]
    [InlineData("overdrive", 0f)] // no badge module
    public void BadgeResourceKey_never_invents_a_label(string id, float value)
        => Assert.Null(DspModuleVisuals.BadgeResourceKey(id, value));
}
