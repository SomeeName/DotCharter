using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The tone-rig design's board graph: the caption under it, the "Mixer A"
///     nodes, the series card's summary line, the footer's OUTPUT meter (the last
///     unit's output) and the gear panel's "Add to" picker.
/// </summary>
public sealed class RigBoardGraphTests
{
    private static IReadOnlyList<DspModuleType> Catalog() =>
    [
        new DspModuleType("overdrive", "Overdrive", [new DspParamInfo("drive", "Drive", 0f, 10f, 5f)]),
        new DspModuleType("cab", "Cabinet", [new DspParamInfo("model", "Model", 0f, 1f, 0f) { ValueLabels = ["1x12 Combo", "4x12 Stack"] }]),
        new DspModuleType("delay", "Delay", [new DspParamInfo("mix", "Mix", 0f, 1f, 0.3f)]),
        new DspModuleType("splitter", "Splitter", [new DspParamInfo("enabledBranches", "Branches", 2f, 3f, 2f)]),
        new DspModuleType("merger", "Merger", [new DspParamInfo("mode", "Mode", 0f, 1f, 0f)])
    ];

    private static (FakeDspChainService Dsp, PedalboardViewModel Rig, AmpFxRigViewModel Screen) Create()
    {
        var dsp = new FakeDspChainService(Catalog());
        var rig = TestRigBuilders.Pedalboard(dsp, new FakeDspTonePresetService(), FakeLocalization.English());
        return (dsp, rig, new AmpFxRigViewModel(rig));
    }

    private static PedalNodeViewModel Add(PedalboardViewModel rig, string id) =>
        rig.AddGear(rig.AvailableModules.First(m => m.Id == id))!;

    [Fact]
    public void TheCaption_NamesTheRouting_AndFollowsIt()
    {
        var (_, rig, screen) = Create();
        var loc = FakeLocalization.English();
        Add(rig, "overdrive");
        Assert.Equal(loc["Rig_Board_SerialSummary"], screen.RoutingSummary);

        var changes = new List<string?>();
        screen.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        Add(rig, "splitter");
        Assert.Equal(loc.Format("Rig_Board_ParallelSummary", 2), screen.RoutingSummary);
        Assert.Contains(nameof(AmpFxRigViewModel.RoutingSummary), changes);

        rig.RoutingSplit3Command.Execute(null);
        Assert.Equal(loc.Format("Rig_Board_ParallelSummary", 3), screen.RoutingSummary);
        rig.RoutingSerialCommand.Execute(null);
        Assert.Equal(loc["Rig_Board_SerialSummary"], screen.RoutingSummary);
    }

    [Fact]
    public void EveryLane_HasItsOwnMixerLabel()
    {
        var (_, rig, screen) = Create();
        Add(rig, "splitter");
        rig.RoutingSplit3Command.Execute(null);
        var lanes = Assert.Single(screen.BoardSegments.OfType<RigSplitSegment>()).Lanes;
        Assert.Equal(
            new[] { "A", "B", "C" }.Select(l => FakeLocalization.English().Format("Rig_Editor_MixerLabel", l)),
            lanes.Select(l => l.MixerLabel));
    }

    [Fact]
    public void TheSeriesCard_SummarisesTheUnit()
    {
        var (_, rig, _) = Create();
        var drive = Add(rig, "overdrive");
        var cab = Add(rig, "cab");

        // a knob unit: its first knob and value; a modelled unit: the model name
        Assert.Equal($"Drive {drive.KnobParams[0].DisplayText}", drive.BoardSummary);
        Assert.Equal("1x12 Combo", cab.BoardSummary);
        cab.Params.Single(p => p.Id == "model").Value = 1f;
        Assert.Equal("4x12 Stack", cab.BoardSummary);

        // bypassed wins, and the card hears about it
        var changes = new List<string?>();
        drive.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        drive.Bypassed = true;
        Assert.Equal(FakeLocalization.English()["Rig_Unit_Bypassed"], drive.BoardSummary);
        Assert.Contains(nameof(PedalNodeViewModel.BoardSummary), changes);
    }

