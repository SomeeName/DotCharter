using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Audio.Dsp.RsToneConversion;

public sealed class DcRigStringResetTests
{
    [Theory]
    [InlineData("cab", "irPath")]
    [InlineData("acousticsim", "irPath")]
    [InlineData("namamp", "modelPath")]
    [InlineData("miku", "phrases")]
    public void ApplyingAPresetWithoutCustomContent_ClearsPreviousContent(string type, string parameter)
    {
        var chain = new FakeDspChainService([new DspModuleType(type, type, [])]);
        var node = Assert.IsType<DspNode>(chain.AddNode(type));
        Assert.True(chain.SetNodeString(node.NodeId, parameter, "previous custom content"));
        var applier = new DcRigApplier(chain);
        var preset = new DcRigPreset([new DcRigModule(type, new Dictionary<string, float>())]);

        var applied = applier.Apply(preset);

        Assert.Equal(string.Empty, chain.GetNodeString(node.NodeId, parameter));
        Assert.Equal(0, applied.FailedOps);
        Assert.Equal(node.NodeId, Assert.Single(chain.GetNodes()).NodeId);
        Assert.True(applier.Apply(preset).NoOp);
    }

    [Fact]
    public void RestoringDefaultCabSnapshot_ClearsPreviewIrOnReusedCab()
    {
        var chain = new FakeDspChainService([new DspModuleType("cab", "Cabinet", [])]);
        var node = Assert.IsType<DspNode>(chain.AddNode("cab"));
        var snapshot = DcRigSnapshot.FromNodes(chain.GetNodes());
        Assert.True(chain.SetNodeString(node.NodeId, "irPath", "preview.wav"));

        new DcRigApplier(chain).Apply(snapshot);

        Assert.Equal(string.Empty, chain.GetNodeString(node.NodeId, "irPath"));
    }
}
