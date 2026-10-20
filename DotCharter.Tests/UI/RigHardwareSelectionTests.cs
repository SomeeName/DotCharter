using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

public sealed class RigHardwareSelectionTests
{
    [Fact]
    public void InitialEditorPrefersAmpAndPreservesTheUsersSelectionAndCollapse()
    {
        var chain = new FakeDspChainService();
        var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), FakeLocalization.English());
        rig.AddGear(chain.Catalog.Single(m => m.Id == "overdrive"));
        rig.AddGear(chain.Catalog.Single(m => m.Id == "amp"));
        rig.SelectNodeAt(-1);
        var model = new AmpFxRigViewModel(rig);
        model.EnsureEditorSelection();
        Assert.Equal("amp", model.DockUnit?.Node.ModuleTypeId);
        rig.SelectNodeAt(0);
        model.IsDockOpen = false;
        model.EnsureEditorSelection();
        Assert.Equal("overdrive", model.DockUnit?.Node.ModuleTypeId);
        Assert.False(model.IsDockBodyVisible);
    }
    [Fact]
    public void UsedGearCanBeAddedAgainWithoutLosingTheInRigBadge()
    {
        var chain = new FakeDspChainService();
        var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), FakeLocalization.English());
        var model = new AmpFxRigViewModel(rig);
        var cabinet = model.GearGroups.SelectMany(g => g.Items).Single(i => i.Type.Id == "cab");
        model.AddGearCommand.Execute(cabinet);
        Assert.True(cabinet.IsInRig);
        Assert.True(cabinet.CanAdd);
        model.AddGearCommand.Execute(cabinet);
        Assert.Equal(2, rig.Nodes.Count(n => n.ModuleTypeId == "cab"));
        Assert.True(cabinet.IsInRig);
    }
}
