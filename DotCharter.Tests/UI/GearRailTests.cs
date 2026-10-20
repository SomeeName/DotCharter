using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tests.UI;

/// <summary>
/// Pure logic behind the pedalboard's gear rail: the category/search filter
/// (<see cref="GearRailFilter"/>), the miniature silhouette pick
/// (<see cref="GearThumbnailMath.KindFor"/>) and the miniature-face layout
/// math (<see cref="GearThumbnailMath"/>). No Avalonia app instance required.
/// </summary>
public class GearRailTests
{
    // ── GearRailFilter ───────────────────────────────────────────────────────

    [Fact]
    public void Matches_all_tab_no_query_accepts_everything()
        => Assert.True(GearRailFilter.Matches("Overdrive", "overdrive",
            DspModuleCategory.Drive, tab: null, query: null));

    [Fact]
    public void Matches_category_tab_filters_by_family()
    {
        Assert.True(GearRailFilter.Matches("Overdrive", "overdrive",
            DspModuleCategory.Drive, DspModuleCategory.Drive, null));
        Assert.False(GearRailFilter.Matches("Delay", "delay",
            DspModuleCategory.Time, DspModuleCategory.Drive, null));
    }

    [Theory]
    [InlineData("over")]   // prefix
    [InlineData("DRIVE")]  // case-insensitive
    [InlineData("erdr")]   // substring
    public void Matches_query_is_case_insensitive_substring_on_name(string query)
        => Assert.True(GearRailFilter.Matches("Overdrive", "overdrive",
            DspModuleCategory.Drive, null, query));

    [Fact]
    public void Matches_query_also_hits_the_catalogue_id()
        => Assert.True(GearRailFilter.Matches("Screamer", "overdrive",
            DspModuleCategory.Drive, null, "overdr"));

    [Fact]
    public void Matches_query_miss_rejects()
        => Assert.False(GearRailFilter.Matches("Overdrive", "overdrive",
            DspModuleCategory.Drive, null, "chorus"));

    [Fact]
    public void Matches_blank_query_is_ignored()
        => Assert.True(GearRailFilter.Matches("Overdrive", "overdrive",
            DspModuleCategory.Drive, null, "   "));

    [Fact]
    public void Matches_tab_and_query_combine()
    {
        Assert.True(GearRailFilter.Matches("Tape Delay", "tapedelay",
            DspModuleCategory.Time, DspModuleCategory.Time, "tape"));
        Assert.False(GearRailFilter.Matches("Tape Delay", "tapedelay",
            DspModuleCategory.Time, DspModuleCategory.Drive, "tape"));
    }

    // ── GearRailFilter.MatchesModelLabel (browse-by-model search) ────────────

    [Fact]
    public void MatchesModelLabel_hits_a_named_model_on_the_tab()
        // "Plexi" isn't in the module name ("Amp") but IS a voicing label.
        => Assert.True(GearRailFilter.MatchesModelLabel(
            DspModuleCategory.Amp, tab: null,
            new[] { "Blackface Twin 85", "Plexi 50", "Plexi 100" }, "plexi"));

    [Fact]
    public void MatchesModelLabel_respects_the_tab()
        => Assert.False(GearRailFilter.MatchesModelLabel(
            DspModuleCategory.Amp, DspModuleCategory.Drive,
            new[] { "Plexi 100" }, "plexi"));

    [Fact]
    public void MatchesModelLabel_blank_query_or_no_labels_never_matches()
    {
        Assert.False(GearRailFilter.MatchesModelLabel(DspModuleCategory.Amp, null, new[] { "Plexi 100" }, "  "));
        Assert.False(GearRailFilter.MatchesModelLabel(DspModuleCategory.Amp, null, null, "plexi"));
    }

    // ── GearRailFilter.KindOf (the browser's STOMP/AMP/CAB/RACK tab row) ─────

