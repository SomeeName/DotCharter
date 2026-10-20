using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The pedal enclosure's projection over the unit (<c>RigDockUnit.Enclosure</c>):
///     every parameter the module has appears in its grid exactly once and in
///     catalogue order, the grid follows the artwork's column rule, and the
///     finish comes from the design's per-pedal coat. Nothing here invents a
///     control (root AGENTS.md §7).
/// </summary>
public sealed class RigDockUnitEnclosureTests
{
    private static DspModuleType Overdrive() => new("overdrive", "Overdrive",
    [
        new("model", "Pedal Model", 0, 2, 0) { ValueLabels = ["Generic", "Screamer", "Bluesy"] },
        new("drive", "Drive", 0, 1, 0.5f), new("tone", "Tone", 0, 1, 0.5f), new("level", "Level", 0, 1, 0.7f)
    ]);

    private static DspModuleType Delay() => new("delay", "Delay (Digital)",
    [
        new("timeMs", "Time (ms)", 1, 2000, 350), new("feedback", "Feedback", 0, 1, .3f), new("mix", "Mix", 0, 1, .3f),
        new("sync", "Tempo Sync", 0, 1, 0) { ValueLabels = ["Off", "On"] },
        new("division", "Division", 0, 3, 1) { ValueLabels = ["1/4", "1/8", "1/8.", "1/16"] },
        new("toneHz", "Tone (Hz)", 500, 8000, 4000), new("bpm", "BPM", 40, 240, 120)
    ]);

    private static DspModuleType CleanBoost() => new("gain", "Clean Boost",
        [new("gainDb", "Gain (dB)", -24, 24, 0)]);

    private static RigDockUnit Unit(DspModuleType type)
    {
        var dsp = new FakeDspChainService(new[] { type });
        var node = dsp.AddNode(type.Id)!;
        return new RigDockUnit(new PedalNodeViewModel(dsp, node, type, FakeLocalization.English()));
    }

    [Fact]
    public void TheGrid_HoldsEveryParamOnce_InCatalogueOrder_WithPickersForLabelledOnes()
    {
        var unit = Unit(Overdrive());
        Assert.True(unit.IsPedalFamily);
        var controls = unit.EnclosureControls;
        Assert.Equal(4, controls.Count);
        Assert.IsType<PedalModelSelectorViewModel>(controls[0]);
        Assert.Equal("model", ((PedalModelSelectorViewModel)controls[0]).ParamId);
        Assert.Equal(["drive", "tone", "level"], controls.Skip(1).Cast<PedalParamViewModel>().Select(p => p.Id));
        // the pickers are the NODE's own, never a second wrapper
        Assert.Same(unit.Node.ModelSelectors.Single(), controls[0]);
        Assert.Same(unit.Node.KnobParams[0], controls[1]);
        Assert.Equal(2, unit.EnclosureColumns);
        Assert.Equal(2 * 142 + 68, unit.EnclosureNaturalWidth);
        Assert.False(unit.ShowsEnclosureBrand);
    }

    /// <summary>The artwork rule still gives a lone control the two-column
    /// enclosure (its eyebrow and name need the width), but the grid lays the
    /// one knob in ONE column at the big single dial, so it stands centred
    /// instead of in the left cell of an empty pair.</summary>
    [Fact]
    public void ALoneControl_KeepsTheTwoColumnEnclosure_ButOneGridColumn_AtTheBigDial()
    {
        var unit = Unit(CleanBoost());
        Assert.True(unit.IsPedalFamily);
        Assert.Single(unit.EnclosureControls);
        Assert.Equal(2, unit.EnclosureColumns);
        Assert.Equal(2 * 142 + 68, unit.EnclosureNaturalWidth);
        Assert.Equal(1, unit.EnclosureGridColumns);
        Assert.Equal(RigDockUnit.SingleDial, unit.EnclosureDialSize);
        Assert.True(RigDockUnit.SingleDial > 64, "bigger than the 64 px hardware dial");

        var pair = Unit(Overdrive());
        Assert.Equal(pair.EnclosureColumns, pair.EnclosureGridColumns);
        Assert.Equal(0, pair.EnclosureDialSize);
    }

    [Fact]
    public void ASevenControlPedal_GoesFourAcross_AndKeepsItsBrand()
    {
        var unit = Unit(Delay());
        Assert.Equal(7, unit.EnclosureControls.Count);
        Assert.Equal(4, unit.EnclosureColumns);
        Assert.True(unit.ShowsEnclosureBrand);
        Assert.Equal("Delay", unit.EnclosureTitle); // the parenthetical belongs to the dock bar
        var ids = unit.EnclosureControls.Select(c => c is PedalParamViewModel p ? p.Id : ((PedalModelSelectorViewModel)c).ParamId).ToList();
        Assert.Equal(ids.Distinct().Count(), ids.Count);
        Assert.Equal(unit.Node.Params.Select(p => p.Id), ids);
    }

    [Fact]
    public void TheFinish_IsTheDesignsCoat_AndTheFootswitchLabelFollowsBypass()
    {
        var unit = Unit(Overdrive());
        Assert.Equal(RigArtwork.CoatFor("overdrive"), unit.CoatColor);
        Assert.Equal(RigArtwork.AccentFor("overdrive"), unit.EnclosureAccent);
        Assert.True(unit.IsEngaged);
        var labels = new List<string?>();
        unit.PropertyChanged += (_, e) => labels.Add(e.PropertyName);
        unit.Node.Bypassed = true;
        Assert.False(unit.IsEngaged);
        Assert.Contains(nameof(RigDockUnit.FootswitchLabel), labels);
        Assert.Contains(nameof(RigDockUnit.IsEngaged), labels);
        Assert.NotEqual(Unit(Overdrive()).FootswitchLabel, unit.FootswitchLabel);
    }

    [Fact]
    public void AnAmp_IsNotAPedalFamily_SoTheEnclosureDoesNotClaimIt()
    {
        var amp = new DspModuleType("amp", "Legacy Amp", [new("gain", "Gain", 0, 1, .5f)]);
        Assert.False(Unit(amp).IsPedalFamily);
    }
}
