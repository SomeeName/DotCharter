using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     The tone LIBRARY's reading aids (2026-09-27): the rail's per-row counts
///     under the current search, the card of the tone that is actually playing,
///     each card's chain spelled out in the catalogue's words, the empty grid
///     saying WHY it is empty, and the designer column's width rule. No Avalonia
///     instance.
/// </summary>
public sealed class PresetBrowserLibraryTests
{
    private static DcRigPreset Rig(params string[] modules) =>
        new(modules.Select(m => new DcRigModule(m, new Dictionary<string, float>())).ToList());

    private static readonly IReadOnlyList<DspTonePreset> Catalogue =
    [
        new("c1", "Glass Clean", DspToneCategory.Clean, "Sparkly.", Rig("compressor", "amp", "cab")),
        new("c2", "Warm Clean", DspToneCategory.Clean, "", Rig("amp", "cab")),
        new("f1", "Fuzz Wall", DspToneCategory.HighGain, "A wall of fuzz.", Rig("fuzz", "amp", "cab")),
        new("u1", "My Fuzz", DspToneCategory.HighGain, "", Rig("fuzz", "amp"), DspToneOrigin.User)
    ];

    private static PresetBrowserViewModel NewVm(Func<string, string>? names = null) =>
        new(new FakeDspTonePresetService(Catalogue), FakeLocalization.English(), () => Rig("amp"), () => { },
            moduleDisplayName: names);

    private static PresetRailRow Row(PresetBrowserViewModel vm, PresetFilterKind kind, DspToneCategory cat = default)
        => vm.Rail.First(r => r.Kind == kind && (kind != PresetFilterKind.Category || r.Category == cat));

    /// <summary>
    ///     Each rail row counts its slice UNDER THE SEARCH, and a row that holds
    ///     nothing is flagged so the rail can dim it. Proven non-vacuous by
    ///     counting without the search: "fuzz" then left Clean at 2 and this
    ///     failed on it.
    /// </summary>
    [Fact]
    public void The_rail_counts_each_slice_under_the_current_search()
    {
        var vm = NewVm();
        Assert.Equal(4, Row(vm, PresetFilterKind.All).Count);
        Assert.Equal(2, Row(vm, PresetFilterKind.Category, DspToneCategory.Clean).Count);
        Assert.Equal(1, Row(vm, PresetFilterKind.User).Count);

        vm.SearchText = "fuzz";

        Assert.Equal(2, Row(vm, PresetFilterKind.All).Count);
        Assert.Equal(0, Row(vm, PresetFilterKind.Category, DspToneCategory.Clean).Count);
        Assert.True(Row(vm, PresetFilterKind.Category, DspToneCategory.Clean).IsEmpty);
        Assert.Equal("2", Row(vm, PresetFilterKind.Category, DspToneCategory.HighGain).CountText);
        Assert.False(Row(vm, PresetFilterKind.Category, DspToneCategory.HighGain).IsEmpty);
    }

    /// <summary>The card of the tone on the rig is marked, and only that one —
    /// and the mark follows an apply that came from elsewhere (the designer's
    /// Hear it), which the selection does not.</summary>
    [Fact]
    public void Exactly_the_applied_tone_is_marked_active()
    {
        var vm = NewVm();
        Assert.DoesNotContain(vm.Cards, c => c.IsActive);

        vm.ApplyCard(vm.Cards.First(c => c.Id == "f1"));
        Assert.Equal(["f1"], vm.Cards.Where(c => c.IsActive).Select(c => c.Id));

        vm.ApplyPreset(new DspTonePreset("ai", "Designed", DspToneCategory.Lead, "", Rig("amp")));
        Assert.DoesNotContain(vm.Cards, c => c.IsActive);
        Assert.Same(vm.Cards.First(c => c.Id == "f1"), vm.SelectedCard);
    }

    /// <summary>A card spells its chain out in the catalogue's words, in rig
    /// order, and falls back to the id for a unit the catalogue cannot name.</summary>
    [Fact]
    public void A_card_spells_its_chain_in_the_catalogues_words()
    {
        var vm = NewVm(id => id switch { "fuzz" => "Fuzz Face", "amp" => "Legacy Amp", _ => id });
        var card = vm.Cards.First(c => c.Id == "f1");

        Assert.Equal("Fuzz Face → Legacy Amp → cab", card.ChainSummary);
        Assert.Equal(["Fuzz Face", "Legacy Amp", "cab"], card.ModuleGlyphs.Select(g => g.Name));
        Assert.All(card.ModuleGlyphs, g => Assert.StartsWith("FireUnit", g.TokenKey, StringComparison.Ordinal));
        Assert.True(card.HasDescription);
        Assert.False(vm.Cards.First(c => c.Id == "c2").HasDescription);
    }

    /// <summary>
    ///     The empty grid says WHY it is empty — the search, an empty My Tones,
    ///     or nothing in the slice — and a search that matched nothing can be
    ///     cleared from there.
    /// </summary>
    [Fact]
    public void The_empty_grid_names_its_reason_and_a_search_can_be_cleared()
    {
        var loc = FakeLocalization.English();
        var vm = NewVm();

        vm.SearchText = "zzz";
        Assert.True(vm.ShowNoCards);
        Assert.True(vm.IsSearching);
        Assert.Equal(loc.Format("PresetBrowser_NoMatch", "zzz"), vm.EmptyText);

        vm.ClearSearchCommand.Execute(null);
        Assert.Equal(string.Empty, vm.SearchText);
        Assert.False(vm.IsSearching);
        Assert.True(vm.ShowCards, "clearing the search did not bring the cards back");

        var empty = new PresetBrowserViewModel(new FakeDspTonePresetService([Catalogue[0]]), FakeLocalization.English(), () => Rig("amp"), () => { });
        empty.SelectedRail = Row(empty, PresetFilterKind.User);
        Assert.True(empty.ShowNoCards);
        Assert.Equal(loc["PresetBrowser_EmptyUser"], empty.EmptyText);

        empty.SelectedRail = Row(empty, PresetFilterKind.Category, DspToneCategory.Bass);
        Assert.Equal(loc["PresetBrowser_Empty"], empty.EmptyText);
    }

    /// <summary>
    ///     The designer column keeps its token on a wide body, gives way on a
    ///     narrow one so the grid keeps a whole card, and never drops below a
    ///     usable floor. At 1280×800 the token alone left the grid narrower than
    ///     one card and cut it.
    /// </summary>
    [Theory]
    [InlineData(1520, 460, 460)]   // 1920 wide: the token
    [InlineData(870, 460, 365)]    // 1280 wide: 42 % of the body
    [InlineData(600, 460, 340)]    // narrower still: the floor
    [InlineData(2400, 580, 580)]   // the wide ladder's token
    [InlineData(500, 300, 300)]    // a token below the floor is its own floor
    [InlineData(0, 460, 460)]      // not laid out yet: the token
    public void The_designer_column_gives_way_to_the_grid_on_a_small_window(
        double available, double token, double expected) => Assert.Equal(expected, RigTonesSizing.DesignerWidth(available, token));
}
