using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Presentation.Dialogs;

public sealed class TonePreviewSessionTests
{
    [Fact]
    public async Task OffOnBeforeRestoreCompletes_KeepsTheOriginalBoard_AndRejectsTheOldRestore()
    {
        var chain = new FakeDspChainService();
        chain.AddNode("reverb");
        var work = new List<Action>();
        var preview = new TonePreviewSession(chain, new DcRigApplier(chain))
        {
            OffThreadRunner = action => { work.Add(action); return Task.CompletedTask; }
        };
        var tone = RsToneAuthoring.CreateDefaultTone("preview", "preview", RsGearCatalog.Shared);
        preview.Apply(tone);
        work[0]();
        Assert.True(preview.Stop());
        preview.Apply(tone with { VolumeDb = -9 });
        Assert.Equal("reverb", Assert.Single(preview.CurrentRig().Modules).ModuleTypeId);
        // Workers may start in reverse order. The obsolete restore must not win.
        work[2]();
        work[1]();
        Assert.Contains(chain.GetNodes(), n => n.ModuleTypeId == "amp");
        Assert.True(preview.Stop());
        Assert.False(preview.Stop());
        work[3]();
        await preview.LastTask!;
        Assert.Equal("reverb", Assert.Single(chain.GetNodes()).ModuleTypeId);
    }
}
