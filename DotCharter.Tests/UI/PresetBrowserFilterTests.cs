using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tests.UI;

/// <summary>
/// Pure category/search filtering behind the tone-preset browser
/// (<see cref="PresetBrowserFilter"/>). No Avalonia app instance required.
/// </summary>
public sealed class PresetBrowserFilterTests
{
    private static DspTonePreset P(string id, string name, DspToneCategory cat, DspToneOrigin origin, string desc = "")
        => new(id, name, cat, desc, new DcRigPreset([]), origin);

    private static IReadOnlyList<DspTonePreset> Catalog() =>
    [
        P("f-clean", "Glass Clean", DspToneCategory.Clean, DspToneOrigin.BuiltIn, "sparkling blackface"),
        P("f-crunch", "Edge Crunch", DspToneCategory.Crunch, DspToneOrigin.BuiltIn),
        P("f-metal", "Modern Metal", DspToneCategory.HighGain, DspToneOrigin.BuiltIn),
        P("u-clean", "My Clean", DspToneCategory.Clean, DspToneOrigin.User),
        P("u-lead", "My Lead", DspToneCategory.Lead, DspToneOrigin.User, "creamy solo"),
    ];

    [Fact]
    public void All_returns_everything_in_order()
    {
        var r = PresetBrowserFilter.Filter(Catalog(), PresetFilterKind.All, default);
        Assert.Equal(new[] { "f-clean", "f-crunch", "f-metal", "u-clean", "u-lead" },
            r.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void Category_keeps_only_that_category_across_factory_and_user()
    {
        var r = PresetBrowserFilter.Filter(Catalog(), PresetFilterKind.Category, DspToneCategory.Clean);
        Assert.Equal(new[] { "f-clean", "u-clean" }, r.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void Category_with_no_matches_is_empty()
    {
        var r = PresetBrowserFilter.Filter(Catalog(), PresetFilterKind.Category, DspToneCategory.Bass);
        Assert.Empty(r);
    }

    [Fact]
    public void User_returns_only_user_presets()
    {
        var r = PresetBrowserFilter.Filter(Catalog(), PresetFilterKind.User, default);
        Assert.Equal(new[] { "u-clean", "u-lead" }, r.Select(p => p.Id).ToArray());
        Assert.All(r, p => Assert.True(p.IsUser));
    }

    [Fact]
    public void Search_matches_name_case_insensitively()
    {
        var r = PresetBrowserFilter.Filter(Catalog(), PresetFilterKind.All, default, "clean");
        Assert.Equal(new[] { "f-clean", "u-clean" }, r.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void Search_matches_description()
    {
        var r = PresetBrowserFilter.Filter(Catalog(), PresetFilterKind.All, default, "solo");
        Assert.Equal(new[] { "u-lead" }, r.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void Search_combines_with_category()
    {
        var r = PresetBrowserFilter.Filter(Catalog(), PresetFilterKind.Category, DspToneCategory.Clean, "my");
        Assert.Equal(new[] { "u-clean" }, r.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void Empty_catalog_returns_empty()
        => Assert.Empty(PresetBrowserFilter.Filter([], PresetFilterKind.All, default));

    [Fact]
    public void Blank_search_is_ignored()
    {
        var r = PresetBrowserFilter.Filter(Catalog(), PresetFilterKind.All, default, "   ");
        Assert.Equal(5, r.Count);
    }

    // ── the capture/legacy split (0.37) ──────────────────────────────────────

    private static DspTonePreset Nam(string id, string name, DspToneCategory cat)
        => new(id, name, cat, "", new DcRigPreset([
            new DcRigModule("namamp", new Dictionary<string, float>())
        ]), DspToneOrigin.BuiltIn);

    /// <summary>A catalogue shaped like the real one: two paired tones (an
    /// algorithmic original plus its `nam-` twin), one factory tone with NO twin,
    /// and a user tone.</summary>
    private static IReadOnlyList<DspTonePreset> PairedCatalog() =>
    [
        Nam("nam-f-clean", "Glass Clean", DspToneCategory.Clean),
        Nam("nam-f-crunch", "Edge Crunch", DspToneCategory.Crunch),
        P("f-clean", "Glass Clean (Legacy)", DspToneCategory.Clean, DspToneOrigin.BuiltIn),
        P("f-crunch", "Edge Crunch (Legacy)", DspToneCategory.Crunch, DspToneOrigin.BuiltIn),
        P("f-acoustic", "Natural Acoustic", DspToneCategory.Acoustic, DspToneOrigin.BuiltIn),
        P("u-clean", "My Clean", DspToneCategory.Clean, DspToneOrigin.User),
    ];

    [Fact]
    public void ASupersededOriginalLeavesAllAndItsCategory_ForTheLegacyShelf()
    {
        var r = PresetBrowserFilter.Filter(PairedCatalog(), PresetFilterKind.All, default);
        Assert.Equal(new[] { "nam-f-clean", "nam-f-crunch", "f-acoustic", "u-clean" },
            r.Select(p => p.Id).ToArray());
    }

    /// <summary>
    ///     The originals are MOVED, not removed and not hidden — everything that
    ///     left All/its category is reachable on the Legacy shelf, and the two
    ///     slices together account for the whole catalogue exactly once.
    /// </summary>
    [Fact]
    public void TheLegacyShelfHoldsExactlyWhatLeftTheOtherTabs()
    {
        var all = PairedCatalog();
        var legacy = PresetBrowserFilter.Filter(all, PresetFilterKind.Legacy, default);
        Assert.Equal(new[] { "f-clean", "f-crunch" }, legacy.Select(p => p.Id).ToArray());

        var mainShelf = PresetBrowserFilter.Filter(all, PresetFilterKind.All, default);
        Assert.Empty(mainShelf.Select(p => p.Id).Intersect(legacy.Select(p => p.Id)));
        Assert.Equal(
            all.Select(p => p.Id).OrderBy(x => x, StringComparer.Ordinal),
            mainShelf.Concat(legacy).Select(p => p.Id).OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>
    ///     The load-bearing half. Moving is keyed on a twin ACTUALLY EXISTING,
    ///     not on the name or on being a factory preset — so a rig with no
    ///     capture version (the acoustic sims, the vocal-synth rigs, anything a
    ///     player saved) stays exactly where it always was.
    /// </summary>
    [Fact]
    public void APresetWithNoCaptureTwin_NeverMovesToTheLegacyShelf()
    {
        var ids = PairedCatalog().Select(p => p.Id).ToHashSet(StringComparer.Ordinal);

        Assert.False(PresetBrowserFilter.IsSupersededByACapture(
            PairedCatalog().Single(p => p.Id == "f-acoustic"), ids));
        Assert.False(PresetBrowserFilter.IsSupersededByACapture(
            PairedCatalog().Single(p => p.Id == "u-clean"), ids));
        // ...and a twin never supersedes itself, whatever it is named.
        Assert.False(PresetBrowserFilter.IsSupersededByACapture(
            PairedCatalog().Single(p => p.Id == "nam-f-clean"), ids));

        Assert.True(PresetBrowserFilter.IsSupersededByACapture(
            PairedCatalog().Single(p => p.Id == "f-clean"), ids));

        // A user tone is never swept onto the Legacy shelf.
        var legacy = PresetBrowserFilter.Filter(PairedCatalog(), PresetFilterKind.Legacy, default);
        Assert.DoesNotContain(legacy, p => p.IsUser);
    }

    [Fact]
    public void TheLegacyShelfComposesWithSearch()
    {
        var legacy = PresetBrowserFilter.Filter(
            PairedCatalog(), PresetFilterKind.Legacy, default, "Glass Clean");
        Assert.Equal(new[] { "f-clean" }, legacy.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void CategoryAndSearchOnTheMainShelf_NeverSurfaceALegacyTone()
    {
        var clean = PresetBrowserFilter.Filter(PairedCatalog(), PresetFilterKind.Category, DspToneCategory.Clean);
        Assert.Equal(new[] { "nam-f-clean", "u-clean" }, clean.Select(p => p.Id).ToArray());

        // Searching must not resurrect a moved tone — otherwise typing its own
        // name shows the duplicate the shelf exists to separate.
        var searched = PresetBrowserFilter.Filter(PairedCatalog(), PresetFilterKind.All, default, "Glass Clean");
        Assert.Equal(new[] { "nam-f-clean" }, searched.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void CaptureBacked_ReadsTheRig_NotTheId()
    {
        Assert.True(PresetBrowserFilter.IsCaptureBacked(Nam("nam-x", "X", DspToneCategory.Clean)));
        Assert.False(PresetBrowserFilter.IsCaptureBacked(
            P("f-clean", "Glass Clean (Legacy)", DspToneCategory.Clean, DspToneOrigin.BuiltIn)));

        // A USER tone built on a capture badges like a factory one — the id says
        // nothing, the chain does.
        var userCapture = new DspTonePreset(
            "user-mine", "Mine", DspToneCategory.Lead, "",
            new DcRigPreset([new DcRigModule("namamp", new Dictionary<string, float>())]),
            DspToneOrigin.User);
        Assert.True(PresetBrowserFilter.IsCaptureBacked(userCapture));
    }
}