    /// <summary>The form tab is derived from the board skin family, never from
    /// the character: a fuzz and a delay are both stomps, and a routing junction
    /// really is a rack blank (visual language §2.4) rather than a pedal.</summary>
    [Theory]
    [InlineData("overdrive", GearRailKind.Stomp)]
    [InlineData("graphiceq", GearRailKind.Stomp)]
    [InlineData("wah", GearRailKind.Stomp)]
    [InlineData("amp", GearRailKind.Amp)]
    [InlineData("namamp", GearRailKind.Amp)]
    [InlineData("cab", GearRailKind.Cab)]
    [InlineData("splitter", GearRailKind.Rack)]
    [InlineData("merger", GearRailKind.Rack)]
    [InlineData("something-native-added-later", GearRailKind.Stomp)]
    public void KindOf_sorts_a_module_onto_its_form_tab(string id, GearRailKind expected)
        => Assert.Equal(expected, GearRailFilter.KindOf(id));

    /// <summary>No form tab selected = every form, which is the browser's
    /// default (the curated order leads with amps and cabs).</summary>
    [Fact]
    public void MatchesKind_null_tab_accepts_every_form()
    {
        Assert.True(GearRailFilter.MatchesKind(null, "amp"));
        Assert.True(GearRailFilter.MatchesKind(null, "overdrive"));
        Assert.True(GearRailFilter.MatchesKind(GearRailKind.Amp, "amp"));
        Assert.False(GearRailFilter.MatchesKind(GearRailKind.Stomp, "amp"));
    }

    // ── GearThumbnailMath.KindFor (silhouette pick) ──────────────────────────

    [Theory]
    [InlineData("amp", GearThumbKind.AmpHead)]
    [InlineData("cab", GearThumbKind.Cabinet)]
    [InlineData("graphiceq", GearThumbKind.GraphicEq)]
    [InlineData("wah", GearThumbKind.Wah)]
    [InlineData("overdrive", GearThumbKind.Stompbox)]
    [InlineData("chorus", GearThumbKind.Stompbox)]
    [InlineData("something-native-added-later", GearThumbKind.Stompbox)]
    public void KindFor_mirrors_the_board_skin_families(string id, GearThumbKind expected)
        => Assert.Equal(expected, GearThumbnailMath.KindFor(id));

    [Fact]
    public void KindFor_promotes_namamp_onto_the_amp_chassis()
        // namamp has no specialized board skin (generic stompbox face) but is
        // Amp-family — the rail miniature still reads as an amp head.
        => Assert.Equal(GearThumbKind.AmpHead, GearThumbnailMath.KindFor("namamp"));

    [Fact]
    public void KindFor_keeps_miku_a_stompbox()
        // miku is Pitch-family → the teal stomp, not a wide chassis.
        => Assert.Equal(GearThumbKind.Stompbox, GearThumbnailMath.KindFor("miku"));

    [Theory]
    [InlineData(GearThumbKind.AmpHead, true)]
    [InlineData(GearThumbKind.Cabinet, true)]
    [InlineData(GearThumbKind.GraphicEq, false)]
    [InlineData(GearThumbKind.Wah, false)]
    [InlineData(GearThumbKind.Stompbox, false)]
    public void IsWide_only_amp_and_cab_span_the_card(GearThumbKind kind, bool expected)
        => Assert.Equal(expected, GearThumbnailMath.IsWide(kind));

    // ── GearThumbnailMath.Compute ────────────────────────────────────────────

