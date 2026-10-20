using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The cabinet mic placement drives the REAL mic model — the native <c>micpos</c>
///     unit's mic, position and distance — never the cab's filters dressed up as a
///     mic (what the old pad did). The layout and the pointer are
///     <c>Perf/RigCabinetEditorUiPerfSmokeTests</c>.
/// </summary>
public sealed class RigMicPlacementTests
{
    private static readonly DspModuleType MicType = new("micpos", "Cab Mic Position",
    [
        new("mic", "Mic", 0, 4, 0) { ValueLabels = ["57", "121", "421", "Condenser", "Tube"] },
        new("position", "Position (Cap-Edge)", 0, 1, 0.3f), new("distanceCm", "Distance (cm)", 0, 30, 5),
        new("room", "Room", 0, 1, 0), new("blend2", "Second Mic", 0, 1, 0), new("level", "Level", 0, 1, 0.7f),
        new("angleDeg", "Angle (deg)", 0, 60, 0)
    ]);

    private static readonly DspModuleType CabType = new("cab", "Cabinet",
    [
        new("mix", "Mix", 0, 1, 1), new("lowCutHz", "Low Cut", 40, 200, 60), new("highCutHz", "High Cut", 3000, 12000, 8000)
    ]);

    private static readonly DspModuleType DelayType = new("delay", "Delay", [new("mix", "Mix", 0, 1, 0.3f)]);

