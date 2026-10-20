using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Audio.Dsp.RsToneConversion;

public sealed class DcRigInsertionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedConfiguration_StaysBypassedWhenRetried(bool rejectString)
    {
        var chain = new FakeDspChainService([
            new DspModuleType("cab", "Cabinet", [new DspParamInfo("model", "Model", 0, 11, 0)])])
        {
            FailParam = !rejectString,
            FailSetString = rejectString
        };
        var preset = new DcRigPreset([
            new DcRigModule("cab", new Dictionary<string, float> { ["model"] = 2 })
            { StringParams = new Dictionary<string, string> { ["irPath"] = "custom.wav" } }]);
        var applier = new DcRigApplier(chain);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.True(applier.Apply(preset).FailedOps > 0);
            Assert.True(Assert.Single(chain.GetNodes()).Bypassed);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewlyPublishedNodes_AreBypassedUntilConfigured(bool stayBypassed)
    {
        var chain = new FakeDspChainService([
            new DspModuleType("gain", "Gain", [new DspParamInfo("gainDb", "Gain", -24, 24, 12)])]);
        bool? bypassWhenPublished = null;
        chain.NodesChanged += () => bypassWhenPublished ??= Assert.Single(chain.GetNodes()).Bypassed;

        var result = new DcRigApplier(chain).Apply(new DcRigPreset([
            new DcRigModule("gain", new Dictionary<string, float> { ["gainDb"] = -12 }, stayBypassed)]));

        Assert.True(bypassWhenPublished);
        var finalNode = Assert.Single(chain.GetNodes());
        Assert.Equal(-12f, finalNode.Values["gainDb"]);
        Assert.Equal(stayBypassed, finalNode.Bypassed);
        Assert.Equal(0, result.FailedOps);
    }
}
