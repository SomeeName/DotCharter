using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
/// The gear rail's "browse by model" surface (ABI v24): an amp/cab card exposes
/// one option per named model, adds it PRE-SET to that voicing, and the search
/// matches model label text ("Plexi" → the Amp card). Headless VM logic.
/// </summary>
public sealed class GearRailViewModelTests
{
    private static DspModuleType Amp() => new("amp", "Amp", new[]
    {
        new DspParamInfo("voicing", "Voicing", 0, 23, 1)
        {
            ValueLabels = new[]
            {
                "Blackface Twin 85", "Plexi 50", "Five-Oh-One Lead", "AC-Thirty", "Recto Dual",
                "Jazz Solid 120", "Brown Variac", "Tweed Champ 5W", "Tweed Twin 40", "Blackface Deluxe 22",
                "Plexi 100", "JMP-Eight-Hundred", "Two-Oh-Three Crunch", "AC-Fifteen", "Recto Triple",
                "Modern Djent 8-String", "PowerBall Lead", "Overdrive Special", "Class-A Boutique 30",
                "Bass Three-Sixty", "Bass Flip-Top", "SVT Bass 300W", "Acoustic Pre", "Studio Direct"
            }
        },
        new DspParamInfo("gain", "Gain", 0, 1, 0.5f)
    });

    private static DspModuleType Delay() => new("delay", "Delay", new[]
    {
        new DspParamInfo("time", "Time", 0, 2000, 350)
    });

    [Fact]
    public void AmpCard_ExposesOneOptionPerModel()
    {
        var rail = new GearRailViewModel(new[] { Amp() }, FakeLocalization.English(), _ => { });
        var card = rail.Cards.Single(c => c.ModuleTypeId == "amp");

        Assert.True(card.HasModels);
        Assert.Equal(24, card.Models.Count);
        Assert.Equal("Plexi 100", card.Models[10].Label);
        Assert.Equal("voicing", card.Models[10].ParamId);
        Assert.Equal(10, card.Models[10].Index);
    }

    [Fact]
    public void ModuleWithoutModels_HasNoBrowseAffordance()
    {
        var rail = new GearRailViewModel(new[] { Delay() }, FakeLocalization.English(), _ => { });
        Assert.False(rail.Cards.Single().HasModels);
    }

    [Fact]
    public void AddGearModel_InvokesTheModelCallback_WithParamAndIndex()
    {
        (DspModuleType Type, string ParamId, int Index)? added = null;
        var rail = new GearRailViewModel(new[] { Amp() }, FakeLocalization.English(), _ => { },
            (t, p, i) => added = (t, p, i));

        var card = rail.Cards.Single(c => c.ModuleTypeId == "amp");
        // The rail's own `AddGearModelCommand` is gone (0.58) — the model pickers
        // are not on the card. Each option's OWN command is the live path over the
        // same callback, which is what the flyout binds (a popup namescope cannot
        // reach an ancestor binding).
        card.Models[10].AddCommand!.Execute(null); // Plexi 100

        Assert.NotNull(added);
        Assert.Equal("amp", added!.Value.Type.Id);
        Assert.Equal("voicing", added.Value.ParamId);
        Assert.Equal(10, added.Value.Index);
    }

    [Fact]
    public void Search_MatchesModelLabelText_SurfacingTheAmpCard()
    {
        var rail = new GearRailViewModel(new[] { Amp(), Delay() }, FakeLocalization.English(), _ => { })
        {
            SearchText = "plexi"
        };

        Assert.Single(rail.Cards);
        Assert.Equal("amp", rail.Cards[0].ModuleTypeId);
    }

    /// <summary>Static perf audit 2026-08-29 P1 #16b: typing rebuilt the whole
    /// card list per keystroke. With a post the typing run coalesces into ONE
    /// rebuild that lands when the post drains; a tab click still rebuilds at
    /// once, because it is a discrete gesture.</summary>
    [Fact]
    public void Typing_CoalescesIntoOneRebuild_WhenThePostDrains()
    {
        var pending = new List<Action>();
        var rail = new GearRailViewModel(new[] { Amp(), Delay() }, FakeLocalization.English(), _ => { }, searchPost: pending.Add);
        var passesAfterConstruction = rail.SearchPass.PassCount;

        rail.SearchText = "p";
        rail.SearchText = "pl";
        rail.SearchText = "ple";
        rail.SearchText = "plexi";

        Assert.Equal(passesAfterConstruction, rail.SearchPass.PassCount);
        Assert.Single(pending);
        Assert.Equal(2, rail.Cards.Count); // still the unfiltered list

        pending[0]();
        Assert.Equal(passesAfterConstruction + 1, rail.SearchPass.PassCount);
        Assert.Single(rail.Cards);
        Assert.Equal("amp", rail.Cards[0].ModuleTypeId);
    }
}