    [Theory]
    [InlineData(0, 0, 0, 60)]        // the dead centre keeps the default bearing
    [InlineData(0.5, 0, 0.5, 90)]    // right of the cap, half way out
    [InlineData(0, -0.3, 0.3, 0)]    // straight up
    [InlineData(-2, 0, 1, 270)]      // past the edge clamps to it
    public void APress_PlacesByRadius_AndKeepsItsBearing(double dx, double dy, double position, double bearing)
    {
        var (p, b) = MicPlacementMath.Place(dx, dy);
        Assert.Equal(position, p, 6);
        Assert.Equal(bearing, b, 6);
        if (position > 0)
        {
            var (x, y) = MicPlacementMath.Point(p, b);
            Assert.Equal(Math.Clamp(dx, -1, 1), x, 6);
            Assert.Equal(dy, y, 6);
        }
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.17, 0)]
    [InlineData(0.18, 1)]
    [InlineData(0.39, 1)]
    [InlineData(0.40, 2)]
    [InlineData(0.71, 2)]
    [InlineData(0.72, 3)]
    [InlineData(1.0, 3)]
    public void TheZones_NameWhereOnTheSpeakerTheCapsuleIs(double position, int zone)
        => Assert.Equal(zone, MicPlacementMath.Zone(position));

    [Fact]
    public void Distance_MapsTheRuler_AndFarAwayDrawsSmaller()
    {
        Assert.Equal(15, MicPlacementMath.DistanceForFraction(0.5), 6);
        Assert.Equal(30, MicPlacementMath.DistanceForFraction(3), 6);
        Assert.Equal(0.5, MicPlacementMath.FractionForDistance(15), 6);
        Assert.Equal(1.0, MicPlacementMath.PerspectiveScale(0), 6);
        Assert.Equal(0.6, MicPlacementMath.PerspectiveScale(30), 6);
        Assert.True(MicPlacementMath.ShadowOffset(30) > MicPlacementMath.ShadowOffset(0));
        Assert.Equal([0, 1, 2], new[] { 1.0, 5, 20 }.Select(MicPlacementMath.DistanceZone));
    }

    [Fact]
    public void ThePlacement_WritesTheMicModelsOwnParams_AndFollowsThem()
    {
        var dsp = new FakeDspChainService(new[] { MicType });
        var node = new PedalNodeViewModel(dsp, dsp.AddNode("micpos")!, MicType, FakeLocalization.English());
        using var mic = RigMicPlacementViewModel.For(node)!;
        var changed = new List<string?>();
        mic.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        mic.Position = 0.9;
        Assert.Equal(0.9f, node.Params.Single(p => p.Id == "position").Value, 3);
        Assert.Equal(3, mic.Zone);
        Assert.Contains(nameof(RigMicPlacementViewModel.PositionReadout), changed);

        mic.DistanceCm = 45; // clamps to the model's range
        Assert.Equal(30f, node.Params.Single(p => p.Id == "distanceCm").Value, 3);

        mic.Mics[2].SelectCommand.Execute(null);
        Assert.Equal(2f, node.Params.Single(p => p.Id == "mic").Value);
        Assert.True(mic.Mics[2].IsActive);
        Assert.False(mic.Mics[0].IsActive);

        // a knob (or a preset) moving the param moves the placement
        node.Params.Single(p => p.Id == "position").Value = 0.1f;
        Assert.Equal(0.1, mic.Position, 3);
        Assert.Equal(0, mic.Zone);

        mic.ResetPlacementCommand.Execute(null);
        Assert.Equal(0.3, mic.Position, 3);
        Assert.Equal(5, mic.DistanceCm, 3);

        Assert.NotNull(mic.Room);
        Assert.NotNull(mic.SecondMic);
        Assert.NotNull(mic.Level);

        // #75: one card per mic the model offers (the tube condenser is the
        // fifth), and the angle knob is the model's own `angleDeg`.
        Assert.Equal(5, mic.Mics.Count);
        mic.Mics[4].SelectCommand.Execute(null);
        Assert.Equal(4f, node.Params.Single(p => p.Id == "mic").Value);
        Assert.Equal(4, mic.ActiveMic.Index);
        Assert.Same(node.Params.Single(p => p.Id == "angleDeg"), mic.Angle);
        Assert.Null(RigMicPlacementViewModel.For(new PedalNodeViewModel(dsp, dsp.AddNode("micpos")!, DelayType, FakeLocalization.English())));
    }

    [Fact]
    public void ACabinet_FindsTheMicAfterIt_ButNotPastTheNextCab()
    {
        var chain = new FakeDspChainService(new[] { CabType, MicType, DelayType });
        using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), FakeLocalization.English());
        var cab1 = rig.AddGear(CabType)!;
        rig.AddGear(DelayType);
        var cab2 = rig.AddGear(CabType)!;
        var mic = rig.AddGear(MicType)!;

        Assert.Null(RigDockUnit.MicAfterCab(rig, cab1));   // cab2 stands between them
        Assert.Same(mic, RigDockUnit.MicAfterCab(rig, cab2));
    }

    [Fact]
    public void AddingAMic_PutsTheMicModelStraightAfterTheCab()
    {
        var chain = new FakeDspChainService(new[] { CabType, MicType, DelayType });
        using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), FakeLocalization.English());
        var cab = rig.AddGear(CabType)!;
        rig.AddGear(DelayType);
        rig.SelectNodeAt(0);
        var unit = new RigDockUnit(cab, rig: rig);

        Assert.True(unit.IsCabinetFamily);
        Assert.False(unit.HasCabMic);
        Assert.True(unit.CanAddCabMic);
        unit.AddCabMicCommand.Execute(null);

        Assert.Equal(["cab", "micpos", "delay"], rig.Nodes.Select(n => n.ModuleTypeId));
        Assert.True(unit.HasCabMic);
        Assert.False(unit.CanAddCabMic);
        Assert.Same(rig.Nodes[1], unit.CabMic!.Node);
        Assert.Same(cab, rig.SelectedNode);

        unit.OpenCabMicCommand.Execute(null);
        Assert.Same(rig.Nodes[1], rig.SelectedNode);
        unit.Release();
    }

    [Fact]
    public void TheMicUnit_OpensItsOwnPlacement()
    {
        Assert.Equal(RigEditorFamily.MicPlacement, RigDockUnit.EditorFamilyFor("micpos", hasFaceplate: true));
        var dsp = new FakeDspChainService(new[] { MicType });
        var unit = new RigDockUnit(new PedalNodeViewModel(dsp, dsp.AddNode("micpos")!, MicType, FakeLocalization.English()));
        Assert.True(unit.IsMicPlacementFamily);
        Assert.NotNull(unit.MicPlacement);
        Assert.Null(unit.CabMic);
        unit.Release();
    }

    [Fact]
    public void TheCabinetsResponseCurve_IsFlatInTheMiddle_AndThreeDbDownAtTheCuts()
    {
        Assert.Equal(0, CabinetFilterMath.FilterGainDb(1000, 60, 8000), 1);
        Assert.Equal(-3.0, CabinetFilterMath.FilterGainDb(60, 60, 8000), 0);
        Assert.Equal(-3.0, CabinetFilterMath.FilterGainDb(8000, 60, 8000), 0);
        Assert.True(CabinetFilterMath.FilterGainDb(20, 60, 8000) < -15);
    }
}
