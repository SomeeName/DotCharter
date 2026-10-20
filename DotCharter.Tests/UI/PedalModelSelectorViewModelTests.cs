using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
/// The model-picker VM (ABI v24 value labels): flat options for the compact
/// selector, grouped options for the amp popup, and a two-way binding to the
/// underlying param value (index == value − min). Driven over a throw-free
/// <see cref="NullDspChainService"/> — the native write no-ops but the managed
/// value + label reflect, which is what the picker binds. No Avalonia instance.
/// </summary>
public sealed class PedalModelSelectorViewModelTests
{
    private static readonly string[] AmpVoicings =
    [
        "Blackface Twin 85", "Plexi 50", "Five-Oh-One Lead", "AC-Thirty", "Recto Dual",
        "Jazz Solid 120", "Brown Variac", "Tweed Champ 5W", "Tweed Twin 40", "Blackface Deluxe 22",
        "Plexi 100", "JMP-Eight-Hundred", "Two-Oh-Three Crunch", "AC-Fifteen", "Recto Triple",
        "Modern Djent 8-String", "PowerBall Lead", "Overdrive Special", "Class-A Boutique 30",
        "Bass Three-Sixty", "Bass Flip-Top", "SVT Bass 300W", "Acoustic Pre", "Studio Direct"
    ];

    private static PedalParamViewModel Param(string id, float min, float max, float value, string[] labels)
        => new(new NullDspChainService(), 1,
            new DspParamInfo(id, id, min, max, min) { ValueLabels = labels }, value);

    [Fact]
    public void FlatSelector_BuildsOptions_AndSelectionWritesTheParamValue()
    {
        var p = Param("model", 0, 3, 0, ["Generic", "Screamer", "Rat", "Metal"]);
        var sel = new PedalModelSelectorViewModel(p, PedalModelSelectorKind.Flat, FakeLocalization.English());

        Assert.Equal(4, sel.Options.Count);
        Assert.Equal("Generic", sel.CurrentLabel);
        Assert.False(sel.IsGrouped);

        // Selecting an option writes value = min + index.
        sel.SelectedOption = sel.Options.First(o => o.Label == "Metal");
        Assert.Equal(3f, p.Value);
        Assert.Equal("Metal", sel.CurrentLabel);
    }

    [Fact]
    public void ExternalValueChange_ReflectsBackIntoSelection()
    {
        var p = Param("model", 0, 3, 0, ["Generic", "Screamer", "Rat", "Metal"]);
        var sel = new PedalModelSelectorViewModel(p, PedalModelSelectorKind.Flat, FakeLocalization.English());

        p.Value = 1f; // e.g. a preset apply / ‹ › move
        Assert.Equal(1, sel.SelectedOption!.Index);
        Assert.Equal("Screamer", sel.CurrentLabel);
    }

    [Fact]
    public void AmpVoicing_IsGrouped_ByFamily_InDisplayOrder()
    {
        var p = Param("voicing", 0, 23, 10, AmpVoicings);
        var sel = new PedalModelSelectorViewModel(p, PedalModelSelectorKind.AmpVoicing, FakeLocalization.English());

        Assert.True(sel.IsGrouped);
        Assert.Equal("Plexi 100", sel.CurrentLabel); // value 10

        // Every model appears exactly once across the groups.
        var flattened = sel.Groups.SelectMany(g => g.Options).Select(o => o.Label).ToList();
        Assert.Equal(24, flattened.Count);
        Assert.Contains("Plexi 100", flattened);

        // "Plexi 100" (index 10) lands under the British group.
        var british = sel.Groups.Single(g =>
            g.Options.Any(o => o.Label == "Plexi 100"));
        Assert.Contains(british.Options, o => o.Label == "Plexi 50");
    }

    [Fact]
    public void SelectCommand_SetsModel_AndClosesPopup()
    {
        var p = Param("voicing", 0, 23, 0, AmpVoicings);
        var sel = new PedalModelSelectorViewModel(p, PedalModelSelectorKind.AmpVoicing, FakeLocalization.English())
        {
            IsPopupOpen = true
        };

        var plexi100 = sel.Options.First(o => o.Label == "Plexi 100");
        Assert.NotNull(plexi100.SelectCommand); // the option carries its own command (no ElementName)
        plexi100.SelectCommand!.Execute(null);

        Assert.Equal(10f, p.Value);
        Assert.False(sel.IsPopupOpen);
    }
}