    [Theory]
    [InlineData(0, 1)]  // even a knobless module draws one knob
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(10, 4)] // capped
    public void MiniKnobCount_clamps_to_card_scale(int paramCount, int expected)
        => Assert.Equal(expected, GearThumbnailMath.MiniKnobCount(paramCount));

    [Fact]
    public void Compute_stompbox_face_is_narrower_and_centred()
    {
        var l = GearThumbnailMath.Compute(100, 88, 3, isWide: false);

        Assert.Equal(100 * GearThumbnailMath.StompboxWidthFraction, l.FaceWidth, 5);
        Assert.Equal((100 - l.FaceWidth) / 2, l.FaceX, 5);
        Assert.True(l.FaceHeight < 88); // name band reserved below the face
        Assert.Equal(l.FaceHeight, l.NameBandY, 5);
    }

    [Fact]
    public void Compute_amp_head_spans_full_width()
    {
        var l = GearThumbnailMath.Compute(100, 88, 5, isWide: true);

        Assert.Equal(0, l.FaceX, 5);
        Assert.Equal(100, l.FaceWidth, 5);
    }

    [Fact]
    public void Compute_knob_row_is_centred_and_ordered()
    {
        var l = GearThumbnailMath.Compute(120, 90, 3, isWide: false);

        Assert.Equal(3, l.KnobCenterXs.Length);
        // Strictly increasing, symmetric around the face centre.
        Assert.True(l.KnobCenterXs[0] < l.KnobCenterXs[1]);
        Assert.True(l.KnobCenterXs[1] < l.KnobCenterXs[2]);
        var faceCenter = l.FaceX + l.FaceWidth / 2;
        Assert.Equal(faceCenter, l.KnobCenterXs[1], 5);
        Assert.Equal(faceCenter - l.KnobCenterXs[0], l.KnobCenterXs[2] - faceCenter, 5);
    }

    [Fact]
    public void Compute_knob_row_always_fits_the_face()
    {
        foreach (var knobs in new[] { 1, 2, 3, 4 })
        {
            var l = GearThumbnailMath.Compute(100, 88, knobs, isWide: false);
            Assert.True(l.KnobCenterXs.First() - l.KnobRadius >= l.FaceX - 1e-9);
            Assert.True(l.KnobCenterXs.Last() + l.KnobRadius <= l.FaceX + l.FaceWidth + 1e-9);
        }
    }

    [Fact]
    public void Compute_degenerate_bounds_collapse_to_empty()
    {
        var l = GearThumbnailMath.Compute(0, 88, 3, false);
        Assert.Equal(0, l.FaceWidth, 5);
        Assert.Empty(l.KnobCenterXs);
    }

    // ── thumbnail coverage: NO catalogue module may render as a blank plate ──

    [Fact]
    public void KindFor_maps_the_routing_ids_to_the_junction_face()
    {
        Assert.Equal(GearThumbKind.Junction, GearThumbnailMath.KindFor("splitter"));
        Assert.Equal(GearThumbKind.Junction, GearThumbnailMath.KindFor("merger"));
        Assert.True(GearThumbnailMath.IsFanOut("splitter"));
        Assert.False(GearThumbnailMath.IsFanOut("merger"));
        // A junction is NOT a wide chassis — it's the slim patchbay card.
        Assert.False(GearThumbnailMath.IsWide(GearThumbKind.Junction));
    }

    [Fact]
    public void Every_catalogue_module_renders_a_real_thumbnail_face()
    {
        Assert.NotEmpty(DcRigTargetCatalog.ModuleTypeIds);
        var kinds = Enum.GetValues<GearThumbKind>();

        foreach (var id in DcRigTargetCatalog.ModuleTypeIds)
        {
            var kind = GearThumbnailMath.KindFor(id);
            Assert.Contains(kind, kinds); // a DEFINED silhouette, never a stray value

            // …and that silhouette produces a non-empty face at card size, so the
            // gear rail / chain canvas can never draw an empty grey box.
            var layout = GearThumbnailMath.Compute(
                120, 96, paramCount: 3, GearThumbnailMath.IsWide(kind));
            Assert.True(layout.FaceWidth > 0, $"module '{id}' has no thumbnail face");
            Assert.True(layout.FaceHeight > 0, $"module '{id}' has no thumbnail face");
            Assert.NotEmpty(layout.KnobCenterXs);
        }
    }

    [Fact]
    public void Every_catalogue_module_keeps_a_zero_param_thumbnail_face()
    {
        // A routing module has (almost) no params — the face must still draw.
        foreach (var id in DcRigTargetCatalog.ModuleTypeIds)
        {
            var kind = GearThumbnailMath.KindFor(id);
            var layout = GearThumbnailMath.Compute(
                120, 96, paramCount: 0, GearThumbnailMath.IsWide(kind));
            Assert.True(layout.FaceWidth > 0);
            Assert.Single(layout.KnobCenterXs); // MiniKnobCount floors at one
        }
    }
}