    [Fact]
    public void TheOutputMeter_ReadsTheLastUnit_WhileAHostIsLive()
    {
        var (dsp, rig, screen) = Create();
        var drive = Add(rig, "overdrive");
        var delay = Add(rig, "delay");
        dsp.MeteringActive = true;
        dsp.Meters[drive.NodeId] = new DspNodeMeter(0.5f, 0.3f, 0.5f, 0.3f);
        dsp.Meters[delay.NodeId] = new DspNodeMeter(0.5f, 0.3f, 1f, 0.7f);

        // no live host: nothing is leased and nothing is read
        screen.PollMeters();
        Assert.False(rig.OutputMeters.HasReading);
        Assert.Equal(0, dsp.MeterLeases);

        var host = new object();
        rig.OutputMeters.SetSurfaceLive(host, true);
        screen.PollMeters();
        Assert.True(rig.OutputMeters.HasReading);
        Assert.Equal(0.0, rig.OutputMeters.OutputDb, 3); // the delay's 1.0 peak, not the drive's

        // the chain's end moves with the chain
        rig.RemoveCommand.Execute(delay);
        screen.PollMeters();
        Assert.Equal(20 * Math.Log10(0.5), rig.OutputMeters.OutputDb, 3);

        rig.OutputMeters.SetSurfaceLive(host, false);
        Assert.Equal(0, dsp.MeterLeases);
        Assert.False(rig.OutputMeters.HasReading);
    }

    [Fact]
    public void TheAddToPicker_ShowsAndWritesTheTargetLane()
    {
        var (_, rig, screen) = Create();
        Add(rig, "overdrive");
        Add(rig, "splitter");
        Assert.Equal([null, 1, 2], screen.AddTargetLanes.Select(o => o.Lane).ToArray());

        // the picker's face is the current target (the serial run at first)
        Assert.Null(screen.AddTargetOption!.Lane);

        var changes = new List<string?>();
        screen.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        screen.AddTargetOption = screen.AddTargetLanes.Single(o => o.Lane == 2);
        Assert.Equal(2, screen.AddTargetLane);
        Assert.True(screen.AddTargetLanes.Single(o => o.Lane == 2).IsActive);

        // …and it follows a write from the other side
        screen.AddTargetLane = 1;
        Assert.Equal(1, screen.AddTargetOption!.Lane);
        Assert.Contains(nameof(AmpFxRigViewModel.AddTargetOption), changes);

        // a lane that stops existing drops the picker back to the serial run
        rig.RoutingSerialCommand.Execute(null);
        Assert.Null(screen.AddTargetOption!.Lane);
    }

    [Fact]
    public void TheGearList_HighlightsARow_AndAddToRigAddsIt()
    {
        var (_, rig, screen) = Create();
        Add(rig, "overdrive");
        RigGearItem Row(string id) => screen.GearGroups.SelectMany(g => g.Items).Single(i => i.ModuleTypeId == id);

        // nothing highlighted: the button is off
        Assert.False(screen.HasSelectedGear);
        Assert.False(screen.AddSelectedGearCommand.CanExecute(null));

        // a click highlights ONE row
        screen.SelectGearCommand.Execute(Row("delay"));
        Assert.True(Row("delay").IsSelected);
        Assert.Same(Row("delay"), screen.SelectedGear);
        screen.SelectGearCommand.Execute(Row("cab"));
        Assert.False(Row("delay").IsSelected);
        Assert.True(Row("cab").IsSelected);
        Assert.True(screen.AddSelectedGearCommand.CanExecute(null));

        // Add to rig seats it at the end of the chain, and the highlight survives
        // the list being rebuilt around the new unit
        screen.AddSelectedGearCommand.Execute(null);
        Assert.Equal("cab", rig.Nodes[^1].ModuleTypeId);
        Assert.True(Row("cab").IsSelected);
        Assert.Same(Row("cab"), screen.SelectedGear);
    }
}
