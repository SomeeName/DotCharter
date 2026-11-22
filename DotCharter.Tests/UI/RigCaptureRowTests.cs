using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Online.Tone3000;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The dock's capture column marks exactly the capture its param plays: loading
///     B clears A's mark, whichever path loaded B (#580).
/// </summary>
public sealed class RigCaptureRowTests
{
    private static readonly DspModuleType CabType = new("cab", "Cabinet", [new("mix", "Mix", 0, 1, 1)])
    {
        StringParams = [new("irPath", "IR A", "*.wav")]
    };

    private static RigCaptureRow Row(string file, PedalStringParamViewModel target) =>
        new(new NamCaptureEntry(
                new NamCaptureManifest(1, 1, file, "a", "cab", "wav", "std", "cc", "", "", file, "", 0, DateTimeOffset.UnixEpoch),
                file),
            target);

    [Fact]
    public void LoadingB_ClearsAsMark()
    {
        var chain = new FakeDspChainService([CabType]);
        var node = new PedalNodeViewModel(chain, chain.AddNode("cab")!, CabType, FakeLocalization.English());
        var target = Assert.Single(node.StringParams);
        var a = Row("a.wav", target);
        var b = Row("b.wav", target);
        var aChanges = new List<string?>();
        a.PropertyChanged += (_, e) => aChanges.Add(e.PropertyName);

        a.LoadCommand.Execute(null);
        Assert.True(a.IsActive);
        Assert.Contains(nameof(RigCaptureRow.IsActive), aChanges);

        aChanges.Clear();
        b.LoadCommand.Execute(null);
        Assert.True(b.IsActive);
        Assert.False(a.IsActive);
        Assert.Contains(nameof(RigCaptureRow.IsActive), aChanges);

        // a value that arrives from elsewhere (Browse…, a preset, an undo) moves the mark too
        var bChanges = new List<string?>();
        b.PropertyChanged += (_, e) => bChanges.Add(e.PropertyName);
        target.Value = "c.wav";
        Assert.Contains(nameof(RigCaptureRow.IsActive), bChanges);
        Assert.False(b.IsActive);
    }
}
